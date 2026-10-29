using System.Diagnostics;
using EiTRVO.Core.App.Services;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M8-3：界面节流的性能冒烟测试（不追求基准精度，只钉住"不会随通知量线性增长"）。
/// </summary>
/// <remarks>
/// 下载进度每 200ms 报一次、事件日志可能成百上千条地刷；如果 ViewModel 对每条通知都刷新，
/// UI 线程会被淹没。这里直接向节流器灌 10 万条通知，断言：
/// ① 合并窗口内只挂一个尾帧；② 内存不会因为待处理回调堆积而持续增长；③ 尾帧仍会送达。
/// </remarks>
[TestClass]
public sealed class PerformanceSmokeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 21, 0, 0, TimeSpan.FromHours(8));

    /// <summary>十万条通知不应让待处理回调或内存线性堆积。</summary>
    [TestMethod]
    public void AStormOfNotificationsDoesNotGrowMemoryWithTheNotificationCount()
    {
        var clock = new ManualClock(Start);
        var dispatcher = new ManualDispatcherService(() => clock.Now);
        var throttle = new UiRefreshThrottle(dispatcher, clock);
        var runs = 0;

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < 100_000; i++)
        {
            throttle.Request(() => runs++);
        }

        stopwatch.Stop();
        var after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.AreEqual(1, runs, "第一帧立即执行，其余 99999 条应被合并。");
        Assert.AreEqual(1, dispatcher.DelayedCount, "整个窗口只允许挂一个尾帧。");
        Assert.IsTrue(after - before < 16 * 1024 * 1024, "10 万条通知后托管内存增长不应超过 16 MB（实际 {0}）。", after - before);

        clock.Advance(UiRefreshThrottle.DefaultInterval);
        Assert.AreEqual(1, dispatcher.FireDue());
        Assert.AreEqual(2, runs, "尾帧必须把最后一条通知补上。");
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "10 万条通知的合并路径不应慢到 5 秒以上。");
    }
}
