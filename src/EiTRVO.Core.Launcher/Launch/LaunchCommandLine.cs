namespace EiTRVO.Core.Launcher.Launch;

/// <summary>命令行构建过程中发现的、调用方需要知道的问题种类。</summary>
/// <remarks>
/// 分成"哪一类"而不是只给一段文本，是因为这四类的**处置方式完全不同**：
/// 缺文件要提示重新安装、参数被拒要看日志、占位符未解析说明版本 JSON 不完整、
/// 库路径越界则说明这份 JSON 不可信。
/// </remarks>
public enum LaunchProblemKind
{
    /// <summary>版本声明了某个文件，但磁盘上没有（客户端 JAR、库、日志配置）。</summary>
    MissingFile,

    /// <summary>参数被安全策略或 Java 版本兼容性拒绝。</summary>
    RejectedArgument,

    /// <summary>版本 JSON 引用了本启动器不认识的占位符。</summary>
    UnresolvedPlaceholder,

    /// <summary>版本 JSON 里的库路径越出了游戏目录（远端数据试图写到目录之外）。</summary>
    UnsafeLibraryPath,
}

/// <summary>构建命令行时发现的一个问题。</summary>
/// <param name="Kind">种类。</param>
/// <param name="Subject">问题的主体（参数、库坐标、占位符名、文件路径）。</param>
/// <param name="Reason">可读的原因（内核文本属**数据**，不进界面资源表）。</param>
public sealed record LaunchProblem(LaunchProblemKind Kind, string Subject, string Reason)
{
    /// <summary>
    /// 这个问题是否应当**拦住启动**。
    /// <para>
    /// 缺客户端 JAR / 缺库要拦（不拦就是起来之后 NoClassDefFoundError）；
    /// 缺**日志配置**不拦 —— log4j 找不到配置文件只影响日志格式，游戏照常跑，
    /// 为它把启动拦下来是"为了一个不影响玩的因素挡住用户"。
    /// </para>
    /// </summary>
    public bool BlocksLaunch { get; init; } = true;
}

/// <summary>
/// 一次启动的完整命令行。
/// </summary>
/// <param name="Arguments">
/// 完整参数表：JVM 参数 → 主类 → 游戏参数，可直接交给 <c>ProcessStartInfo.ArgumentList</c>。
/// <para>
/// 之所以是"一个列表 + 主类下标"而不是分成 JVM/游戏两段：
/// <c>ArgumentList</c> 要的就是一个有序序列，而"哪一段是主类之前的"用一个下标就能表达 ——
/// 分成两段反而会在拼接处多一次出错机会（Neo 的调用方就得自己拼）。
/// </para>
/// <para>
/// <b>这一项里含访问令牌，不得直接进日志</b>（C-15）：要文本就用 <see cref="RedactedCommandText"/>，
/// 要结构化参数就用 <see cref="RedactedArguments"/>。本类型刻意**不提供**未脱敏的文本访问器 ——
/// 有这样一个属性，迟早会有人顺手打印它。
/// </para>
/// </param>
/// <param name="MainClassIndex"><see cref="Arguments"/> 里主类的下标。</param>
/// <param name="Classpath">最终类路径：已按构件去重、已排除 module-path 上的构件，顺序即优先级。</param>
/// <param name="Problems">
/// 构建过程中发现的问题；没有问题时为空。
/// <para>
/// 这些文本在交给调用方之前已经过脱敏（恶意版本 JSON 可以把 <c>${auth_access_token}</c>
/// 放进一个注定被拒的参数里，让启动器自己把令牌写进事件日志）。
/// </para>
/// </param>
public sealed record LaunchCommandLine(
    IReadOnlyList<string> Arguments,
    int MainClassIndex,
    IReadOnlyList<string> Classpath,
    IReadOnlyList<LaunchProblem> Problems)
{
    /// <summary>主类。</summary>
    public string MainClass => Arguments[MainClassIndex];

    /// <summary>主类之前的 JVM 参数（含 <c>-cp</c> 与它的值）。</summary>
    public IReadOnlyList<string> JvmArguments => [.. Arguments.Take(MainClassIndex)];

    /// <summary>主类之后的游戏参数。</summary>
    public IReadOnlyList<string> GameArguments => [.. Arguments.Skip(MainClassIndex + 1)];

    /// <summary>是否发现了任何问题。</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>脱敏后的实参表（结构化日志、事件的参数用它）。</summary>
    public IReadOnlyList<string> RedactedArguments => LaunchRedaction.Redact(Arguments);

    /// <summary>脱敏后的命令行文本（日志、事件、崩溃报告里的那一行用它）。</summary>
    public string RedactedCommandText => LaunchRedaction.Describe(Arguments);

    /// <summary>取某一类问题。</summary>
    /// <param name="kind">问题种类。</param>
    /// <returns>该类问题（可能为空）。</returns>
    public IReadOnlyList<LaunchProblem> ProblemsOf(LaunchProblemKind kind) =>
        [.. Problems.Where(problem => problem.Kind == kind)];
}
