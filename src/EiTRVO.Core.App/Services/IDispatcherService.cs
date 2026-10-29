namespace EiTRVO.Core.App.Services;

/// <summary>
/// 把工作切回 UI 线程。
/// <para>
/// 后端服务在后台线程产出进度与日志，ViewModel 不能直接碰 <c>Dispatcher</c>
/// （AGENTS.md §2.1 禁止 UI 类型进入 ViewModel），所以调度收敛到这一层。
/// </para>
/// </summary>
internal interface IDispatcherService
{
    /// <summary>当前是否在 UI 线程上。</summary>
    bool IsOnUiThread { get; }

    /// <summary>在 UI 线程上同步执行。已在 UI 线程时直接执行，不排队。</summary>
    /// <param name="action">要执行的动作。</param>
    void Invoke(Action action);

    /// <summary>在 UI 线程上异步执行并等待完成。</summary>
    /// <param name="operation">要执行的异步动作。</param>
    /// <returns>任务。</returns>
    Task InvokeAsync(Func<Task> operation);

    /// <summary>
    /// 在 UI 线程上、<paramref name="delay"/> 之后执行一次。
    /// </summary>
    /// <remarks>
    /// 存在的理由是"节流要能送达尾帧"：合并窗口内最后一次变更必须在窗口末尾跑到界面上，
    /// 否则下载完成那一条会永远停在上一帧。实现只能是 UI 线程自己的计时器
    /// （WPF 的 <c>DispatcherTimer</c> 属于创建它的线程），所以这件事收在这层，
    /// ViewModel 侧不必知道任何计时器类型（AGENTS.md §2.2 禁止 UI 类型进 ViewModel）。
    /// <para>
    /// 不保证精确到毫秒（调度可能让它晚一点）；也不提供取消 —— 调用方若已自行刷新过，
    /// 只需让回调醒来时判断"还需不需要跑"。
    /// </para>
    /// </remarks>
    /// <param name="delay">延迟；小于零按零处理。</param>
    /// <param name="action">要执行的动作。</param>
    void InvokeAfter(TimeSpan delay, Action action);
}
