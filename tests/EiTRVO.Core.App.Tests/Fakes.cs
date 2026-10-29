using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Sample;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Launcher;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Versions;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>页面 ViewModel 的构造助手：M3 起 p05/p04/p10/p17 都多了一个 Java 检测依赖， 而大多数用例并不关心它 —— 把"不关心时用什么"集中在这里， 免得每个用例都写一串
/// <c>new FakeJavaRuntimeCatalog()</c> 把真正要断言的东西淹掉。</summary>
internal static class TestViewModels
{
    /// <summary>p10 Java 与设置。</summary>
    public static JavaSettingsViewModel JavaSettings(
        ISettingsCatalog? settings = null,
        IVersionCatalog? versions = null,
        IJavaRuntimeCatalog? java = null,
        ISystemInfoProvider? systemInfo = null,
        IDownloadCatalog? downloads = null,
        INavigationService? navigation = null,
        Localization.ILocalizationService? localization = null,
        IDialogService? dialogs = null,
        JavaRuntimeWarningDialogViewModel? warningDialog = null) =>
        new(
            settings ?? new SampleSettingsCatalog(),
            versions ?? new FakeVersionCatalog(),
            java ?? new FakeJavaRuntimeCatalog(),
            systemInfo ?? new FakeSystemInfoProvider(),
            downloads ?? new FakeDownloadCatalog(),
            navigation ?? new FakeNavigationService(),
            localization ?? Loc.Service,
            dialogs,
            warningDialog);

    /// <summary>p17 实例设置。</summary>
    public static InstanceSettingsViewModel InstanceSettings(
        ISettingsCatalog? settings = null,
        IInstanceCatalog? instances = null,
        IEventLog? events = null,
        IJavaRuntimeCatalog? java = null,
        ISystemInfoProvider? systemInfo = null) =>
        new(
            settings ?? new SampleSettingsCatalog(),
            instances ?? new SampleInstanceCatalog(),
            events ?? new SampleEventLog(),
            java ?? new FakeJavaRuntimeCatalog(),
            systemInfo ?? new FakeSystemInfoProvider());

    /// <summary>p05 所有版本。</summary>
    public static VersionsViewModel Versions(
        IVersionCatalog versions,
        INavigationService navigation,
        IJavaRuntimeCatalog? java = null) =>
        new(versions, navigation, java ?? new FakeJavaRuntimeCatalog());

    /// <summary>p04 新建实例向导。</summary>
    public static NewInstanceViewModel NewInstance(
        IVersionCatalog versions,
        IInstanceCatalog instances,
        INavigationService navigation,
        IDownloadCatalog downloads,
        IJavaRuntimeCatalog? java = null,
        IAccountDownloadPolicy? downloadAccess = null) =>
        new(versions, instances, navigation, downloads, java ?? new FakeJavaRuntimeCatalog(), downloadAccess);

    /// <summary>p02 本地实例。</summary>
    public static InstancesViewModel Instances(
        IInstanceCatalog? instances = null,
        IGameLauncher? launcher = null,
        INavigationService? navigation = null)
    {
        var catalog = instances ?? new SampleInstanceCatalog();
        var nav = navigation ?? new FakeNavigationService();
        return new(
            catalog,
            launcher ?? new FakeGameLauncher(),
            nav,
            new RenameInstanceDialogViewModel(catalog),
            new DeleteConfirmDialogViewModel(catalog, nav));
    }

    /// <summary>p14 正在启动。</summary>
    public static LaunchingViewModel Launching(
        IGameLauncher? launcher = null,
        IInstanceCatalog? instances = null,
        IUiRefreshThrottle? throttle = null) =>
        new(
            launcher ?? new FakeGameLauncher(),
            instances ?? new SampleInstanceCatalog(),
            throttle ?? new ImmediateRefreshThrottle());

    /// <summary>p11 账户（D-9 起多了皮肤服务与文件选择两个依赖）。</summary>
    public static AccountsViewModel Accounts(
        IAccountCatalog? accounts = null,
        ISkinService? skins = null,
        IDialogService? dialogs = null,
        OfflineAccountDialogViewModel? addOffline = null,
        RemoveAccountDialogViewModel? removeAccount = null)
    {
        // 对话框必须和页面共用**同一个**账户目录，否则"加了账户"页面看不到。
        var catalog = accounts ?? new SampleAccountCatalog();
        return new(
            catalog,
            skins ?? new FakeSkinService(),
            dialogs ?? new FakeDialogService(),
            addOffline ?? new OfflineAccountDialogViewModel(catalog),
            removeAccount ?? new RemoveAccountDialogViewModel(catalog));
    }
}

/// <summary>皮肤服务的替身（D-9）：记录"被要求做什么"，并按需要失败。 <para> 真实实现（官方接口 + 令牌 + 冷却）在 Auth 侧用 HTTP 桩逐条测过；这一层要验的是 **ViewModel
/// 有没有把用户动作正确交给内核、把失败原因显示出来、以及不该调的时候没调**。 </para></summary>
internal sealed class FakeSkinService : ISkinService
{
    /// <summary>被要求上传的（账户、文件、模型）。</summary>
    public List<(string PlayerName, string FilePath, SkinModel Model)> Uploads { get; } = [];

    /// <summary>被要求换模型的（账户、模型）。</summary>
    public List<(string PlayerName, SkinModel Model)> ModelChanges { get; } = [];

    /// <summary>被要求恢复默认的账户。</summary>
    public List<string> Resets { get; } = [];

    /// <summary>要抛的异常（三个动作都会抛它）。</summary>
    public SkinChangeException? Failure { get; set; }

    /// <summary>当前返回的冷却剩余时间（成功之后由替身自己拉起来，与真实实现一致）。</summary>
    public TimeSpan Cooldown { get; set; }

    /// <summary>成功时要返回的皮肤。</summary>
    public SkinProfile Result { get; set; } = new(
        "Steve",
        TextTemplate.Of("自定义皮肤 · {0}", "ab12cd34"),
        HasCape: false,
        SkinModel.Classic,
        TextTemplate.Of("今天 {0}", "09:00"));

    /// <inheritdoc />
    public TimeSpan CooldownRemaining(string playerName) => Cooldown;

    /// <inheritdoc />
    public Task<SkinProfile> UploadAsync(
        string playerName,
        string pngFilePath,
        SkinModel model,
        CancellationToken cancellationToken = default)
    {
        Uploads.Add((playerName, pngFilePath, model));
        return Complete();
    }

    /// <inheritdoc />
    public Task<SkinProfile> SetModelAsync(
        string playerName,
        SkinModel model,
        CancellationToken cancellationToken = default)
    {
        ModelChanges.Add((playerName, model));
        return Complete();
    }

    /// <inheritdoc />
    public Task<SkinProfile> ResetAsync(string playerName, CancellationToken cancellationToken = default)
    {
        Resets.Add(playerName);
        return Complete();
    }

    private Task<SkinProfile> Complete()
    {
        if (Failure is not null)
        {
            return Task.FromException<SkinProfile>(Failure);
        }

        Cooldown = TimeSpan.FromSeconds(60);
        return Task.FromResult(Result);
    }
}

/// <summary>文件选择框的替身：默认"用户取消了"，用例要选文件时给 <see cref="FileToPick"/> 赋值。</summary>
internal sealed class FakeDialogService : IDialogService
{
    /// <summary>要返回的文件路径；<see langword="null"/> 表示用户取消。</summary>
    public string? FileToPick { get; set; }

    /// <summary>保存对话框要返回的路径；<see langword="null"/> 表示取消。</summary>
    public string? SaveFileToPick { get; set; }

    /// <summary>被要求打开保存框的（标题、过滤器、建议文件名）。</summary>
    public List<(string Title, string Filter, string? DefaultName)> SavePicked { get; } = [];

    /// <summary>被要求打开选择框的（标题、过滤器）—— 用来断言"PNG 过滤器真的传下去了"。</summary>
    public List<(string Title, string Filter)> Picked { get; } = [];

    /// <inheritdoc />
    public string? PickFile(string title, string filter)
    {
        Picked.Add((title, filter));
        return FileToPick;
    }

    /// <inheritdoc />
    public string? PickSaveFile(string title, string filter, string? defaultFileName = null)
    {
        SavePicked.Add((title, filter, defaultFileName));
        return SaveFileToPick;
    }

    /// <summary>目录选择框要返回的路径；<see langword="null"/> 表示取消。</summary>
    public string? FolderToPick { get; set; }

    /// <summary>被要求打开的目录选择框标题。</summary>
    public List<string> FolderPicked { get; } = [];

    /// <inheritdoc />
    public string? PickFolder(string title)
    {
        FolderPicked.Add(title);
        return FolderToPick;
    }
}

/// <summary>启动编排的替身（M4-d）：快照由测试摆布，不真的起进程。 <para> 真实的启动链（参数构建、进程回收、退出码诊断）在 Launcher 侧用真进程测过；
/// 这里要测的是**界面有没有把快照投影成正确的行**（阶段、进度、失败原因、取消按钮）。 </para></summary>
internal sealed class FakeGameLauncher : IGameLauncher
{
    /// <summary>当前快照（测试直接改它，再手动触发 <see cref="Changed"/> 或让命令去读）。</summary>
    public LaunchSnapshot Snapshot { get; set; } = LaunchSnapshot.Idle;

    /// <summary>被要求启动的实例。</summary>
    public List<string> Launched { get; } = [];

    /// <summary>被要求停止的次数。</summary>
    public int StopCount { get; private set; }

    /// <summary>启动时返回的结果（默认"起来过、正常退出"）。</summary>
    public LaunchOutcome Outcome { get; set; } = new(
        Started: true,
        LaunchSessionState.Completed,
        Failure: null,
        Result: new GameProcessResult(0, TimeSpan.FromMinutes(2), false, null, null, []));

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>被读了几次快照（M6-d：界面刷新的次数上限就是按它断言的）。</summary>
    public int SnapshotReads { get; private set; }

    /// <inheritdoc />
    public LaunchSnapshot GetSnapshot()
    {
        SnapshotReads++;
        return Snapshot;
    }

    /// <inheritdoc />
    public IReadOnlyList<GameOutputLine> GetRecentOutput(int maximumCount) => [];

    /// <inheritdoc />
    public Task<LaunchOutcome> LaunchAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        Launched.Add(instanceName);
        return Task.FromResult(Outcome);
    }

    /// <inheritdoc />
    public void RequestStop() => StopCount++;

    /// <summary>模拟内核报出一次变更（阶段推进、进程起停）。</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Java 运行时检测的替身（M3）：给定一批运行时事实，不跑进程。 真实检测（跑 <c>java -version</c>）的行为在 Launcher 侧测过，这里要测的是 **ViewModel
/// 有没有把事实投影成正确的行**（可用 / 不适用 / 未检测到 / 缺 Java）。</summary>
internal sealed class FakeJavaRuntimeCatalog : IJavaRuntimeCatalog
{
    private readonly List<JavaInstallation> _installations;

    /// <summary>构造替身。</summary>
    /// <param name="installations">要报出的运行时。</param>
    /// <param name="hasDetectionResult">是否算"已经检测过"（未检测过时界面不许说"缺少 Java"）。</param>
    public FakeJavaRuntimeCatalog(
        IReadOnlyList<JavaInstallation>? installations = null,
        bool hasDetectionResult = true)
    {
        _installations = [.. installations ?? []];
        HasDetectionResult = hasDetectionResult;
    }

    /// <inheritdoc />
    public bool HasDetectionResult { get; set; }

    /// <summary>被要求刷新的次数。</summary>
    public int RefreshCount { get; private set; }

    /// <summary>被要求强制重新检测的次数。</summary>
    public int RedetectCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<JavaInstallation> GetInstallations() => _installations;

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RedetectAsync(CancellationToken cancellationToken = default)
    {
        RedetectCount++;
        return Task.CompletedTask;
    }
}

/// <summary>本机信息的替身：固定 32 GB 机器（与设计稿的「物理内存 32.0 GB」一致）， 于是内存推荐区间能被逐字断言（8–16 GB · 安全区上限 16384 MB）。</summary>
internal sealed class FakeSystemInfoProvider : ISystemInfoProvider
{
    /// <summary>物理内存总量（MB）；用例可以改，用来验证小内存机器上的收敛。</summary>
    public long TotalPhysicalMemoryMb { get; set; } = 32768;

    /// <inheritdoc />
    public string OperatingSystem => "Windows 11 24H2 (26100)";

    /// <inheritdoc />
    public string Processor => "12th Gen Intel Core i7-12700";

    /// <inheritdoc />
    public string InstalledMemory => "32.0 GB (可用 18.6 GB)";

    /// <inheritdoc />
    public string TotalMemory => TotalPhysicalMemoryMb >= 1024
        ? string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{TotalPhysicalMemoryMb / 1024.0:0.0} GB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{TotalPhysicalMemoryMb} MB");

    /// <inheritdoc />
    public MemoryRecommendation Memory => MemoryRecommendations.For(TotalPhysicalMemoryMb);

    /// <inheritdoc />
    public string GraphicsCard => "NVIDIA GeForce RTX 4060";
}

/// <summary>
/// 节流替身：**立即执行**（等价于"合并窗口为零"）。
/// <para>
/// 绝大多数用例只关心"队列报了变更 → 页面读到新数据"，不关心合并；
/// 用它可以让那条链保持同步、确定。合并窗口本身的语义由 <c>UiRefreshThrottleTests</c> 用真实现加手动时钟覆盖。
/// </para>
/// </summary>
internal sealed class ImmediateRefreshThrottle : IUiRefreshThrottle
{
    /// <summary>被请求了几次（用例可以断言"页面订阅了变更"）。</summary>
    public int Requests { get; private set; }

    /// <inheritdoc />
    public void Request(Action refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);

        Requests++;
        refresh();
    }
}

/// <summary>可拨动的手动时钟：节流窗口与事件缓存窗口都按它算，于是"等 100ms"在测试里是确定的。</summary>
internal sealed class ManualClock(DateTimeOffset now) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset Now { get; private set; } = now;

    /// <summary>把时钟往前拨。</summary>
    /// <param name="delta">往前拨多久。</param>
    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>
/// 把"延时执行"**接住**的调度替身：记下每一次 <see cref="InvokeAfter"/>，
/// 由用例在把时钟拨到点之后自己触发（<see cref="FireDue"/>）。
/// <para>
/// 为什么需要它：节流的关键行为是"窗口内合并、窗口末尾送达尾帧" ——
/// 若延时回调立即执行，就永远测不出"合并"，而睡眠等待真实时间会让用例变慢又不稳。
/// </para>
/// </summary>
internal sealed class ManualDispatcherService(Func<DateTimeOffset>? now = null) : IDispatcherService
{
    private readonly List<(DateTimeOffset DueAt, Action Action)> _delayed = [];

    /// <summary>当前是否在 UI 线程上（替身永远报"是"）。</summary>
    public bool IsOnUiThread => true;

    /// <summary>同步执行了几次（每次刷新都会走它，于是"刷新跑在 UI 线程上"可断言）。</summary>
    public int Invokes { get; private set; }

    /// <summary>当前挂着的延时回调个数。</summary>
    public int DelayedCount => _delayed.Count;

    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Invokes++;
        action();
    }

    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> operation) => operation();

    /// <inheritdoc />
    public void InvokeAfter(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dueAt = (now?.Invoke() ?? DateTimeOffset.MinValue) + delay;
        _delayed.Add((dueAt, action));
    }

    /// <summary>触发所有**已经到点**的延时回调（模拟消息循环跑起来）。</summary>
    /// <returns>被触发的个数。</returns>
    public int FireDue()
    {
        if (now is null)
        {
            var all = _delayed.ToArray();
            _delayed.Clear();

            foreach (var (_, action) in all)
            {
                action();
            }

            return all.Length;
        }

        var current = now();
        var due = _delayed.Where(entry => entry.DueAt <= current).ToArray();

        foreach (var entry in due)
        {
            _delayed.Remove(entry);
        }

        foreach (var (_, action) in due)
        {
            action();
        }

        return due.Length;
    }
}

/// <summary>测试替身。刻意手写而不是引 mock 库：AGENTS.md §3.2 的白名单里没有 mock 框架， 而这两个接口各自只有一两个成员需要记录调用，手写比引依赖便宜。</summary>
internal sealed class FakeNavigationService : INavigationService
{
    private readonly Func<PageId, PageViewModelBase>? _resolve;

    /// <summary>构造导航替身。</summary>
    /// <param name="resolve">页面解析函数；只关心「跳到哪里」的测试可以不传， 这样 <see cref="Current"/> 会保持为默认值而不需要为每个页面造一个 ViewModel。</param>
    public FakeNavigationService(Func<PageId, PageViewModelBase>? resolve = null) => _resolve = resolve;

    /// <summary>按顺序记录跳转过的页面。</summary>
    public List<PageId> Visited { get; } = [];

    /// <inheritdoc />
    public PageViewModelBase Current { get; private set; } = null!;

    /// <inheritdoc />
    public event EventHandler<PageViewModelBase>? Navigated;

    /// <inheritdoc />
    public void Navigate(PageId page)
    {
        Visited.Add(page);

        if (_resolve is null)
        {
            return;
        }

        Current = _resolve(page);
        Navigated?.Invoke(this, Current);
    }
}

/// <summary>帮助窗口替身：只记录"被要求打开几次"，不真的建窗口。</summary>
internal sealed class FakeHelpWindowService : IHelpWindowService
{
    /// <summary>被要求打开的这次数。</summary>
    public int ShowCount { get; private set; }

    /// <inheritdoc />
    public void Show() => ShowCount++;
}

/// <summary>事件搜索窗口替身：记录每次打开时传入的事件快照。</summary>
internal sealed class FakeEventSearchWindowService : IEventSearchWindowService
{
    /// <summary>每次打开时传入的事件。</summary>
    public List<IReadOnlyList<EventEntry>> ShownEvents { get; } = [];

    /// <inheritdoc />
    public void Show(IReadOnlyList<EventEntry> events) => ShownEvents.Add(events);
}

/// <summary>主窗口控制替身：记录还原 / 最小化 / 关闭，不碰真实窗口。</summary>
internal sealed class FakeWindowService : IWindowService
{
    /// <summary>被要求还原（并置前）的次数。</summary>
    public int RestoreCount { get; private set; }

    /// <summary>被要求最小化的次数。</summary>
    public int MinimizeCount { get; private set; }

    /// <summary>被要求关闭的次数。</summary>
    public int CloseCount { get; private set; }

    /// <inheritdoc />
    public bool IsMainWindowVisible => true;

    /// <inheritdoc />
    public void Minimize() => MinimizeCount++;

    /// <inheritdoc />
    public void Restore() => RestoreCount++;

    /// <inheritdoc />
    public void Close() => CloseCount++;
}

/// <summary>记录被打开/复制的目标，不做任何真实的外壳动作。</summary>
internal sealed class FakeShellService : IShellService
{
    /// <summary>被要求打开的目录。</summary>
    public List<string> Folders { get; } = [];

    /// <summary>被要求打开的链接。</summary>
    public List<string> Urls { get; } = [];

    /// <summary>被要求复制的文本。</summary>
    public List<string> Clipboard { get; } = [];

    /// <inheritdoc />
    public bool OpenFolder(string path)
    {
        Folders.Add(path);
        return true;
    }

    /// <inheritdoc />
    public bool OpenUrl(string url)
    {
        Urls.Add(url);
        return true;
    }

    /// <inheritdoc />
    public bool CopyToClipboard(string text)
    {
        Clipboard.Add(text);
        return true;
    }
}

/// <summary>只把事件记在内存里的诊断日志：用来断言"记录了哪条事件"， 而不必去解析落盘文件（落盘行为由 Launcher 侧的 <c>DiagnosticsLogTests</c> 覆盖）。</summary>
internal sealed class RecordingDiagnosticsLog : IDiagnosticsLog
{
    /// <summary>按写入顺序记录的事件。</summary>
    public List<LauncherEvent> Entries { get; } = [];

    /// <summary>读盘次数（快照缓存的用例要断言"窗口内只读一次"，而不是靠行数间接推断）。</summary>
    public int ReadCount { get; private set; }

    /// <inheritdoc />
    public void Write(
        LauncherEventLevel level,
        string source,
        string messageKey,
        IReadOnlyList<string>? parameters = null,
        string? instanceName = null)
    {
        Entries.Add(new LauncherEvent
        {
            Level = level,
            Source = source,
            MessageKey = messageKey,
            Parameters = parameters ?? [],
            InstanceName = instanceName,
        });

        // 真实实现（FileDiagnosticsLog）落盘之后就通知；替身也照做，
        // 这样"写一条 → 界面收到通知"这条链在所有用到它的用例里都是真的。
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public EventLogSnapshot Read(int maximumCount)
    {
        ReadCount++;
        return new(Entries.Count, maximumCount <= 0 ? [] : [.. Entries.TakeLast(maximumCount)]);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;
}

/// <summary>记录「建了哪个实例 / 删了哪个实例」的实例目录替身。 <para> 真实实现（<c>FileInstanceCatalog</c>）的行为已经在 Launcher 侧用真实文件测过了； 这里要测的是
/// **ViewModel 有没有把用户的选择正确交给内核** —— 所以只需要记录调用。 </para></summary>
internal sealed class FakeInstanceCatalog : IInstanceCatalog
{
    /// <summary>被请求创建实例的参数。</summary>
    public List<InstanceCreationRequest> Created { get; } = [];

    /// <summary>被请求删除的实例与选项。</summary>
    public List<(string Name, InstanceDeletionOptions Options)> Deleted { get; } = [];

    /// <inheritdoc />
    public string ActiveInstanceName { get; set; } = "1.20.4-Forge";

    /// <inheritdoc />
    public IReadOnlyList<InstanceOverview> GetOverview() =>
    [
        .. Created.Select(request => new InstanceOverview(
            request.Name,
            InstanceNaming.DisplayWithVersion(request.Loader, request.LoaderVersion),
            ModCount: 0,
            TextTemplate.Of("从未启动"),
            TextTemplate.Of("未安装"),
            Tone.Warn,
            IsActive: false)),
    ];

    /// <summary>被调用 <see cref="GetInstances"/> 的次数（用于确认点选行不会重扫磁盘）。</summary>
    public int GetInstancesCalls { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<InstanceRecord> GetInstances()
    {
        GetInstancesCalls++;

        return
        [
            .. Created.Select(request => new InstanceRecord(
            request.Name,
            TextTemplate.Of("{0}", request.GameVersion),
            InstanceNaming.DisplayWithVersion(request.Loader, request.LoaderVersion),
            request.Loader,
            "0 B",
            TextTemplate.Of("从未启动"),
            TextTemplate.Of("未安装"),
            Tone.Warn,
            IsActive: false)),
        ];
    }

    /// <inheritdoc />
    public InstanceDetail GetDetail(string instanceName) => new(
        Name: instanceName,
        GameVersion: TextTemplate.Of("{0}", "1.20.4"),
        LoaderDisplay: InstanceNaming.DisplayWithVersion(LoaderKind.Forge, "49.0.30"),
        JavaRuntime: TextTemplate.Of("自动匹配"),
        JavaRuntimeSummary: TextTemplate.Of("自动匹配"),
        GameDirectory: @"D:\Games\.minecraft",
        InstancePath: @"D:\Games\.minecraft\instances\1.20.4-Forge",
        LastLaunched: TextTemplate.Of("从未启动"),
        ResourceVerification: TextTemplate.Of("从未校验"),
        Account: TextTemplate.Of("未绑定"),
        MemoryMb: 4096,
        SizeOnDisk: "0 B",
        ModSummary: TextTemplate.Of("{0} 个", "0"),
        PlayTime: TextTemplate.Of("{0} 分", "0"),
        LaunchCount: 0,
        LastExitCode: 0,
        WindowMode: TextTemplate.Of("{0} × {1} 窗口化", "1920", "1080"),
        IsolatedDirectory: true);

    /// <inheritdoc />
    public IReadOnlyList<InstanceAction> GetActions(string instanceName) => [];

    /// <inheritdoc />
    public InstanceCounts GetCounts(string instanceName) => new(0, 0, 0);

    /// <inheritdoc />
    public InstanceLaunchInfo GetLaunchInfo(string instanceName) => new(
        Name: instanceName,
        GameVersion: "1.20.4",
        Loader: LoaderKind.Vanilla,
        LoaderVersion: null,
        IsolatedDirectory: true,
        InstancePath: @"D:\Games\.minecraft\instances\1.20.4-Forge",
        GameDirectory: @"D:\Games\.minecraft",
        Account: null);

    /// <summary>启动记账（测试可以断言"这次会话被记下来了"）。</summary>
    public List<(string Name, DateTimeOffset LaunchedAt, int ExitCode, long PlaySeconds)> Recorded { get; } = [];

    /// <inheritdoc />
    public bool RecordLaunch(string instanceName, DateTimeOffset launchedAt, int exitCode, long playSeconds)
    {
        Recorded.Add((instanceName, launchedAt, exitCode, playSeconds));
        return true;
    }

    /// <inheritdoc />
    public InstanceDetail CreateInstance(InstanceCreationRequest request)
    {
        Created.Add(request);
        return GetDetail(request.Name);
    }

    /// <inheritdoc />
    public InstanceDeletionResult DeleteInstance(string instanceName, InstanceDeletionOptions options)
    {
        Deleted.Add((instanceName, options));
        return new InstanceDeletionResult(instanceName, @"C:\recycle\entry", null, null);
    }

    /// <summary>被请求的重命名。</summary>
    public List<(string OldName, string NewName)> Renamed { get; } = [];

    /// <inheritdoc />
    public InstanceDetail RenameInstance(string instanceName, string newName)
    {
        Renamed.Add((instanceName, newName));
        ActiveInstanceName = newName;
        return GetDetail(newName);
    }
}

/// <summary>下载队列替身：记录"被要求安装了哪些任务"，供向导等 ViewModel 的单测使用 （真实安装要联网、要落盘，不属于 ViewModel 层的测试范围）。</summary>
internal sealed class FakeDownloadCatalog : IDownloadCatalog
{
    /// <summary>被入队的安装请求。</summary>
    public List<InstallRequest> Enqueued { get; } = [];

    /// <summary>被入队的 Java 运行时请求（§12.7）。</summary>
    public List<JavaRuntimeRequest> RuntimeRequests { get; } = [];

    /// <summary>被入队的内容下载请求（M7-d：p07 的「更新所选」）。</summary>
    public List<ContentDownloadRequest> ContentRequests { get; } = [];

    /// <summary>被入队的整合包导入请求（M7-e-2：p08 的「从文件导入 .mrpack」）。</summary>
    public List<MrpackInstallRequest> MrpackRequests { get; } = [];

    /// <summary>被入队的「从 Modrinth 下载整合包」请求（2026-10-21）。</summary>
    public List<(ModrinthModpackRequest Request, string Title)> ModrinthModpackRequests { get; } = [];

    /// <summary>被入队的存档备份请求（M7-f：p09 的「备份所选 / 备份全部存档」）。</summary>
    public List<WorldBackupRequest> WorldBackupRequests { get; } = [];

    /// <summary>被入队的存档恢复请求（M7-f：p09 的「恢复所选」）。</summary>
    public List<WorldRestoreRequest> WorldRestoreRequests { get; } = [];

    /// <summary>要返回给界面的进度（测试可改，用来验证"变更后刷新"这条链）。</summary>
    public DownloadProgress Progress { get; set; } = new(
        CurrentTask: TextTemplate.Of("—"),
        Percent: 0,
        AmountText: string.Empty,
        SpeedText: string.Empty,
        FilesText: string.Empty,
        RemainingText: TextTemplate.Of("—"),
        ConnectionsText: string.Empty,
        DataSource: string.Empty);

    /// <summary>要返回给界面的队列。</summary>
    public IReadOnlyList<DownloadQueueItem> Queue { get; set; } = [];

    /// <summary>被要求取消的任务 id（M7-g）。</summary>
    public List<string> Cancelled { get; } = [];

    /// <summary>被要求重试的任务 id（M7-g）。</summary>
    public List<string> Retried { get; } = [];

    /// <summary><see cref="ClearFinished"/> 被调用了几次（M7-g：p06 的「清空已完成」）。</summary>
    public int ClearFinishedCount { get; private set; }

    /// <inheritdoc />
    public bool Cancel(string jobId)
    {
        Cancelled.Add(jobId);
        return true;
    }

    /// <inheritdoc />
    public InstallJob? Retry(string jobId)
    {
        Retried.Add(jobId);
        return null;
    }

    /// <inheritdoc />
    public int ClearFinished()
    {
        ClearFinishedCount++;
        return 0;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>手动广播一次变更（模拟后台线程报进度）。</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public int PendingTaskCount =>
        Enqueued.Count + ContentRequests.Count + MrpackRequests.Count
        + WorldBackupRequests.Count + WorldRestoreRequests.Count;

    /// <summary>被要求读了几次进度（M6-d：界面刷新的次数上限就是按它断言的）。</summary>
    public int ProgressReads { get; private set; }

    /// <inheritdoc />
    public DownloadProgress GetProgress()
    {
        ProgressReads++;
        return Progress;
    }

    /// <inheritdoc />
    public IReadOnlyList<DownloadQueueItem> GetQueue() => Queue;

    /// <inheritdoc />
    public InstallJob EnqueueInstall(InstallRequest request)
    {
        Enqueued.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("安装 {0}", request.VersionId),
            new GameInstallTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueJavaRuntime(JavaRuntimeRequest request)
    {
        RuntimeRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("获取 Java 运行时（适配 {0}）", request.GameVersion ?? string.Empty),
            new JavaRuntimeTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueContentDownload(ContentDownloadRequest request)
    {
        ContentRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("更新 {0} 个模组 · {1}", request.Targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Subject),
            new ContentDownloadTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public IReadOnlyList<InstallJob> GetJobs() => [];

    /// <inheritdoc />
    public Task WhenIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public InstallJob EnqueueModrinthModpack(ModrinthModpackRequest request, string title)
    {
        ModrinthModpackRequests.Add((request, title));

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("安装整合包 {0}", title),
            new ModrinthModpackTask(request, title),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueMrpackImport(MrpackInstallRequest request)
    {
        MrpackRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("导入整合包 {0}", request.Subject),
            new MrpackInstallTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueWorldBackup(WorldBackupRequest request)
    {
        WorldBackupRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("备份存档 {0}", request.Subject),
            new WorldBackupTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueWorldRestore(WorldRestoreRequest request)
    {
        WorldRestoreRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("恢复存档 {0}", request.Subject),
            new WorldRestoreTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }
}

/// <summary>设备代码流的替身：给一组固定的链接与代码，不联网、不轮询。 <para> 它替换的是此前产品代码里的 <c>SampleDeviceCodeFlow</c> —— M5-1 起真实实现就在那里 （
/// <c>DeviceCodeFlow</c>），把"返回一组假代码"的东西留在产品里只剩误导，而测试需要它。 </para></summary>
internal sealed class FakeDeviceCodeFlow : IDeviceCodeFlow
{
    /// <summary>要返回的会话。</summary>
    public DeviceCodeSession Session { get; set; } = new(
        VerificationUrl: "https://microsoft.com/link",
        UserCode: "K7QP-4M2X",
        State: DeviceCodeState.Waiting,
        ExpiresInMinutes: 15);

    /// <inheritdoc />
    public DeviceCodeSession? Current { get; private set; }

    /// <summary>被发起的次数。</summary>
    public int Starts { get; private set; }

    /// <inheritdoc />
    public Task<DeviceCodeSession> StartAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Starts++;
        Current = Session;
        progress?.Report(new AuthProgress(AuthStage.WaitingForUser));
        return Task.FromResult(Current);
    }

    /// <inheritdoc />
    public Task<DeviceCodeOutcome> WaitAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new AuthProgress(AuthStage.Authorized));
        return Task.FromResult(new DeviceCodeOutcome(
            Current ?? Session,
            new MicrosoftTokens("ms-access", "refresh", DateTimeOffset.UtcNow.AddHours(1)),
            Failure: null));
    }
}

/// <summary>登录服务的替身。 <para> 默认**挂起**（<see cref="Gate"/> 未完成）：这样用例可以在"正在等待授权"那一刻断言界面， 再自己决定让它成功或失败 ——
/// 一个立刻返回的替身会让所有中间态都观察不到。 </para></summary>
internal sealed class FakeMicrosoftSignIn : IMicrosoftSignIn
{
    /// <summary>完成信号；用例用 <c>Gate.SetResult(...)</c> 让它成功。</summary>
    public TaskCompletionSource<Account> Gate { get; } = new();

    /// <summary>要抛的异常（优先于 <see cref="Gate"/>）。</summary>
    public Exception? Failure { get; set; }

    /// <summary>成功时要返回的账户。</summary>
    public Account Account { get; set; } = new(
        "Steve",
        AccountKind.Microsoft,
        "069a79f4-44e9-4726-a5be-fca90e38aaf5",
        TextTemplate.Of("今天 {0}", "16:01"),
        TokenState.Valid,
        IsDefault: true,
        IsActive: false);

    /// <summary>被调用的次数。</summary>
    public int Calls { get; private set; }

    /// <summary>真实实现会先走一遍设备代码流；替身也要走，否则界面拿不到会话。</summary>
    public IDeviceCodeFlow? Flow { get; set; }

    /// <inheritdoc />
    public async Task<Account> SignInAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        if (Flow is not null)
        {
            await Flow.StartAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        progress?.Report(new AuthProgress(AuthStage.CompletingSignIn));

        return await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>只为满足 <c>FileAccountCatalog</c> 的构造参数：离线路径永远不碰认证链。</summary>
internal sealed class UnusedAuthChain : IMinecraftAuthChain
{
    /// <inheritdoc />
    public Task<MicrosoftLoginResult> CompleteAsync(
        MicrosoftTokens microsoft,
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("离线账户不涉及认证链。");
}

/// <summary>同上：离线路径永远不需要刷新令牌。</summary>
internal sealed class UnusedDeviceCodeClient : IDeviceCodeClient
{
    /// <inheritdoc />
    public Task<DeviceCodeChallenge> RequestCodeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("离线账户不涉及设备代码流。");

    /// <inheritdoc />
    public Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("离线账户不涉及设备代码流。");

    /// <inheritdoc />
    public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("离线账户不涉及令牌刷新。");
}

/// <summary>账户目录替身：给出一条正版 + 一条离线账户，并记录"刷新被调用了几次"。 <para> p11 的刷新按钮在 M5-1 起走真实的 <c>RefreshMicrosoftAsync</c>；
/// 这一层要验的是 "按钮真的调了内核""失败要说出来""列表会重读"，而不是加密落盘（那是 Auth 侧的事）。 </para></summary>
internal sealed class FakeAccountCatalog : IAccountCatalog
{
    /// <summary>要报出的账户（第一条是正版、第二条是离线）。</summary>
    public List<Account> Accounts { get; } =
    [
        new("Steve", AccountKind.Microsoft, "069a79f4-44e9-4726-a5be-fca90e38aaf5", TextTemplate.Of("今天 {0}", "16:01"), TokenState.Expiring, true, false),
        new("Alex_Offline", AccountKind.Offline, "f284a681-7831-35a6-847e-1234112049bf", TextTemplate.Of("{0}", "2026/9/25"), TokenState.Valid, false, false),
    ];

    /// <summary>刷新被调用的次数。</summary>
    public int RefreshCalls { get; private set; }

    /// <summary>移除账户的调用记录。</summary>
    public List<string> RemoveCalls { get; } = [];

    /// <summary>设为默认账户的调用记录（页头账户下拉，S10-b）。</summary>
    public List<string> DefaultSetCalls { get; } = [];

    /// <summary>设为默认时要抛的异常（模拟"账户文件读不出来"这类写入失败）。</summary>
    public Exception? SetDefaultFailure { get; set; }

    /// <summary>移除时要抛的异常（模拟"账户文件读不出来"这类失败）。</summary>
    public Exception? RemoveFailure { get; set; }

    /// <summary>刷新时要抛的异常。</summary>
    public Exception? RefreshFailure { get; set; }

    /// <summary>取启动凭据时要返回的凭据（为 null 时保留旧的「替身没有真实令牌」异常）。</summary>
    public LaunchCredential? Credential { get; set; }

    /// <summary>取启动凭据时要抛的异常。</summary>
    public Exception? CredentialFailure { get; set; }

    /// <summary>账户文件的读取状态（模拟"文件在但读不出来"）。</summary>
    public AccountStoreStatus? StoreStatus { get; set; }

    /// <summary>要报出的皮肤四格（用例可以直接改它，模拟"内核刚把新档案写回来"）。</summary>
    public SkinProfile Skin { get; set; } = new(
        "Steve",
        TextTemplate.Of("未获取"),
        HasCape: false,
        SkinModel.Classic,
        TextTemplate.Of("从未更新"));

    /// <summary>被写回来的档案（D-9）。</summary>
    public List<(string PlayerName, MinecraftProfile Profile)> Profiles { get; } = [];

    /// <summary>记录的「上次使用」调用。</summary>
    public List<(string PlayerName, DateTimeOffset UsedAt)> Used { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<Account> GetAccounts() => Accounts;

    /// <inheritdoc />
    public Account? GetDefaultAccount() =>
        Accounts.FirstOrDefault(static account => account.IsDefault) ?? Accounts.FirstOrDefault();

    /// <inheritdoc />
    public SkinProfile GetSkin(string playerName) => Skin with { PlayerName = playerName };

    /// <inheritdoc />
    public MinecraftProfile? GetProfile(string playerName) =>
        Profiles.Count > 0 ? Profiles[^1].Profile : null;

    /// <inheritdoc />
    public Account UpdateProfile(string playerName, MinecraftProfile profile)
    {
        Profiles.Add((playerName, profile));

        var index = Accounts.FindIndex(account => string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            throw new KeyNotFoundException($"库里没有玩家「{playerName}」。");
        }

        // 与真实实现一样：四格跟着新档案走。
        Skin = new SkinProfile(
            playerName,
            string.IsNullOrEmpty(profile.SkinUrl)
                ? TextTemplate.Of("默认皮肤")
                : TextTemplate.Of("自定义皮肤 · {0}", "ab12cd34"),
            profile.HasCape,
            string.Equals(profile.SkinVariant, "SLIM", StringComparison.OrdinalIgnoreCase)
                ? SkinModel.Slim
                : SkinModel.Classic,
            TextTemplate.Of("{0}", "刚刚"));

        return Accounts[index];
    }

    /// <inheritdoc />
    public void RecordUse(string playerName, DateTimeOffset usedAt) => Used.Add((playerName, usedAt));

    /// <inheritdoc />
    public Account AddOffline(string playerName) => throw new NotSupportedException("替身不支持新建离线账户。");

    /// <inheritdoc />
    public void SetDefault(string playerName)
    {
        DefaultSetCalls.Add(playerName);

        if (SetDefaultFailure is not null)
        {
            throw SetDefaultFailure;
        }

        var index = Accounts.FindIndex(account => string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            throw new KeyNotFoundException($"库里没有玩家「{playerName}」。");
        }

        for (var i = 0; i < Accounts.Count; i++)
        {
            Accounts[i] = Accounts[i] with { IsDefault = i == index };
        }
    }

    /// <inheritdoc />
    public void Remove(string playerName)
    {
        RemoveCalls.Add(playerName);

        if (RemoveFailure is not null)
        {
            throw RemoveFailure;
        }

        var index = Accounts.FindIndex(account => string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            throw new KeyNotFoundException($"库里没有玩家「{playerName}」。");
        }

        Accounts.RemoveAt(index);
    }

    /// <inheritdoc />
    public Account AddMicrosoft(MicrosoftLoginResult login) => throw new NotSupportedException("替身不支持写入正版账户。");

    /// <inheritdoc />
    public Task<Account> RefreshMicrosoftAsync(string playerName, CancellationToken cancellationToken = default)
    {
        RefreshCalls++;

        if (RefreshFailure is not null)
        {
            return Task.FromException<Account>(RefreshFailure);
        }

        var index = Accounts.FindIndex(account => string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
        Accounts[index] = Accounts[index] with { TokenState = TokenState.Valid };

        return Task.FromResult(Accounts[index]);
    }

    /// <inheritdoc />
    public Task<LaunchCredential> GetCredentialAsync(string? playerName, CancellationToken cancellationToken = default)
    {
        if (CredentialFailure is not null)
        {
            return Task.FromException<LaunchCredential>(CredentialFailure);
        }

        return Credential is not null
            ? Task.FromResult(Credential)
            : throw new NotSupportedException("替身没有真实令牌。");
    }
}

/// <summary>版本清单替身：可以"刷新后换一套数据"，用来验证 ViewModel 在真实数据变化时的行为 （清单来自网络，所以"刚进页面时是空的、几百毫秒后才有行"是真实会发生的状态）。</summary>
internal sealed class FakeVersionCatalog : IVersionCatalog
{
    private IReadOnlyList<MinecraftVersion> _rows;

    /// <summary>构造替身。</summary>
    /// <param name="rows">初始版本行（可以为空，代表"本机还没有清单"）。</param>
    /// <param name="loaders">该替身返回的加载器选项。</param>
    public FakeVersionCatalog(
        IReadOnlyList<MinecraftVersion>? rows = null,
        IReadOnlyList<LoaderOption>? loaders = null)
    {
        _rows = rows ?? [];
        Loaders = loaders ?? [];
    }

    /// <summary>被要求刷新的次数。</summary>
    public int RefreshCount { get; private set; }

    /// <summary>被要求刷新加载器的次数。</summary>
    public int LoaderRefreshCount { get; private set; }

    /// <summary>最近一次刷新加载器时用的游戏版本。</summary>
    public string? LastLoaderGameVersion { get; private set; }

    /// <summary>下一次刷新后要换成哪套数据（<see langword="null"/> 表示不变）。</summary>
    public IReadOnlyList<MinecraftVersion>? RowsAfterRefresh { get; set; }

    /// <summary>该替身返回的加载器选项。</summary>
    public IReadOnlyList<LoaderOption> Loaders { get; set; }

    /// <summary>被写进来的"当前版本"（记录写入本身，用来验证 p05 把选中行交给了向导）。</summary>
    public string WrittenActiveVersionId { get; private set; } = string.Empty;

    /// <summary>向导体积提示的替身返回值（<see langword="null"/> 表示"估不出来"）。</summary>
    public VersionDownloadEstimate? EstimatedDownload { get; set; }

    /// <summary>被请求估算过的版本 id。</summary>
    public List<string> EstimateRequests { get; } = [];

    /// <inheritdoc />
    public Task<VersionDownloadEstimate?> EstimateDownloadAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        EstimateRequests.Add(versionId);
        return Task.FromResult(EstimatedDownload);
    }

    /// <inheritdoc />
    /// <remarks>读的时候按"用户写过的 → 行里标了 IsActive 的"解析，与真实实现的语义一致。</remarks>
    public string ActiveVersionId
    {
        get => WrittenActiveVersionId.Length > 0
            ? WrittenActiveVersionId
            : _rows.FirstOrDefault(static r => r.IsActive)?.Id ?? string.Empty;
        set
        {
            WrittenActiveVersionId = value ?? string.Empty;
            _rows = [.. _rows.Select(r => r with { IsActive = string.Equals(r.Id, WrittenActiveVersionId, StringComparison.Ordinal) })];
        }
    }

    /// <inheritdoc />
    public int TotalCount => _rows.Count;

    /// <inheritdoc />
    public int SnapshotCount => _rows.Count(static r => r.Kind == VersionKind.Snapshot);

    /// <inheritdoc />
    public int ReleaseCount => _rows.Count(static r => r.Kind == VersionKind.Release);

    /// <inheritdoc />
    public IReadOnlyList<MinecraftVersion> GetVersions() => _rows;

    /// <inheritdoc />
    public IReadOnlyList<LoaderOption> GetLoaders(string gameVersion) => Loaders;

    /// <inheritdoc />
    public IReadOnlyList<WizardStep> GetWizardSteps() =>
    [
        new(1, TextTemplate.Of("游戏版本"), WizardStepState.Pending),
        new(2, TextTemplate.Of("模组加载器"), WizardStepState.Pending),
        new(3, TextTemplate.Of("名称与目录"), WizardStepState.Pending),
        new(4, TextTemplate.Of("确认并安装"), WizardStepState.Pending),
    ];

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;

        if (RowsAfterRefresh is not null)
        {
            _rows = RowsAfterRefresh;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RefreshLoaderAsync(LoaderKind loader, string gameVersion, CancellationToken cancellationToken = default)
    {
        LoaderRefreshCount++;
        LastLoaderGameVersion = gameVersion;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RefreshLoadersAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        LoaderRefreshCount++;
        LastLoaderGameVersion = gameVersion;
        return Task.CompletedTask;
    }
}
