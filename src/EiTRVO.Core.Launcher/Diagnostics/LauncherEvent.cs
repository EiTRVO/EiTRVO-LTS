namespace EiTRVO.Core.Launcher.Diagnostics;

/// <summary>诊断事件的级别。刻意与 <c>Microsoft.Extensions.Logging.LogLevel</c> 分开：
/// 这是**要落盘并展示给用户**的领域事件，不是开发期日志。</summary>
public enum LauncherEventLevel
{
    /// <summary>跟踪信息。</summary>
    Trace,

    /// <summary>一般信息。</summary>
    Information,

    /// <summary>成功（设计稿事件表里的绿色行）。</summary>
    Success,

    /// <summary>警告（例如设置文件损坏后回退到默认值）。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error,

    /// <summary>致命（崩溃）。</summary>
    Critical,
}

/// <summary>
/// 一条诊断事件（Q8 已决策的形状：**级别 + 来源 + 参数**）。
/// <para>
/// 这里刻意**不放**拼好的句子。后端只给：
/// </para>
/// <list type="bullet">
/// <item><see cref="Level"/>：级别；</item>
/// <item><see cref="Source"/>：来源（稳定的英文标识，如 <c>settings</c>）；</item>
/// <item><see cref="MessageKey"/>：资源键（= 简体中文原文），App 层用它查 <c>.resx</c> 拼句子；</item>
/// <item><see cref="Parameters"/>：代入参数（已按不变文化格式化好）；</item>
/// <item><see cref="InstanceName"/>：相关实例（全局事件为 <see langword="null"/>）。</item>
/// </list>
/// <para>
/// 这样做的理由：内核层（<c>net8.0</c>，不引用任何 UI 类型）不参与本地化，
/// 而事件是**要展示**的文本。若在后端拼好中文句子，等英文界面接进来时
/// 事件表就会是中英混排，且后端得为了改文案重新编译。
/// </para>
/// </summary>
public sealed record LauncherEvent
{
    /// <summary>事件时间（来自 <c>IClock</c>）。</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>级别。</summary>
    public LauncherEventLevel Level { get; init; }

    /// <summary>来源标识。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>资源键（简体中文原文）。</summary>
    public string MessageKey { get; init; } = string.Empty;

    /// <summary>代入参数。</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary>相关实例名；全局事件为 <see langword="null"/>。</summary>
    public string? InstanceName { get; init; }
}

/// <summary>诊断事件的落点。写入方只依赖这个接口，因此换成文件 / 内存 / 转发都能测。</summary>
public interface IDiagnosticsLog
{
    /// <summary>写一条事件。</summary>
    /// <param name="level">级别。</param>
    /// <param name="source">来源标识。</param>
    /// <param name="messageKey">资源键（简体中文原文）。</param>
    /// <param name="parameters">代入参数。</param>
    /// <param name="instanceName">相关实例名。</param>
    void Write(
        LauncherEventLevel level,
        string source,
        string messageKey,
        IReadOnlyList<string>? parameters = null,
        string? instanceName = null);

    /// <summary>
    /// 读日志：**最近 <paramref name="maximumCount"/> 条**（由旧到新）+ 日志里当前的总条数。
    /// </summary>
    /// <remarks>
    /// 一次扫描同时给出两件事（表头要「共 N 个」、表格只要最近若干行）：分成"读最近 N 条"与
    /// "数总条数"两个调用，就会每刷一次界面扫两遍文件 —— 而事件文件上限 4 MB，
    /// 下载期间界面刷新很频繁（AGENTS.md §6 的节流要求）。坏行（崩溃时被打断写下的半截行）
    /// 跳过，不让它毁掉整份日志。
    /// </remarks>
    /// <param name="maximumCount">最多返回多少条；≤ 0 表示只要总数。</param>
    /// <returns>日志快照。</returns>
    EventLogSnapshot Read(int maximumCount);

    /// <summary>
    /// 写成功之后触发一次（**在写入线程上**，通常是后台线程）。
    /// </summary>
    /// <remarks>
    /// **M6-f**：这是"通知"的源头 —— 界面靠它知道"有新事件了"，而不是按固定间隔去轮询整个文件。
    /// 三条约定：
    /// <list type="number">
    /// <item>**落盘之后才触发** —— 订阅者立刻去读就能读到刚写的那一条；</item>
    /// <item>**逐订阅者吞异常** —— 日志是诊断的最后一道防线，某个订阅者坏了不能让这次写入失败；</item>
    /// <item>**不承诺线程**：调用方通常在后台线程写，订阅者自己负责切回自己的线程
    /// （App 侧由 <c>FileEventLog</c> 经刷新节流切回 UI 线程）。</item>
    /// </list>
    /// </remarks>
    event EventHandler? Changed;
}

/// <summary>一次事件日志读取的结果。</summary>
/// <param name="TotalCount">日志里当前有多少条事件（跳过坏行之后）。</param>
/// <param name="Recent">最近若干条事件，由旧到新。</param>
public sealed record EventLogSnapshot(int TotalCount, IReadOnlyList<LauncherEvent> Recent);
