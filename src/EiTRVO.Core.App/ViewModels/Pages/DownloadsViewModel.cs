using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p06 下载与队列。对应 mockup/pages/p06-downloads.html。
/// <para>
/// 页头「队列」徽标的数字也来自这里的 <see cref="IDownloadCatalog.PendingTaskCount"/>，
/// 所以徽标与页内「N 个活动任务 · M 个排队」永远一致。
/// </para>
/// <para>
/// M2 起这一页是**真实数据**：队列与进度来自安装任务队列（<c>FileDownloadCatalog</c>）。
/// 两件事必须一起做，否则页面只会显示"进页面那一刻"的快照：
/// 被导航到时重新读一次，以及订阅队列的变更事件（后者在后台线程触发 —— 走
/// <see cref="IUiRefreshThrottle"/> 合并成每秒最多十次刷新，再切回 UI 线程改绑定属性）。
/// </para>
/// </summary>
internal sealed partial class DownloadsViewModel : EventListPageViewModel
{
    private readonly IDownloadCatalog _downloads;
    private readonly IEventLog _events;
    private readonly IUiRefreshThrottle _throttle;

    /// <summary>构造下载与队列 ViewModel。</summary>
    /// <param name="downloads">下载队列。</param>
    /// <param name="events">事件日志。</param>
    /// <param name="throttle">刷新节流（队列在后台按 200ms 报进度，界面不能每来一条就整表重建）。</param>
    public DownloadsViewModel(
        IDownloadCatalog downloads,
        IEventLog events,
        IUiRefreshThrottle throttle)
        : base(PageId.Downloads, "下载与队列")
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(throttle);

        _downloads = downloads;
        _events = events;
        _throttle = throttle;

        _downloads.Changed += OnDownloadsChanged;

        // M6-f：事件表也要跟着走。这条流的源头（FileEventLog）已经按 100ms 节流 + 尾帧，
        // 所以这里不再套一层节流器 —— 再套一层只会多一次等待，不会少一次刷新。
        _events.Changed += OnEventsChanged;
        Refresh();
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Downloads;

    /// <inheritdoc />
    public override string SectionSubtitle
    {
        get
        {
            var running = Queue.Count(static item => item.State == DownloadQueueState.Running);
            var queued = Queue.Count(static item => item.State == DownloadQueueState.Queued);
            return Loc.T("{0} 个活动任务 · {1} 个排队", running, queued);
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>总体进度（属性区与进度条共用）。</summary>
    public DownloadProgress Progress { get; private set; } = null!;

    /// <summary>「当前任务」那行（内核给键 + 参数，这里拼成句子）。</summary>
    public string CurrentTaskText => LocalizedText.Of(Progress.CurrentTask);

    /// <summary>「预计剩余」那行（`即将完成` / `约 {0} 秒` / `—`）。</summary>
    public string RemainingTextText => LocalizedText.Of(Progress.RemainingText);

    /// <summary>队列内容（标题与明细已按当前语言拼好）。</summary>
    public IReadOnlyList<DownloadQueueRowViewModel> Queue { get; private set; } = [];

    /// <summary>下载事件流（p06 的事件表读的是 <see cref="EventListPageViewModel.FilteredEvents"/>）。</summary>
    public IReadOnlyList<EventEntry> Events { get; private set; } = [];

    /// <inheritdoc />
    protected override IReadOnlyList<EventEntry> EventRows => Events;

    /// <inheritdoc />
    public override bool CanSearchEvents => false;

    /// <summary>事件总数。</summary>
    public int EventTotal { get; private set; }

    /// <summary>事件表上方的统计文本。</summary>
    public string EventCountText => FormatCount(Loc.T("所有事件"), EventTotal);

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();
        Refresh();
    }

    private void OnDownloadsChanged(object? sender, EventArgs e) => _throttle.Request(Refresh);

    private void OnEventsChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        Progress = _downloads.GetProgress();

        // 「键 + 参数 → 句子」只在 App 层发生（同 p14 的阶段表）。
        Queue =
        [
            .. _downloads.GetQueue().Select(static item => DownloadQueueRowViewModel.From(item, DownloadQueueRowViewModel.Describe(item))),
        ];
        Events = _events.GetEvents();
        EventTotal = _events.TotalCount;

        // 空属性名通知一次：副标题、事件统计文本这些派生属性一并重算。
        OnPropertyChanged(string.Empty);

        // 两个动作条按钮的可执行性也是"现算"的，但它不跟着空属性名通知走 —— 必须显式再问一次
        // （与 p07「更新所选」同一条规矩），否则队列清空之后「取消全部」还亮着。
        CancelAllCommand.NotifyCanExecuteChanged();
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 行内动作（M7-g）：还没结束的行是「取消」，失败 / 已取消的行是「重试」。
    /// </summary>
    /// <param name="row">被点的那一行。</param>
    /// <remarks>
    /// 分派放在这一层而不是每个行类型各挂一个命令：两种动作只差一个方法调用，
    /// 而 <see cref="DownloadQueueRowViewModel.IsRetry"/> 已经是"该调哪一个"的答案。
    /// </remarks>
    [RelayCommand]
    private void QueueAction(DownloadQueueRowViewModel? row)
    {
        if (row is null || !row.CanAct)
        {
            // 按钮本来就不可按（已完成的那些根本不显示动作）；真走到这里说明状态在两次求值之间变了。
            return;
        }

        if (row.IsRetry)
        {
            _downloads.Retry(row.Id);
        }
        else
        {
            _downloads.Cancel(row.Id);
        }

        // 不等节流器：用户刚点的那一下必须立刻看见（徽标换成状态词、页头徽标的数字也要跟着动）。
        Refresh();
    }

    /// <summary>取消所有还没结束的任务（设计稿的「全部暂停」）。</summary>
    /// <remarks>
    /// 名字与设计稿不一样：设计稿写「暂停」，而**暂停要能把任务接着跑**（保留断点、之后继续），
    /// 那是另一件事（要 HTTP Range 与可续传的进度），P1-14 要的是取消 / 重试。
    /// 把"停了就没了"叫做暂停会骗人，所以这里叫「取消全部」并登记在 `tools\icon-label-renames.txt`。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll()
    {
        var unfinished = Queue.Where(static row => row.CanAct && !row.IsRetry).Select(static row => row.Id).ToArray();

        foreach (var id in unfinished)
        {
            _downloads.Cancel(id);
        }

        Refresh();
    }

    /// <summary>清空队列里的历史（设计稿的「清空已完成」）。</summary>
    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        _downloads.ClearFinished();
        Refresh();
    }

    /// <summary>还有没结束的任务时才可按。</summary>
    private bool CanCancelAll() => Queue.Any(static row => row.CanAct && !row.IsRetry);

    /// <summary>有历史行时才可按。</summary>
    private bool CanClearFinished() => _downloads.GetQueue().Any(static item => item.State is DownloadQueueState.Completed or DownloadQueueState.Failed or DownloadQueueState.Cancelled);
}
