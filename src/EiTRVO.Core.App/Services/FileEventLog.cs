using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 真实的 <see cref="IEventLog"/>：内核把事件写进 <c>log/events.jsonl</c>，这里把它读成界面行。
/// </summary>
/// <remarks>
/// 为什么在 **App 层**而不是内核里：Q8 之后内核只给「级别 + 来源 + `MessageKey` + 参数」，
/// **句子在 App 层拼**（内核是 <c>net8.0</c>，不参与本地化）。所以这一层做三件事：
/// 查 <c>.resx</c> 把键与参数拼成句子、把来源标识翻成来源名、按实例/来源过滤。
/// <para>
/// 读盘走 <see cref="IDiagnosticsLog.Read"/>（内核侧已有：一次扫描同时给总数与最近 N 条、
/// 跳过崩溃时写坏的半截行）。这里再套两层缓存（AGENTS.md §6 的节流要求）：
/// <b>500ms 快照缓存</b>（同一窗口内只读一次盘）与<b>按形状的投影缓存</b>
/// （同一份快照里，"全部事件 / 某实例 / 只要设置变更"各只拼一次句子）。
/// 后者的意义有两层：一是刷新只花一次格式化（200 行的 <c>Loc.T</c> 不再每次重算），
/// 二是返回**同一个列表实例** —— WPF 看见 <c>ItemsSource</c> 还是那个对象就不会重建 200 行。
/// </para>
/// </remarks>
internal sealed class FileEventLog : IEventLog
{
    /// <summary>表格最多显示多少行（旧的在下面；表头的「共 N 个」是总数，不受它影响）。</summary>
    private const int MaxRows = 200;

    /// <summary>同一份快照里最多缓存几种投影形状（五页最多用到三种：全部 / 某实例 / 某实例的设置）。</summary>
    private const int MaxCachedProjections = 8;

    /// <summary>同一份快照在这个窗口内复用。</summary>
    private static readonly TimeSpan CacheWindow = TimeSpan.FromMilliseconds(500);

    private readonly IDiagnosticsLog _diagnostics;
    private readonly IClock _clock;
    private readonly IUiRefreshThrottle _throttle;
    private readonly object _gate = new();
    private readonly Dictionary<(string? Instance, bool SettingsOnly), IReadOnlyList<EventEntry>> _projections = [];

    private EventLogSnapshot? _snapshot;
    private DateTimeOffset _snapshotAt;

    /// <summary>构造事件日志。</summary>
    /// <param name="diagnostics">落盘的事件日志（内核侧）。</param>
    /// <param name="clock">时钟（缓存窗口按它算，测试可以拨快）。</param>
    /// <param name="throttle">
    /// 刷新节流（M6-f：内核每写一条事件都会通知，短窗口内合并成一次，
    /// 并把"切回 UI 线程"这件事一并办掉）。
    /// </param>
    public FileEventLog(IDiagnosticsLog diagnostics, IClock clock, IUiRefreshThrottle throttle)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(throttle);

        _diagnostics = diagnostics;
        _clock = clock;
        _throttle = throttle;

        // M6-f：通知的源头在内核（后台线程写事件），这里只负责"合并 + 切回 UI 线程"。
        _diagnostics.Changed += OnDiagnosticsChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>
    /// 内核写了新事件（**在写入线程上**）→ 交给节流器合并，稍后在 UI 线程上通知界面。
    /// </summary>
    /// <remarks>
    /// 这里刻意**不在写入线程上直接抛 <see cref="Changed"/>**：订阅者是 ViewModel，
    /// 它们要改绑定属性。节流器内部会走 <c>IDispatcherService</c> 切回 UI 线程，
    /// 并且保证**窗口末尾补跑最后一次** —— 下载期间一秒几百条事件，界面也只刷十次，
    /// 但最后一条一定到（否则事件表会停在上一条上）。
    /// </remarks>
    private void OnDiagnosticsChanged(object? sender, EventArgs e) => _throttle.Request(NotifyChanged);

    /// <summary>通知界面"事件表变了"。</summary>
    /// <remarks>
    /// **先让快照失效**：`Snapshot()` 会在 500ms 窗口内复用上一次读盘的结果，
    /// 而"刚写了一条事件"的通知极可能落在这个窗口里 —— 不失效的话，
    /// 订阅者这次刷新读到的还是**旧快照**（新事件要等下一次刷新才出现，
    /// 而没有下一次的话就永远不出现：通知看起来毫无作用）。
    /// </remarks>
    private void NotifyChanged()
    {
        lock (_gate)
        {
            _snapshot = null;
            _projections.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public int TotalCount => Snapshot().TotalCount;

    /// <summary>当前缓存了几种投影形状（用例用它证明"缓存不随刷新次数增长"）。</summary>
    internal int CachedProjectionCount
    {
        get
        {
            lock (_gate)
            {
                return _projections.Count;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EventEntry> GetEvents() => Projection(instance: null, settingsOnly: false);

    /// <inheritdoc />
    public IReadOnlyList<EventEntry> GetInstanceEvents(string instanceName) =>
        Projection(instance: instanceName, settingsOnly: false);

    /// <inheritdoc />
    public IReadOnlyList<EventEntry> GetSettingsEvents(string instanceName) =>
        Projection(instance: instanceName, settingsOnly: true);

    /// <summary>取快照：同一个窗口内只读一次盘；读到新快照就把上一次的投影清掉。</summary>
    private EventLogSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _clock.Now;

            if (_snapshot is { } cached && now - _snapshotAt < CacheWindow)
            {
                return cached;
            }

            _snapshot = _diagnostics.Read(MaxRows);
            _snapshotAt = now;

            // 快照换了 ⇒ 行也该换。不清的话"某一行的实例名被改成另一个"这种事永远不会反映出来，
            // 而且缓存条数会随刷新次数无限长大（每次重读都算一份新投影）。
            _projections.Clear();

            return _snapshot;
        }
    }

    /// <summary>取某一形状的投影行（同一份快照内复用同一个列表实例）。</summary>
    /// <param name="instance">只保留这个实例的事件；<see langword="null"/> 表示不过滤。</param>
    /// <param name="settingsOnly">只保留「实例设置」来源的事件。</param>
    private IReadOnlyList<EventEntry> Projection(string? instance, bool settingsOnly)
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            var key = (instance, settingsOnly);

            if (_projections.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var rows = Project(snapshot, instance, settingsOnly);

            // 上限是兜底：真实调用点只有三种形状，但"传进来的实例名"来自界面，
            // 不堵住这条路就等于让缓存大小由调用方决定。
            if (_projections.Count < MaxCachedProjections)
            {
                _projections[key] = rows;
            }

            return rows;
        }
    }

    /// <summary>把内核事件投影成界面行。</summary>
    /// <param name="snapshot">快照（总数 + 最近若干条，由旧到新）。</param>
    /// <param name="instance">只保留这个实例的事件；<see langword="null"/> 表示不过滤。</param>
    /// <param name="settingsOnly">只保留「实例设置」来源的事件。</param>
    private static List<EventEntry> Project(
        EventLogSnapshot snapshot,
        string? instance,
        bool settingsOnly)
    {
        var events = snapshot.Recent;
        var total = snapshot.TotalCount;
        List<EventEntry> rows = [];

        for (var index = 0; index < events.Count; index++)
        {
            var entry = events[index];

            if (settingsOnly && !string.Equals(entry.Source, DiagnosticsSources.InstanceSettings, StringComparison.Ordinal))
            {
                continue;
            }

            if (instance is not null && !string.Equals(entry.InstanceName, instance, StringComparison.Ordinal))
            {
                continue;
            }

            // 事件号：日志里第几条（最新的那条等于总数）。轮转之后从头数 —— 它只是给人看的序号。
            var id = total - (events.Count - 1 - index);

            rows.Add(new EventEntry(
                InstanceName: entry.InstanceName ?? string.Empty,
                Id: id,
                Level: MapLevel(entry.Level),
                Source: MapSource(entry.Source),
                SourceText: SourceText(entry.Source),
                Message: RenderMessage(entry),
                Time: entry.Timestamp.ToString("yyyy/M/d HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)));
        }

        rows.Reverse();
        return rows;
    }

    /// <summary>拼句子：`MessageKey` 就是简体中文原文（也就是资源键），参数按不变文化已格式化好。</summary>
    /// <param name="entry">内核事件。</param>
    /// <returns>本地化后的句子。</returns>
    private static string RenderMessage(LauncherEvent entry) => LocalizedText.Of(entry.MessageKey, entry.Parameters);

    /// <summary>级别映射：内核六个级别 → 设计稿事件表的四个。</summary>
    /// <param name="level">内核级别。</param>
    /// <returns>界面级别。</returns>
    private static EventLevel MapLevel(LauncherEventLevel level) => level switch
    {
        LauncherEventLevel.Success => EventLevel.Success,
        LauncherEventLevel.Warning => EventLevel.Warning,
        LauncherEventLevel.Error or LauncherEventLevel.Critical => EventLevel.Error,

        // Trace 与 Information 都是"一条信息"：设计稿的事件表没有第五种颜色。
        _ => EventLevel.Info,
    };

    /// <summary>来源标识 → 枚举（事件表的小图标按它选）。</summary>
    /// <param name="source">内核来源标识。</param>
    /// <returns>界面来源。</returns>
    private static EventSource MapSource(string source) => source switch
    {
        DiagnosticsSources.Downloader => EventSource.Downloader,
        DiagnosticsSources.Modrinth => EventSource.Modrinth,
        DiagnosticsSources.JavaRuntime => EventSource.JavaRuntime,
        DiagnosticsSources.ModLoader => EventSource.ModLoader,
        DiagnosticsSources.LaunchCore => EventSource.LaunchCore,
        DiagnosticsSources.InstanceSettings => EventSource.InstanceSettings,

        // 账户链与"启动器本体"都归到 Launcher：设计稿的来源列只有这几类。
        _ => EventSource.Launcher,
    };

    /// <summary>来源显示名（本地化）。</summary>
    /// <param name="source">内核来源标识。</param>
    /// <returns>来源名。</returns>
    private static string SourceText(string source) => source switch
    {
        DiagnosticsSources.Downloader => Loc.T("下载器"),
        DiagnosticsSources.Modrinth => Loc.T("Modrinth"),
        DiagnosticsSources.JavaRuntime => Loc.T("Java 运行时"),
        DiagnosticsSources.ModLoader => Loc.T("模组加载器"),
        DiagnosticsSources.LaunchCore => Loc.T("启动核心"),
        DiagnosticsSources.InstanceSettings => Loc.T("实例设置"),

        // 账户链与"启动器本体"都归到「启动器」：设计稿的来源列只有这几类。
        _ => Loc.T("启动器"),
    };
}
