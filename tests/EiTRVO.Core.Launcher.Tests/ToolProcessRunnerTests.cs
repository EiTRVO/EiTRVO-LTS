using EiTRVO.Core.Launcher.Installer;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-g：跑外部工具（官方安装器）的那一层。这里**用真进程** ——
/// 它的风险全在真实进程的边界上（编码、非零退出、超时终止）。
/// 用 <c>cmd.exe</c> 当"被跑的工具"，不依赖机器上装了什么。
/// </summary>
[TestClass]
public sealed class ToolProcessRunnerTests
{
    private static string CommandShell => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [TestMethod]
    public async Task TheOutputAndExitCodeComeBackFromARealProcess()
    {
        var result = await new ToolProcessRunner().RunAsync(
            CommandShell,
            ["/c", "echo hello-installer"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(30));

        Assert.IsTrue(result.Succeeded, result.OutputTail);
        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.OutputTail, "hello-installer");
        Assert.IsFalse(result.TimedOut);
    }

    [TestMethod]
    public async Task ANonZeroExitCodeIsReportedWithWhateverTheToolSaid()
    {
        var result = await new ToolProcessRunner().RunAsync(
            CommandShell,
            ["/c", "echo These libraries failed to download. 1>&2 & exit /b 1"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(30));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, result.ExitCode);
        StringAssert.Contains(result.OutputTail, "These libraries failed to download");
    }

    [TestMethod]
    public async Task AToolThatNeverFinishesIsKilledAndReportedAsATimeout()
    {
        var result = await new ToolProcessRunner().RunAsync(
            CommandShell,
            ["/c", "ping -n 60 127.0.0.1"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(2));

        Assert.IsTrue(result.TimedOut, "到点必须终止并如实报超时，而不是一直挂着。");
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task CancellationEndsTheToolAndThrowsInsteadOfLookingLikeATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var run = new ToolProcessRunner().RunAsync(
            CommandShell,
            ["/c", "ping -n 60 127.0.0.1"],
            Environment.CurrentDirectory,
            TimeSpan.FromMinutes(5),
            cancellationToken: cancellation.Token);

        await Task.Delay(500);
        await cancellation.CancelAsync();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => run);
    }

    [TestMethod]
    public async Task AMissingExecutableSaysWhichFileWasNotFound()
    {
        var result = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => new ToolProcessRunner().RunAsync(
                Path.Combine(Environment.CurrentDirectory, "没有这个程序.exe"),
                [],
                Environment.CurrentDirectory,
                TimeSpan.FromSeconds(5)));

        StringAssert.Contains(result.Message, "没有这个程序.exe");
    }
}
