using System.Diagnostics;
using System.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>一次外部工具运行的结果。</summary>
/// <param name="ExitCode">退出码；被超时/取消结束时为进程被终止后的码（用 <see cref="TimedOut"/> 区分）。</param>
/// <param name="OutputTail">stdout + stderr 的**最后若干行**（按到达顺序）；失败时要靠它说话。</param>
/// <param name="TimedOut">是否因为超过时限被终止。</param>
public sealed record ToolRunResult(int ExitCode, string OutputTail, bool TimedOut)
{
    /// <summary>是否正常结束（退出码 0 且没超时）。</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// 跑一次**外部工具**（Forge / NeoForge 的官方安装器）。
/// <para>
/// 与 <see cref="EiTRVO.Core.Launcher.Launch.IGameProcessRunner"/> 刻意分开：那一个的语义是"一个游戏会话"
/// （Job Object、输出环形缓冲、"已经有一个游戏在运行"），装加载器时跑的不是游戏 ——
/// 共用一套状态会出现"装 Forge 时说已经有一个游戏在运行"这种错话，而且启动器的
/// <c>RequestStop</c>（杀进程树）会误杀正在跑的安装器。
/// </para>
/// </summary>
public interface IToolRunner
{
    /// <summary>运行一个外部工具并等它结束。</summary>
    /// <param name="executablePath">可执行文件（这里是 <c>java.exe</c>）。</param>
    /// <param name="arguments">参数表（不进 shell，避免引号地狱）。</param>
    /// <param name="workingDirectory">工作目录。</param>
    /// <param name="timeout">时限；到点终止并如实报 <see cref="ToolRunResult.TimedOut"/>。</param>
    /// <param name="environment">
    /// 要**覆盖**的环境变量（只覆盖列出的键，其余继承当前进程）。
    /// <para>
    /// 存在的理由只有一个：OptiFine 的安装器（M4-i）用 <c>%APPDATA%\.minecraft</c> 决定往哪儿装，
    /// 而我们要让它装进一个沙箱目录 —— 不重定向就只能去动用户真实的 <c>.minecraft</c>。
    /// </para>
    /// </param>
    /// <param name="cancellationToken">取消标记（按 CA1068 必须在最后）。</param>
    /// <returns>运行结果。</returns>
    Task<ToolRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IToolRunner"/> 的真实实现：起进程 → 同时读 stdout/stderr（保留尾部）→ 等退出（带时限）。
/// <para>
/// 编码固定 UTF-8（与 M4-c 同一条理由：Java 工具在中文系统上会按控制台代码页输出，
/// 不钉住就会读出乱码，而失败原因恰恰要靠这些输出）。
/// </para>
/// </summary>
public sealed class ToolProcessRunner : IToolRunner
{
    /// <summary>保留多少行输出。</summary>
    public const int OutputTailLines = 40;

    /// <summary>保留多少字符输出。</summary>
    public const int OutputTailCharacters = 2000;

    /// <inheritdoc />
    public async Task<ToolRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        Process? process;

        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // 默认的 Win32Exception 只说"系统找不到指定的文件"，不说找不到哪个：
            // 安装器这一步的文件名是我们算出来的，必须把它带出来。
            throw new InvalidOperationException($"找不到要运行的程序：{executablePath}", exception);
        }

        var lines = new Queue<string>();

        if (process is null)
        {
            throw new InvalidOperationException($"无法启动外部程序：{executablePath}");
        }

        var stdout = PumpAsync(process.StandardOutput, lines);
        var stderr = PumpAsync(process.StandardError, lines);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested;

            TryKill(process);

            // 让读泵收尾，否则尾部输出会缺最后几行（失败原因最需要的就是它们）。
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

            // 用户取消要按取消语义抛出去（编排据此走"已取消"），超时则如实返回 TimedOut。
            cancellationToken.ThrowIfCancellationRequested();

            process.Dispose();
            return new ToolRunResult(-1, Tail(lines), TimedOut: timedOut);
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        var exitCode = process.ExitCode;
        process.Dispose();

        return new ToolRunResult(exitCode, Tail(lines), TimedOut: false);
    }

    private static async Task PumpAsync(StreamReader reader, Queue<string> lines)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (lines)
                {
                    lines.Enqueue(line);

                    while (lines.Count > OutputTailLines)
                    {
                        lines.Dequeue();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程被终止时管道会断：这不是错误，尾部已经收到的行照样要报出去。
        }
    }

    private static string Tail(Queue<string> lines)
    {
        string text;

        lock (lines)
        {
            text = string.Join(Environment.NewLine, lines);
        }

        return text.Length <= OutputTailCharacters ? text : text[^OutputTailCharacters..];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 已经自己退出了：没什么可做的。
        }
    }
}
