using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 页头两个下拉菜单（S10-a / S10-b）的 ViewModel 行为：
/// 队列摘要三段、就地取消 / 重试 / 取消全部、账户列表与"设为默认"、两个菜单互斥。
/// </summary>
/// <remarks>
/// 这里测的是"菜单里的东西是不是真的来自内核、动作是不是真的交回内核"，
/// 而不是 XAML 长得对不对（后者由 <see cref="ShellMenuWiringXamlTests"/> 钉住）。
/// </remarks>
[TestClass]
public sealed class ShellMenusTests
{
    [TestMethod]
    public void QueueMenu_ShowsRunningWaitingAndTheTwoMostRecentTasks()
    {
        var harness = Harness.Create();
        harness.Downloads.Queue =
        [
            Row("r1", "安装 1.21.4-Vanilla", DownloadQueueState.Running, "正在下载"),
            Row("q1", "下载 Sodium 0.6.5", DownloadQueueState.Queued, "等待中"),
            Row("q2", "补全 Java 21 运行时", DownloadQueueState.Queued, "等待中"),
            Row("c1", "安装 1.20.4-Forge", DownloadQueueState.Completed, "已完成"),
            Row("f1", "下载 Create 0.5.1f", DownloadQueueState.Failed, "校验失败"),
            Row("x1", "更新模组", DownloadQueueState.Cancelled, "已取消"),
        ];

        harness.Shell.ToggleQueueMenuCommand.Execute(null);

        Assert.IsTrue(harness.Shell.IsQueueMenuOpen);
        Assert.IsNotNull(harness.Shell.ActiveQueueTask);
        Assert.AreEqual("r1", harness.Shell.ActiveQueueTask.Id);
        Assert.AreEqual(2, harness.Shell.WaitingQueueTasks.Count);
        Assert.AreEqual("1 个进行中 · 2 个排队", harness.Shell.QueueMenuSummary);
        Assert.IsFalse(harness.Shell.HasNoQueueTasks);

        // 最近的结束行最多两条、新的在前：取消（最后一条）→ 失败。
        Assert.AreEqual(2, harness.Shell.RecentQueueTasks.Count);
        Assert.AreEqual(DownloadQueueState.Cancelled, harness.Shell.RecentQueueTasks[0].State);
        Assert.AreEqual(DownloadQueueState.Failed, harness.Shell.RecentQueueTasks[1].State);
        Assert.AreEqual("失败：校验失败", harness.Shell.RecentQueueTasks[1].Detail, "失败行要由界面加「失败：」前缀（与 p06 同一处实现）。");
    }

    [TestMethod]
    public void QueueMenu_HasAnHonestEmptyState()
    {
        var harness = Harness.Create();

        harness.Shell.ToggleQueueMenuCommand.Execute(null);

        Assert.IsTrue(harness.Shell.HasNoQueueTasks, "空队列时菜单该显示空状态，而不是画一排假行。");
        Assert.IsFalse(harness.Shell.HasActiveQueueTask);
        Assert.IsFalse(harness.Shell.HasWaitingQueueTasks);
        Assert.IsFalse(harness.Shell.HasRecentQueueTasks);
        Assert.IsFalse(harness.Shell.CanCancelAllQueueTasks);
    }

    [TestMethod]
    public void QueueMenu_CancelAndRetryGoThroughTheCatalog()
    {
        var harness = Harness.Create();
        harness.Downloads.Queue =
        [
            Row("r1", "安装 1.21.4-Vanilla", DownloadQueueState.Running, "正在下载"),
            Row("f1", "下载 Create 0.5.1f", DownloadQueueState.Failed, "校验失败"),
        ];

        harness.Shell.ToggleQueueMenuCommand.Execute(null);
        harness.Shell.QueueRowActionCommand.Execute(harness.Shell.ActiveQueueTask);

        CollectionAssert.Contains(harness.Downloads.Cancelled, "r1");

        harness.Shell.QueueRowActionCommand.Execute(harness.Shell.RecentQueueTasks[0]);

        CollectionAssert.Contains(harness.Downloads.Retried, "f1");
    }

    [TestMethod]
    public void QueueMenu_CancelAllOnlyCancelsUnfinishedTasks()
    {
        var harness = Harness.Create();
        harness.Downloads.Queue =
        [
            Row("r1", "安装 1.21.4-Vanilla", DownloadQueueState.Running, "正在下载"),
            Row("q1", "下载 Sodium 0.6.5", DownloadQueueState.Queued, "等待中"),
            Row("c1", "安装 1.20.4-Forge", DownloadQueueState.Completed, "已完成"),
        ];

        harness.Shell.ToggleQueueMenuCommand.Execute(null);
        harness.Shell.CancelAllQueueCommand.Execute(null);

        CollectionAssert.AreEquivalent(new[] { "r1", "q1" }, harness.Downloads.Cancelled);
        CollectionAssert.DoesNotContain(harness.Downloads.Cancelled, "c1", "已完成的不能取消（它不是没结束的任务）。");
    }

    [TestMethod]
    public void QueueMenu_RefreshesWhileOpenWhenTheQueueChanges()
    {
        var harness = Harness.Create();
        harness.Shell.ToggleQueueMenuCommand.Execute(null);

        harness.Downloads.Queue = [Row("q9", "新排队的任务", DownloadQueueState.Queued, "等待中")];
        harness.Downloads.RaiseChanged();

        Assert.AreEqual(1, harness.Shell.WaitingQueueTasks.Count, "菜单展开时队列变化必须刷新（后台线程经节流后回到 UI 线程）。");
        Assert.AreEqual("q9", harness.Shell.WaitingQueueTasks[0].Id);
    }

    [TestMethod]
    public void QueueMenu_ShowsTheRealProgressOfTheRunningTask()
    {
        var harness = Harness.Create();
        harness.Downloads.Queue = [Row("r1", "安装 1.21.4-Vanilla", DownloadQueueState.Running, "正在下载")];
        harness.Downloads.Progress = new DownloadProgress(
            CurrentTask: TextTemplate.Of("安装 {0}", "1.21.4-Vanilla"),
            Percent: 62,
            AmountText: "256 MB / 412 MB",
            SpeedText: "8.4 MB/s",
            FilesText: "1204 / 3180",
            RemainingText: TextTemplate.Of("约 {0} 秒", "19"),
            ConnectionsText: "64",
            DataSource: "piston-data.mojang.com");

        harness.Shell.ToggleQueueMenuCommand.Execute(null);

        Assert.AreEqual(62, harness.Shell.ActiveQueuePercent);
        StringAssert.Contains(harness.Shell.ActiveQueueMeta, "256 MB / 412 MB");
        StringAssert.Contains(harness.Shell.ActiveQueueMeta, "约 19 秒");
    }

    [TestMethod]
    public void Menus_AreMutuallyExclusiveAndCloseOnNavigation()
    {
        var harness = Harness.Create();

        harness.Shell.ToggleQueueMenuCommand.Execute(null);
        Assert.IsTrue(harness.Shell.IsQueueMenuOpen);

        harness.Shell.ToggleAccountMenuCommand.Execute(null);
        Assert.IsTrue(harness.Shell.IsAccountMenuOpen);
        Assert.IsFalse(harness.Shell.IsQueueMenuOpen, "两个浮层同时展开会互相盖住。");

        harness.Shell.NavigateCommand.Execute(PageId.Downloads);
        Assert.IsFalse(harness.Shell.IsAccountMenuOpen, "换页之后菜单的目标已经变了，必须收起。");
    }

    [TestMethod]
    public void AccountMenu_ShowsTheAccountsAndMarksTheDefaultOne()
    {
        var harness = Harness.Create();

        harness.Shell.ToggleAccountMenuCommand.Execute(null);

        Assert.AreEqual(2, harness.Shell.AccountRows.Count);
        Assert.IsTrue(harness.Shell.AccountRows[0].IsDefault);
        Assert.AreEqual("Steve", harness.Shell.AccountRows[0].PlayerName);
        Assert.IsFalse(harness.Shell.AccountRows[1].IsDefault);
        Assert.IsFalse(harness.Shell.HasNoAccounts);
    }

    [TestMethod]
    public void AccountMenu_ClickingAnotherAccountSetsItDefaultAndCloses()
    {
        var harness = Harness.Create();
        harness.Shell.ToggleAccountMenuCommand.Execute(null);

        harness.Shell.SelectAccountCommand.Execute(harness.Shell.AccountRows[1]);

        CollectionAssert.Contains(harness.Accounts.DefaultSetCalls, "Alex_Offline");
        Assert.AreEqual("Alex_Offline", harness.Shell.AccountLabel);
        Assert.IsFalse(harness.Shell.IsAccountMenuOpen, "选完默认账户应当收起菜单。");
    }

    [TestMethod]
    public void AccountMenu_ClickingTheDefaultAccountDoesNothing()
    {
        var harness = Harness.Create();
        harness.Shell.ToggleAccountMenuCommand.Execute(null);

        harness.Shell.SelectAccountCommand.Execute(harness.Shell.AccountRows[0]);

        Assert.AreEqual(0, harness.Accounts.DefaultSetCalls.Count, "已经是默认账户时不该再写一次账户文件。");
        Assert.IsFalse(harness.Shell.IsAccountMenuOpen);
    }

    [TestMethod]
    public void AccountMenu_ShowsTheWriteFailureInsteadOfPretendingItWorked()
    {
        var harness = Harness.Create();
        harness.Accounts.SetDefaultFailure = new AccountStoreException(AccountStoreFailure.Corrupt, "C:\\账户.json");
        harness.Shell.ToggleAccountMenuCommand.Execute(null);

        harness.Shell.SelectAccountCommand.Execute(harness.Shell.AccountRows[1]);

        CollectionAssert.Contains(harness.Accounts.DefaultSetCalls, "Alex_Offline");
        Assert.IsTrue(harness.Shell.HasAccountMenuError, "切换默认账户失败必须显示原因（读失败一律不得静默）。");
        Assert.IsTrue(harness.Shell.IsAccountMenuOpen, "失败时菜单不关，用户还能看到原因。");
        Assert.AreEqual("Steve", harness.Shell.AccountLabel, "失败时默认账户没有变化。");
    }

    [TestMethod]
    public void AccountMenu_ShowsTheStoreFailureInsteadOfAnEmptyList()
    {
        var harness = Harness.Create();
        harness.Accounts.Accounts.Clear();
        harness.Accounts.StoreStatus = new AccountStoreStatus(AccountStoreFailure.Corrupt, "C:\\账户.json");
        harness.Shell.ToggleAccountMenuCommand.Execute(null);

        Assert.IsTrue(harness.Shell.HasNoAccounts);
        Assert.IsTrue(harness.Shell.HasAccountMenuError, "「读不出来」不能显示成「还没有登录账户」。");
    }

    [TestMethod]
    public void AccountMenu_AddAndManageNavigateToTheirPages()
    {
        var harness = Harness.Create();
        harness.Shell.ToggleAccountMenuCommand.Execute(null);

        harness.Shell.AddMicrosoftAccountCommand.Execute(null);
        CollectionAssert.Contains(harness.Navigation.Visited, PageId.AccountLogin);

        harness.Shell.ToggleAccountMenuCommand.Execute(null);
        harness.Shell.ManageAccountsCommand.Execute(null);
        CollectionAssert.Contains(harness.Navigation.Visited, PageId.Accounts);

        harness.Shell.ToggleAccountMenuCommand.Execute(null);
        harness.Shell.AddOfflineAccountCommand.Execute(null);
        CollectionAssert.Contains(harness.Navigation.Visited, PageId.Accounts);
    }

    private static DownloadQueueItem Row(string id, string title, DownloadQueueState state, string detail) =>
        new(id, TextTemplate.Of("{0}", title), TextTemplate.Of("{0}", detail), state, state == DownloadQueueState.Running);

    /// <summary>一套最小可用的外壳 + 两个替身（只造顺序需要的页面，别的页面一跳就失败）。</summary>
    private sealed class Harness
    {
        private Harness()
        {
            Downloads = new FakeDownloadCatalog();
            Accounts = new FakeAccountCatalog();
            Navigation = new FakeNavigationService(Resolve);
            Shell = new ShellViewModel(Navigation, Accounts, Downloads, Loc.Service, new ImmediateRefreshThrottle(), new FakeHelpWindowService(), new FakeEventSearchWindowService());
        }

        public FakeDownloadCatalog Downloads { get; }

        public FakeAccountCatalog Accounts { get; }

        public FakeNavigationService Navigation { get; }

        public ShellViewModel Shell { get; }

        public static Harness Create() => new();

        private PageViewModelBase Resolve(PageId page) => page switch
        {
            PageId.Dashboard => new DashboardViewModel(
                new SampleInstanceCatalog(),
                new FakeSystemInfoProvider(),
                new SampleEventLog(),
                Navigation,
                new FakeAccountCatalog(),
                new FakeJavaRuntimeCatalog()),
            PageId.Downloads => new DownloadsViewModel(Downloads, new SampleEventLog(), new ImmediateRefreshThrottle()),
            PageId.Accounts => TestViewModels.Accounts(Accounts),
            PageId.AccountLogin => new AccountLoginViewModel(
                Accounts,
                new ViewModels.Dialogs.AddAccountDialogViewModel(
                    new FakeMicrosoftSignIn(),
                    new FakeDeviceCodeFlow(),
                    new FakeShellService(),
                    Navigation),
                Navigation),
            _ => throw new AssertFailedException($"外壳菜单用例没有为 {page} 准备页面替身。"),
        };
    }
}
