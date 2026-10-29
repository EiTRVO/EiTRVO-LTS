using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth.Sample;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-d：界面刷新的节流（AGENTS.md §6）。
/// </summary>
/// <remarks>
/// 这一步要证明的是**三条语义**，缺一条都会变成另一种毛病：
/// ① 一个窗口内来 N 条通知只刷一次（否则 24px 行高的表会被刷爆）；
/// ② 窗口里的最后一次通知**必须**被执行（否则「下载完成」那一条永远显示不出来，
/// 界面停在 98%）；③ 刷新跑在 UI 线程上。
/// <para>
/// 用真实现 + 手动时钟 + 接住延时回调的调度替身来测：睡眠等真实时间既慢又不稳，
/// 而"延时回调立即执行"会让第 ① 条永远测不出来（合并窗口就失去了意义）。
/// </para>
/// </remarks>
[TestClass]
public sealed class UiRefreshThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 22, 0, 0, TimeSpan.FromHours(8));

    /// <summary>窗口内来一大串通知 → 只刷一次（首帧立即执行）。</summary>
    [TestMethod]
    public void AStormOfNotificationsInsideOneWindowProducesASingleRefresh()
    {
        var (throttle, dispatcher, clock) = NewThrottle();
        var runs = 0;

        for (var i = 0; i < 5000; i++)
        {
            throttle.Request(() => runs++);
        }

        Assert.AreEqual(1, runs, "首帧要立即执行，窗口内的其余 4999 次只应合并成「待刷新」。");
        Assert.AreEqual(1, dispatcher.Invokes, "刷新必须经 IDispatcherService 跑到 UI 线程上。");
        Assert.AreEqual(1, dispatcher.DelayedCount, "窗口内只该挂**一个**尾帧，而不是每次通知各挂一个。");

        // 窗口到点：尾帧送达，且只送这一次。
        clock.Advance(UiRefreshThrottle.DefaultInterval);
        Assert.AreEqual(1, dispatcher.FireDue());
        Assert.AreEqual(2, runs, "窗口末尾必须把最后那条通知补上。");
        Assert.AreEqual(2, dispatcher.Invokes);

        Assert.AreEqual(0, dispatcher.FireDue(), "尾帧只执行一次。");
        Assert.AreEqual(2, runs);
    }

    /// <summary>尾帧执行的是**最后一次**通知的动作（中间的都被合并掉了）。</summary>
    [TestMethod]
    public void TheTrailingRefreshRunsOnlyTheLatestRequest()
    {
        var (throttle, dispatcher, clock) = NewThrottle();
        var ran = new List<string>();

        throttle.Request(() => ran.Add("第一次"));
        throttle.Request(() => ran.Add("第二次"));
        throttle.Request(() => ran.Add("第三次"));

        Assert.AreEqual(1, ran.Count, "第一次落在窗口边缘，立即执行。");
        Assert.AreEqual("第一次", ran[0]);

        clock.Advance(UiRefreshThrottle.DefaultInterval);
        dispatcher.FireDue();

        CollectionAssert.AreEqual(new[] { "第一次", "第三次" }, ran, "窗口内的中间动作被合并掉，只留最后一次。");
    }

    /// <summary>持续来通知时，刷新次数不超过"时间 / 窗口"，而且一次都不少。</summary>
    [TestMethod]
    public void ContinuousNotificationsNeverExceedOneRefreshPerWindow()
    {
        var (throttle, dispatcher, clock) = NewThrottle();
        var runs = 0;
        var elapsed = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(10);

        for (var i = 0; i < 500; i++)
        {
            clock.Advance(step);
            elapsed += step;
            throttle.Request(() => runs++);

            // 让"消息循环"把到点的尾帧跑掉 —— 真实运行时由 DispatcherTimer 负责这件事。
            dispatcher.FireDue();
        }

        var windows = elapsed / UiRefreshThrottle.DefaultInterval;
        var upper = (int)Math.Ceiling(windows) + 1;

        Assert.IsTrue(
            runs <= upper,
            $"5000ms 内刷了 {runs} 次，超过了上限 {upper} 次（{UiRefreshThrottle.DefaultInterval.TotalMilliseconds}ms 一次）。");
        Assert.IsTrue(runs >= windows, $"只刷了 {runs} 次 —— 尾帧丢了（应当接近 {windows} 次）。");
    }

    /// <summary>p06：队列按 200ms 报进度，页面不能每来一条就整表重建。</summary>
    [TestMethod]
    public void TheDownloadsPageDoesNotRefreshForEveryQueueNotification()
    {
        var clock = new ManualClock(Start);
        var dispatcher = new ManualDispatcherService(() => clock.Now);
        var downloads = new FakeDownloadCatalog();
        var page = new DownloadsViewModel(
            downloads,
            new SampleEventLog(),
            new UiRefreshThrottle(dispatcher, clock));

        Assert.AreEqual(1, downloads.ProgressReads, "构造时读一次（进页面那一刻的快照）。");

        for (var i = 0; i < 5000; i++)
        {
            downloads.RaiseChanged();
        }

        Assert.AreEqual(2, downloads.ProgressReads, "5000 次变更里只有第一次落在窗口边缘，其余应被合并。");

        // 窗口内最后一次变更带来的数据**必须**送出去，否则页面永远停在旧进度上。
        downloads.Progress = downloads.Progress with { Percent = 42 };
        clock.Advance(UiRefreshThrottle.DefaultInterval);
        Assert.AreEqual(1, dispatcher.FireDue());

        Assert.AreEqual(3, downloads.ProgressReads);
        Assert.AreEqual(42, page.Progress.Percent, "尾帧送达后页面读到的是最新进度。");
    }

    /// <summary>页头徽标同样走节流（它与 p06 是**两个**订阅者，各自一份实例）。</summary>
    [TestMethod]
    public void TheShellBadgeRefreshesAtTheSameBoundedRate()
    {
        var clock = new ManualClock(Start);
        var dispatcher = new ManualDispatcherService(() => clock.Now);
        var downloads = new FakeDownloadCatalog();
        var shell = new ShellViewModel(
            new FakeNavigationService(),
            new SampleAccountCatalog(),
            downloads,
            Loc.Service,
            new UiRefreshThrottle(dispatcher, clock),
            new FakeHelpWindowService(), new FakeEventSearchWindowService());

        // 只数 IsBusy：别的通知（例如别处切了语言）不该影响这条断言。
        var busyNotifications = 0;
        shell.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ShellViewModel.IsBusy), StringComparison.Ordinal))
            {
                busyNotifications++;
            }
        };

        for (var i = 0; i < 5000; i++)
        {
            downloads.RaiseChanged();
        }

        Assert.AreEqual(1, busyNotifications, "5000 条变更只该让页头重算一次忙碌态。");

        downloads.EnqueueInstall(new InstallRequest("测试实例", "1.20.4", LoaderKind.Vanilla, null));
        clock.Advance(UiRefreshThrottle.DefaultInterval);
        dispatcher.FireDue();

        Assert.IsTrue(shell.IsBusy, "尾帧送达后页头才知道有任务在跑（扫光不能等下一次变更）。");
    }

    /// <summary>p14：会话在后台变 —— 快照读取次数同样有上限。</summary>
    [TestMethod]
    public void TheLaunchingPageDoesNotRereadTheSnapshotForEverySessionChange()
    {
        var clock = new ManualClock(Start);
        var dispatcher = new ManualDispatcherService(() => clock.Now);
        var launcher = new FakeGameLauncher();
        var page = new LaunchingViewModel(
            launcher,
            new SampleInstanceCatalog(),
            new UiRefreshThrottle(dispatcher, clock));

        Assert.AreEqual(1, launcher.SnapshotReads, "构造时读一次。");

        for (var i = 0; i < 1000; i++)
        {
            launcher.RaiseChanged();
        }

        Assert.AreEqual(2, launcher.SnapshotReads, "窗口内的 1000 次变更只让页面重读了一次快照。");

        // 尾帧同样要送达：会话进入终态那一次变更往往就是"最后一条"。
        launcher.Snapshot = launcher.Snapshot with { Percent = 66 };
        clock.Advance(UiRefreshThrottle.DefaultInterval);
        dispatcher.FireDue();

        Assert.AreEqual(3, launcher.SnapshotReads);
        Assert.AreEqual(66d, page.Percent);
    }

    private static (UiRefreshThrottle Throttle, ManualDispatcherService Dispatcher, ManualClock Clock) NewThrottle()
    {
        var clock = new ManualClock(Start);
        var dispatcher = new ManualDispatcherService(() => clock.Now);

        return (new UiRefreshThrottle(dispatcher, clock), dispatcher, clock);
    }
}
