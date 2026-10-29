using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-f：**事件通知链路** —— 内核写一条事件（后台线程）→ <c>FileEventLog</c> 合并并切回 UI 线程 →
/// 页面的桩刷新。三条断言分别对应这条链的三段。
/// </summary>
/// <remarks>
/// 为什么需要它：M6-a 之前五页的事件表是**纯拉取**的（只在导航到页面时读一次），
/// 于是"下载 10 分钟"期间 p01 的事件表**一直不动**，而用户恰恰是看着那一页在等 ——
/// 崩溃（Critical）这种最该被看到的事件也一样。
/// <para>
/// 这里用真实的 <see cref="FileEventLog"/>（App 侧实现，含快照缓存 + 通知）与
/// <see cref="RecordingDiagnosticsLog"/>（内核侧替身，写一条就通知），只有读盘换成内存 ——
/// 事件的形状与真实实现一致。
/// </para>
/// </remarks>
[TestClass]
public sealed class EventNotificationTests
{
    private const string Instance = "1.20.4-Forge";

    [TestMethod]
    public void ANewEventReachesTheDashboardEventTable()
    {
        var diagnostics = new RecordingDiagnosticsLog();
        var events = NewEventLog(diagnostics);
        var page = new DashboardViewModel(
            new FakeInstanceCatalog(),
            new FakeSystemInfoProvider(),
            events,
            new FakeNavigationService(),
            new FakeAccountCatalog(),
            new FakeJavaRuntimeCatalog());

        Assert.AreEqual(0, page.Events.Count);

        var changed = new List<string>();
        page.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Downloader, "任务开始");

        CollectionAssert.Contains(changed, nameof(DashboardViewModel.Events));
        CollectionAssert.Contains(changed, nameof(DashboardViewModel.EventTotal));
        Assert.AreEqual(1, page.Events.Count, "通知之后那一行必须已经在表里了。");
        Assert.AreEqual(1, page.EventTotal);
    }

    [TestMethod]
    public void ANewEventReachesTheInstanceEventTables()
    {
        var diagnostics = new RecordingDiagnosticsLog();
        var events = NewEventLog(diagnostics);
        var instances = new FakeInstanceCatalog();
        var navigation = new FakeNavigationService();

        var detail = new InstanceDetailViewModel(instances, events, new FakeShellService());
        var delete = new DeleteInstanceViewModel(
            instances,
            events,
            new FakeShellService(),
            new DeleteConfirmDialogViewModel(instances, navigation));

        diagnostics.Write(
            LauncherEventLevel.Success,
            DiagnosticsSources.Launcher,
            "已启动实例 {0}",
            [Instance],
            instanceName: Instance);

        Assert.AreEqual(1, detail.Events.Count, "实例详情页看得到这个实例的事件。");
        Assert.AreEqual(1, delete.Events.Count, "删除确认页用的是同一个实例事件表。");
        Assert.AreEqual(1, detail.EventTotal);
        StringAssert.Contains(detail.EventCountText, "1 项", "实例详情的事件计数与仪表板统一用「项」。");
    }

    [TestMethod]
    public void ANewEventReachesTheSettingsAndDownloadPages()
    {
        var diagnostics = new RecordingDiagnosticsLog();
        var events = NewEventLog(diagnostics);

        var settings = TestViewModels.InstanceSettings(events: events);
        var downloads = new DownloadsViewModel(new FakeDownloadCatalog(), events, new ImmediateRefreshThrottle());

        diagnostics.Write(
            LauncherEventLevel.Success,
            DiagnosticsSources.InstanceSettings,
            "已保存实例 {0} 的启动参数",
            [Instance],
            instanceName: Instance);

        Assert.AreEqual(1, settings.ChangeEvents.Count, "p17 只看「实例设置」来源的事件。");
        Assert.AreEqual(1, downloads.Events.Count, "下载页的事件表也跟着刷新。");
        Assert.AreEqual(1, downloads.EventTotal);
    }

    /// <summary>
    /// 通知高频到达时，**表里最多 200 行**（表头的「共 N 个」仍是总数）——
    /// 这正是 F-6 说的"日志条数上限"落到界面上的那一层。
    /// </summary>
    [TestMethod]
    public void TheEventTableIsCappedAtTwoHundredRowsWhileTheHeaderCountsAllOfThem()
    {
        var diagnostics = new RecordingDiagnosticsLog();
        var events = NewEventLog(diagnostics);

        for (var index = 0; index < 250; index++)
        {
            diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Downloader,
                "第 {0} 件事",
                [index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        Assert.AreEqual(200, events.GetEvents().Count, "表格最多 200 行（旧的在下面）。");
        Assert.AreEqual(250, events.TotalCount, "表头的「共 N 个」是总数，不受上限影响。");
    }

    [TestMethod]
    public void TheDashboardCanFilterEventsByLevel()
    {
        var diagnostics = new RecordingDiagnosticsLog();
        var events = NewEventLog(diagnostics);
        var page = new DashboardViewModel(
            new FakeInstanceCatalog(),
            new FakeSystemInfoProvider(),
            events,
            new FakeNavigationService(),
            new FakeAccountCatalog(),
            new FakeJavaRuntimeCatalog());

        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Downloader, "信息");
        diagnostics.Write(LauncherEventLevel.Warning, DiagnosticsSources.Downloader, "警告");
        diagnostics.Write(LauncherEventLevel.Error, DiagnosticsSources.Downloader, "错误");

        Assert.AreEqual(3, page.Events.Count);
        StringAssert.Contains(page.EventCountText, "所有事件");
        StringAssert.Contains(page.EventCountText, "3 项");

        page.EventLevelChoice = page.EventLevelChoices.Single(option => option.Value == EventLevel.Warning);

        Assert.AreEqual(1, page.Events.Count);
        StringAssert.Contains(page.EventCountText, "所有事件", "计数沿用实例详情口径：总数不随筛选变化。");
        StringAssert.Contains(page.FilterHintText, "已筛出 1 / 3 条");

        page.EventLevelChoice = page.EventLevelChoices[0];

        Assert.AreEqual(3, page.Events.Count);
        Assert.AreEqual(3, page.EventTotal);
    }

    /// <summary>真实的 App 侧事件日志（快照缓存 + 通知），只有读盘来自内存替身。</summary>
    private static FileEventLog NewEventLog(RecordingDiagnosticsLog diagnostics) =>
        new(diagnostics, new ManualClock(DateTimeOffset.Now), new ImmediateRefreshThrottle());
}
