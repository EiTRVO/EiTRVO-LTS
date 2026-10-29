using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Versions;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Sample;

// ============================================================================
//  仅前端还原用的内存数据源。
//
//  本次交付的范围是「仅前端」，所以这些实现返回的是设计稿里逐字抄下来的演示数据，
//  不访问网络、不读写磁盘。它们的唯一目的是让界面结构与真实数据形状一致：
//  ViewModel 只依赖 Abstractions 里的接口，将来把这里换成真正的 HTTP / 文件实现即可，
//  View 与 ViewModel 不需要改动。
//
//  重要：所有「带实例」的查询都**按传入的实例名**返回各自的数据，不是共用一份。
//  否则在实例列表里换一行选中之后，详情页只会换个名字、其余字段还是原来那个实例的。
// ============================================================================

/// <summary>演示用实例目录：6 个实例各自有完整的属性与内容。</summary>
public sealed class SampleInstanceCatalog : IInstanceCatalog
{
    /// <summary>一个实例的全部演示数据。其余查询都从这里投影出来，避免同一件事写两遍。</summary>
    private sealed record Seed(
        string Name,
        string GameVersion,
        LoaderKind Loader,
        string? LoaderVersion,
        string SizeOnDisk,
        TextTemplate LastLaunched,
        TextTemplate StatusText,
        Tone StatusTone,
        TextTemplate BadgeText,
        Tone BadgeTone,
        int ModsEnabled,
        int ModsDisabled,
        TextTemplate JavaRuntime,
        TextTemplate JavaRuntimeSummary,
        TextTemplate ResourceVerification,
        int MemoryMb,
        TextTemplate PlayTime,
        int LaunchCount,
        int LastExitCode,
        TextTemplate WindowMode);

    private const string GameDirectory = @"E:\Minecraft\.minecraft";

    /// <summary>
    /// 演示数据里的加载器显示名：走**真实的那条规则**（<see cref="InstanceNaming.DisplayWithVersion"/>），
    /// 不另抄一遍 `Forge 49.0.30` 这样的字符串 —— 抄一遍就等于把"原版是文案"这件事漏在演示数据里。
    /// </summary>
    private static TextTemplate LoaderText(Seed seed) =>
        InstanceNaming.DisplayWithVersion(seed.Loader, seed.LoaderVersion);

    private static TextTemplate Today(string time) => TextTemplate.Of("今天 {0}", time);

    private static TextTemplate Yesterday(string time) => TextTemplate.Of("昨天 {0}", time);

    /// <summary>绝对时间那一支是纯数据键（<c>{0}</c>）。</summary>
    private static TextTemplate Absolute(string text) => TextTemplate.Of("{0}", text);

    private static TextTemplate Done(TextTemplate when) => TextTemplate.Of("已完成") with { Note = when };

    /// <summary>游戏版本号、账户名这类**原样显示的数据**走纯数据键 <c>{0}</c>。</summary>
    private static TextTemplate Data(string value) => TextTemplate.Of("{0}", value);

    /// <summary>Java 运行时那一格：版本 + 位数 + 供应商（三者都是数据，所以全在参数里，<c>位</c> 在键里）。</summary>
    private static TextTemplate Java(string version, string bits, string vendor) =>
        TextTemplate.Of("{0} ({1} 位 · {2})", version, bits, vendor);

    /// <summary>Java 运行时的简写（仪表板那一格）。</summary>
    private static TextTemplate JavaShort(string version, string bits) =>
        TextTemplate.Of("{0} ({1} 位)", version, bits);

    /// <summary>时长走**真实的那条规则**（<see cref="InstanceText.FormatPlayTime"/>），演示数据不另写一份。</summary>
    private static TextTemplate Play(long seconds) => InstanceText.FormatPlayTime(seconds);

    /// <summary>窗口同理（<see cref="InstanceText.FormatWindowMode"/>；演示数据都是窗口化）。</summary>
    private static TextTemplate Window(int width, int height) =>
        InstanceText.FormatWindowMode(width, height, WindowLaunchMode.Windowed);

    private static readonly Seed[] Seeds =
    [
        new("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30",
            "12.4 GB", Today("16:01"),
            TextTemplate.Of("就绪"), Tone.Ok, TextTemplate.Of("就绪"), Tone.Ok,
            84, 3, Java("17.0.9", "64", "Temurin"), JavaShort("17.0.9", "64"), Done(Today("15:52")),
            4096, Play(455460), 203, 0, Window(1920, 1080)),

        new("1.20.1-Fabric", "1.20.1", LoaderKind.Fabric, "0.15.7",
            "6.1 GB", Yesterday("21:30"),
            TextTemplate.Of("就绪"), Tone.Ok, TextTemplate.Of("就绪"), Tone.Ok,
            42, 0, Java("17.0.9", "64", "Temurin"), JavaShort("17.0.9", "64"), Done(Yesterday("21:12")),
            4096, Play(209040), 91, 0, Window(1920, 1080)),

        new("1.21.4-Vanilla", "1.21.4", LoaderKind.Vanilla, null,
            "1.2 GB", Absolute("2026/9/20 19:02"),
            TextTemplate.Of("需补 Java 21"), Tone.Warn, TextTemplate.Of("需补运行时"), Tone.Warn,
            0, 0,
            TextTemplate.Of("未检测到 Java {0}", "21"), TextTemplate.Of("未检测到"), TextTemplate.Of("从未校验"),
            4096, Play(45600), 18, 0, Window(1920, 1080)),

        new("1.21.1-NeoForge", "1.21.1", LoaderKind.NeoForge, "21.1.252",
            "8.7 GB", Absolute("2026/9/18 22:11"),
            TextTemplate.Of("就绪"), Tone.Ok, TextTemplate.Of("就绪"), Tone.Ok,
            61, 2, Java("25.0.3", "64", "Oracle"), JavaShort("25.0.3", "64"), Done(Absolute("2026/9/18 21:56")),
            6144, Play(296100), 117, 0, Window(2560, 1440)),

        new("1.12.2-Forge", "1.12.2", LoaderKind.Forge, "14.23.5.2860",
            "4.3 GB", Absolute("2026/9/12 20:45"),
            TextTemplate.Of("Java 8 未安装"), Tone.Muted, TextTemplate.Of("Java 8 未安装"), Tone.Muted,
            115, 3, Java("8u421", "64", "Oracle"), JavaShort("8u421", "64"), Done(Absolute("2026/9/12 20:30")),
            3072, Play(267720), 140, 0, Window(1280, 720)),

        new("1.20.4-OptiFine", "1.20.4", LoaderKind.OptiFine, "HD U I8",
            "1.9 GB", Absolute("2026/9/10 18:20"),
            TextTemplate.Of("待确认安装"), Tone.Warn, TextTemplate.Of("待确认安装"), Tone.Warn,
            2, 1, Java("17.0.9", "64", "Temurin"), JavaShort("17.0.9", "64"), Done(Absolute("2026/9/10 18:05")),
            2048, Play(21900), 9, 0, Window(1600, 900)),
    ];

    /// <inheritdoc />
    public string ActiveInstanceName { get; set; } = "1.20.4-Forge";

    /// <inheritdoc />
    public IReadOnlyList<InstanceOverview> GetOverview() =>
    [
        .. Seeds.Select(seed => new InstanceOverview(
            seed.Name,
            LoaderText(seed),
            seed.ModsEnabled + seed.ModsDisabled,
            seed.LastLaunched,
            seed.BadgeText,
            seed.BadgeTone,
            IsActive: false)),
    ];

    /// <inheritdoc />
    public IReadOnlyList<InstanceRecord> GetInstances() =>
    [
        .. Seeds.Select(seed => new InstanceRecord(
            seed.Name,
            Data(seed.GameVersion),
            LoaderText(seed),
            seed.Loader,
            seed.SizeOnDisk,
            seed.LastLaunched,
            seed.StatusText,
            seed.StatusTone,
            IsActive: false)),
    ];

    /// <inheritdoc />
    public InstanceDetail GetDetail(string instanceName)
    {
        var seed = Find(instanceName);
        var mods = seed.ModsEnabled + seed.ModsDisabled;

        return new InstanceDetail(
            Name: seed.Name,
            GameVersion: Data(seed.GameVersion),
            LoaderDisplay: LoaderText(seed),
            JavaRuntime: seed.JavaRuntime,
            JavaRuntimeSummary: seed.JavaRuntimeSummary,
            GameDirectory: GameDirectory,
            InstancePath: Path.Combine(GameDirectory, "versions", seed.Name),
            LastLaunched: seed.LastLaunched,
            ResourceVerification: seed.ResourceVerification,
            Account: Data("Steve (Microsoft)"),
            MemoryMb: seed.MemoryMb,
            SizeOnDisk: seed.SizeOnDisk,
            ModSummary: mods == 0
                ? TextTemplate.Of("{0} 个", "0")
                : TextTemplate.Of(
                    "{0} 个 (已启用 {1})",
                    mods.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    seed.ModsEnabled.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            PlayTime: seed.PlayTime,
            LaunchCount: seed.LaunchCount,
            LastExitCode: seed.LastExitCode,
            WindowMode: seed.WindowMode,
            IsolatedDirectory: true);
    }

    /// <inheritdoc />
    public IReadOnlyList<InstanceAction> GetActions(string instanceName)
    {
        var seed = Find(instanceName);
        var mods = seed.ModsEnabled + seed.ModsDisabled;
        var enabled = seed.ModsEnabled.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return
        [
            new(
                TextTemplate.Of("模组管理"),
                mods == 0
                    ? TextTemplate.Of("该实例没有模组")
                    : TextTemplate.Of(
                        "{0} 个模组 · 已启用 {1}",
                        mods.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        enabled),
                InstanceActionTarget.Mods),
            new(
                TextTemplate.Of("资源包 / 存档"),
                TextTemplate.Of("{0} 个资源包 · {1} 个存档 · {2} 个已备份", "4", "9", "2"),
                InstanceActionTarget.Resources),
            new(
                TextTemplate.Of("实例设置"),
                // 与真实实现用**同一个键**（演示数据不另造文案）；第二句（窗口模式）挂在 Note 上。
                // 这里刻意不把 Java 运行时摘要也塞进来：它是「键 + 参数」，塞进参数会露出键
                // （AGENTS.md §5.3 第 ⑨ 条），而真实实现那一句本来也只有内存与窗口。
                TextTemplate.Of(
                    "内存 {0} MB",
                    seed.MemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture)) with
                {
                    Note = seed.WindowMode,
                },
                InstanceActionTarget.InstanceSettings),
        ];
    }

    private static Seed Find(string instanceName) =>
        Seeds.FirstOrDefault(seed => string.Equals(seed.Name, instanceName, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"演示数据里没有实例「{instanceName}」。");

    /// <inheritdoc />
    public InstanceCounts GetCounts(string instanceName)
    {
        var seed = Find(instanceName);
        return new InstanceCounts(
            Mods: seed.ModsEnabled + seed.ModsDisabled,
            ResourcePacks: 4,
            Saves: 9);
    }

    /// <inheritdoc />
    /// <remarks>演示数据源是只读的：新建 / 删除只由真实实现（<c>FileInstanceCatalog</c>）提供。</remarks>
    public InstanceDetail CreateInstance(InstanceCreationRequest request) =>
        throw new NotSupportedException("演示数据源不支持新建实例。");

    /// <inheritdoc />
    public InstanceLaunchInfo GetLaunchInfo(string instanceName)
    {
        var seed = Find(instanceName);
        var detail = GetDetail(instanceName);

        return new InstanceLaunchInfo(
            Name: seed.Name,
            GameVersion: seed.GameVersion,
            Loader: seed.Loader,
            // 演示数据也如实给加载器版本：M6-e-3d 起它同时是显示名（`Forge 49.0.30`）的来源，
            // 给 null 会让"显示名有版本、启动信息没有版本"自相矛盾。
            LoaderVersion: seed.LoaderVersion,
            IsolatedDirectory: detail.IsolatedDirectory,
            InstancePath: detail.InstancePath,
            GameDirectory: detail.GameDirectory,
            Account: null);
    }

    /// <inheritdoc />
    /// <remarks>演示数据源是只读的：启动记账只由真实实现提供。</remarks>
    public bool RecordLaunch(string instanceName, DateTimeOffset launchedAt, int exitCode, long playSeconds) => false;

    /// <inheritdoc />
    /// <remarks>演示数据源是只读的：新建 / 删除只由真实实现（<c>FileInstanceCatalog</c>）提供。</remarks>
    public InstanceDeletionResult DeleteInstance(string instanceName, InstanceDeletionOptions options) =>
        throw new NotSupportedException("演示数据源不支持删除实例。");

    /// <inheritdoc />
    public InstanceDetail RenameInstance(string instanceName, string newName) =>
        throw new NotSupportedException("演示数据源不支持重命名实例。");
}

/// <summary>演示用版本清单。</summary>
public sealed class SampleVersionCatalog : IVersionCatalog
{
    private static readonly MinecraftVersion[] Versions =
    [
        new("1.21.4", VersionKind.Release, "2024/12/03", "Java 21", VersionLocalState.MissingJava, LocalState(VersionLocalState.MissingJava, 21), false),
        new("1.21.1", VersionKind.Release, "2024/08/08", "Java 21", VersionLocalState.Installed, LocalState(VersionLocalState.Installed), false),
        new("1.20.4", VersionKind.Release, "2023/12/07", "Java 17", VersionLocalState.Installed, LocalState(VersionLocalState.Installed), true),
        new("1.20.2", VersionKind.Release, "2023/09/21", "Java 17", VersionLocalState.Installed, LocalState(VersionLocalState.Installed), false),
        new("1.20.1", VersionKind.Release, "2023/06/12", "Java 17", VersionLocalState.Installed, LocalState(VersionLocalState.Installed), false),
        new("1.12.2", VersionKind.Release, "2017/09/18", "Java 8", VersionLocalState.MissingJava, LocalState(VersionLocalState.MissingJava, 8), false),
        new("1.7.10", VersionKind.Release, "2014/06/26", "Java 8", VersionLocalState.NotInstalled, LocalState(VersionLocalState.NotInstalled), false),
        new("26.4-snapshot-2", VersionKind.Snapshot, "2026/09/24", "Java 25", VersionLocalState.NotInstalled, LocalState(VersionLocalState.NotInstalled), false),
        new("26.3", VersionKind.Release, "2026/08/19", "Java 25", VersionLocalState.MissingJava, LocalState(VersionLocalState.MissingJava, 25), false),
        new("1.16.5", VersionKind.Release, "2021/01/15", "Java 8", VersionLocalState.Installed, LocalState(VersionLocalState.Installed), false),
    ];

    /// <summary>
    /// 向导第 2 步的演示加载器列表。
    /// </summary>
    /// <remarks>
    /// M6-e-3d：显示名与说明文本**复用真实实现的那些键**（演示数据不另造文案），
    /// 版本列表则存**纯版本号** + <see cref="LoaderOption.RecommendedVersion"/> ——
    /// 「（推荐）」是显示文本，由 App 层拼（<c>LoaderOptionViewModel</c>）。
    /// </remarks>
    private static readonly LoaderOption[] Loaders =
    [
        new(LoaderKind.Vanilla, InstanceNaming.Display(LoaderKind.Vanilla),
            TextTemplate.Of("不安装任何加载器，纯净游戏"),
            LoaderAvailability.Discouraged, false, []),
        new(LoaderKind.Fabric, InstanceNaming.Display(LoaderKind.Fabric),
            TextTemplate.Of("{0} · 轻量 · 更新快", "0.15.7"),
            LoaderAvailability.Available, false, ["0.15.7", "0.15.6"]),
        new(LoaderKind.Forge, InstanceNaming.Display(LoaderKind.Forge),
            TextTemplate.Of("{0} · 生态兼容性最好", "49.0.30"),
            LoaderAvailability.Recommended, true, ["49.0.30", "49.0.29", "49.0.28"],
            RecommendedVersion: "49.0.30"),
        new(LoaderKind.NeoForge, InstanceNaming.Display(LoaderKind.NeoForge),
            TextTemplate.Of("该游戏版本没有对应版本"),
            LoaderAvailability.Unavailable, false, []),
        new(LoaderKind.Quilt, InstanceNaming.Display(LoaderKind.Quilt),
            TextTemplate.Of("{0} · Fabric 的分支", "0.27.1"),
            LoaderAvailability.Available, false, ["0.27.1"]),
        new(LoaderKind.OptiFine, InstanceNaming.Display(LoaderKind.OptiFine),
            TextTemplate.Of("{0} · 光影与画质增强", "HD_U_I8"),
            LoaderAvailability.Available, false, ["HD_U_I8", "HD_U_I6"]),
    ];

    /// <summary>
    /// 演示数据里的「本地状态」：走**真实的那条规则**（`已安装` / `未安装` / `缺少 Java N`），
    /// 不另抄一遍中文 —— 抄一遍就等于把"这些是文案"这件事漏在演示数据里（同 <c>LoaderText</c>）。
    /// </summary>
    /// <param name="state">本地状态。</param>
    /// <param name="java">缺少的 Java 主版本（只有 <see cref="VersionLocalState.MissingJava"/> 用得上）。</param>
    /// <returns>键 + 参数。</returns>
    private static TextTemplate LocalState(VersionLocalState state, int java = 0) => state switch
    {
        VersionLocalState.Installed => TextTemplate.Of("已安装"),
        VersionLocalState.MissingJava => TextTemplate.Of(
            "缺少 Java {0}",
            java.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => TextTemplate.Of("未安装"),
    };

    private static readonly WizardStep[] Steps =
    [
        new(1, TextTemplate.Of("游戏版本"), WizardStepState.Done),
        new(2, TextTemplate.Of("模组加载器"), WizardStepState.Current),
        new(3, TextTemplate.Of("名称与目录"), WizardStepState.Pending),
        new(4, TextTemplate.Of("确认并安装"), WizardStepState.Pending),
    ];

    /// <inheritdoc />
    public int TotalCount => 917;

    /// <inheritdoc />
    /// <remarks>
    /// 演示数据源里"当前行"就是那份硬编码里标了 <c>IsActive = true</c> 的那一条（1.20.4），
    /// 写进来的值也照存 —— 让 p05 的选择逻辑在演示数据下同样能跑通。
    /// </remarks>
    public string ActiveVersionId
    {
        get => _activeVersionId.Length > 0
            ? _activeVersionId
            : Versions.FirstOrDefault(static v => v.IsActive)?.Id ?? string.Empty;
        set => _activeVersionId = value?.Trim() ?? string.Empty;
    }

    private string _activeVersionId = string.Empty;

    /// <inheritdoc />
    public int SnapshotCount => 612;

    /// <inheritdoc />
    public int ReleaseCount => 305;

    /// <inheritdoc />
    public IReadOnlyList<MinecraftVersion> GetVersions() => Versions;

    /// <inheritdoc />
    /// <remarks>演示数据源没有真实版本 JSON，因此不编一个数字（界面会显示"暂时无法估算"）。</remarks>
    public Task<VersionDownloadEstimate?> EstimateDownloadAsync(
        string versionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<VersionDownloadEstimate?>(null);

    /// <inheritdoc />
    public IReadOnlyList<LoaderOption> GetLoaders(string gameVersion) => Loaders;

    /// <inheritdoc />
    public IReadOnlyList<WizardStep> GetWizardSteps() => Steps;

    /// <inheritdoc />
    /// <remarks>演示数据源没有网络，刷新是空操作（真实实现见 <c>FileVersionCatalog</c>）。</remarks>
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>演示数据源没有网络，刷新是空操作（真实实现见 <c>FileVersionCatalog</c>）。</remarks>
    public Task RefreshLoaderAsync(LoaderKind loader, string gameVersion, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>演示数据源没有网络，刷新是空操作（真实实现见 <c>FileVersionCatalog</c>）。</remarks>
    public Task RefreshLoadersAsync(string gameVersion, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>演示用下载队列。</summary>
public sealed class SampleDownloadCatalog : IDownloadCatalog
{
    private static readonly DownloadQueueItem[] Queue =
    [
        new(
            "aaaa1111",
            TextTemplate.Of("安装 {0}", "1.21.4-Vanilla"),
            TextTemplate.Of("{0} · {1}", "256 MB / 412 MB", "8.4 MB/s"),
            DownloadQueueState.Running,
            true),
        new(
            "bbbb2222",
            TextTemplate.Of("下载 {0}", "Sodium 0.6.5"),
            TextTemplate.Of("等待中 · 已排队 {0} 秒", "12"),
            DownloadQueueState.Queued,
            false),
        new(
            "cccc3333",
            TextTemplate.Of("补全 Java {0} 运行时", "21"),
            TextTemplate.Of("等待中 · {0}", "218 MB"),
            DownloadQueueState.Queued,
            false),
    ];


    /// <inheritdoc />
    public int PendingTaskCount => 3;

    /// <inheritdoc />
    public DownloadProgress GetProgress() => new(
        CurrentTask: TextTemplate.Of("安装 {0}", "1.21.4-Vanilla"),
        Percent: 62,
        AmountText: "256 MB / 412 MB",
        SpeedText: "8.4 MB/s",
        FilesText: "1,204 / 3,180",
        RemainingText: TextTemplate.Of("约 {0} 秒", "19"),
        ConnectionsText: "64",
        DataSource: "piston-data.mojang.com");

    /// <inheritdoc />
    public IReadOnlyList<DownloadQueueItem> GetQueue() => Queue;

    /// <inheritdoc />
    /// <remarks>演示数据源是静态的，从不变化，因此不广播事件。</remarks>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 演示数据源没有真实安装能力：**如实抛异常**而不是返回一个假任务 ——
    /// 假成功会让"安装到底有没有做"这件事失去唯一可信的证据。
    /// </remarks>
    public InstallJob EnqueueInstall(InstallRequest request) =>
        throw new NotSupportedException("演示数据源不支持真实安装（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上：演示数据源不下载任何东西，如实抛异常。</remarks>
    public InstallJob EnqueueJavaRuntime(JavaRuntimeRequest request) =>
        throw new NotSupportedException("演示数据源不支持获取 Java 运行时（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上：演示数据源不下载任何东西，如实抛异常。</remarks>
    public InstallJob EnqueueContentDownload(ContentDownloadRequest request) =>
        throw new NotSupportedException("演示数据源不支持下载 Modrinth 内容（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上：演示数据源不导入任何东西，如实抛异常。</remarks>
    public InstallJob EnqueueMrpackImport(MrpackInstallRequest request) =>
        throw new NotSupportedException("演示数据源不支持导入整合包（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上：演示数据源不下载任何东西，如实抛异常。</remarks>
    public InstallJob EnqueueModrinthModpack(ModrinthModpackRequest request, string title) =>
        throw new NotSupportedException("演示数据源不支持从 Modrinth 下载整合包（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上：演示数据源不碰用户的存档文件，如实抛异常。</remarks>
    public InstallJob EnqueueWorldBackup(WorldBackupRequest request) =>
        throw new NotSupportedException("演示数据源不支持备份存档（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上：演示数据源不碰用户的存档文件，如实抛异常。</remarks>
    public InstallJob EnqueueWorldRestore(WorldRestoreRequest request) =>
        throw new NotSupportedException("演示数据源不支持恢复存档（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>演示数据源没有真实队列：没有任务可取消，如实抛异常而不是假装"取消成功了"。</remarks>
    public bool Cancel(string jobId) =>
        throw new NotSupportedException("演示数据源没有真实队列（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上。</remarks>
    public InstallJob? Retry(string jobId) =>
        throw new NotSupportedException("演示数据源没有真实队列（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    /// <remarks>同上。</remarks>
    public int ClearFinished() =>
        throw new NotSupportedException("演示数据源没有真实队列（真实实现见 FileDownloadCatalog）。");

    /// <inheritdoc />
    public IReadOnlyList<InstallJob> GetJobs() => [];

    /// <inheritdoc />
    /// <remarks>演示数据源永远处于空闲状态。</remarks>
    public Task WhenIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>演示用事件日志：实例名按传入值改写，所以每个实例看到的是自己的事件。</summary>
public sealed class SampleEventLog : IEventLog
{
    private static readonly EventEntry[] GlobalEvents =
    [
        new("1.20.4-Forge", 1076, EventLevel.Warning, EventSource.Downloader, "下载器", "资源索引校验跳过", "2026/9/29 16:32:00"),
        new("1.20.4-Forge", 8015, EventLevel.Info, EventSource.Modrinth, "Modrinth", "官方源下载成功", "2026/9/29 16:31:44"),
        new("1.20.4-Forge", 134, EventLevel.Success, EventSource.LaunchCore, "启动核心", "游戏已启动 (PID 8124)", "2026/9/29 16:31:38"),
        new("1.20.1-Fabric", 2210, EventLevel.Success, EventSource.JavaRuntime, "Java 运行时", "17.0.9 校验通过", "2026/9/28 21:30:12"),
    ];

    private static readonly EventEntry[] TemplateEvents =
    [
        new(string.Empty, 1076, EventLevel.Warning, EventSource.Downloader, "下载器", "资源索引校验跳过（已存在）", "2026/9/29 16:32:00"),
        new(string.Empty, 8015, EventLevel.Info, EventSource.Modrinth, "Modrinth", "cdn.modrinth.com 超时，已回退镜像", "2026/9/29 16:31:44"),
        new(string.Empty, 134, EventLevel.Success, EventSource.LaunchCore, "启动核心", "游戏已启动 (PID 8124)", "2026/9/29 16:31:38"),
        new(string.Empty, 2210, EventLevel.Error, EventSource.ModLoader, "模组加载器", "3 个模组缺失依赖（详见日志）", "2026/9/29 16:28:03"),
    ];

    private static readonly EventEntry[] SettingsTemplate =
    [
        new(string.Empty, 4412, EventLevel.Success, EventSource.InstanceSettings, "实例设置", "最大内存 4096 MB → 6144 MB", "2026/9/28 21:02"),
        new(string.Empty, 4390, EventLevel.Info, EventSource.InstanceSettings, "实例设置", "Java 覆盖设为 17.0.9（Temurin）", "2026/9/20 19:40"),
        new(string.Empty, 4371, EventLevel.Warning, EventSource.InstanceSettings, "实例设置", "JVM 参数含 -XX:+UseG1GC，与全局默认不同", "2026/9/20 19:40"),
        new(string.Empty, 4208, EventLevel.Success, EventSource.InstanceSettings, "实例设置", "启动方式设为窗口化 1920 × 1080", "2026/9/12 20:11"),
    ];

    /// <inheritdoc />
    public int TotalCount => 35;

    /// <inheritdoc />
    public IReadOnlyList<EventEntry> GetEvents() => GlobalEvents;

    /// <inheritdoc />
    public IReadOnlyList<EventEntry> GetInstanceEvents(string instanceName) =>
        [.. TemplateEvents.Select(entry => entry with { InstanceName = instanceName })];

    /// <inheritdoc />
    public IReadOnlyList<EventEntry> GetSettingsEvents(string instanceName) =>
        [.. SettingsTemplate.Select(entry => entry with { InstanceName = instanceName })];
    /// <inheritdoc />
    /// <remarks>
    /// M6-f：这个替身只记录，不发通知（没有界面在等它）。显式写 add/remove 而不是空事件字段，
    /// 是因为"字段式事件从未被触发"会触发 CS0067，而本项目把警告当错误。
    /// </remarks>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>演示用设置。保存写入内存，不落盘 —— 但「读取 → 修改 → 保存 → 再读取」这条链路是真的。</summary>
public sealed class SampleSettingsCatalog : ISettingsCatalog
{
    private GlobalSettings _global = new(
        GameDirectory: @"E:\Minecraft\.minecraft",
        DownloadSource: DownloadSourcePolicy.Official,
        ConcurrentDownloads: 64,
        MinimizeAfterLaunch: true,
        // 界面语言在这里**故意留空**：空 = 用户还没选过，启动语言交给 appsettings.json
        // （见 LocalizationStartup）。一旦用户在「Java 与设置」页选过并保存，这里就有值了，
        // 之后启动一律以它为准。
        Language: string.Empty,
        KeepLogsOnCrash: true,
        MaxMemoryMb: 4096,
        JvmArguments: string.Empty,
        WindowWidth: 1920,
        WindowHeight: 1080,
        Fullscreen: false,
        ExtraGameArguments: string.Empty,
        AutoFetchJava: true,
        JavaOverride: null,
        ForceSelectedJava: false,
        ForcedJavaHome: null);

    private readonly Dictionary<string, InstanceOverrides> _instanceOverrides = new(StringComparer.Ordinal)
    {
        // M3 起 InstanceOverrides.JavaOverride 的语义是**运行时根目录**（能直接拿去启动的那条路径），
        // 不再是「17.0.9（Temurin）」这种给人看的标签，所以这里的样本值一律是 null（= 自动按版本匹配）。
        // 设计稿下拉框里那句是**显示文本**，由 ViewModel 按真实检测结果拼出来
        // （见 InstanceSettingsViewModel.JavaOverrideOptions）。
        ["1.20.4-Forge"] = new(
            InstanceName: "1.20.4-Forge",
            JavaOverride: null,
            MaxMemoryMb: 6144,
            // 设计稿这一项与全局默认同值，按「未覆盖的项继承全局默认」的语义就应当记为未覆盖。
            // 界面上仍然显示这串参数（ViewModel 在读取时会回退到全局值），观感与设计稿一致。
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true),
        ["1.20.1-Fabric"] = new(
            InstanceName: "1.20.1-Fabric",
            JavaOverride: null,
            MaxMemoryMb: 4096,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true),
        ["1.21.1-NeoForge"] = new(
            InstanceName: "1.21.1-NeoForge",
            JavaOverride: null,
            MaxMemoryMb: 6144,
            JvmArguments: null,
            WindowWidth: 2560,
            WindowHeight: 1440,
            LaunchMode: WindowLaunchMode.Borderless,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true),
        ["1.21.4-Vanilla"] = new(
            InstanceName: "1.21.4-Vanilla",
            JavaOverride: null,
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true),
        ["1.12.2-Forge"] = new(
            InstanceName: "1.12.2-Forge",
            JavaOverride: null,
            MaxMemoryMb: 3072,
            JvmArguments: null,
            WindowWidth: 1280,
            WindowHeight: 720,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: false),
        ["1.20.4-OptiFine"] = new(
            InstanceName: "1.20.4-OptiFine",
            JavaOverride: null,
            MaxMemoryMb: 2048,
            JvmArguments: null,
            WindowWidth: 1600,
            WindowHeight: 900,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true),
    };

    /// <inheritdoc />
    public GlobalSettings GetGlobal() => _global;

    /// <inheritdoc />
    public InstanceOverrides GetInstanceOverrides(string instanceName) =>
        _instanceOverrides.TryGetValue(instanceName, out var overrides)
            ? overrides
            : new InstanceOverrides(
                InstanceName: instanceName,
                JavaOverride: null,
                MaxMemoryMb: null,
                JvmArguments: null,
                WindowWidth: _global.WindowWidth,
                WindowHeight: _global.WindowHeight,
                LaunchMode: WindowLaunchMode.Windowed,
                ExtraGameArguments: string.Empty,
                MinimizeAfterLaunch: _global.MinimizeAfterLaunch);

    /// <inheritdoc />
    public void SaveGlobal(GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _global = settings;
    }

    /// <inheritdoc />
    public void SaveInstanceOverrides(InstanceOverrides instanceOverrides)
    {
        ArgumentNullException.ThrowIfNull(instanceOverrides);
        _instanceOverrides[instanceOverrides.InstanceName] = instanceOverrides;
    }
}

/// <summary>演示用启动器元信息。</summary>
public sealed class SampleLauncherInfo : ILauncherInfo
{
    /// <inheritdoc />
    public string ProductName => "EiTRVO Core";

    /// <inheritdoc />
    public string Version => "1.0.0 (Build 20260930)";

    /// <inheritdoc />
    public string UiBaseline => "Server Manager 三段式骨架";

    /// <inheritdoc />
    public string UiProvenance => "独立实现（未引入 Neo UI 资产）";

    /// <inheritdoc />
    public string Runtime => ".NET 8 (net8.0-windows)";

    /// <inheritdoc />
    public string TargetPlatform => "Windows 10 1903+ / 11 (win-x64)";

    /// <inheritdoc />
    public string Architecture => "WPF + MVVM (CommunityToolkit.Mvvm)";

    /// <inheritdoc />
    public string Authentication => "Microsoft OAuth 2.0 设备代码流";

    /// <inheritdoc />
    public string VersionManifest => "piston-meta.mojang.com / version_manifest_v2";

    /// <inheritdoc />
    public string DependencyPolicy => "仅微软或微软背书库";

    /// <inheritdoc />
    public string DataDirectory => @"%APPDATA%\EiTRVO Core\";

    /// <inheritdoc />
    public string LogDirectory => Path.Combine(AppContext.BaseDirectory, "log") + Path.DirectorySeparatorChar;

    /// <inheritdoc />
    public string License => "待定";

    /// <inheritdoc />
    public string ThirdPartyNotices => "CommunityToolkit.Mvvm (MIT) 等";
}
