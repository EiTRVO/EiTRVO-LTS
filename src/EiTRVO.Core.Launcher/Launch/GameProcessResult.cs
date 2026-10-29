using System.Globalization;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 一次游戏进程的结局（C-14）。
/// </summary>
/// <param name="ExitCode">退出码；进程没能结束时为 <c>-1</c>。</param>
/// <param name="Duration">本次会话时长（用 <c>Stopwatch</c> 量，不受系统时钟调整影响）。</param>
/// <param name="WasKilled">是否由启动器结束（用户取消/结束游戏，或启动器退出）。</param>
/// <param name="JobObjectFailure">
/// Job Object 未能生效的原因；<see langword="null"/> 表示进程树回收**有保障**。
/// <para>
/// 分配失败时**不阻止启动**（游戏照常跑，只是"关掉启动器后它可能留在后台"），但必须如实说出来：
/// 否则"游戏没被回收"这件事在日志里没有任何线索。
/// </para>
/// </param>
/// <param name="Diagnosis">可读结论（正常退出为 <see langword="null"/>），已脱敏、已限长。</param>
/// <param name="StandardErrorTail">标准错误的尾部若干行（崩溃堆栈通常在这里）。</param>
public sealed record GameProcessResult(
    int ExitCode,
    TimeSpan Duration,
    bool WasKilled,
    string? JobObjectFailure,
    string? Diagnosis,
    IReadOnlyList<string> StandardErrorTail)
{
    /// <summary>
    /// 多短的一次会话**不计入**游玩时长。
    /// <para>
    /// 迁移自 Neo 的规则（<c>elapsedSeconds &gt;= 30</c>）：启动即崩、装完就退这种会话会污染
    /// "累计游玩时长"，而那个数字是给用户看的统计。30 秒是"确实进过游戏"的下限。
    /// </para>
    /// </summary>
    public static readonly TimeSpan PlaySessionMinimum = TimeSpan.FromSeconds(30);

    /// <summary>进程树回收是否有保障（Job Object 是否真的生效）。</summary>
    public bool ProcessTreeGuaranteed => JobObjectFailure is null;

    /// <summary>是否算作一次游玩（正常退出、没被结束、且够长）。</summary>
    public bool CountsAsPlaySession =>
        !WasKilled && ExitCode == 0 && Duration >= PlaySessionMinimum;

    /// <summary>
    /// 按退出码与 stderr 尾部给出一句可读结论（C-14）。
    /// </summary>
    /// <param name="exitCode">退出码。</param>
    /// <param name="wasKilled">是否由启动器结束。</param>
    /// <param name="standardErrorTail">标准错误尾部。</param>
    /// <returns>结论；正常退出时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 只对**几个确切知道原因**的退出码给"可能原因"，其余只说事实（退出码 + 尾部）。
    /// 因为编造原因比不给原因更糟：用户会照着去修一个不存在的问题。
    /// <para>
    /// Neo 在这里有个会误报的写法：它用 <c>exitCodeStr.EndsWith("1")</c> 判断
    /// "大概是文件不完整"，于是**退出码 11、21、31 都会被说成"版本文件不完整"**。
    /// 这里按数值精确比较。
    /// </para>
    /// </remarks>
    public static string? BuildDiagnosis(int exitCode, bool wasKilled, IReadOnlyList<string> standardErrorTail)
    {
        ArgumentNullException.ThrowIfNull(standardErrorTail);

        if (wasKilled)
        {
            return "游戏已被启动器结束（用户取消了启动，或请求结束游戏）。它启动的所有子进程已一并回收。";
        }

        if (exitCode == 0)
        {
            return null;
        }

        var code = exitCode.ToString(CultureInfo.InvariantCulture);
        var summary = exitCode switch
        {
            1 => "游戏非正常退出（退出码 1）。常见原因：版本文件不完整、Java 版本不匹配，或分配的内存不足。",
            -1073741819 => "游戏进程崩溃（退出码 -1073741819 / 0xC0000005 访问冲突）。常见原因：显卡驱动，或某个模组崩溃。",
            -1073741801 => "游戏因内存不足被系统结束（退出码 -1073741801 / 0xC0000017）。把「内存分配」调小一些通常就能启动。",
            -1073741510 => "游戏被中断（退出码 -1073741510 / 0xC000013A：Ctrl+C 或控制台被关闭）。",
            _ => $"游戏异常退出（退出码 {code}）。",
        };

        if (standardErrorTail.Count == 0)
        {
            return summary;
        }

        return summary + "\n\n--- 标准错误尾部 ---\n" + string.Join('\n', standardErrorTail);
    }
}
