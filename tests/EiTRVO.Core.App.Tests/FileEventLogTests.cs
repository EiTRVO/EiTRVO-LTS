using System.IO;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-a：事件表读的是**真实**发生的日志（<c>log/events.jsonl</c>），不是演示数据。
/// </summary>
/// <remarks>
/// 这一层要验三件事：内核事件被如实投影成界面行（级别 / 来源 / 句子 / 事件号 / 时间）、
/// 两个过滤（按实例、只看实例设置）只留下自己的行、以及**快照在短窗口内复用**
/// （下载期间队列每次变化都会刷新页面，不能每次都去解析整个事件文件）。
/// </remarks>
[TestClass]
public sealed class FileEventLogTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "eitrvo-eventlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void RealEventsAreProjectedIntoTheTableView()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.FromHours(8)));
        var diagnostics = new FileDiagnosticsLog(new LauncherPaths(_root), clock);

        diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Downloader,
            "已创建实例：{0}（{1}）",
            ["1.20.4-Forge", "1.20.4-Forge（原版）"],
            instanceName: "1.20.4-Forge");
        diagnostics.Write(LauncherEventLevel.Warning, DiagnosticsSources.InstanceSettings, "最大内存已调整", ["4096"]);
        diagnostics.Write(LauncherEventLevel.Critical, DiagnosticsSources.LaunchCore, "游戏异常退出（退出码 {0}，运行 {1} 秒）{2}", ["1", "37", ""]);

        var log = new FileEventLog(diagnostics, clock, new ImmediateRefreshThrottle());
        var rows = log.GetEvents();

        Assert.AreEqual(3, log.TotalCount, "「共 N 个」是日志里的真实条数。");
        Assert.AreEqual(3, rows.Count);

        // 最新的一条在上面（设计稿的事件表就是这么排的）。
        Assert.AreEqual("游戏异常退出（退出码 1，运行 37 秒）", rows[0].Message, "参数要真的被代进句子里 —— 键里没有占位符时它们是**静默丢掉**的。");
        Assert.AreEqual(EventLevel.Error, rows[0].Level, "Critical 在事件表里就是「错误」这一档。");
        Assert.AreEqual("启动核心", rows[0].SourceText);
        Assert.AreEqual(EventSource.LaunchCore, rows[0].Source);
        Assert.AreEqual(3, rows[0].Id, "事件号就是「日志里第几条」，最新那条等于总数。");

        Assert.AreEqual(EventLevel.Warning, rows[1].Level);
        Assert.AreEqual(2, rows[1].Id);

        Assert.AreEqual(EventLevel.Info, rows[2].Level);
        Assert.AreEqual("1.20.4-Forge", rows[2].InstanceName);
        Assert.AreEqual("下载器", rows[2].SourceText);
        StringAssert.StartsWith(rows[2].Time, "2026/10/6 21:30", "时间来自事件自己的时间戳。");
    }

    [TestMethod]
    public void TheInstanceAndSettingsFiltersOnlyKeepTheirOwnRows()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.FromHours(8)));
        var diagnostics = new FileDiagnosticsLog(new LauncherPaths(_root), clock);

        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Downloader, "任务开始", instanceName: "A");
        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Downloader, "任务开始", instanceName: "B");
        diagnostics.Write(LauncherEventLevel.Success, DiagnosticsSources.InstanceSettings, "已保存实例 {0} 的启动参数", ["A"], instanceName: "A");
        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Launcher, "已保存全局设置");

        var log = new FileEventLog(diagnostics, clock, new ImmediateRefreshThrottle());

        Assert.AreEqual(4, log.GetEvents().Count, "全局事件表看得到所有事件（含带实例名的）。");
        Assert.AreEqual(2, log.GetInstanceEvents("A").Count, "实例详情页只看这个实例的。");
        Assert.AreEqual(1, log.GetSettingsEvents("A").Count, "实例设置页只看这个实例的**设置**变更。");
        Assert.AreEqual(0, log.GetInstanceEvents("C").Count, "别的实例看不到。");
    }

    [TestMethod]
    public void TheSnapshotIsReusedInsideTheCacheWindowAndRereadAfterwards()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.FromHours(8)));
        var diagnostics = new RecordingDiagnosticsLog();

        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Launcher, "第一件事");

        var log = new FileEventLog(diagnostics, clock, new ImmediateRefreshThrottle());
        Assert.AreEqual(1, log.GetEvents().Count);
        Assert.AreEqual(1, diagnostics.ReadCount, "第一次读盘。");

        // 窗口内再读：**没有新事件**时不再读盘（下载期间页面刷得很频繁，不能每次都解析整个文件）。
        Assert.AreEqual(1, log.GetEvents().Count);
        Assert.AreEqual(1, log.TotalCount);
        Assert.AreEqual(1, diagnostics.ReadCount, "500ms 内只读一次盘。");

        // 越过窗口：重新读。
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.AreEqual(1, log.GetEvents().Count);
        Assert.AreEqual(1, log.TotalCount);
        Assert.AreEqual(2, diagnostics.ReadCount, "越过窗口后重新读。");
    }

    /// <summary>
    /// M6-f：**内核写了一条事件 ⇒ 缓存立刻失效**（不等 500ms 窗口过去）。
    /// </summary>
    /// <remarks>
    /// 这条是"通知"有没有用的判据：通知到达后订阅者会重读属性，
    /// 若那次读还命中 500ms 内的旧快照，界面上就**看不到刚写的那一条** ——
    /// 通知看起来完全没发生（而且没有"下一次"的话就永远不显示）。
    /// </remarks>
    [TestMethod]
    public void ANewEventInvalidatesTheSnapshotImmediately()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.FromHours(8)));
        var diagnostics = new RecordingDiagnosticsLog();

        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Launcher, "第一件事");

        var log = new FileEventLog(diagnostics, clock, new ImmediateRefreshThrottle());
        var notifications = 0;
        log.Changed += (_, _) => notifications++;

        Assert.AreEqual(1, log.GetEvents().Count);

        // 500ms 窗口之内写第二条：读到的必须是两条（快照被通知失效了）。
        diagnostics.Write(LauncherEventLevel.Warning, DiagnosticsSources.Downloader, "第二件事");

        Assert.AreEqual(1, notifications, "写一条就通知一次（节流器是立即执行的那种）。");
        Assert.AreEqual(2, log.GetEvents().Count, "刚写的那一条必须马上读得到。");
        Assert.AreEqual(2, log.TotalCount);
    }

    /// <summary>
    /// M6-f：**通知经节流器切回 UI 线程送达**，后台线程连写多条不会把界面冲垮，且**尾帧一定送到**。
    /// </summary>
    [TestMethod]
    public void NotificationsArriveOnTheUiThreadThroughTheThrottle()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.FromHours(8)));
        var dispatcher = new ManualDispatcherService(() => clock.Now);
        var diagnostics = new RecordingDiagnosticsLog();

        var log = new FileEventLog(diagnostics, clock, new UiRefreshThrottle(dispatcher, clock));

        var notifications = 0;
        log.Changed += (_, _) => notifications++;

        // 后台线程连写三条（真实场景：安装 worker 一边下载一边写事件）。
        Task.Run(() =>
        {
            for (var index = 0; index < 3; index++)
            {
                diagnostics.Write(
                    LauncherEventLevel.Information,
                    DiagnosticsSources.Downloader,
                    "第 {0} 件事",
                    [index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
        }).GetAwaiter().GetResult();

        Assert.AreEqual(1, notifications, "首帧立即执行：同一窗口内三条只通知一次。");
        Assert.AreEqual(1, dispatcher.Invokes, "通知必须经 IDispatcherService 跑到 UI 线程上。");

        // 尾帧：窗口过去之后一定补跑最后一次 —— 少了它，最后一条事件就永远不显示。
        clock.Advance(TimeSpan.FromMilliseconds(200));
        dispatcher.FireDue();

        Assert.AreEqual(2, notifications, "尾帧补跑了一次。");
        Assert.AreEqual(3, log.GetEvents().Count, "尾帧那次读到的就是最新的一份（三条都在）。");
        Assert.AreEqual(3, log.TotalCount);
    }

    [TestMethod]
    public void AMissingLogIsAnEmptyTableInsteadOfAnError()
    {
        var clock = new AdvancingClock(DateTimeOffset.Now);
        var log = new FileEventLog(new FileDiagnosticsLog(new LauncherPaths(_root), clock), clock, new ImmediateRefreshThrottle());

        Assert.AreEqual(0, log.TotalCount);
        Assert.AreEqual(0, log.GetEvents().Count);
        Assert.AreEqual(0, log.GetInstanceEvents("A").Count);
    }

    /// <summary>
    /// M6-d：同一份快照里，每种"形状"只拼一次句子，而且交给界面的是**同一个列表实例**。
    /// </summary>
    /// <remarks>
    /// 两层意义：200 行的 <c>Loc.T</c> 不再每次刷新重算；WPF 看见 <c>ItemsSource</c> 还是那个对象，
    /// 就不会把 200 行拆掉重建。而"读到新快照就把投影丢掉"是必须的 ——
    /// 不丢的话界面会一直显示旧行，缓存还会随刷新次数长大。
    /// </remarks>
    [TestMethod]
    public void TheProjectionIsReusedInsideTheSnapshotWindowAndDroppedWithANewSnapshot()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.FromHours(8)));
        var diagnostics = new FileDiagnosticsLog(new LauncherPaths(_root), clock);

        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Downloader, "任务开始", instanceName: "A");
        diagnostics.Write(LauncherEventLevel.Success, DiagnosticsSources.InstanceSettings, "已保存实例 {0} 的启动参数", ["A"], instanceName: "A");

        var log = new FileEventLog(diagnostics, clock, new ImmediateRefreshThrottle());
        var first = log.GetEvents();

        Assert.AreSame(first, log.GetEvents(), "同一份快照里「全部事件」只该项目一次。");
        Assert.AreEqual(1, log.CachedProjectionCount);

        // 另外两种形状各算一份（五页最多用到三种）。
        Assert.AreEqual(2, log.GetInstanceEvents("A").Count);
        Assert.AreEqual(1, log.GetSettingsEvents("A").Count);

        // 同一形状再来一次仍然是那一个实例。
        Assert.AreSame(log.GetInstanceEvents("A"), log.GetInstanceEvents("A"));
        Assert.AreEqual(3, log.CachedProjectionCount);

        // 新快照：旧投影作废（总数也变了）。
        clock.Advance(TimeSpan.FromMilliseconds(600));
        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Launcher, "第三件事");

        var afterNewSnapshot = log.GetEvents();

        Assert.AreNotSame(first, afterNewSnapshot);
        Assert.AreEqual(3, afterNewSnapshot.Count);
        Assert.AreEqual(1, log.CachedProjectionCount, "换快照后只留下刚算过的那一份。");
    }

    /// <summary>投影缓存的条数由"形状上限"兜住，不由调用方决定（实例名来自界面）。</summary>
    [TestMethod]
    public void TheProjectionCacheStaysBoundedWhenCallersAskForManyShapes()
    {
        var clock = new AdvancingClock(new DateTimeOffset(2026, 10, 6, 21, 30, 0, TimeSpan.FromHours(8)));
        var diagnostics = new FileDiagnosticsLog(new LauncherPaths(_root), clock);

        diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Downloader, "任务开始", instanceName: "A");

        var log = new FileEventLog(diagnostics, clock, new ImmediateRefreshThrottle());

        for (var index = 0; index < 200; index++)
        {
            // 每个实例名都是一份新形状：缓存必须封顶（超了就现算），否则它的体量由传进来的字符串决定。
            Assert.AreEqual(0, log.GetInstanceEvents("实例 " + index).Count);
        }

        Assert.IsTrue(
            log.CachedProjectionCount <= 8,
            $"投影缓存长到了 {log.CachedProjectionCount} 份 —— 上限失效了。");

        Assert.AreEqual(1, log.GetInstanceEvents("A").Count, "没被缓存的行也要照样算对。");
    }

    /// <summary>可拨动的时钟：缓存窗口按它算。</summary>
    private sealed class AdvancingClock(DateTimeOffset now) : IClock
    {
        /// <inheritdoc />
        public DateTimeOffset Now { get; private set; } = now;

        /// <summary>把时钟往前拨。</summary>
        /// <param name="delta">往前拨多久。</param>
        public void Advance(TimeSpan delta) => Now += delta;
    }
}
