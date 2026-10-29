using System.Diagnostics;
using System.Globalization;
using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 游戏进程的生命周期（C-12 进程树回收、C-13 输出节流、C-14 退出码诊断）。
/// <para>
/// 它**不碰 UI**：输出以节流后的批次经 <see cref="OutputReceived"/> 抛给订阅方（在后台线程触发），
/// 订阅方自己决定怎么切回 UI 线程（App 层用 <c>IDispatcherService</c>）。
/// </para>
/// <para>
/// 与 Neo 的 <c>RunGameProcessAsync</c> 相比，四处刻意的差别：
/// </para>
/// <list type="number">
/// <item><b>输出读到流结束，而不是读到进程退出</b>。Neo 的读取循环条件是
/// <c>while (!_gameProcess.HasExited)</c> —— 进程一退，读取立刻停，而**缓冲区里最后那几行正好是崩溃原因**。
/// 这里改成"读到 <c>ReadLine</c> 返回 null（管道 EOF）"，并对"子进程抱着管道不放"这一种病态情况
/// 设了 2 秒上限，避免挂死。</item>
/// <item><b>节流批量上报</b>（C-13）：Neo 每读一行就回调一次，而 Minecraft 启动时每秒能刷几百行，
/// 等于每行一次 UI 派发 —— p14 的 24px 行高表格会在启动那一刻卡住。这里按 200ms 或 200 行成批上报，
/// 并在进程退出时**强制补报**（否则短命进程的最后几行永远送不出去，M2-d 在进度上报上踩过同一个坑）。</item>
/// <item><b>非零退出码是结果，不是异常</b>。Neo 把退出码打包成 <c>Exception</c> 往上抛，
/// 于是"游戏崩了"和"启动器自己出错了"在调用方那里长得一样。这里返回
/// <see cref="GameProcessResult"/>（含退出码、时长、诊断），由上层决定提示什么。</item>
/// <item><b>明确的输出编码</b>：<c>StandardOutputEncoding = UTF-8</c>，与 M4-a 给 JVM 的
/// <c>-Dfile.encoding/-Dstdout.encoding/-Dstderr.encoding=UTF-8</c> 配对 ——
/// 不设的话 .NET 会按控制台代码页解，中文日志在 p14 里就是乱码。</item>
/// </list>
/// </summary>
public sealed class GameProcessRunner : IGameProcessRunner
{
    /// <summary>标准错误环形缓冲保留的行数（Neo 也是 20）。</summary>
    public const int StandardErrorTailLines = 20;

    /// <summary>诊断文本里 stderr 尾部的字符上限（Neo 是 1200）。</summary>
    public const int StandardErrorTailCharacters = 1200;

    /// <summary>一批最多多少行：防止一次洪泛把内存与界面一起撑爆。</summary>
    public const int DefaultMaximumBatchLines = 200;

    /// <summary>默认的节流间隔（与下载进度用的是同一个数量级）。</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>进程退出后，等待输出泵把管道读干的上限。</summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>取消后等待进程真正结束的上限。</summary>
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

    private readonly IDiagnosticsLog _diagnostics;
    private readonly object _gate = new();
    private readonly List<GameOutputLine> _pending = [];

    private Process? _process;
    private JobObject? _job;
    private volatile bool _killed;

    /// <summary>构造进程运行器。</summary>
    /// <param name="diagnostics">诊断事件（退出码结论要落盘，C-14）。</param>
    public GameProcessRunner(IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public event EventHandler<GameOutputBatch>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler<int>? Started;

    /// <summary>节流间隔。</summary>
    public TimeSpan FlushInterval { get; init; } = DefaultFlushInterval;

    /// <summary>一批的最大行数。</summary>
    public int MaximumBatchLines { get; init; } = DefaultMaximumBatchLines;

    /// <summary>正在运行的进程 id；没有在跑时为 <see langword="null"/>。</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>是否有一个进程正在跑。</summary>
    public bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 启动游戏并等它结束。
    /// </summary>
    /// <param name="request">启动输入。</param>
    /// <param name="cancellationToken">取消（用户取消启动 / 请求结束游戏 / 启动器退出）。</param>
    /// <returns>本次会话的结局。</returns>
    /// <exception cref="FileNotFoundException">要启动的程序不存在。</exception>
    /// <exception cref="InvalidOperationException">已经有一个进程在跑；或进程没能起来。</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">系统拒绝创建进程（权限、不是可执行文件等）。</exception>
    public async Task<GameProcessResult> RunAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        if (!File.Exists(request.ExecutablePath))
        {
            throw new FileNotFoundException($"找不到要启动的程序：{request.ExecutablePath}", request.ExecutablePath);
        }

        if (_process is not null)
        {
            throw new InvalidOperationException("已经有一个游戏进程在运行；先结束它再启动新的。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // 与 M4-a 给 JVM 的 -Dstdout.encoding=UTF-8 配对：两边一致，中文日志才不是乱码。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _killed = false;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动进程：{request.ExecutablePath}");

        _process = process;

        // Job Object：只保留 KILL_ON_JOB_CLOSE（Q3）。分配失败不阻止启动，但如实记下来。
        _job = JobObject.TryCreate(out var jobFailure);

        if (_job is not null && !_job.TryAssign(process, out var assignFailure))
        {
            jobFailure = assignFailure;
            _job.Dispose();
            _job = null;
        }

        var stopwatch = Stopwatch.StartNew();
        Queue<string> standardErrorTail = [];

        // 两个令牌各管一件事：
        //   · linked —— 观察**调用方**的取消（用户取消启动 / 结束游戏 / 启动器退出）；
        //   · pumps  —— 只管输出泵，由我们在"读干管道"之后自己取消。
        // 分开的理由：进程被杀之后，管道里可能还有它最后写下的几行 ——
        // 若把调用方的取消直接连到输出泵上，这几行（往往正是崩溃原因）会跟着一起被丢掉。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var pumps = new CancellationTokenSource();
        var standardOutputPump = PumpAsync(process.StandardOutput, GameOutputChannel.StandardOutput, request, standardErrorTail, pumps.Token);
        var standardErrorPump = PumpAsync(process.StandardError, GameOutputChannel.StandardError, request, standardErrorTail, pumps.Token);
        var flushLoop = FlushLoopAsync(pumps.Token);

        var wasKilled = false;

        // 进程与输出泵都已就位：把 pid 报出去（编排层据此把会话切到"正在运行"）。
        Started?.Invoke(this, process.Id);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消可能恰好发生在进程已经退出之后（竞态）：那就按正常退出处理，别说成"被结束"。
            if (!process.HasExited)
            {
                wasKilled = true;
                _killed = true;
                await KillAndWaitAsync(process).ConfigureAwait(false);
            }
        }

        // Kill() 是另一条结束路径（用户直接按「结束游戏」）：此时进程是"自己退出"的，
        // 但结论必须说清楚是**被启动器结束**，否则诊断会误导成"游戏异常退出（退出码 1）"。
        wasKilled |= _killed;

        try
        {
            // 让输出泵把管道读干：读到 EOF（而不是"进程退出了就停"），否则最后几行 —— 崩溃原因 —— 会丢。
            // 有子进程继承了管道写端时 EOF 不会来，所以给个上限。
            var drained = Task.WhenAll(standardOutputPump, standardErrorPump);
            await Task.WhenAny(drained, Task.Delay(OutputDrainTimeout, CancellationToken.None)).ConfigureAwait(false);
            await pumps.CancelAsync().ConfigureAwait(false);

            try
            {
                await drained.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 上面的上限到了：放弃剩余输出，继续收尾。
            }

            await flushLoop.ConfigureAwait(false);
            stopwatch.Stop();

            EmitPending(isFinal: true);

            var exitCode = process.HasExited ? process.ExitCode : -1;
            var tail = BuildStandardErrorTail(standardErrorTail);
            var diagnosis = GameProcessResult.BuildDiagnosis(exitCode, wasKilled, tail);
            var result = new GameProcessResult(
                ExitCode: exitCode,
                Duration: stopwatch.Elapsed,
                WasKilled: wasKilled,
                JobObjectFailure: jobFailure,
                Diagnosis: diagnosis,
                StandardErrorTail: tail);

            WriteExitEvent(request, result);
            return result;
        }
        finally
        {
            Cleanup(process);
        }
    }

    /// <summary>请求结束游戏（用户按了「结束游戏」或启动器正在退出）。进程树由 Job Object 兜底回收。</summary>
    public void Kill()
    {
        _killed = true;
        var process = _process;

        if (process is not null)
        {
            KillTree(process);
        }
    }

    /// <summary>
    /// 结束本次会话并回收进程树（启动器退出时调它，或由 <c>using</c> 兜底）。
    /// <para>
    /// 主路径**只关 Job 句柄**：<c>KILL_ON_JOB_CLOSE</c> 会把整棵树（含游戏自己拉起的子进程）收掉 ——
    /// 这正是"关掉启动器，游戏跟着退出"的机制本身。Job 不可用时退回到直接杀进程树
    /// （那种情况下孙进程不保证被回收，诊断里会说明）。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _killed = true;

        var job = _job;
        _job = null;

        if (job is null)
        {
            Kill();
            return;
        }

        job.Dispose();
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        KillTree(process);

        try
        {
            using var timeout = new CancellationTokenSource(KillTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 进程杀不掉（内核态卡住的驱动之类）：继续收尾，退出码按 -1 报。
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
            or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // 已经退出 / 已释放 / 平台不支持 / 权限不足：Job Object 仍会兜底，这里不抛。
        }
    }

    /// <summary>把一路输出读到流结束，按批次上报，并维护 stderr 环形缓冲。</summary>
    private async Task PumpAsync(
        StreamReader reader,
        GameOutputChannel stream,
        GameLaunchRequest request,
        Queue<string> standardErrorTail,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;

            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                // 管道被强制关闭（进程被杀）：不是故障。
                return;
            }

            if (line is null)
            {
                // 流结束 —— 这才是正确的退出条件。
                return;
            }

            var text = LaunchRedaction.RedactText(line, request.Secrets);
            var flood = false;

            lock (_gate)
            {
                if (stream == GameOutputChannel.StandardError)
                {
                    standardErrorTail.Enqueue(text);

                    while (standardErrorTail.Count > StandardErrorTailLines)
                    {
                        standardErrorTail.Dequeue();
                    }
                }

                _pending.Add(new GameOutputLine(stream, text));
                flood = _pending.Count >= MaximumBatchLines;
            }

            if (flood)
            {
                EmitPending(isFinal: false);
            }
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(FlushInterval, cancellationToken).ConfigureAwait(false);
                EmitPending(isFinal: false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收尾路径。
        }
    }

    private void EmitPending(bool isFinal)
    {
        List<GameOutputLine> batch;

        lock (_gate)
        {
            if (_pending.Count == 0 && !isFinal)
            {
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
        }

        OutputReceived?.Invoke(this, new GameOutputBatch(batch, isFinal));
    }

    private static string[] BuildStandardErrorTail(Queue<string> standardErrorTail)
    {
        var text = string.Join('\n', standardErrorTail);

        if (text.Length > StandardErrorTailCharacters)
        {
            text = text[^StandardErrorTailCharacters..];
        }

        return text.Length == 0 ? [] : text.Split('\n');
    }

    /// <summary>把结论落盘（C-14）。参数是**值**而不是拼好的句子：文案由界面层查资源表。</summary>
    private void WriteExitEvent(GameLaunchRequest request, GameProcessResult result)
    {
        var seconds = ((long)Math.Round(result.Duration.TotalSeconds, MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture);

        try
        {
            if (result.WasKilled)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Information,
                    DiagnosticsSources.LaunchCore,
                    "游戏已被启动器结束（运行 {0} 秒）",
                    [seconds],
                    request.InstanceName);

                return;
            }

            if (result.ExitCode == 0)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Success,
                    DiagnosticsSources.LaunchCore,
                    "游戏已退出（退出码 {0}，运行 {1} 秒）",
                    ["0", seconds],
                    request.InstanceName);

                return;
            }

            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.LaunchCore,
                "游戏异常退出（退出码 {0}，运行 {1} 秒）{2}",
                [result.ExitCode.ToString(CultureInfo.InvariantCulture), seconds, result.Diagnosis ?? string.Empty],
                request.InstanceName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 磁盘满 / 没权限：退出流程的可用性优先于"记录完整"（与 CrashHandler 同一条原则）。
        }
    }

    private void Cleanup(Process process)
    {
        _process = null;
        _job?.Dispose();
        _job = null;
        process.Dispose();
        _killed = false;
    }
}
