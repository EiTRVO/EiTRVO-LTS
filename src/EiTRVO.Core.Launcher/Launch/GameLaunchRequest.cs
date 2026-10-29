namespace EiTRVO.Core.Launcher.Launch;

/// <summary>输出的来源流。</summary>
public enum GameOutputChannel
{
    /// <summary>标准输出（Minecraft 的正常日志）。</summary>
    StandardOutput,

    /// <summary>标准错误（JVM 的报错、崩溃堆栈）。</summary>
    StandardError,
}

/// <summary>一行游戏输出。</summary>
/// <param name="Stream">来源流。</param>
/// <param name="Text">文本（已按 C-15 脱敏）。</param>
public sealed record GameOutputLine(GameOutputChannel Stream, string Text);

/// <summary>
/// 一批游戏输出。
/// <para>
/// 之所以是"一批"而不是"一行一个回调"（Neo 就是这么做的）：p14 的日志表行高 24px，
/// 而 Minecraft 启动时每秒能刷出几百行 —— 逐行回调等于每行一次 UI 派发，
/// 界面会在启动那一刻卡住（这正是 C-13 要求"节流批量刷新"的原因）。
/// </para>
/// </summary>
/// <param name="Lines">本批的行（按发生顺序）。</param>
/// <param name="IsFinal">
/// 是否是最后一批（进程已退出、剩余输出已补报）。
/// <para>
/// 这个标志是**必须**的：输出在时间上被节流，小进程可能在一批的窗口内就跑完了 ——
/// 若只按时间上报，最后那几行（往往正是崩溃原因）会永远送不出去（M2-d 在下载进度上踩过同一个坑）。
/// </para>
/// </param>
public sealed record GameOutputBatch(IReadOnlyList<GameOutputLine> Lines, bool IsFinal);

/// <summary>
/// 启动游戏进程所需的输入。
/// </summary>
/// <param name="ExecutablePath">要启动的程序（通常是 <c>javaw.exe</c>）。</param>
/// <param name="Arguments">完整实参表（M4-a 的 <c>LaunchCommandLine.Arguments</c>）。</param>
/// <param name="WorkingDirectory">
/// 工作目录。必须显式给：游戏会用相对路径找 <c>options.txt</c>、<c>saves\</c>、<c>mods\</c>，
/// 而"启动器自己的工作目录"与"实例目录"通常不是同一个地方 —— 传错的后果是存档写进了启动器的目录。
/// </param>
public sealed record GameLaunchRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    /// <summary>相关实例名（写进诊断事件；只装版本、没有实例时为 <see langword="null"/>）。</summary>
    public string? InstanceName { get; init; }

    /// <summary>版本 id（写进诊断事件）。</summary>
    public string? VersionId { get; init; }

    /// <summary>
    /// 输出里要抹掉的敏感值（C-15）：游戏或模组可能把启动参数回显到日志里。
    /// 直接给 <c>LaunchIdentity</c> 里的那几个值即可（长度不足 8 的会被忽略，见 <see cref="LaunchRedaction"/>）。
    /// </summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];
}
