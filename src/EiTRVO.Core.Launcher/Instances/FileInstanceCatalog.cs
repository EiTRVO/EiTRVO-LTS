using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Instances;

/// <summary>
/// <see cref="IInstanceCatalog"/> 的真实实现：一个实例就是 <c>&lt;游戏目录&gt;\versions\</c> 下的
/// **一个目录**（对齐 Neo 26.1）。元数据 <c>instance.json</c>、启动参数覆盖 <c>overrides.json</c>、
/// 版本 JSON <c>version.json</c> 与 <c>mods\</c> / <c>saves\</c> 等游戏数据都在这个目录里。
/// <para>
/// 几个刻意的行为：
/// </para>
/// <list type="bullet">
/// <item><b>目录才是真相</b>：列表来自 <c>versions\</c> 的目录扫描（只认含有 <c>instance.json</c>
/// 的目录，因此单纯装了一个版本、没有建实例的目录不会被当成实例），不是某个索引文件。</item>
/// <item><b>缺 instance.json 不算坏实例</b>：按默认值显示（版本「未指定」），
/// 这是"把别人的实例目录拷进来"的正常路径，不该被当成错误。
/// 但扫描列表时它也不会被列出来 —— 与 Neo 一样，<c>instance.json</c> 是"这是实例"的标记。</item>
/// <item><b>删除 = 移动，不是递归删除</b>：整个实例目录一次改名进回收目录，
/// 再在里面拆分「保留」与「其余」。任何一步失败都还在回收目录里，不会真的丢数据。</item>
/// <item><b>旧布局只读迁移</b>：启动器目录下的 <c>instances\&lt;实例&gt;</c> 里的元数据会被**复制**
/// 到新目录（旧文件保留），之后一切读写只看新目录。</item>
/// <item><b>目录大小带缓存</b>：实例目录里可能有上万个文件，
/// 每次进 p02 都重新遍历会让页面卡顿（AGENTS.md §6 明确提到这类刷新开销）。</item>
/// </list>
/// </summary>
public sealed class FileInstanceCatalog : IInstanceCatalog
{
    /// <summary>默认要保留的内容（勾选「保留存档与配置」时）。</summary>
    public static readonly string[] PreservedEntries =
    [
        "saves",
        "config",
        "options.txt",
        "servers.dat",
        "resourcepacks",
        "shaderpacks",
        "screenshots",
    ];

    /// <summary>新建实例时建立的目录骨架。</summary>
    public static readonly string[] SkeletonDirectories =
    [
        "mods",
        "config",
        "resourcepacks",
        "saves",
        "shaderpacks",
    ];

    /// <summary>目录大小的缓存有效期。</summary>
    private static readonly TimeSpan SizeCacheLifetime = TimeSpan.FromSeconds(60);

    private readonly ILauncherPaths _paths;
    private readonly ISettingsCatalog _settings;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly Dictionary<string, (long Bytes, DateTimeOffset MeasuredAt)> _sizeCache =
        new(StringComparer.Ordinal);

    /// <summary>已经就"元数据损坏"报过警的文件（同一版内容只报一次）。</summary>
    private readonly FileStampGate _corruptMetadataWarnings = new();

    private string _activeInstanceName = string.Empty;

    /// <summary>旧布局（启动器目录 <c>instances\</c>）是否已经尝试过复制迁移。每个进程一次。</summary>
    private bool _legacyLayoutMigrated;

    /// <summary>实例目录：<c>&lt;游戏目录&gt;\versions\&lt;实例名&gt;</c>（元数据与游戏数据同一个目录）。</summary>
    private string InstanceDirectory(string instanceName) =>
        InstancePaths.Directory(_settings.GetGlobal(), instanceName);

    /// <summary>实例的游戏数据目录（命令行里的 <c>${game_directory}</c>）。与实例目录是同一个目录。</summary>
    private string ContentDirectory(string instanceName) => InstanceDirectory(instanceName);

    /// <summary>构造实例目录。</summary>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="settings">设置读写（游戏根目录、内存、窗口都要从全局设置继承）。</param>
    /// <param name="clock">时间源（"今天 / 昨天"与缓存有效期要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public FileInstanceCatalog(
        ILauncherPaths paths,
        ISettingsCatalog settings,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _paths = paths;
        _settings = settings;
        _clock = clock;
        _diagnostics = diagnostics;
        _paths.EnsureCreated();
        MigrateLegacyLayout();
    }

    /// <inheritdoc />
    public string ActiveInstanceName
    {
        get => ResolveActiveInstanceName();
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Length == 0)
            {
                _activeInstanceName = string.Empty;
                return;
            }

            if (!LauncherPaths.IsValidInstanceName(value) || !Directory.Exists(InstanceDirectory(value)))
            {
                throw new ArgumentException($"实例不存在或名字不可用：'{value}'。", nameof(value));
            }

            _activeInstanceName = value;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<InstanceOverview> GetOverview() =>
    [
        .. ListInstanceNames().Select(name =>
        {
            var record = BuildRecord(name, activeName: string.Empty);

            // 摘要由 App 层拼（「加载器 · N 个模组 · 上次 &lt;时间&gt;」）：加载器那一段自己就是模板，
            // 内核渲染不了它、也不该把它的**键**当参数（AGENTS.md §5.3 第 ⑤ / ⑨ 条）。
            return new InstanceOverview(
                record.Name,
                record.LoaderDisplay,
                ModCount(name),
                record.LastLaunched,
                record.StatusText,
                record.StatusTone,
                IsActive: false);
        }),
    ];

    /// <inheritdoc />
    public IReadOnlyList<InstanceRecord> GetInstances()
    {
        var active = ActiveInstanceName;
        return [.. ListInstanceNames().Select(name => BuildRecord(name, active))];
    }

    /// <inheritdoc />
    public InstanceDetail GetDetail(string instanceName)
    {
        ArgumentNullException.ThrowIfNull(instanceName);

        var directory = RequireInstanceDirectory(instanceName);
        var contentDirectory = ContentDirectory(instanceName);
        var metadata = ReadMetadata(directory);
        var global = _settings.GetGlobal();
        var overrides = _settings.GetInstanceOverrides(instanceName);
        var now = _clock.Now;

        return new InstanceDetail(
            Name: instanceName,
            // 真实版本号是数据（纯数据键 {0}），"未指定"是文案（自己的键）—— 判据同 §5.3 第 ⑥ 条。
            GameVersion: string.IsNullOrWhiteSpace(metadata.GameVersion)
                ? TextTemplate.Of("未指定")
                : TextTemplate.Of("{0}", metadata.GameVersion),
            LoaderDisplay: InstanceNaming.DisplayWithVersion(metadata.Loader, metadata.LoaderVersion),
            // Java 运行时检测属于 M3；在此之前只如实反映"用户是否手工指定过"。
            JavaRuntime: JavaOverrideText(overrides.JavaOverride),
            JavaRuntimeSummary: JavaOverrideText(overrides.JavaOverride),
            GameDirectory: global.GameDirectory,
            InstancePath: contentDirectory,
            LastLaunched: InstanceText.FormatTimestamp(metadata.LastLaunchedAt, now, TextTemplate.Of("从未启动")),
            // "已完成 + 时间"是两句话：第二句挂在 Note 上（中文时间戳塞进参数会让英文界面露出中文）。
            // 注意：`metadata.ResourceVerifiedAt` 目前**还没有任何写入点**（资源校验落实时才写），
            // 所以这一支现在只有演示数据走得到；null 那一支（从未校验）才是当前的真实状态。
            ResourceVerification: metadata.ResourceVerifiedAt is null
                ? TextTemplate.Of("从未校验")
                : TextTemplate.Of("已完成") with
                {
                    Note = InstanceText.FormatTimestamp(metadata.ResourceVerifiedAt, now, TextTemplate.Of("从未校验")),
                },
            Account: string.IsNullOrWhiteSpace(metadata.Account)
                ? TextTemplate.Of("未绑定")
                : TextTemplate.Of("{0}", metadata.Account),
            MemoryMb: overrides.MaxMemoryMb ?? global.MaxMemoryMb,
            SizeOnDisk: InstanceText.FormatSize(MeasureSize(instanceName, now)),
            ModSummary: ModCountFullText(instanceName),
            PlayTime: InstanceText.FormatPlayTime(metadata.TotalPlaySeconds),
            LaunchCount: metadata.LaunchCount,
            LastExitCode: metadata.LastExitCode,
            WindowMode: InstanceText.FormatWindowMode(overrides.WindowWidth, overrides.WindowHeight, overrides.LaunchMode),
            IsolatedDirectory: metadata.IsolatedDirectory);
    }

    /// <inheritdoc />
    public IReadOnlyList<InstanceAction> GetActions(string instanceName)
    {
        _ = RequireInstanceDirectory(instanceName);
        var contentDirectory = ContentDirectory(instanceName);
        var detail = GetDetail(instanceName);
        var counts = GetCounts(instanceName);

        return
        [
            new(
                TextTemplate.Of("模组管理"),
                counts.Mods == 0
                    ? TextTemplate.Of("该实例没有模组")
                    : TextTemplate.Of(
                        "{0} 个模组",
                        CountEnabledMods(contentDirectory).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                InstanceActionTarget.Mods),
            new(
                TextTemplate.Of("资源包 / 存档"),
                TextTemplate.Of(
                    "{0} 个资源包 · {1} 个存档",
                    counts.ResourcePacks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    counts.Saves.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                InstanceActionTarget.Resources),
            new(
                TextTemplate.Of("实例设置"),
                // 第二句（窗口模式）挂在 Note 上：它自己也是「键 + 参数」，
                // 塞进参数会让英文界面在英文句子里露出中文（AGENTS.md §5.3 第 ⑤/⑦ 条）。
                TextTemplate.Of(
                    "内存 {0} MB",
                    detail.MemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture)) with
                {
                    Note = detail.WindowMode,
                },
                InstanceActionTarget.InstanceSettings),
        ];
    }

    /// <inheritdoc />
    public InstanceCounts GetCounts(string instanceName)
    {
        var directory = ContentDirectory(instanceName);

        return new InstanceCounts(
            Mods: CountFiles(Path.Combine(directory, "mods"), "*.jar")
                  + CountFiles(Path.Combine(directory, "mods"), "*.disabled"),
            ResourcePacks: CountChildren(Path.Combine(directory, "resourcepacks")),
            Saves: CountChildren(Path.Combine(directory, "saves")));
    }

    /// <inheritdoc />
    public InstanceDetail CreateInstance(InstanceCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim() ?? string.Empty;
        if (!LauncherPaths.IsValidInstanceName(name))
        {
            throw new ArgumentException($"实例名不能用作目录名：'{request.Name}'。", nameof(request));
        }

        var directory = InstanceDirectory(name);
        if (Directory.Exists(directory))
        {
            throw new InvalidOperationException($"实例「{name}」已经存在。");
        }

        // 一个实例只有一个目录：元数据、启动参数覆盖、version.json 与 mods/config/saves 全在里面。
        // 这也是 Neo 26.1 的结构 —— 删除时只要搬一个目录，安装完也不需要"记得写另一处"。
        Directory.CreateDirectory(directory);
        foreach (var skeleton in SkeletonDirectories)
        {
            Directory.CreateDirectory(Path.Combine(directory, skeleton));
        }

        var metadata = new InstanceFile
        {
            GameVersion = request.GameVersion,
            Loader = request.Loader,
            LoaderVersion = request.LoaderVersion,
            IsolatedDirectory = request.IsolatedDirectory,
            CreatedAt = _clock.Now,
            InstallState = InstanceInstallState.NotInstalled,
        };

        WriteMetadata(directory, metadata);
        InvalidateSize(name);

        if (_activeInstanceName.Length == 0)
        {
            _activeInstanceName = name;
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.InstanceSettings,
            "已创建实例：{0}（{1}）",
            // 事件参数是**排障数据**：用 DataDisplay（ASCII、与语言无关），不是界面上的显示名。
            [name, InstanceNaming.DataDisplay(request.Loader, request.LoaderVersion)],
            name);

        return GetDetail(name);
    }

    /// <inheritdoc />
    public InstanceLaunchInfo GetLaunchInfo(string instanceName)
    {
        ArgumentNullException.ThrowIfNull(instanceName);

        var directory = RequireInstanceDirectory(instanceName);
        var metadata = ReadMetadata(directory);
        var global = _settings.GetGlobal();

        return new InstanceLaunchInfo(
            Name: instanceName,
            GameVersion: metadata.GameVersion ?? string.Empty,
            Loader: metadata.Loader,
            LoaderVersion: metadata.LoaderVersion,
            IsolatedDirectory: metadata.IsolatedDirectory,
            InstancePath: ContentDirectory(instanceName),
            GameDirectory: global.GameDirectory,
            Account: string.IsNullOrWhiteSpace(metadata.Account) ? null : metadata.Account);
    }

    /// <inheritdoc />
    public bool RecordLaunch(string instanceName, DateTimeOffset launchedAt, int exitCode, long playSeconds)
    {
        ArgumentNullException.ThrowIfNull(instanceName);

        var directory = RequireInstanceDirectory(instanceName);

        // 写路径必须**严格**：读不出来就不写（AGENTS.md §6）。用宽容的 ReadMetadata 会让
        // "元数据坏了"变成"元数据被重置成只有这次启动"—— 用户的实例名/创建时间就此消失。
        if (!TryReadMetadataForWrite(directory, out var metadata))
        {
            return false;
        }

        WriteMetadata(directory, metadata with
        {
            LastLaunchedAt = launchedAt,
            LaunchCount = metadata.LaunchCount + 1,
            LastExitCode = exitCode,
            TotalPlaySeconds = metadata.TotalPlaySeconds + Math.Max(0, playSeconds),
        });

        InvalidateSize(instanceName);
        return true;
    }

    /// <inheritdoc />
    public InstanceDetail RenameInstance(string instanceName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var targetName = newName.Trim();
        if (!LauncherPaths.IsValidInstanceName(targetName))
        {
            throw new ArgumentException($"实例名不能用作目录名：'{newName}'。", nameof(newName));
        }

        var source = RequireInstanceDirectory(instanceName);
        var target = InstanceDirectory(targetName);

        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return GetDetail(instanceName);
        }

        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"同名实例已经存在：'{targetName}'。");
        }

        Directory.Move(source, target);
        InvalidateSize(instanceName);
        InvalidateSize(targetName);

        if (string.Equals(_activeInstanceName, instanceName, StringComparison.OrdinalIgnoreCase))
        {
            _activeInstanceName = targetName;
        }

        return GetDetail(targetName);
    }

    /// <inheritdoc />
    public InstanceDeletionResult DeleteInstance(string instanceName, InstanceDeletionOptions options)
    {
        ArgumentNullException.ThrowIfNull(instanceName);
        ArgumentNullException.ThrowIfNull(options);

        var directory = RequireInstanceDirectory(instanceName);
        var metadata = ReadMetadata(directory);
        var account = string.IsNullOrWhiteSpace(metadata.Account) ? null : metadata.Account;

        Directory.CreateDirectory(_paths.RecycleDirectory);
        var recyclePath = CreateRecycleEntryPath(instanceName);
        var moved = Path.Combine(recyclePath, "instance");

        // 第一步：整目录改名。同卷改名的原子性由文件系统保证 —— 这一步之后
        // 用户的数据已经安全落在回收目录里，后面再拆分就不会丢东西了。
        // （Directory.Move 要求目标父目录已存在，所以先把回收条目目录建出来。）
        Directory.CreateDirectory(recyclePath);
        try
        {
            Directory.Move(directory, moved);

            // 旧布局的元数据目录（启动器目录 instances\）如果还在，一并搬进回收条目：
            // 新目录已经包含它的副本，但旧文件不属于我们，不能留在原处变成"孤儿"。
            var legacy = _paths.InstanceDirectory(instanceName);
            if (Directory.Exists(legacy))
            {
                MoveIfExists(legacy, Path.Combine(recyclePath, "legacy-instance-metadata"));
            }
        }
        catch
        {
            // 改名失败（例如有文件被占用）：把刚建出来的空回收条目清掉，别留垃圾。
            DeleteDirectoryQuietly(recyclePath);
            throw;
        }

        string? preserved = null;
        if (options.KeepSavesAndConfig)
        {
            preserved = Path.Combine(recyclePath, "kept");
            Directory.CreateDirectory(preserved);
            foreach (var entry in PreservedEntries)
            {
                MoveIfExists(Path.Combine(moved, entry), Path.Combine(preserved, entry));
            }
        }

        var marker = new RecycleMarker
        {
            InstanceName = instanceName,
            DeletedAt = _clock.Now,
            KeptSavesAndConfig = options.KeepSavesAndConfig,
            DeleteAccountBinding = options.DeleteAccountBinding,
            Account = options.DeleteAccountBinding ? account : null,
        };
        AtomicFile.WriteAllText(
            Path.Combine(recyclePath, "recycle.json"),
            LauncherJson.Serialize(marker, LauncherJson.Indented));

        InvalidateSize(instanceName);

        // 删掉的正好是当前实例：把当前实例挪到剩下的第一个，避免后续页面拿着不存在的实例名。
        if (string.Equals(_activeInstanceName, instanceName, StringComparison.Ordinal))
        {
            _activeInstanceName = string.Empty;
        }

        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.InstanceSettings,
            options.KeepSavesAndConfig ? "已删除实例 {0}，存档与配置已保留（回收目录：{1}）" : "已删除实例 {0}，含存档与配置（回收目录：{1}）",
            [instanceName, recyclePath],
            instanceName);

        return new InstanceDeletionResult(
            instanceName,
            recyclePath,
            preserved,
            options.DeleteAccountBinding ? account : null);
    }

    // ------------------------------------------------------------ 扫描 ---

    /// <summary>
    /// 列出实例名，按「上次启动时间倒序、从未启动的排后面、再按名字」排序。
    /// <para>
    /// 这个排序与 p02 筛选行上写的「按上次启动排序」一致 —— 界面上写着按什么排，
    /// 数据就得按什么排，否则那行字就是假的。
    /// </para>
    /// </summary>
    private List<string> ListInstanceNames()
    {
        var versionsDirectory = InstancePaths.VersionsDirectory(_settings.GetGlobal());

        if (!Directory.Exists(versionsDirectory))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            var name = Path.GetFileName(directory);

            // 名字不合法的目录（手工造的、或被别的程序占用的）直接跳过：
            // 界面上不能出现一个点进去就报错的实例。
            if (!LauncherPaths.IsValidInstanceName(name))
            {
                continue;
            }

            // `instance.json` 是"这是实例"的标记（Neo 26.1 同一条判据）：
            // 只装了一个版本、还没建实例的 `versions\<版本 id>` 不能出现在实例列表里。
            if (!File.Exists(Path.Combine(directory, InstancePaths.MetadataFileName)))
            {
                continue;
            }

            names.Add(name);
        }

        return
        [
            .. names
                .OrderByDescending(name => ReadMetadata(InstanceDirectory(name)).LastLaunchedAt ?? DateTimeOffset.MinValue)
                .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private InstanceRecord BuildRecord(string name, string activeName)
    {
        var directory = InstanceDirectory(name);
        var metadata = ReadMetadata(directory);

        // 状态按**磁盘现算**（metadata 里的 InstallState 从来没有人写，只看它等于永远显示「未安装」）：
        // 实例目录里有 `version.json`（或兼容的 `<实例名>.json`）才算装好。
        var (statusText, tone) = EffectiveInstallState(name, metadata) switch
        {
            InstanceInstallState.Installed => (TextTemplate.Of("就绪"), Tone.Ok),
            InstanceInstallState.Partial => (TextTemplate.Of("需要修复"), Tone.Warn),
            _ => (TextTemplate.Of("未安装"), Tone.Warn),
        };

        return new InstanceRecord(
            Name: name,
            GameVersion: string.IsNullOrWhiteSpace(metadata.GameVersion)
                ? TextTemplate.Of("未指定")
                : TextTemplate.Of("{0}", metadata.GameVersion),
            LoaderDisplay: InstanceNaming.DisplayWithVersion(metadata.Loader, metadata.LoaderVersion),
            Loader: metadata.Loader,
            SizeOnDisk: InstanceText.FormatSize(MeasureSize(name, _clock.Now)),
            LastLaunched: InstanceText.FormatTimestamp(metadata.LastLaunchedAt, _clock.Now, TextTemplate.Of("从未启动")),
            StatusText: statusText,
            StatusTone: tone,
            IsActive: string.Equals(name, activeName, StringComparison.Ordinal));
    }

    /// <summary>
    /// 这个实例现在算不算「装好了」：按**实例目录里的版本 JSON** 现算。
    /// </summary>
    /// <param name="instanceName">实例名（同时是目录名）。</param>
    /// <param name="metadata">实例元数据。</param>
    /// <returns>安装状态。</returns>
    /// <remarks>
    /// **不读也不写 <c>metadata.InstallState</c>**：它没有写入点，读它只会永远显示「未安装」。
    /// 新布局装完一定会在实例目录里留下 <c>version.json</c>（安装器镜像的产物）；
    /// 旧布局（版本装在共享的 <c>versions\&lt;版本 id&gt;</c>）继续按加载器命名规则回退判断，
    /// 否则升级之后老实例会突然全变成「未安装」。
    /// </remarks>
    private InstanceInstallState EffectiveInstallState(string instanceName, InstanceFile metadata)
    {
        var directory = InstanceDirectory(instanceName);

        if (File.Exists(Path.Combine(directory, InstancePaths.VersionJsonFileName))
            || File.Exists(Path.Combine(directory, instanceName + ".json")))
        {
            return InstanceInstallState.Installed;
        }

        var versionId = LegacyVersionId(metadata);

        if (versionId is null)
        {
            return InstanceInstallState.NotInstalled;
        }

        var versionDirectory = Path.Combine(_settings.GetGlobal().GameDirectory, "versions", versionId);
        var json = Path.Combine(versionDirectory, versionId + ".json");

        if (File.Exists(json))
        {
            return InstanceInstallState.Installed;
        }

        // 目录在、JSON 不在 = 上一次装到一半（安装器的最后一步才写 JSON）→「需要修复」；
        // 目录都不在 = 还没开始装 →「未安装」。
        return Directory.Exists(versionDirectory)
            ? InstanceInstallState.Partial
            : InstanceInstallState.NotInstalled;
    }

    /// <summary>
    /// 旧布局下这个实例的版本目录名（原版 = 游戏版本；加载器 = <see cref="LoaderNaming.VersionId"/>）。
    /// </summary>
    /// <param name="metadata">实例元数据。</param>
    /// <returns>版本 id；元数据不够算出它时为 <see langword="null"/>。</returns>
    private static string? LegacyVersionId(InstanceFile metadata)
    {
        var gameVersion = metadata.GameVersion?.Trim();
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return null;
        }

        if (metadata.Loader == LoaderKind.Vanilla)
        {
            return gameVersion;
        }

        if (string.IsNullOrWhiteSpace(metadata.LoaderVersion))
        {
            return null;
        }

        try
        {
            return LoaderNaming.VersionId(metadata.Loader, metadata.LoaderVersion, gameVersion);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
    private string ResolveActiveInstanceName()
    {
        if (_activeInstanceName.Length > 0 && Directory.Exists(InstanceDirectory(_activeInstanceName)))
        {
            return _activeInstanceName;
        }

        // 当前实例被删掉、或首次启动还没有任何实例时，落到第一个（没有则为空串）。
        _activeInstanceName = ListInstanceNames().FirstOrDefault() ?? string.Empty;
        return _activeInstanceName;
    }

    private string RequireInstanceDirectory(string instanceName)
    {
        if (!LauncherPaths.IsValidInstanceName(instanceName))
        {
            throw new KeyNotFoundException($"实例名不可用：'{instanceName}'。");
        }

        var directory = InstanceDirectory(instanceName);
        if (!Directory.Exists(directory))
        {
            throw new KeyNotFoundException($"实例不存在：'{instanceName}'。");
        }

        return directory;
    }

    // -------------------------------------------------------- 元数据 IO ---

    private InstanceFile ReadMetadata(string directory)
    {
        var path = Path.Combine(directory, "instance.json");
        var json = AtomicFile.ReadAllTextOrNull(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new InstanceFile();
        }

        try
        {
            return LauncherJson.Deserialize<InstanceFile>(json, LauncherJson.Indented) ?? new InstanceFile();
        }
        catch (JsonException ex)
        {
            // 元数据坏了但目录还在：按默认值显示（版本「未指定」），并把原文件留着。
            if (_corruptMetadataWarnings.ShouldReport(path))
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Launcher,
                    "实例元数据不是合法 JSON（{0}，{1}），已按默认值显示（原文件未改动）",
                    [Path.GetFileName(directory), ex.GetType().Name],
                    Path.GetFileName(directory));
            }

            return new InstanceFile();
        }
    }

    private static void WriteMetadata(string directory, InstanceFile metadata) =>
        AtomicFile.WriteAllText(
            Path.Combine(directory, "instance.json"),
            LauncherJson.Serialize(metadata, LauncherJson.Indented));

    /// <summary>
    /// 写路径专用的严格读取：文件**存在但读不出来**时返回 <see langword="false"/>。
    /// <para>
    /// 与 <see cref="ReadMetadata"/> 是刻意分开的：显示可以宽容（按默认值显示 + 报一次警告），
    /// 但写入绝不能宽容 —— "读不出来当没有"然后写回默认值，等于把用户那份可能还有救的文件清空
    /// （<c>FileAccountCatalog.LoadForWrite</c> 是同一条规则的另一个落点）。
    /// 文件**不存在**不算失败：那是"还没写过"，按默认值写第一份即可。
    /// </para>
    /// </summary>
    private static bool TryReadMetadataForWrite(string directory, out InstanceFile metadata)
    {
        var path = Path.Combine(directory, "instance.json");
        var json = AtomicFile.ReadAllTextOrNull(path);

        if (string.IsNullOrWhiteSpace(json))
        {
            metadata = new InstanceFile();
            return true;
        }

        try
        {
            metadata = LauncherJson.Deserialize<InstanceFile>(json, LauncherJson.Indented) ?? new InstanceFile();
            return true;
        }
        catch (JsonException)
        {
            metadata = new InstanceFile();
            return false;
        }
    }

    // ------------------------------------------------------ 旧布局迁移 ---

    /// <summary>
    /// 把旧布局（启动器目录 <c>instances\&lt;实例&gt;</c>）里的实例元数据**复制**到
    /// <c>&lt;游戏目录&gt;\versions\&lt;实例&gt;</c>。旧文件原样保留，不做删除或改名。
    /// </summary>
    /// <remarks>
    /// 为什么复制而不是移动：这是用户数据（账户绑定、游玩时长、窗口设置都在里面），
    /// 迁移失败或用户回退版本时，旧文件还在原处就能拿回来。复制成功后新目录才是唯一读取位置，
    /// 旧目录从此只占空间、不再影响任何行为（删除实例时会连同旧目录一起移进回收站）。
    /// </remarks>
    private void MigrateLegacyLayout()
    {
        if (_legacyLayoutMigrated)
        {
            return;
        }

        _legacyLayoutMigrated = true;

        var legacyRoot = _paths.InstancesDirectory;
        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        var migrated = 0;
        var failed = 0;

        foreach (var legacyDirectory in Directory.EnumerateDirectories(legacyRoot))
        {
            var name = Path.GetFileName(legacyDirectory);
            var legacyMetadata = Path.Combine(legacyDirectory, InstancePaths.MetadataFileName);

            if (!LauncherPaths.IsValidInstanceName(name) || !File.Exists(legacyMetadata))
            {
                continue;
            }

            try
            {
                var targetDirectory = InstanceDirectory(name);
                var targetMetadata = Path.Combine(targetDirectory, InstancePaths.MetadataFileName);

                Directory.CreateDirectory(targetDirectory);

                if (!File.Exists(targetMetadata))
                {
                    File.Copy(legacyMetadata, targetMetadata);
                }

                var legacyOverrides = Path.Combine(legacyDirectory, InstancePaths.OverridesFileName);
                var targetOverrides = Path.Combine(targetDirectory, InstancePaths.OverridesFileName);
                if (File.Exists(legacyOverrides) && !File.Exists(targetOverrides))
                {
                    File.Copy(legacyOverrides, targetOverrides);
                }

                migrated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        if (migrated > 0)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.InstanceSettings,
                "已把 {0} 个旧布局实例的元数据复制到游戏目录的 versions 目录（旧文件保留）",
                [migrated.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (failed > 0)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.InstanceSettings,
                "旧布局实例元数据迁移有 {0} 个失败（旧文件未改动，新目录可能不完整）",
                [failed.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
    }

    // ---------------------------------------------------------- 目录大小 ---

    private long MeasureSize(string instanceName, DateTimeOffset now)
    {
        if (_sizeCache.TryGetValue(instanceName, out var cached)
            && now - cached.MeasuredAt < SizeCacheLifetime
            && now >= cached.MeasuredAt)
        {
            return cached.Bytes;
        }

        var contentDirectory = InstanceDirectory(instanceName);
        var bytes = Directory.Exists(contentDirectory) ? MeasureDirectory(contentDirectory) : 0;

        _sizeCache[instanceName] = (bytes, now);
        return bytes;
    }

    private void InvalidateSize(string instanceName) => _sizeCache.Remove(instanceName);

    /// <summary>
    /// 递归统计目录字节数。
    /// <para>
    /// 遇到重解析点**不跟进去**（<see cref="PathSafety"/> 同源的判断）：
    /// 否则一个指向自己上层的 Junction 就能让这里转不出来，而这发生在 UI 线程上。
    /// </para>
    /// </summary>
    private static long MeasureDirectory(string directory)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件读不到就跳过：占用空间只是个估算，不该让整页失败。
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static int CountChildren(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private int ModCount(string instanceName) =>
        CountFiles(Path.Combine(ContentDirectory(instanceName), "mods"), "*.jar");

    /// <summary>已启用的模组数（动作条上的"87 个模组"指的是能生效的那些）。</summary>
    private static int CountEnabledMods(string directory) =>
        CountFiles(Path.Combine(directory, "mods"), "*.jar");

    private TextTemplate ModCountFullText(string instanceName)
    {
        var directory = ContentDirectory(instanceName);
        var enabled = CountFiles(Path.Combine(directory, "mods"), "*.jar");
        var disabled = CountFiles(Path.Combine(directory, "mods"), "*.disabled");

        return enabled + disabled == 0
            ? TextTemplate.Of("{0} 个", "0")
            : TextTemplate.Of(
                "{0} 个 (已启用 {1})",
                (enabled + disabled).ToString(System.Globalization.CultureInfo.InvariantCulture),
                enabled.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Java 运行时的显示文本：用户手工指定过就是那个路径（纯数据键），否则是「自动匹配」。</summary>
    /// <param name="javaOverride">实例的 Java 覆盖（根目录或可执行文件路径）；空表示自动匹配。</param>
    /// <returns>键 + 参数。</returns>
    private static TextTemplate JavaOverrideText(string? javaOverride) =>
        string.IsNullOrWhiteSpace(javaOverride)
            ? TextTemplate.Of("自动匹配")
            : TextTemplate.Of("{0}", javaOverride);

    // -------------------------------------------------------------- 回收 ---

    private string CreateRecycleEntryPath(string instanceName)
    {
        var stamp = _clock.Now.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var stem = $"{stamp}-{PathSafety.SanitizeNameComponent(instanceName)}";

        var candidate = Path.Combine(_paths.RecycleDirectory, stem);
        for (var index = 2; Directory.Exists(candidate); index++)
        {
            candidate = Path.Combine(
                _paths.RecycleDirectory,
                $"{stem}-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return candidate;
    }

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
    }

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
