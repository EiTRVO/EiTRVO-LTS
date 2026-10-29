using System.Windows.Threading;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// <see cref="IDispatcherService"/> 的 WPF 实现。
/// <para>
/// Dispatcher 通过一个取值函数获得（默认取 <c>Application.Current.Dispatcher</c>），
/// 这样单测可以注入当前线程的 Dispatcher 来验证行为 —— 否则这个类只能靠肉眼。
/// </para>
/// </summary>
internal sealed class DispatcherService : IDispatcherService
{
    /// <summary>一次性计时器的最小延迟（WPF 的计时器按毫秒调度，0 与亚毫秒都按 1ms 处理）。</summary>
    private static readonly TimeSpan MinimumTimerDelay = TimeSpan.FromMilliseconds(1);

    private readonly Func<Dispatcher?> _dispatcherAccessor;

    /// <summary>构造服务。</summary>
    /// <param name="dispatcherAccessor">
    /// Dispatcher 取值函数；缺省取当前应用的 Dispatcher。传入函数而不是实例，
    /// 是因为组合根构造它时窗口可能还不存在。
    /// </param>
    public DispatcherService(Func<Dispatcher?>? dispatcherAccessor = null)
    {
        _dispatcherAccessor = dispatcherAccessor ?? (static () => System.Windows.Application.Current?.Dispatcher);
    }

    /// <inheritdoc />
    public bool IsOnUiThread => Dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 已在 UI 线程时 Dispatcher.Invoke 会直接执行（不排队），正是我们要的语义。
        Dispatcher.Invoke(action);
    }

    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var dispatcher = Dispatcher;

        // 已在 UI 线程时直接执行：省一次排队，也避开"没有消息循环就永远等不到"的陷阱。
        return dispatcher.CheckAccess()
            ? operation()
            : dispatcher.InvokeAsync(operation).Task.Unwrap();
    }

    private Dispatcher Dispatcher =>
        _dispatcherAccessor()
        ?? throw new InvalidOperationException(
            "还没有可用的 Dispatcher：应用尚未启动，或者组合根没有把它传进来。");

    /// <inheritdoc />
    public void InvokeAfter(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher;

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        // 计时器的分辨率本来就是毫秒级；亚毫秒的间隔没有意义，还会踩到"间隔必须为正"这条边界。
        if (delay < MinimumTimerDelay)
        {
            delay = MinimumTimerDelay;
        }

        if (dispatcher.CheckAccess())
        {
            StartTimer(dispatcher, delay, action);
            return;
        }

        // 不在 UI 线程：先切过去再建计时器 —— DispatcherTimer 属于**创建它的那个线程**，
        // 在后台线程 new 出来只会得到一个永远不触发的计时器。
        dispatcher.Invoke(() => StartTimer(dispatcher, delay, action));
    }

    /// <summary>在 dispatcher 线程上起一个一次性计时器（必须在那个线程上调用）。</summary>
    /// <param name="dispatcher">目标 dispatcher。</param>
    /// <param name="delay">延迟。</param>
    /// <param name="action">到点执行的动作。</param>
    private static void StartTimer(Dispatcher dispatcher, TimeSpan delay, Action action)
    {
        // 交给 DispatcherTimer 而不是 System.Threading.Timer：后者的回调在池线程上，
        // 那里漏出的异常没有任何人接（连 DispatcherUnhandledException 都接不到），会直接杀进程。
        var timer = new DispatcherTimer(
            delay,
            DispatcherPriority.Normal,
            (sender, _) =>
            {
                // 一次性：先在回调里停掉自己，免得同一个动作被重复触发。
                (sender as DispatcherTimer)?.Stop();
                action();
            },
            dispatcher);

        // 四参构造在部分版本上不自动启动；Start 对已启动的计时器是空操作，所以无条件再点一次。
        timer.Start();
    }
}
