using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// <see cref="IUiRefreshThrottle"/> 的实现：一个合并窗口 + 一个"尾帧"调度。
/// </summary>
/// <remarks>
/// 时间判断用 <see cref="IClock"/>（测试可以拨快），延迟送达用
/// <see cref="IDispatcherService.InvokeAfter"/>（于是尾帧跑到 UI 线程上，
/// 与其它刷新走同一条路，异常也由外壳统一处理）。
/// <para>
/// 尾帧用**代号**而不是可取消的句柄：延迟回调可能已经在路上，
/// 期间又发生了一次"首帧立即执行"，那次执行把代号清成 0，
/// 于是这个回调醒来看见代号不对就自己退出 —— 不需要跨线程去 Stop 一个计时器。
/// </para>
/// </remarks>
internal sealed class UiRefreshThrottle : IUiRefreshThrottle
{
    /// <summary>合并窗口：100ms ≈ 每秒最多 10 次刷新（内核每 200ms 报一次进度，够用且留了余量）。</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly IDispatcherService _dispatcher;
    private readonly IClock _clock;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();

    private Action? _pending;
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private int _generation;
    private int _scheduled;

    /// <summary>构造节流器。</summary>
    /// <param name="dispatcher">UI 线程调度（刷新与尾帧都在 UI 线程上跑）。</param>
    /// <param name="clock">时间源。</param>
    /// <param name="interval">合并窗口；缺省 <see cref="DefaultInterval"/>。</param>
    public UiRefreshThrottle(IDispatcherService dispatcher, IClock clock, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);

        _dispatcher = dispatcher;
        _clock = clock;
        _interval = interval ?? DefaultInterval;
    }

    /// <inheritdoc />
    public void Request(Action refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);

        Action? runNow = null;

        lock (_gate)
        {
            _pending = refresh;

            var elapsed = _clock.Now - _lastRun;

            if (elapsed >= _interval)
            {
                // 首帧：立即执行（并让挂着的尾帧作废 —— 它醒来时代号已经不对）。
                runNow = Take();
            }
            else if (_scheduled == 0)
            {
                var generation = ++_generation;
                _scheduled = generation;

                // 尾帧：这一句之后窗口内的请求只覆盖 _pending，不再各排一个计时器。
                _dispatcher.InvokeAfter(_interval - elapsed, () => Flush(generation));
            }
        }

        if (runNow is not null)
        {
            Run(runNow);
        }
    }

    /// <summary>取走"待刷新"并记下这一次执行时刻（调用方必须持有 <see cref="_gate"/>）。</summary>
    /// <returns>要执行的动作；没有待刷新时为 <see langword="null"/>。</returns>
    private Action? Take()
    {
        var refresh = _pending;
        _pending = null;
        _lastRun = _clock.Now;
        _scheduled = 0;

        return refresh;
    }

    /// <summary>尾帧到点：只有在"这一代还是最后一代"时才有活干。</summary>
    /// <param name="generation">排这个尾帧时的代号。</param>
    private void Flush(int generation)
    {
        Action? refresh;

        lock (_gate)
        {
            if (_scheduled != generation)
            {
                return;
            }

            refresh = Take();
        }

        if (refresh is not null)
        {
            Run(refresh);
        }
    }

    private void Run(Action refresh) => _dispatcher.Invoke(refresh);
}
