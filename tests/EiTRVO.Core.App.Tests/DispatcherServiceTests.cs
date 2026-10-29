using System.Windows.Threading;
using EiTRVO.Core.App.Services;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// <see cref="DispatcherService"/> 的调度语义。
/// <para>
/// 它靠注入 Dispatcher 取值函数来测（默认取 <c>Application.Current.Dispatcher</c>，
/// 单测里换成本线程的 Dispatcher）。<see cref="IWindowService"/> 与 <see cref="IDialogService"/>
/// 直接操作真实窗口与对话框，无法单测 —— 因此它们被刻意写成"只转发、不含判断"的薄壳，
/// 逻辑一律留在 ViewModel 里。
/// </para>
/// </summary>
[TestClass]
public sealed class DispatcherServiceTests
{
    [TestMethod]
    public void InvokeRunsTheActionInlineWhenAlreadyOnTheDispatcherThread()
    {
        var service = new DispatcherService(static () => Dispatcher.CurrentDispatcher);
        var ran = false;

        service.Invoke(() => ran = true);

        Assert.IsTrue(ran, "已在 UI 线程时应当直接执行，而不是排队等待消息循环。");
        Assert.IsTrue(service.IsOnUiThread);
    }

    [TestMethod]
    public async Task InvokeAsyncCompletesWithoutAMessageLoop()
    {
        var service = new DispatcherService(static () => Dispatcher.CurrentDispatcher);
        var value = 0;

        await service.InvokeAsync(() =>
        {
            value = 42;
            return Task.CompletedTask;
        });

        Assert.AreEqual(42, value);
    }

    [TestMethod]
    public void MissingDispatcherProducesAnActionableError()
    {
        var service = new DispatcherService(static () => null);

        var ex = Assert.ThrowsException<InvalidOperationException>(() => service.Invoke(static () => { }));

        StringAssert.Contains(ex.Message, "Dispatcher");
    }

    [TestMethod]
    public void NullArgumentsAreRejected()
    {
        var service = new DispatcherService(static () => Dispatcher.CurrentDispatcher);

        Assert.ThrowsException<ArgumentNullException>(() => service.Invoke(null!));
        Assert.ThrowsException<ArgumentNullException>(() => service.InvokeAsync(null!));
        Assert.ThrowsException<ArgumentNullException>(() => service.InvokeAfter(TimeSpan.Zero, null!));
    }

    /// <summary>
    /// M6-d：延时执行要真的在到点之后、在 dispatcher 线程上跑一次。
    /// </summary>
    /// <remarks>
    /// 这条必须用**真** dispatcher（而不是替身）：节流的尾帧能不能送达，全靠 WPF 的
    /// <c>DispatcherTimer</c> 在消息循环里被触发 —— 替身测不出这件事。
    /// 这里推一个嵌套消息循环等它，带保险丝（5 秒）以免计时器没触发时用例挂住。
    /// </remarks>
    [TestMethod]
    public void InvokeAfterRunsOnceOnTheDispatcherThreadAfterTheDelay()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var service = new DispatcherService(() => dispatcher);
        var ran = new List<bool>();

        service.InvokeAfter(TimeSpan.FromMilliseconds(10), () => ran.Add(dispatcher.CheckAccess()));

        Assert.AreEqual(0, ran.Count, "延时执行不能当场就跑。");

        var guard = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();

        var poll = new DispatcherTimer(
            TimeSpan.FromMilliseconds(20),
            DispatcherPriority.Send,
            (_, _) =>
            {
                if (ran.Count > 0 || guard.Elapsed > TimeSpan.FromSeconds(5))
                {
                    frame.Continue = false;
                }
            },
            dispatcher);

        poll.Start();
        Dispatcher.PushFrame(frame);
        poll.Stop();

        Assert.AreEqual(1, ran.Count, "到点之后必须恰好执行一次。");
        Assert.IsTrue(ran[0], "动作必须跑在 dispatcher 线程上（ViewModel 侧不能在后台线程改绑定属性）。");
    }
}
