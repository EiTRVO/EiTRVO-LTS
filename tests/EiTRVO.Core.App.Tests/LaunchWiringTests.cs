using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M4-d：p02「启动游戏」与 p14「正在启动」的接线。
/// <para>
/// 内核侧（阶段推进、失败拦截、会话记账）由 <c>GameLauncherTests</c> 用替身钉住；
/// 这里要验的是**界面有没有把它接对**：按钮是否真的调启动编排、失败原因是否露出来、
/// 取消是否真的中止会话、阶段与进度是不是照着快照显示的。
/// </para>
/// </summary>
[TestClass]
public sealed class LaunchWiringTests
{
    [TestMethod]
    public void StartGameLaunchesTheActiveInstanceAndOpensTheLaunchingPage()
    {
        var launcher = new FakeGameLauncher();
        var navigation = new FakeNavigationService();
        var page = TestViewModels.Instances(launcher: launcher, navigation: navigation);

        page.StartGameCommand.Execute(null);

        Assert.AreEqual(1, launcher.Launched.Count);
        Assert.AreEqual("1.20.4-Forge", launcher.Launched[0], "启动的是**当前实例**。");
        CollectionAssert.Contains(navigation.Visited.ToList(), PageId.Launching, "启动后应当切到 p14。");
    }

    [TestMethod]
    public void WithoutAnInstanceTheLaunchButtonIsDisabledAndDoesNothing()
    {
        var launcher = new FakeGameLauncher();
        var instances = new FakeInstanceCatalog { ActiveInstanceName = string.Empty };
        var page = TestViewModels.Instances(instances: instances, launcher: launcher);

        Assert.IsFalse(page.CanStartGame, "零实例是首次运行的正常状态：按钮应当不可点。");

        page.StartGameCommand.Execute(null);

        Assert.AreEqual(0, launcher.Launched.Count);
    }

    [TestMethod]
    public void TheLaunchingPageProjectsTheSessionSnapshot()
    {
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with
            {
                State = LaunchSessionState.Preparing,
                InstanceName = "1.20.4-Forge",
                VersionId = "1.20.4",
                Detail = TextTemplate.Of("正在校验本地文件…"),
                Stages = Stages(completed: 1, running: 2),
                Percent = 25,
                JavaRuntime = "Eclipse Temurin 17.0.9",
                MemoryText = TextTemplate.Of("{0} MB", "4096"),
                VerifiedFileCount = 0,
            },
        };

        var page = TestViewModels.Launching(launcher: launcher);

        Assert.AreEqual("2. 校验本地文件（第 2 阶段，共 4 阶段）", page.CurrentStageText);
        Assert.AreEqual(25, page.Percent);
        Assert.AreEqual("正在校验本地文件…", page.CurrentFileText);
        Assert.AreEqual("Eclipse Temurin 17.0.9", page.JavaRuntimeText);
        Assert.IsTrue(page.IsJavaVerified);
        Assert.AreEqual("4096 MB", page.MemoryText);
        Assert.IsTrue(page.IsBusy, "准备中也算忙：这时「取消启动」必须可点。");
        Assert.IsFalse(page.HasFailure);

        // 启动不下载文件：文件进度报的是**已校验的文件数**，速度如实说"不适用"。
        Assert.AreEqual("尚未开始", page.FileProgressText);
        Assert.AreEqual("不适用（启动不下载文件）", page.SpeedText);
    }

    [TestMethod]
    public void TheIdleSessionShowsPlaceholdersInsteadOfFakeNumbers()
    {
        var page = TestViewModels.Launching();

        Assert.AreEqual("尚未开始", page.CurrentStageText);
        Assert.AreEqual(0, page.Percent);
        Assert.AreEqual("尚未选择", page.JavaRuntimeText);
        Assert.IsFalse(page.IsJavaVerified);
        Assert.IsFalse(page.IsBusy, "还没启动时「取消启动」不该可点。");
        Assert.AreEqual(4, page.Stages.Count, "四个阶段始终画出来（都停在待执行）。");
        Assert.IsTrue(page.Stages.All(static stage => stage.State == LaunchStageState.Pending));
    }

    [TestMethod]
    public void TheChangedEventRefreshesTheLaunchingPage()
    {
        var launcher = new FakeGameLauncher();
        var page = TestViewModels.Launching(launcher: launcher);

        Assert.AreEqual("尚未开始", page.CurrentStageText);

        launcher.Snapshot = LaunchSnapshot.Idle with
        {
            State = LaunchSessionState.Running,
            Stages = Stages(completed: 3, running: 4),
            Percent = 75,
            JavaRuntime = "Eclipse Temurin 17.0.9",
        };
        launcher.RaiseChanged();

        Assert.AreEqual("4. 构建参数并启动进程（第 4 阶段，共 4 阶段）", page.CurrentStageText);
        Assert.AreEqual(75, page.Percent);
    }

    [TestMethod]
    public void AFinishedSessionClearsTheLaunchInfoInsteadOfLeavingTheOldExitState()
    {
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with
            {
                State = LaunchSessionState.Completed,
                Detail = TextTemplate.Of("游戏已退出（退出码 {0}）", "0"),
                Stages = Stages(completed: 4, running: 0),
                Percent = 100,
                JavaRuntime = "Eclipse Temurin 17.0.9",
                VerifiedFileCount = 65,
                Result = new GameProcessResult(0, TimeSpan.FromMinutes(12), false, null, null, []),
            },
        };

        var page = TestViewModels.Launching(launcher: launcher);

        // p14 是「正在启动」，不是「启动结果」：进程退出后这一页回到空态，
        // 退出码与时长已经写进实例元数据与事件日志。
        Assert.AreEqual("尚未开始", page.CurrentStageText);
        Assert.AreEqual(0, page.Percent);
        Assert.IsFalse(page.IsBusy);
        Assert.IsTrue(page.Stages.All(static stage => stage.State == LaunchStageState.Pending));
        Assert.IsNull(page.Failure);
    }

    [TestMethod]
    public void ACancelledPreparationStillExplainsWhatHappened()
    {
        // 准备阶段被取消的 Completed 没有 Result（进程根本没起来过）：不能当成"游戏退出"清掉，
        // 否则用户点完取消什么提示都看不到。
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with
            {
                State = LaunchSessionState.Completed,
                Detail = TextTemplate.Of("启动已取消"),
                Stages = Stages(completed: 2, running: 0),
                Percent = 50,
            },
        };

        var page = TestViewModels.Launching(launcher: launcher);

        Assert.AreEqual("已完成", page.CurrentStageText);
        Assert.AreEqual(50, page.Percent);
    }

    [TestMethod]
    public void AFailureIsSurfacedWithItsReason()
    {
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with
            {
                State = LaunchSessionState.Failed,
                Detail = TextTemplate.Of("这个实例的文件不完整：缺 {0} 个（例如 {1}）。请先在「下载与队列」里安装这个版本。", "1", "客户端 JAR"),
                Failure = TextTemplate.Of("这个实例的文件不完整：缺 {0} 个（例如 {1}）。请先在「下载与队列」里安装这个版本。", "1", "客户端 JAR"),
                Stages = Stages(completed: 1, running: 0, failed: 2),
            },
        };

        var page = TestViewModels.Launching(launcher: launcher);

        Assert.IsTrue(page.HasFailure, "失败原因必须露出来（p14 上的红色提示条读它）。");
        StringAssert.Contains(page.Failure!, "文件不完整");
        Assert.AreEqual("已停止", page.CurrentStageText);
        Assert.IsFalse(page.IsBusy, "失败之后不该还提供「取消启动」。");
        Assert.AreEqual(LaunchStageState.Failed, page.Stages.Single(stage => stage.Order == 2).State);
    }

    [TestMethod]
    public void AnAbnormalExitIsSurfacedAfterTheLaunchInfoIsCleared()
    {
        // 用户反馈（2026-10-21）：游戏起来后崩掉，Core 什么都不说 —— p14 清空之后连退出码都看不到。
        // Neo 有「游戏异常退出」对话框；这里用 p14 的红色提示条承载同一件事。
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with
            {
                State = LaunchSessionState.Completed,
                Stages = Stages(completed: 4),
                Percent = 100,
                JavaRuntime = "Oracle 1.8.0_271",
                Result = new GameProcessResult(
                    -1,
                    TimeSpan.FromSeconds(4),
                    false,
                    null,
                    "游戏异常退出（退出码 -1）。",
                    ["[LWJGL] Failed to locate library: lwjgl.dll", "boom"]),
            },
        };

        var page = TestViewModels.Launching(launcher: launcher);

        // 启动信息仍然清空（这一页不是「启动结果」）……
        Assert.AreEqual(0, page.Percent);
        Assert.IsTrue(page.Stages.All(static stage => stage.State == LaunchStageState.Pending));

        // ……但"游戏崩了"必须留下：退出码 + 标准错误尾部都要能看见。
        Assert.IsTrue(page.HasFailure, "异常退出必须留下可读的提示。");
        StringAssert.Contains(page.Failure!, "游戏异常退出（退出码 -1");
        StringAssert.Contains(page.Failure!, "标准错误尾部");
        StringAssert.Contains(page.Failure!, "lwjgl.dll");
        StringAssert.Contains(page.Failure!, "boom");
    }

    [TestMethod]
    public void ANormalExitDoesNotLeaveAFailureBanner()
    {
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with
            {
                State = LaunchSessionState.Completed,
                Stages = Stages(completed: 4),
                Percent = 100,
                Result = new GameProcessResult(0, TimeSpan.FromMinutes(12), false, null, null, []),
            },
        };

        var page = TestViewModels.Launching(launcher: launcher);

        Assert.AreEqual(0, page.Percent);
        Assert.IsFalse(page.HasFailure, "正常退出不该留下红色提示条。");
    }

    [TestMethod]
    public void StartingAgainClearsTheAbnormalExitMessage()
    {
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with
            {
                State = LaunchSessionState.Completed,
                Result = new GameProcessResult(-1, TimeSpan.FromSeconds(4), false, null, null, ["boom"]),
            },
        };

        var page = TestViewModels.Launching(launcher: launcher);
        Assert.IsTrue(page.HasFailure);

        // 新的一次启动开始：上一次的异常提示属于上一次会话，不能一直挂着。
        launcher.Snapshot = LaunchSnapshot.Idle with
        {
            State = LaunchSessionState.Preparing,
            Stages = Stages(completed: 0, running: 1),
        };
        launcher.RaiseChanged();

        Assert.IsFalse(page.HasFailure, "重新启动后旧的异常提示应当消失。");
    }

    [TestMethod]
    public void CancelStopsTheSession()
    {
        var launcher = new FakeGameLauncher
        {
            Snapshot = LaunchSnapshot.Idle with { State = LaunchSessionState.Running },
        };

        var page = TestViewModels.Launching(launcher: launcher);

        page.CancelCommand.Execute(null);

        Assert.AreEqual(1, launcher.StopCount, "「取消启动 / 结束游戏」必须真的中止会话。");
    }

    /// <summary>摆一张阶段表：前 <paramref name="completed"/> 个已完成，指定序号在跑或失败，其余待执行。</summary>
    private static IReadOnlyList<LaunchStage> Stages(int completed, int running = 0, int failed = 0) =>
    [
        .. Enumerable.Range(1, LaunchStagePlan.Count).Select(order => new LaunchStage(
            order,
            LaunchStagePlan.Title(order),
            order <= completed ? TextTemplate.Of("已完成") : TextTemplate.Of("等待中"),
            order == failed
                ? LaunchStageState.Failed
                : order == running
                    ? LaunchStageState.Running
                    : order <= completed
                        ? LaunchStageState.Completed
                        : LaunchStageState.Pending,
            IsActive: order == running)),
    ];
}
