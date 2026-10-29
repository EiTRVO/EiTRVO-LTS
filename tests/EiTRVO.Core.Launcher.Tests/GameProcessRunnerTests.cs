using System.Diagnostics;
using System.Globalization;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-c：游戏进程的生命周期（C-12 进程树回收、C-13 输出节流、C-14 退出码诊断）。
/// <para>
/// 用**真的进程**测（<c>cmd.exe</c> / <c>powershell.exe</c> 当"假的可执行文件"）：
/// 这一层的全部风险都在真实进程的边界上 —— Job Object 到底有没有回收整棵树、管道里的最后几行会不会丢、
/// 进程被杀之后退出码读出来是什么。用假对象测这些等于什么都没测。
/// </para>
/// <para>
/// 因此这些用例**只在 Windows 上成立**（产品本身也只支持 win-x64；本仓库的 DPAPI 测试同理）。
/// </para>
/// </summary>
[TestClass]
public sealed class GameProcessRunnerTests : LaunchTestData
{
    /// <summary>一轮"等条件成立"的最长时间。</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 起真进程时用的宽松上限：**整套测试是方法级并行**的，机器被几百条用例压着时，
    /// 起一个 powershell 再写一个文件可能远慢于 10 秒 —— 那是机器慢，不是功能坏。
    /// 实测：这一条在全量跑里偶发红（12 次全量里 1 次），单独跑 12 次全绿，
    /// 因此把"等进程做点什么"的上限放宽，而不是把它变成一条会诈和的断言。
    /// </summary>
    private static readonly TimeSpan ProcessWait = TimeSpan.FromSeconds(45);

    [TestMethod]
    public async Task AProcessThatExitsZeroIsRecordedAsASuccessfulSession()
    {
        var log = new RecordingDiagnosticsLog();
        using var runner = CreateRunner(log);

        var result = await runner.RunAsync(Command("exit 0"), CancellationToken.None);

        Assert.AreEqual(0, result.ExitCode, "Job Object 失败原因：" + (result.JobObjectFailure ?? "（无）"));
        Assert.IsNull(result.Diagnosis, "正常退出不需要诊断结论。");
        Assert.IsFalse(result.WasKilled);
        Assert.IsTrue(result.ProcessTreeGuaranteed, "Job Object 应当生效：" + result.JobObjectFailure);
        Assert.IsTrue(result.Duration > TimeSpan.Zero);

        Assert.AreEqual(1, log.Entries.Count, "一次会话落一条事件。");
        Assert.AreEqual(LauncherEventLevel.Success, log.Entries[0].Level);
        Assert.AreEqual(DiagnosticsSources.LaunchCore, log.Entries[0].Source);
        Assert.AreEqual("游戏已退出（退出码 {0}，运行 {1} 秒）", log.Entries[0].MessageKey);
        Assert.AreEqual("0", log.Entries[0].Parameters[0]);
    }

    [TestMethod]
    public async Task ANonZeroExitCarriesTheStandardErrorTailAndAReadableConclusion()
    {
        var log = new RecordingDiagnosticsLog();
        using var runner = CreateRunner(log);

        var result = await runner.RunAsync(Command("echo boom 1>&2 & exit 7"), CancellationToken.None);

        Assert.AreEqual(7, result.ExitCode);
        Assert.IsNotNull(result.Diagnosis);
        StringAssert.Contains(result.Diagnosis, "退出码 7");
        StringAssert.Contains(result.Diagnosis, "boom", "标准错误的尾部要进诊断（崩溃堆栈通常在那里）。");
        CollectionAssert.Contains(
            result.StandardErrorTail.Select(line => line.Trim()).ToList(),
            "boom");

        Assert.AreEqual(1, log.Entries.Count);
        Assert.AreEqual(LauncherEventLevel.Warning, log.Entries[0].Level);
        Assert.AreEqual("游戏异常退出（退出码 {0}，运行 {1} 秒）{2}", log.Entries[0].MessageKey);
        Assert.AreEqual("7", log.Entries[0].Parameters[0]);
    }

    [TestMethod]
    public async Task ExitCodeElevenIsNotBlamedOnBrokenVersionFiles()
    {
        // Neo 用 exitCodeStr.EndsWith("1") 判断"大概是版本文件不完整"，
        // 于是退出码 11 / 21 / 31 都会被扣上同一顶帽子（用户会去重装一个没坏的文件）。
        using var runner = CreateRunner(new RecordingDiagnosticsLog());

        var result = await runner.RunAsync(Command("exit 11"), CancellationToken.None);

        Assert.IsNotNull(result.Diagnosis);
        StringAssert.Contains(result.Diagnosis, "退出码 11");
        Assert.IsFalse(
            result.Diagnosis.Contains("版本文件不完整", StringComparison.Ordinal),
            "11 不是 1：按数值精确比较，不要按字符串后缀猜。");
    }

    [TestMethod]
    public void KnownWindowsExitCodesGetAUsefulHint()
    {
        Assert.IsNull(GameProcessResult.BuildDiagnosis(0, false, ["noise"]), "正常退出不该有结论。");
        StringAssert.Contains(GameProcessResult.BuildDiagnosis(1, false, []), "内存不足");
        StringAssert.Contains(GameProcessResult.BuildDiagnosis(-1073741819, false, []), "访问冲突");
        StringAssert.Contains(GameProcessResult.BuildDiagnosis(-1073741801, false, []), "内存不足");
        StringAssert.Contains(GameProcessResult.BuildDiagnosis(-1073741510, false, []), "中断");
        StringAssert.Contains(GameProcessResult.BuildDiagnosis(99, false, ["tail"]), "tail");

        StringAssert.Contains(
            GameProcessResult.BuildDiagnosis(1, true, []),
            "已被启动器结束",
            "被我们结束的会话不该说成「游戏自己崩了」。");
    }

    [TestMethod]
    public void OnlyLongEnoughCleanSessionsCountAsPlayTime()
    {
        var session = new GameProcessResult(0, TimeSpan.FromMinutes(3), false, null, null, []);

        Assert.IsTrue(session.CountsAsPlaySession);
        Assert.IsFalse((session with { Duration = TimeSpan.FromSeconds(5) }).CountsAsPlaySession,
            "启动即崩的会话不该污染累计游玩时长（Neo 的 30 秒下限）。");
        Assert.IsFalse((session with { ExitCode = 1 }).CountsAsPlaySession);
        Assert.IsFalse((session with { WasKilled = true }).CountsAsPlaySession);
    }

    [TestMethod]
    public async Task OutputIsDeliveredInThrottledBatchesWithAFinalFlush()
    {
        var collector = new Collector();
        using var runner = CreateRunner(new RecordingDiagnosticsLog());
        collector.Attach(runner);

        await runner.RunAsync(Command("for /l %i in (1,1,50) do @echo line-%i"), CancellationToken.None);

        var lines = collector.Lines.Where(line => line.Text.StartsWith("line-", StringComparison.Ordinal)).ToList();

        Assert.AreEqual(50, lines.Count, "一行都不能丢。");
        Assert.IsTrue(collector.Batches.Count > 0);
        Assert.IsTrue(
            collector.Batches.Count < 25,
            $"50 行应当成批上报（C-13：逐行回调会把 p14 的表格拖卡），实际 {collector.Batches.Count} 批。");
        Assert.IsTrue(collector.Batches[^1].IsFinal, "最后一批必须标记为「结束」（终态强制补报）。");
        Assert.IsTrue(collector.Batches.All(batch => batch.IsFinal || batch.Lines.Count > 0), "空批次不该上报。");
    }

    [TestMethod]
    public async Task TheLastLinesSurviveAProcessThatExitsImmediately()
    {
        // 这是 Neo 的实际缺陷：它的读取循环是 while (!HasExited)，
        // 进程一退就停读，而缓冲区里最后那几行 —— 崩溃原因 —— 永远送不出去。
        var collector = new Collector();
        using var runner = CreateRunner(new RecordingDiagnosticsLog());
        collector.Attach(runner);

        var result = await runner.RunAsync(Command("echo first & echo LAST-LINE 1>&2 & exit 3"), CancellationToken.None);

        Assert.AreEqual(3, result.ExitCode);
        Assert.IsTrue(
            collector.Lines.Any(line => line.Text.Contains("LAST-LINE", StringComparison.Ordinal)),
            "进程退出前最后写的那一行必须送到订阅方（也要进 stderr 尾部）。");
        CollectionAssert.Contains(result.StandardErrorTail.Select(line => line.Trim()).ToList(), "LAST-LINE");
    }

    [TestMethod]
    public async Task TheBurstLimitCapsHowMuchOneBatchCanCarry()
    {
        const int cap = 5;

        var collector = new Collector();
        using var runner = CreateRunner(new RecordingDiagnosticsLog(), maximumBatchLines: cap);
        collector.Attach(runner);

        await runner.RunAsync(Command("for /l %i in (1,1,50) do @echo row-%i"), CancellationToken.None);

        Assert.IsTrue(collector.Batches.Count >= 10, $"一批最多 {cap} 行，50 行至少要 10 批。");
        Assert.IsTrue(
            collector.Batches.All(batch => batch.Lines.Count <= cap),
            "一次洪泛不能把一整段输出塞进一个批次（否则内存与界面一起被撑爆）。");
    }

    [TestMethod]
    public async Task TheWorkingDirectoryIsTheInstanceDirectoryNotTheLauncherOwn()
    {
        // 传错工作目录的后果是"存档写进了启动器的目录"，而且不会有任何报错。
        var directory = NewTempDirectory("eitrvo-gameproc-cwd-");

        try
        {
            var collector = new Collector();
            using var runner = CreateRunner(new RecordingDiagnosticsLog());
            collector.Attach(runner);

            await runner.RunAsync(Command("cd", workingDirectory: directory), CancellationToken.None);

            var printed = collector.Lines.Select(line => line.Text).FirstOrDefault(text => text.Length > 0);

            Assert.IsNotNull(printed, "`cd` 应当打印工作目录。");
            Assert.AreEqual(
                Path.TrimEndingDirectorySeparator(directory),
                Path.TrimEndingDirectorySeparator(printed!),
                "`cd` 打印的必须是实例目录。");
        }
        finally
        {
            await TryDeleteDirectoryAsync(directory);
        }
    }

    [TestMethod]
    public async Task CancellingEndsTheProcessAndReportsItAsKilled()
    {
        using var runner = CreateRunner(new RecordingDiagnosticsLog());
        using var cancellation = new CancellationTokenSource();

        var run = runner.RunAsync(Command("ping -n 120 127.0.0.1"), cancellation.Token);

        Assert.IsTrue(await WaitUntilAsync(() => runner.ProcessId is not null, Wait), "进程应当起来。");
        var processId = runner.ProcessId!.Value;
        Assert.IsTrue(IsAlive(processId));

        await cancellation.CancelAsync();

        var result = await run;

        Assert.IsTrue(result.WasKilled);
        Assert.IsNotNull(result.Diagnosis);
        StringAssert.Contains(result.Diagnosis, "已被启动器结束");
        Assert.IsTrue(await WaitUntilAsync(() => !IsAlive(processId), Wait), "取消之后进程必须真的结束。");
    }

    [TestMethod]
    public async Task KillIsReportedAsEndedByTheLauncherToo()
    {
        // 用户按「结束游戏」走的是 Kill()，而不是取消令牌 ——
        // 结论同样必须是"被启动器结束"，否则会误导成"游戏异常退出（退出码 {0}，运行 {1} 秒）{2}"。
        using var runner = CreateRunner(new RecordingDiagnosticsLog());

        var run = runner.RunAsync(Command("ping -n 120 127.0.0.1"), CancellationToken.None);

        Assert.IsTrue(await WaitUntilAsync(() => runner.ProcessId is not null, Wait));
        var processId = runner.ProcessId!.Value;

        runner.Kill();

        var result = await run;

        Assert.IsTrue(result.WasKilled);
        StringAssert.Contains(result.Diagnosis ?? string.Empty, "已被启动器结束");
        Assert.IsTrue(await WaitUntilAsync(() => !IsAlive(processId), Wait));
    }

    [TestMethod]
    public async Task ClosingTheJobHandleReclaimsTheWholeProcessTree()
    {
        // "关掉启动器 → 游戏进程被一并回收"是 M4 的手工验收之一，这里把它变成自动化的。
        // 形状刻意选成"会话还在跑、而且已经有一个孙进程"：这能盖住"只杀直接子进程"的做法
        // （Neo 的 UI 层那套 289 行里有直接 Kill 子进程的逻辑，但孙进程会漏）。
        var directory = NewTempDirectory("eitrvo-gameproc-tree-");
        var pidFile = Path.Combine(directory, "grandchild.pid");

        try
        {
            var script =
                "$p = Start-Process -PassThru -WindowStyle Hidden -FilePath cmd -ArgumentList '/c','ping -n 120 127.0.0.1';"
                + " Set-Content -LiteralPath '" + pidFile + "' -Value $p.Id;"
                + " Start-Sleep -Seconds 120";

            var request = new GameLaunchRequest(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", script],
                directory);

            using var runner = CreateRunner(new RecordingDiagnosticsLog());

            var run = runner.RunAsync(request, CancellationToken.None);

            // 等到**能读出** pid 为止，而不是"文件存在"为止：Set-Content 会先建文件再写内容，
            // 只等 File.Exists 的话，读的那一刻可能正好撞上写进程还占着句柄 ——
            // 那是一个真实的竞态（实测偶发：IOException「文件正被另一进程使用」），
            // 修法是等真正需要的那件事成立，而不是加长等待时间。
            Assert.IsTrue(
                await WaitUntilAsync(() => TryReadPid(pidFile) is not null, ProcessWait),
                "孙进程的 pid 应当被写出来。");

            var grandchildId = TryReadPid(pidFile) ?? 0;
            var directChildId = runner.ProcessId ?? 0;

            Assert.AreNotEqual(0, directChildId);
            Assert.IsTrue(IsAlive(directChildId), "直接子进程应当还在跑。");
            Assert.IsTrue(IsAlive(grandchildId), "会话还在跑时，孙进程也应当还在跑（否则这条用例证明不了什么）。");

            // 只关 Job 句柄 —— 这正是"启动器退出"时操作系统替我们做的事。
            runner.Dispose();

            Assert.IsTrue(
                await WaitUntilAsync(() => !IsAlive(grandchildId), ProcessWait),
                "Job Object 关闭后，整棵进程树（含孙进程）都必须被回收。");
            Assert.IsTrue(await WaitUntilAsync(() => !IsAlive(directChildId), ProcessWait));

            var result = await run;

            Assert.IsTrue(result.WasKilled, "由启动器结束的会话要如实报告，而不是说成「游戏自己崩了」。");
        }
        finally
        {
            // 被杀掉的进程释放工作目录要一点点时间；临时目录删不掉不值得让用例红。
            await TryDeleteDirectoryAsync(directory);
        }
    }

    [TestMethod]
    public async Task OutputFromAChildThatOutlivesTheGameIsStillCaptured()
    {
        // 游戏会拉起子进程（驱动包装、helper），它们可能继承我们的管道并活得更久。这对读取有两个要求：
        //   1) **父进程退出 ≠ 输出结束**：子进程随后写的那一行仍然要收到 —— Neo 的读循环
        //      （`while (!HasExited)`）恰好在这里丢数据；
        //   2) 不能因此挂死：读干管道有 2 秒上限。
        // 这条用例刻意让子进程先睡 400ms 再写，从而**稳定复现**那个竞态（而不是靠运气）。
        var collector = new Collector();
        using var runner = CreateRunner(new RecordingDiagnosticsLog());
        collector.Attach(runner);

        var script =
            "Start-Process -NoNewWindow -FilePath cmd -ArgumentList '/c',"
            + "'ping -n 1 -w 400 127.0.0.1 >nul & echo CHILD-OUTLIVED 1>&2'";

        var request = new GameLaunchRequest(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", script],
            Path.GetTempPath());

        var elapsed = Stopwatch.StartNew();
        await runner.RunAsync(request, CancellationToken.None);
        elapsed.Stop();

        Assert.IsTrue(
            collector.Lines.Any(line => line.Text.Contains("CHILD-OUTLIVED", StringComparison.Ordinal)),
            "父进程退出之后，子进程写的那一行也必须收到（进程退出不等于输出结束）。");
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(10), "读干管道的上限必须生效，不能挂死。");
    }

    [TestMethod]
    public async Task OutputLinesAreRedactedWithTheLaunchSecrets()
    {
        // 游戏或模组可能把启动参数回显到日志里；C-15 的脱敏必须覆盖这条路。
        var collector = new Collector();
        using var runner = CreateRunner(new RecordingDiagnosticsLog());
        collector.Attach(runner);

        await runner.RunAsync(
            Command("echo " + LaunchTestData.TestAccessToken, secrets: [LaunchTestData.TestAccessToken]),
            CancellationToken.None);

        var text = string.Join('\n', collector.Lines.Select(line => line.Text));

        Assert.IsFalse(text.Contains(LaunchTestData.TestAccessToken, StringComparison.Ordinal), "输出里的令牌也要抹掉。");
        StringAssert.Contains(text, LaunchRedaction.Mask);
    }

    [TestMethod]
    public async Task AProcessThatCannotBeStartedIsRejectedLoudly()
    {
        using var runner = CreateRunner(new RecordingDiagnosticsLog());

        var request = Command("exit 0") with
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "eitrvo-does-not-exist-" + Guid.NewGuid().ToString("N") + ".exe"),
        };

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => runner.RunAsync(request, CancellationToken.None));
    }

    // ==================== 工具 ====================

    /// <summary>用 <c>cmd.exe</c> 当"假的可执行文件"。</summary>
    private static GameLaunchRequest Command(
        string script,
        string? workingDirectory = null,
        IReadOnlyList<string>? secrets = null) =>
        new(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/c", script],
            workingDirectory ?? Path.GetTempPath())
        {
            InstanceName = "probe",
            VersionId = "1.20.4",
            Secrets = secrets ?? [],
        };

    private static GameProcessRunner CreateRunner(
        RecordingDiagnosticsLog log,
        int maximumBatchLines = GameProcessRunner.DefaultMaximumBatchLines) =>
        new(log) { MaximumBatchLines = maximumBatchLines };

    private static string NewTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>读 pid 文件；文件还没写完（句柄被占）或内容还不是数字时返回 <see langword="null"/>。</summary>
    private static int? TryReadPid(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                ? pid
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>删临时目录；被杀掉的进程可能还占着它，重试几次，仍失败就算了（不在仓库里）。</summary>
    private static async Task TryDeleteDirectoryAsync(string directory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    /// <summary>收集输出：批次事件在后台线程触发，可能并发，所以自己加锁。</summary>
    private sealed class Collector
    {
        private readonly object _gate = new();

        public List<GameOutputLine> Lines { get; } = [];

        public List<GameOutputBatch> Batches { get; } = [];

        public void Attach(GameProcessRunner runner) =>
            runner.OutputReceived += (_, batch) =>
            {
                lock (_gate)
                {
                    Batches.Add(batch);
                    Lines.AddRange(batch.Lines);
                }
            };
    }
}
