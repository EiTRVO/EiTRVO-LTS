using System.Globalization;
using System.IO.Compression;
using System.Text;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content;

/// <summary>
/// <see cref="IContentCatalog"/> 的真实实现（M7-a）：模组、资源包、存档都来自**实例的游戏目录**。
/// </summary>
/// <remarks>
/// <para>
/// 游戏目录的解析**只有一处**：<see cref="IInstanceCatalog.GetLaunchInfo"/> 的 <c>InstancePath</c>
/// （就是命令行里的 <c>${game_directory}</c>）。自己再拼一次 <c>instances/&lt;名&gt;</c> 是最容易犯的错 ——
/// 非隔离实例的游戏目录在 <c>versions\&lt;版本&gt;</c> 下，两个算法迟早给出不同答案。
/// </para>
/// <para>
/// <b>目录才是真相</b>：没有索引文件。用户在资源管理器里往 <c>mods\</c> 拖一个 jar，
/// 回到这一页就该看见它 —— 索引文件与目录不同步时，界面会显示不存在的东西。
/// </para>
/// <para>
/// <b>读不出来与"没有内容"必须分开</b>（AGENTS.md §6）：目录枚举失败会记一条事件，
/// 而不是安静地显示成 0 个模组。单行读不出来（损坏的 jar、读不动的 level.dat）则退回按文件名/未知显示 ——
/// 那一种不该让整页失败。
/// </para>
/// </remarks>
public sealed class FileContentCatalog : IContentCatalog
{
    private const string ModsDirectoryName = "mods";
    private const string ResourcePacksDirectoryName = "resourcepacks";
    private const string SavesDirectoryName = "saves";
    private const string BackupDirectoryName = "backups";
    private const string JarSuffix = ".jar";
    private const string DisabledSuffix = ".disabled";

    /// <summary>
    /// 模组文件名的长度上限（与 <c>ContentInstaller</c> 落盘时同一条规则：128）。
    /// </summary>
    /// <remarks>
    /// <see cref="PathSafety.IsSafePathSegment"/> 的默认上限是 64，那是给版本 id / 实例名用的；
    /// 模组文件名普遍带版本号与加载器后缀（<c>sodium-fabric-0.5.8+mc1.20.4</c>），64 会误伤真实文件。
    /// </remarks>
    private const int MaxFileNameLength = 128;

    /// <summary>纯数据键（"这一格没有值"）：中英逐字相同，登记在 <c>UntranslatedByDesign</c>。</summary>
    private const string Dash = "—";

    private readonly IInstanceCatalog _instances;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly ModrinthCatalog _modrinth;
    private readonly ModHashIndex _hashes;
    private readonly ModrinthModIndex _modMatches;

    private readonly ScanCache<ModRow> _mods = new();
    private readonly ScanCache<ResourcePackEntry> _resourcePacks = new();
    private readonly ScanCache<WorldSaveEntry> _saves = new();

    /// <summary>
    /// 模组扫描的世代号：对账（异步）改了行的来源 / 状态之后 +1，让 <see cref="ScanCache{T}"/> 立刻失效。
    /// </summary>
    /// <remarks>
    /// 不加这个的话"扫描按目录内容戳缓存"会把对账结果挡在门外：jar 一个字节都没变，
    /// 但那一行的「来源」已经从"本地文件"变成"Modrinth"了 —— 缓存键必须能表达这件事。
    /// </remarks>
    private int _modEpoch;

    /// <summary>已经就"内容目录读不动"报过警的目录（同一状态只报一次，否则日志会被刷满）。</summary>
    private readonly FileStampGate _readFailureWarnings = new();

    /// <summary>构造内容目录。</summary>
    /// <param name="instances">实例目录（游戏目录的唯一来源）。</param>
    /// <param name="clock">时间源（"今天 / 昨天"要可测）。</param>
    /// <param name="diagnostics">诊断事件（只用于"目录读不动"这一类真故障）。</param>
    /// <param name="modrinth">Modrinth 浏览列表（P1-4：同步读缓存、异步刷新）。</param>
    /// <param name="hashes">本地 jar 的 SHA-1 索引（对账用；同步读、异步算）。</param>
    /// <param name="modMatches">模组与 Modrinth 的对账结果。</param>
    public FileContentCatalog(
        IInstanceCatalog instances,
        IClock clock,
        IDiagnosticsLog diagnostics,
        ModrinthCatalog modrinth,
        ModHashIndex hashes,
        ModrinthModIndex modMatches)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(modrinth);
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(modMatches);

        _instances = instances;
        _clock = clock;
        _diagnostics = diagnostics;
        _modrinth = modrinth;
        _hashes = hashes;
        _modMatches = modMatches;
    }

    /// <inheritdoc />
    public IReadOnlyList<ModEntry> GetMods(string instanceName)
    {
        var launch = TryResolve(instanceName);
        return launch is null ? [] : [.. ScanMods(launch).Select(static row => row.Entry)];
    }

    /// <inheritdoc />
    public ModSummary GetModSummary(string instanceName)
    {
        var launch = TryResolve(instanceName);
        var mods = launch is null ? [] : ScanMods(launch);

        var enabled = mods.Count(static row => row.Entry.Enablement != ModEnablement.Disabled);
        var disabled = mods.Count(static row => row.Entry.Enablement == ModEnablement.Disabled);
        var broken = mods.Where(static row => row.Entry.State == ModState.MissingDependency).ToList();

        return new ModSummary(
            // 「已启用」= 能生效的那些：**包含「未验证来源」**（那一条也是启用的，只是来源查不到）。
            Enabled: enabled,
            Disabled: disabled,
            MissingDependency: broken.Count,
            // M7-c 起这是真数字：对账（`ModrinthModIndex`）之后才知道哪些有更新版本。
            Updatable: mods.Count(static row => row.Entry.State == ModState.Updatable),
            LoaderMatch: InstanceNaming.DisplayWithVersion(
                launch?.Loader ?? LoaderKind.Vanilla,
                launch?.LoaderVersion),
            GameVersionMatch: string.IsNullOrWhiteSpace(launch?.GameVersion)
                ? TextTemplate.Of(Dash)
                : TextTemplate.Of("{0}", launch!.GameVersion),
            MissingDependencyDetail: broken.Count == 0
                ? null
                : TextTemplate.Of("{0} 需要 {1}，当前未安装", broken[0].FileName, broken[0].MissingId!));
    }

    /// <inheritdoc />
    public IReadOnlyList<ResourcePackEntry> GetResourcePacks(string instanceName)
    {
        var launch = TryResolve(instanceName);
        if (launch is null)
        {
            return [];
        }

        var directory = Path.Combine(launch.InstancePath, ResourcePacksDirectoryName);
        var optionsFile = Path.Combine(launch.InstancePath, "options.txt");

        return _resourcePacks.Get(
            launch.Name,
            () => Stamp(directory) + "|" + FileStamp(optionsFile),
            () => BuildResourcePacks(launch.InstancePath));
    }

    /// <inheritdoc />
    public IReadOnlyList<WorldSaveEntry> GetWorldSaves(string instanceName)
    {
        var launch = TryResolve(instanceName);
        if (launch is null)
        {
            return [];
        }

        var directory = Path.Combine(launch.InstancePath, SavesDirectoryName);

        // 存档的"内容变了"体现在每个存档里的 level.dat 上，所以戳要探到那一层。
        return _saves.Get(launch.Name, () => Stamp(directory, "level.dat"), () => BuildSaves(launch.InstancePath));
    }

    /// <inheritdoc />
    public ContentActionResult DeleteWorldSave(string instanceName, string directoryName)
    {
        var launch = TryResolve(instanceName);
        if (launch is null)
        {
            return ContentActionResult.Failure(TextTemplate.Of("实例不存在（{0}）。", instanceName));
        }

        var trimmed = (directoryName ?? string.Empty).Trim();

        // 目录名是跨层传回来的不可信输入（虽然它来自我们自己的扫描结果）：
        // 上限用 128 —— 存档目录名可能带版本号与日期，64 会误伤真实名字。
        if (!PathSafety.IsSafePathSegment(trimmed, 128))
        {
            return ContentActionResult.Failure(TextTemplate.Of("存档目录名不合法（{0}）。", trimmed));
        }

        var savesDirectory = Path.Combine(launch.InstancePath, SavesDirectoryName);
        var source = Path.Combine(savesDirectory, trimmed);

        if (!PathSafety.IsContained(savesDirectory, source))
        {
            return ContentActionResult.Failure(TextTemplate.Of("存档目录名不合法（{0}）。", trimmed));
        }

        if (!Directory.Exists(source))
        {
            return ContentActionResult.Failure(TextTemplate.Of(
                "找不到这个存档（{0}）——它可能已经被删掉或改名了。",
                trimmed));
        }

        string directory;

        try
        {
            // 不是真删：整目录移进 backups\saves\<时间戳>\（与 M7-f 恢复时换下的旧存档同一个回收目录）。
            directory = BackupDirectory(launch.InstancePath, SavesDirectoryName);
            Directory.Move(source, Path.Combine(directory, trimmed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "删除存档失败（{0}）：{1}",
                [trimmed, ex.GetType().Name]);

            return ContentActionResult.Failure(TextTemplate.Of(
                "删除失败（{0}）：存档可能被游戏占用，请先退出游戏。",
                trimmed));
        }

        var relative = Path.GetRelativePath(launch.InstancePath, directory);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Launcher,
            "已删除存档 {0}（移进 {1}）",
            [trimmed, relative]);

        return ContentActionResult.Done(TextTemplate.Of("已把它移进 {0}，可以从那里拿回来。", relative));
    }

    /// <inheritdoc />
    /// <remarks>
    /// 已安装的整合包来自每个实例里的 <c>modpack.json</c>（mrpack 导入时写，P1-6）。
    /// 导入还没实现，所以现在这台机器上**如实为空**，而不是给两行演示数据。
    /// </remarks>
    public IReadOnlyList<ModpackEntry> GetModpacks() => [];

    /// <inheritdoc />
    /// <remarks>
    /// Modrinth 浏览结果来自磁盘快照（<c>ModrinthCatalog</c>，M7-b / P1-4）：
    /// 读取永远同步，联网发生在 <see cref="RefreshModrinthAsync"/> 里。
    /// </remarks>
    public IReadOnlyList<ModrinthEntry> GetModrinthCatalog() => _modrinth.GetBrowse();

    /// <inheritdoc />
    public bool HasModrinthCatalog => _modrinth.HasFetched;

    /// <inheritdoc />
    public Task RefreshModrinthAsync(CancellationToken cancellationToken = default) =>
        _modrinth.RefreshAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ModrinthEntry>?> SearchModrinthAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default) =>
        _modrinth.SearchAsync(query, cancellationToken);

    /// <inheritdoc />
    public Task<ModrinthModpackResult> PrepareModrinthModpackAsync(
        ModrinthModpackRequest request,
        CancellationToken cancellationToken = default) =>
        _modrinth.PrepareModpackAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModrinthEntry>?> SearchModrinthContentAsync(
        string instanceName,
        ModrinthContentKind kind,
        string? query,
        CancellationToken cancellationToken = default)
    {
        if (TryResolve(instanceName) is not { } launch)
        {
            return null;
        }

        // 原版没有加载器，模组不可用；资源包不受影响。
        if (kind == ModrinthContentKind.Mod && launch.Loader == LoaderKind.Vanilla)
        {
            return [];
        }

        var loader = kind == ModrinthContentKind.Mod ? ModrinthLoaderName(launch.Loader) : null;
        var search = new ModrinthSearchQuery(query, launch.GameVersion, loader, ModrinthSort.Downloads);

        return await _modrinth.SearchContentAsync(kind, search, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ModrinthContentResult> PrepareModrinthContentAsync(
        string instanceName,
        ModrinthContentKind kind,
        string projectId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (TryResolve(instanceName) is not { } launch)
        {
            return new ModrinthContentResult(
                false,
                null,
                TextTemplate.Of("实例 {0} 已经不在了，这次没有下载任何内容。", instanceName));
        }

        // 原版实例没有模组加载器，即使界面绕过限制也不能下载模组。
        if (kind == ModrinthContentKind.Mod && launch.Loader == LoaderKind.Vanilla)
        {
            return new ModrinthContentResult(
                false,
                null,
                TextTemplate.Of("原版实例不能安装模组；请先创建一个带加载器的实例。"));
        }

        var loader = kind == ModrinthContentKind.Mod ? ModrinthLoaderName(launch.Loader) : null;

        return await _modrinth
            .PrepareContentAsync(
                kind,
                projectId,
                displayName,
                launch.GameVersion,
                loader,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ModDependencyFixResult> PrepareDependencyFixAsync(
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        if (TryResolve(instanceName) is not { } launch)
        {
            return new ModDependencyFixResult(
                false,
                null,
                TextTemplate.Of("实例 {0} 已经不在了，这次没有下载任何内容。", instanceName));
        }

        var missing = ScanMods(launch)
            .Where(static row => row.Entry.State == ModState.MissingDependency && !string.IsNullOrWhiteSpace(row.MissingId))
            .Select(static row => row.MissingId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            return new ModDependencyFixResult(false, null, TextTemplate.Of("当前实例没有缺失依赖。"));
        }

        var loader = ModrinthLoaderName(launch.Loader);
        List<ContentDownloadTarget> targets = [];

        foreach (var modId in missing)
        {
            var entries = await _modrinth
                .SearchContentAsync(
                    ModrinthContentKind.Mod,
                    new ModrinthSearchQuery(modId, launch.GameVersion, loader, ModrinthSort.Downloads),
                    cancellationToken)
                .ConfigureAwait(false);

            ModrinthEntry? project = null;
            foreach (var entry in entries ?? [])
            {
                if (entry.CanInstall)
                {
                    project = entry;
                    break;
                }
            }

            if (project is null || string.IsNullOrWhiteSpace(project.ProjectId))
            {
                return new ModDependencyFixResult(
                    false,
                    null,
                    TextTemplate.Of("Modrinth 上找不到依赖 {0}，这次没有下载任何文件。", modId));
            }

            var prepared = await _modrinth
                .PrepareContentAsync(
                    ModrinthContentKind.Mod,
                    project.ProjectId,
                    project.Name,
                    launch.GameVersion,
                    loader,
                    requireBothHashes: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!prepared.Succeeded || prepared.Target is null)
            {
                return new ModDependencyFixResult(
                    false,
                    null,
                    prepared.Failure ?? TextTemplate.Of("没有找到依赖 {0} 的兼容版本。", modId));
            }

            targets.Add(prepared.Target);
        }

        return new ModDependencyFixResult(
            true,
            new ContentDownloadRequest(launch.Name, targets, InstalledProjectIds: [])
            {
                Kind = ModrinthContentKind.Mod,
            },
            null);
    }

    /// <inheritdoc />
    public ContentActionResult ExportInstance(string instanceName, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return ContentActionResult.Failure(TextTemplate.Of("导出路径为空。"));
        }

        InstanceLaunchInfo launch;

        try
        {
            launch = _instances.GetLaunchInfo(instanceName);
        }
        catch (KeyNotFoundException)
        {
            return ContentActionResult.Failure(TextTemplate.Of("找不到这个实例（{0}）。", instanceName));
        }

        if (string.IsNullOrWhiteSpace(launch.GameVersion))
        {
            return ContentActionResult.Failure(TextTemplate.Of("这个实例还没有指定游戏版本，无法导出成整合包。"));
        }

        var source = launch.InstancePath;
        if (!Directory.Exists(source))
        {
            return ContentActionResult.Failure(TextTemplate.Of("实例目录不存在（{0}）。", source));
        }

        var destination = Path.GetFullPath(destinationPath);

        if (!string.Equals(Path.GetExtension(destination), ".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            return ContentActionResult.Failure(TextTemplate.Of("导出文件必须是 .mrpack。"));
        }

        if (PathSafety.IsContained(source, destination))
        {
            return ContentActionResult.Failure(TextTemplate.Of("导出文件不能放在实例目录里。"));
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = destination + ".part";

        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var index = archive.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(index.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(LauncherJson.Serialize(BuildModpackIndex(instanceName, launch), LauncherJson.Indented));
                }

                foreach (var (path, relative) in EnumerateExportFiles(source))
                {
                    archive.CreateEntryFromFile(path, "overrides/" + relative, CompressionLevel.Optimal);
                }
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDeleteFile(temp);

            return ContentActionResult.Failure(TextTemplate.Of(
                "导出失败（{0}）：请确认目标文件没有被占用。",
                Path.GetFileName(destination)));
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Launcher,
            "已导出实例 {0} 到 {1}",
            [instanceName, destination]);

        return ContentActionResult.Done(TextTemplate.Of("已导出到 {0}。", destination));
    }

    /// <summary>生成 <c>modrinth.index.json</c>（内容全部在 overrides，files 为空）。</summary>
    private static Dictionary<string, object?> BuildModpackIndex(string instanceName, InstanceLaunchInfo launch)
    {
        var dependencies = new Dictionary<string, string>
        {
            ["minecraft"] = launch.GameVersion.Trim(),
        };

        var loaderKey = launch.Loader switch
        {
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            LoaderKind.Fabric => "fabric-loader",
            LoaderKind.Quilt => "quilt-loader",
            LoaderKind.OptiFine => "optifine",
            _ => null,
        };

        if (loaderKey is not null && !string.IsNullOrWhiteSpace(launch.LoaderVersion))
        {
            dependencies[loaderKey] = launch.LoaderVersion.Trim();
        }

        return new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = instanceName,
            ["name"] = instanceName,
            ["summary"] = string.Empty,
            ["files"] = Array.Empty<object>(),
            ["dependencies"] = dependencies,
        };
    }

    /// <summary>要写进 overrides 的内容文件（白名单：不导出日志 / 崩溃报告 / 备份）。</summary>
    private static IEnumerable<(string Path, string Relative)> EnumerateExportFiles(string instancePath)
    {
        string[] directories = ["mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "saves", "kubejs", "scripts"];
        string[] files = ["options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat"];

        foreach (var name in directories)
        {
            var root = Path.Combine(instancePath, name);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(instancePath, path).Replace('\\', '/');
                yield return (path, relative);
            }
        }

        foreach (var name in files)
        {
            var path = Path.Combine(instancePath, name);
            if (File.Exists(path))
            {
                yield return (path, name);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉半成品不影响"这次失败"的结论。
        }
    }
    /// <inheritdoc />
    public async Task RefreshModsAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        var launch = TryResolve(instanceName);
        if (launch is null)
        {
            return;
        }

        var directory = Path.Combine(launch.InstancePath, ModsDirectoryName);
        var jars = ListModFiles(directory).Select(static file => file.Path).ToList();

        // 算哈希要把每个 jar 读一遍（一个两百模组的整合包 ≈ 1 GB 的磁盘读）：
        // 必须在**后台线程**做，界面线程只负责最后把属性写回去（AGENTS.md §6）。
        var sha1s = await Task
            .Run(() => HashJars(jars), cancellationToken)
            .ConfigureAwait(false);

        if (sha1s.Count == 0)
        {
            return;
        }

        var loader = ModrinthLoaderName(launch.Loader);

        var changed = await _modMatches
            .RefreshAsync(sha1s, launch.GameVersion, loader, cancellationToken)
            .ConfigureAwait(false);

        // 缺失依赖要先问 Modrinth 是否存在：模组元数据里可能写着加载器内部模块名 / 占位名，
        // 这类 id 在 Modrinth 上根本不存在，不能报成“缺少前置”。
        // 只检查当前确实缺失、且还没查过的那些；网络失败不改变任何结论。
        var missingIds = ScanMods(launch)
            .Where(static row => !string.IsNullOrWhiteSpace(row.MissingId))
            .Select(static row => row.MissingId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingIds.Count > 0
            && await _modMatches
                .RefreshMissingDependenciesAsync(missingIds, cancellationToken)
                .ConfigureAwait(false))
        {
            changed = true;
        }

        if (changed)
        {
            // 行的「来源 / 状态 / 备注」变了，而目录内容一个字都没变 —— 扫描缓存必须让位（见 _modEpoch）。
            _modEpoch++;
        }
    }

    /// <inheritdoc />
    public ContentDownloadRequest? CreateModUpdateRequest(string instanceName)
    {
        var launch = TryResolve(instanceName);
        if (launch is null)
        {
            return null;
        }

        List<ContentDownloadTarget> targets = [];
        List<string> installed = [];

        foreach (var row in ScanMods(launch))
        {
            // 先要"这个哈希查过"：查过才知道它是不是 Modrinth 上的文件（M7-c 的语义）。
            if (!_hashes.TryGetSha1(row.Path, out var sha1) || !_modMatches.WasLookedUp(sha1))
            {
                continue;
            }

            var match = _modMatches.Find(sha1);

            if (match?.ProjectId is { Length: > 0 } project)
            {
                installed.Add(project);
            }

            if (match?.HasUpdate != true || string.IsNullOrWhiteSpace(match.LatestVersionId))
            {
                continue;
            }

            // 禁用的模组**也更新**，但新文件要以 `.disabled` 落盘（见 ContentDownloadTarget.KeepDisabled）：
            // 用户要的是"都升到最新"，不是"顺手把我关掉的东西打开"。
            targets.Add(new ContentDownloadTarget(
                ProjectId: match.ProjectId,
                VersionId: match.LatestVersionId,
                DisplayName: row.Entry.Name,
                ReplacePath: row.Path,
                KeepDisabled: row.Entry.Enablement == ModEnablement.Disabled));
        }

        return targets.Count == 0
            ? null
            : new ContentDownloadRequest(launch.Name, targets, installed);
    }

    /// <inheritdoc />
    public ContentActionResult SetModEnabled(string instanceName, string fileName, bool enabled)
    {
        var failure = ResolveModFile(instanceName, fileName, out var path, out _);
        if (failure is not null)
        {
            return failure;
        }

        var isDisabled = path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

        if (enabled == !isDisabled)
        {
            // 已经是目标状态：幂等成功，一个字节都不碰（按钮本来就该是灰的，这里是第二道保险）。
            return ContentActionResult.Success;
        }

        var target = enabled ? path[..^DisabledSuffix.Length] : path + DisabledSuffix;

        if (File.Exists(target))
        {
            // 两个文件同时在：改名会覆盖其中一个，而那是用户的文件，不能替他决定删哪个。
            return ContentActionResult.Failure(TextTemplate.Of(
                "mods 目录里同时存在 {0} 与 {1}，这次没有改名。",
                Path.GetFileName(path),
                Path.GetFileName(target)));
        }

        try
        {
            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "启用或禁用模组失败（{0}）：{1}",
                [Path.GetFileName(path), ex.GetType().Name]);

            return ContentActionResult.Failure(TextTemplate.Of(
                "改名失败（{0}）：文件可能被游戏占用，请先退出游戏。",
                Path.GetFileName(path)));
        }

        // 改名不改变内容：哈希索引跟着搬，否则「来源：Modrinth」会在禁用 / 启用一轮之后退回"本地文件"
        // （索引按路径查，而下一次异步对账才会重新算一遍哈希）。
        _hashes.Move(path, target);
        _hashes.Save();

        if (enabled)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Launcher,
                "已启用模组 {0}",
                [Path.GetFileName(target)]);
        }
        else
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Launcher,
                "已禁用模组 {0}",
                [Path.GetFileName(target)]);
        }

        return ContentActionResult.Success;
    }

    /// <inheritdoc />
    public ContentActionResult DeleteMod(string instanceName, string fileName)
    {
        var failure = ResolveModFile(instanceName, fileName, out var path, out var instancePath);
        if (failure is not null)
        {
            return failure;
        }

        var name = Path.GetFileName(path);
        string directory;

        try
        {
            // 不是真删：移进 backups\mods\<时间戳>\（与 M7-d 替换旧版本同一个备份目录）。
            directory = BackupDirectory(instancePath, ModsDirectoryName);
            File.Move(path, Path.Combine(directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "删除模组失败（{0}）：{1}",
                [name, ex.GetType().Name]);

            return ContentActionResult.Failure(TextTemplate.Of(
                "删除失败（{0}）：文件可能被游戏占用，请先退出游戏。",
                name));
        }

        // 文件已经不在了：索引里那条记录也一起忘掉（Save 本来会剪掉不存在的路径，
        // 但这是"我们知道它去哪了"，不必等到下一次落盘）。
        _hashes.Forget(path);
        _hashes.Save();

        var relative = Path.GetRelativePath(instancePath, directory);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Launcher,
            "已删除模组 {0}（移进 {1}）",
            [name, relative]);

        // 成功也要交代一句：文件没有消失，只是搬走了 —— 用户该知道去哪里找回来。
        return ContentActionResult.Done(TextTemplate.Of("已把它移进 {0}，可以从那里拿回来。", relative));
    }

    /// <inheritdoc />
    public ContentActionResult SetResourcePackEnabled(string instanceName, string fileName, bool enabled)
    {
        var failure = ResolveResourcePack(instanceName, fileName, out var path, out var instancePath);
        if (failure is not null)
        {
            return failure;
        }

        var id = ResourcePackOptions.IdOf(Path.GetFileName(path));

        if (!ResourcePackOptions.TryRead(instancePath, out var ids, out failure))
        {
            return failure!;
        }

        var index = ids.FindIndex(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));

        if (enabled == (index >= 0))
        {
            // 已经是目标状态：幂等成功，一个字节都不碰（按钮本来就该是灰的，这里是第二道保险）。
            return ContentActionResult.Success;
        }

        if (enabled)
        {
            ids.Insert(LeadingBuiltInCount(ids), id);
        }
        else
        {
            ids.RemoveAt(index);
        }

        var written = ResourcePackOptions.Write(instancePath, ids);
        if (!written.Succeeded)
        {
            LogOptionsWriteFailure(Path.GetFileName(path));
            return written;
        }

        if (enabled)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Launcher,
                "已启用资源包 {0}",
                [Path.GetFileName(path)]);
        }
        else
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Launcher,
                "已禁用资源包 {0}",
                [Path.GetFileName(path)]);
        }

        return ContentActionResult.Success;
    }

    /// <inheritdoc />
    public ContentActionResult MoveResourcePack(string instanceName, string fileName, int direction)
    {
        if (direction is not (-1) and not 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "方向只有 -1（上移 / 更高优先级）与 +1（下移）两种。");
        }

        var failure = ResolveResourcePack(instanceName, fileName, out var path, out var instancePath);
        if (failure is not null)
        {
            return failure;
        }

        var id = ResourcePackOptions.IdOf(Path.GetFileName(path));

        if (!ResourcePackOptions.TryRead(instancePath, out var ids, out failure))
        {
            return failure!;
        }

        var index = ids.FindIndex(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            // 界面那边两颗按钮本来就是灰的；这里是第二道保险 —— 未启用的包没有优先级可言。
            return ContentActionResult.Failure(TextTemplate.Of("这个资源包当前没有启用，不能调整优先级。"));
        }

        // 数组是低 → 高：上移（更高优先级）是往数组**末尾**挪。
        var target = direction < 0 ? index + 1 : index - 1;

        if (target >= ids.Count || target < LeadingBuiltInCount(ids))
        {
            // 已到顶 / 已到用户区底部：幂等成功（界面那边按钮是灰的）。
            return ContentActionResult.Success;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);

        var written = ResourcePackOptions.Write(instancePath, ids);
        if (!written.Succeeded)
        {
            LogOptionsWriteFailure(Path.GetFileName(path));
            return written;
        }

        if (direction < 0)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Launcher,
                "已上移资源包优先级 {0}",
                [Path.GetFileName(path)]);
        }
        else
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Launcher,
                "已下移资源包优先级 {0}",
                [Path.GetFileName(path)]);
        }

        return ContentActionResult.Success;
    }

    /// <inheritdoc />
    public ContentActionResult DeleteResourcePack(string instanceName, string fileName)
    {
        var failure = ResolveResourcePack(instanceName, fileName, out var path, out var instancePath);
        if (failure is not null)
        {
            return failure;
        }

        var name = Path.GetFileName(path);
        var id = ResourcePackOptions.IdOf(name);

        // options.txt 先读出来：读不出来就**什么都别动**（不然文件移走了、启用列表还留着一条死 id）。
        if (!ResourcePackOptions.TryRead(instancePath, out var ids, out failure))
        {
            return failure!;
        }

        string directory;

        try
        {
            // 不是真删：移进 backups\resourcepacks\<时间戳>\（与模组删除同一种回收形状）。
            directory = BackupDirectory(instancePath, ResourcePacksDirectoryName);
            File.Move(path, Path.Combine(directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "删除资源包失败（{0}）：{1}",
                [name, ex.GetType().Name]);

            return ContentActionResult.Failure(TextTemplate.Of(
                "删除失败（{0}）：文件可能被游戏占用，请先退出游戏。",
                name));
        }

        var relative = Path.GetRelativePath(instancePath, directory);
        var index = ids.FindIndex(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Launcher,
            "已删除资源包 {0}（移进 {1}）",
            [name, relative]);

        if (index < 0)
        {
            // 本来就没启用：不用碰 options.txt。
            return ContentActionResult.Done(TextTemplate.Of("已把它移进 {0}，可以从那里拿回来。", relative));
        }

        ids.RemoveAt(index);

        var written = ResourcePackOptions.Write(instancePath, ids);

        if (!written.Succeeded)
        {
            LogOptionsWriteFailure(name);

            // 文件已经移走了（不可逆的一步已经完成且成功），这里只能说清"列表没更新干净"。
            return ContentActionResult.Done(TextTemplate.Of(
                "已把它移进 {0}，但 options.txt 没能更新（启用列表里可能留着一条无效记录）。",
                relative));
        }

        return ContentActionResult.Done(TextTemplate.Of("已把它移进 {0}，可以从那里拿回来。", relative));
    }

    /// <summary>
    /// 解析一个资源包的磁盘路径（M7-g-2b 的启用 / 停用 / 上下移 / 删除都要先过这里）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="fileName">磁盘上的文件名（来自界面，**不可信**）。</param>
    /// <param name="path">解析出的绝对路径（失败时为空串）。</param>
    /// <param name="instancePath">实例的游戏目录（失败时为空串）。</param>
    /// <returns>失败原因；成功时为 <see langword="null"/>。</returns>
    private ContentActionResult? ResolveResourcePack(
        string instanceName,
        string fileName,
        out string path,
        out string instancePath)
    {
        path = string.Empty;
        instancePath = string.Empty;

        var launch = TryResolve(instanceName);
        if (launch is null)
        {
            return ContentActionResult.Failure(TextTemplate.Of("没有当前实例，这次没有改动。"));
        }

        instancePath = launch.InstancePath;

        var trimmed = fileName?.Trim() ?? string.Empty;

        if (!PathSafety.IsSafePathSegment(trimmed, MaxFileNameLength))
        {
            return ContentActionResult.Failure(TextTemplate.Of("文件名不可用（{0}），这次没有改动。", trimmed));
        }

        var directory = Path.Combine(launch.InstancePath, ResourcePacksDirectoryName);
        var candidate = Path.Combine(directory, trimmed);

        if (!PathSafety.IsContained(directory, candidate))
        {
            return ContentActionResult.Failure(TextTemplate.Of("文件名不可用（{0}），这次没有改动。", trimmed));
        }

        if (File.Exists(candidate) && candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            path = candidate;
            return null;
        }

        if (Directory.Exists(candidate))
        {
            path = candidate;
            return null;
        }

        return ContentActionResult.Failure(TextTemplate.Of(
            "找不到这个资源包（{0}）——它可能已经被删掉或改名了。",
            trimmed));
    }

    /// <summary>数组开头那一段内置包（<c>vanilla</c> / <c>programmer_art</c> …）的条数。</summary>
    /// <param name="ids">低 → 高的 id 列表。</param>
    /// <returns>用户资源包的起始下标（也是"最低用户优先级"的边界）。</returns>
    private static int LeadingBuiltInCount(List<string> ids)
    {
        var count = 0;

        while (count < ids.Count && !ResourcePackOptions.IsFileId(ids[count]))
        {
            count++;
        }

        return count;
    }

    private void LogOptionsWriteFailure(string fileName)
    {
        // 只记文件名：失败原因本身是「键 + 参数」的模板，塞进事件参数会在英文界面里露出中文。
        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.Launcher,
            "资源包设置写入失败（{0}）",
            [fileName]);
    }

    /// <summary>
    /// 解析一个模组文件的绝对路径（M7-g-2 的启用 / 禁用 / 删除都要先过这里）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="fileName">磁盘上的文件名（来自界面，**不可信**）。</param>
    /// <param name="path">解析出的绝对路径（失败时为空串）。</param>
    /// <param name="instancePath">实例的游戏目录（失败时为空串）。</param>
    /// <returns>失败原因；成功时为 <see langword="null"/>。</returns>
    private ContentActionResult? ResolveModFile(
        string instanceName,
        string fileName,
        out string path,
        out string instancePath)
    {
        path = string.Empty;
        instancePath = string.Empty;

        var launch = TryResolve(instanceName);
        if (launch is null)
        {
            return ContentActionResult.Failure(TextTemplate.Of("没有当前实例，这次没有改动。"));
        }

        instancePath = launch.InstancePath;

        // 文件名来自表格行，而表格行来自磁盘扫描 —— 但"来自内部"不是不校验的理由：
        // 它是**跨层**传回来的字符串，中间经过界面投影。
        var trimmed = fileName?.Trim() ?? string.Empty;

        if (!PathSafety.IsSafePathSegment(trimmed, MaxFileNameLength))
        {
            return ContentActionResult.Failure(TextTemplate.Of("文件名不可用（{0}），这次没有改动。", trimmed));
        }

        var directory = Path.Combine(launch.InstancePath, ModsDirectoryName);
        var candidate = Path.Combine(directory, trimmed);

        if (!PathSafety.IsContained(directory, candidate))
        {
            return ContentActionResult.Failure(TextTemplate.Of("文件名不可用（{0}），这次没有改动。", trimmed));
        }

        if (!File.Exists(candidate))
        {
            // 用户在资源管理器里删了 / 改名了：如实说，而不是"成功"一个没发生的事。
            return ContentActionResult.Failure(TextTemplate.Of(
                "找不到这个模组文件（{0}）——它可能已经被删掉或改名了。",
                trimmed));
        }

        path = candidate;
        return null;
    }

    /// <summary>
    /// 这次删除要移进的备份目录（<c>&lt;游戏目录&gt;\backups\&lt;族&gt;\&lt;时间戳&gt;\</c>，同一秒内不重名）。
    /// </summary>
    /// <param name="instancePath">实例的游戏目录。</param>
    /// <param name="family">内容族（<c>mods</c> / <c>resourcepacks</c>）。</param>
    /// <returns>已经创建好的目录。</returns>
    /// <remarks>
    /// 与 <c>ContentInstaller.BackupDirectory</c> 同一形状、同一个 <c>backups\</c>：
    /// 用户在一个地方就能找回所有被移动过的内容（旧版本、被删的模组、被删的资源包）。
    /// </remarks>
    private string BackupDirectory(string instancePath, string family)
    {
        var root = Path.Combine(instancePath, BackupDirectoryName, family);
        var stamp = _clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(root, stamp);

        for (var index = 2; Directory.Exists(directory); index++)
        {
            directory = Path.Combine(
                root,
                stamp + "-" + index.ToString(CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>把一批 jar 的哈希算出来（命中索引的直接返回；算完统一落盘一次）。</summary>
    private List<string> HashJars(IReadOnlyList<string> jars)
    {
        List<string> sha1s = [];

        foreach (var jar in jars)
        {
            if (_hashes.Sha1Of(jar) is { Length: > 0 } sha1)
            {
                sha1s.Add(sha1);
            }
        }

        _hashes.Save();
        return sha1s;
    }

    /// <summary>
    /// 加载器种类 → Modrinth 的加载器名（<c>/version_files/update</c> 的 <c>loaders</c> 参数）。
    /// </summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>名字；原版与 OptiFine 返回 <see langword="null"/>（Modrinth 没有这两个"加载器"，
    /// 带了只会一个都匹配不上）。</returns>
    private static string? ModrinthLoaderName(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null,
    };

    // -------------------------------------------------------------- 模组 ---

    private IReadOnlyList<ModRow> ScanMods(InstanceLaunchInfo launch)
    {
        var directory = Path.Combine(launch.InstancePath, ModsDirectoryName);

        // 戳里带上对账世代号：jar 没变但"来源 / 可更新"变了的时候（M7-c），缓存也要失效。
        return _mods.Get(
            launch.Name,
            () => Stamp(directory) + "#" + _modEpoch,
            () => BuildMods(directory, launch));
    }

    private List<ModRow> BuildMods(string directory, InstanceLaunchInfo launch)
    {
        var files = ListModFiles(directory);
        if (files.Count == 0)
        {
            return [];
        }

        var scanned = files
            .Select(file => (File: file, Metadata: JarMetadataReader.Read(file.Path)))
            .ToList();

        // 依赖是否"在"：只看**已启用**的模组 —— 被禁用的依赖不会加载，依赖它的模组照样起不来。
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scanned)
        {
            if (!item.File.Enabled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Metadata.ModId))
            {
                present.Add(item.Metadata.ModId);
            }

            foreach (var provided in item.Metadata.ProvidedIds)
            {
                if (!string.IsNullOrWhiteSpace(provided))
                {
                    present.Add(provided);
                }
            }
        }

        List<ModRow> rows = [];

        foreach (var item in scanned
                     .Select(item => (
                         item.File,
                         item.Metadata,
                         Name: string.IsNullOrWhiteSpace(item.Metadata.DisplayName)
                             ? Stem(item.File.FileName)
                             : item.Metadata.DisplayName))
                     .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.File.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var missing = item.Metadata.Dependencies.FirstOrDefault(dependency =>
                dependency.Mandatory
                && !JarMetadataReader.IsLoaderOrGame(dependency.ModId)
                && !present.Contains(dependency.ModId)
                && !_modMatches.IsDependencyKnownAbsent(dependency.ModId));

            // 对账结果（M7-c）：**"查过但没查到"与"没查过"必须分开** ——
            // 只有前者才敢写「不在 Modrinth 收录」，后者只能说"未知"（沿用 M7-a 的显示）。
            var lookedUp = _hashes.TryGetSha1(item.File.Path, out var sha1)
                           && _modMatches.WasLookedUp(sha1);
            var match = lookedUp ? _modMatches.Find(sha1) : null;

            var state = missing is not null
                ? ModState.MissingDependency
                : match?.HasUpdate == true
                    ? ModState.Updatable
                    : ModState.Normal;

            // 备注只能写一句，优先级：**缺依赖 > 有更新 > 不在 Modrinth 收录**。
            var note = missing is not null
                ? TextTemplate.Of("需要 {0}", missing.ModId)
                : match?.HasUpdate == true
                    ? TextTemplate.Of("最新 {0}", match.LatestVersionNumber!)
                    : lookedUp && match is null
                        ? TextTemplate.Of("不在 Modrinth 收录")
                        : null;

            var entry = new ModEntry(
                Name: item.Name,
                // 磁盘上的文件名：启用 / 禁用 / 删除都按它点名（显示名会重名，版本也会相同）。
                FileName: item.File.FileName,
                Version: string.IsNullOrWhiteSpace(item.Metadata.Version) ? Dash : item.Metadata.Version,
                Source: match is not null ? ContentSource.Modrinth : ContentSource.LocalFile,
                GameVersion: string.IsNullOrWhiteSpace(launch.GameVersion) ? Dash : launch.GameVersion,
                Enablement: Enablement(item.File.Enabled, lookedUp && match is null),
                State: state,
                Note: note,
                IsActive: rows.Count == 0);

            rows.Add(new ModRow(entry, item.File.Path, item.File.FileName, missing?.ModId));
        }

        return rows;
    }

    /// <summary>
    /// 启用状态：**禁用优先**（用户关掉的模组不该因为"来源查不到"而显示成另一种启用态），
    /// 其次是"启用但来源查不到"（<see cref="ModEnablement.Unverified"/>，设计稿里就是「未验证来源」）。
    /// </summary>
    private static ModEnablement Enablement(bool enabled, bool unverified) => !enabled
        ? ModEnablement.Disabled
        : unverified
            ? ModEnablement.Unverified
            : ModEnablement.Enabled;

    /// <summary>列出 mods 目录里的模组文件（<c>*.jar</c> 启用、<c>*.disabled</c> 禁用）。</summary>
    private List<ModFile> ListModFiles(string directory)
    {
        List<ModFile> files = [];

        if (!Directory.Exists(directory))
        {
            return files;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var fileName = Path.GetFileName(path);

                if (fileName.EndsWith(JarSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(new ModFile(path, fileName, Enabled: true));
                }
                else if (fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // 游戏里禁用模组就是给它加一个 .disabled 后缀；文件名照原样留着（横幅要报它）。
                    files.Add(new ModFile(path, fileName, Enabled: false));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(directory, ex);
            return [];
        }

        return files;
    }

    /// <summary>显示名兜底：<c>sodium-fabric-0.5.8.jar</c> → <c>sodium-fabric-0.5.8</c>。</summary>
    private static string Stem(string fileName)
    {
        var name = fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^DisabledSuffix.Length]
            : fileName;

        return name.EndsWith(JarSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^JarSuffix.Length]
            : name;
    }

    // ------------------------------------------------------------ 资源包 ---

    private IReadOnlyList<ResourcePackEntry> BuildResourcePacks(string gameDirectory)
    {
        try
        {
            return ResourcePackScan.Read(
                Path.Combine(gameDirectory, ResourcePacksDirectoryName),
                ResourcePackScan.EnabledIds(gameDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(Path.Combine(gameDirectory, ResourcePacksDirectoryName), ex);
            return [];
        }
    }

    // -------------------------------------------------------------- 存档 ---

    private List<WorldSaveEntry> BuildSaves(string gameDirectory)
    {
        var savesDirectory = Path.Combine(gameDirectory, SavesDirectoryName);
        var backupsDirectory = Path.Combine(gameDirectory, BackupDirectoryName);

        if (!Directory.Exists(savesDirectory))
        {
            return [];
        }

        List<WorldSaveEntry> entries = [];

        try
        {
            foreach (var directory in Directory
                         .EnumerateDirectories(savesDirectory)
                         .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(directory);
                var facts = WorldSaveReader.Read(directory);

                // 存档目录自己的修改时间作为 LevelName/LastPlayed 都读不到时的兜底：
                // 它一定存在，而"从未游玩"在这种场合是假话（目录就在那里）。
                var lastPlayed = facts?.LastPlayed ?? new DateTimeOffset(File.GetLastWriteTimeUtc(directory));

                entries.Add(new WorldSaveEntry(
                    Name: string.IsNullOrWhiteSpace(facts?.LevelName) ? folder : facts.LevelName,
                    // 目录名是磁盘上的事实（备份文件名与恢复目标都按它定，M7-f），显示名是给人看的。
                    Folder: folder,
                    Size: InstanceText.FormatSize(DirectorySize.Measure(directory)),
                    LastPlayed: InstanceText.FormatTimestamp(lastPlayed, _clock.Now, TextTemplate.Of(Dash)),
                    GameMode: GameModeText(facts?.GameType),
                    GameVersion: string.IsNullOrWhiteSpace(facts?.VersionName) ? Dash : facts.VersionName,
                    IsBackedUp: HasBackup(backupsDirectory, folder),
                    IsActive: entries.Count == 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(savesDirectory, ex);
            return [];
        }

        return entries;
    }

    private static TextTemplate GameModeText(int? gameType) => gameType switch
    {
        0 => TextTemplate.Of("生存"),
        1 => TextTemplate.Of("创造"),
        2 => TextTemplate.Of("冒险"),
        3 => TextTemplate.Of("旁观"),
        _ => TextTemplate.Of(Dash),
    };

    /// <summary>
    /// 这个存档有没有备份。
    /// </summary>
    /// <remarks>
    /// 规则 = 「<c>&lt;游戏目录&gt;\backups</c> 下有 <c>&lt;存档目录名&gt;.zip</c>」。
    /// 写备份的是 P1-9（<c>WorldBackupInstaller</c>，M7-f），**规则与读取必须同源**。
    /// <para>
    /// <b>只认 <c>.zip</c>，不认同名目录</b>（M7-a 起这里还认过"同名的压缩前目录"）：M7-f 定下的
    /// 约定是一个存档一份 zip，而同名目录在 <c>backups\</c> 下另有主人 —— 恢复时换下来的旧存档
    /// 就放在 <c>backups\saves\&lt;时间戳&gt;\&lt;存档目录名&gt;\</c>。多认一种形状，
    /// 一个名叫 <c>saves</c> 的存档就会因为那个回收目录而被报成"已备份"。
    /// </para>
    /// </remarks>
    private static bool HasBackup(string backupsDirectory, string saveFolder) =>
        Directory.Exists(backupsDirectory)
        && File.Exists(Path.Combine(backupsDirectory, saveFolder + ".zip"));

    // ---------------------------------------------------------- 目录与戳 ---

    private InstanceLaunchInfo? TryResolve(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return null;
        }

        try
        {
            return _instances.GetLaunchInfo(instanceName);
        }
        catch (KeyNotFoundException)
        {
            // "没有实例上下文"是首次运行的正常状态，不是错误：p07 在零实例时给空表。
            return null;
        }
    }

    /// <summary>
    /// 目录的内容戳（条目名 + 大小 + 修改时间）。
    /// </summary>
    /// <remarks>
    /// 用来判断"这份扫描结果还能不能复用"：绑定刷新会让同一份列表被读好几次
    /// （<c>ResourcePacks</c> 与 <c>ResourcePackCountText</c> 就各读一次），
    /// 每次都重新开 zip、重算目录大小是白花的钱。用**内容戳**而不是固定 TTL：
    /// 往 mods 里拖一个 jar 之后，下一次读取必须立刻看得见。
    /// </remarks>
    private static string Stamp(string directory, string? probeChild = null)
    {
        if (!Directory.Exists(directory))
        {
            return "-";
        }

        try
        {
            var paths = Directory.GetFiles(directory)
                .Concat(Directory.GetDirectories(directory))
                .Order(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder();

            foreach (var path in paths)
            {
                var isDirectory = Directory.Exists(path);
                builder.Append(Path.GetFileName(path))
                    .Append(':')
                    .Append(isDirectory ? 0 : new FileInfo(path).Length)
                    .Append(':')
                    .Append(File.GetLastWriteTimeUtc(path).Ticks);

                if (probeChild is not null && isDirectory)
                {
                    var probe = new FileInfo(Path.Combine(path, probeChild));
                    builder.Append(':')
                        .Append(probe.Exists ? probe.Length : 0)
                        .Append(':')
                        .Append(probe.Exists ? probe.LastWriteTimeUtc.Ticks : 0);
                }

                builder.Append('|');
            }

            return builder.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "!";
        }
    }

    private static string FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{info.Length}:{info.LastWriteTimeUtc.Ticks}")
            : "-";
    }

    private void Warn(string directory, Exception exception)
    {
        if (!_readFailureWarnings.ShouldReport(directory))
        {
            return;
        }

        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.Launcher,
            "内容目录读取失败（{0}）：{1}",
            [directory, exception.GetType().Name],
            directory);
    }

    /// <summary>mods 目录里的一个文件。</summary>
    /// <param name="Path">绝对路径。</param>
    /// <param name="FileName">磁盘上的文件名（横幅里要报的那个）。</param>
    /// <param name="Enabled">是否启用。</param>
    private sealed record ModFile(string Path, string FileName, bool Enabled);

    /// <summary>
    /// 表格行 + 缺失依赖的详情所需的两段数据。
    /// </summary>
    /// <param name="Entry">表格行。</param>
    /// <param name="Path">磁盘上的绝对路径（M7-d：更新时要把它移进备份）。</param>
    /// <param name="FileName">磁盘上的文件名（横幅里要报的那个，如 <c>create-0.5.1f.jar</c>）。</param>
    /// <param name="MissingId">缺失的依赖 id；没有问题时为 <see langword="null"/>。</param>
    /// <remarks>
    /// 表格行里的 <c>Note</c> 只带依赖 id（<c>需要 flywheel</c>），而横幅要说
    /// <c>create-0.5.1f.jar 需要 flywheel，当前未安装</c> —— 文件名只有这里知道，
    /// 所以扫描时一并带出来，界面不必反查（反查就要再扫一遍目录）。
    /// </remarks>
    private sealed record ModRow(ModEntry Entry, string Path, string FileName, string? MissingId);

    /// <summary>按内容戳缓存一次扫描结果。</summary>
    /// <typeparam name="T">行类型。</typeparam>
    private sealed class ScanCache<T>
    {
        private readonly Dictionary<string, (string Stamp, IReadOnlyList<T> Rows)> _entries =
            new(StringComparer.Ordinal);

        private readonly object _gate = new();

        public IReadOnlyList<T> Get(string key, Func<string> stamp, Func<IReadOnlyList<T>> build)
        {
            var current = stamp();

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var cached)
                    && string.Equals(cached.Stamp, current, StringComparison.Ordinal))
                {
                    return cached.Rows;
                }
            }

            // 扫描放在锁外：这里没有"必须只跑一次"的语义，重复扫描只是多花一次钱，
            // 而把 IO 放在锁里会让两个页面的绑定互相等。
            var rows = build();

            lock (_gate)
            {
                _entries[key] = (current, rows);
            }

            return rows;
        }
    }
}
