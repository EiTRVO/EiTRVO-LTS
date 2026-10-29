using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Downloads;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 页头「队列」下拉菜单（2026-10-18，S10-a）：点按钮展开任务摘要，不跳页。
/// <para>
/// 这一块与 p06 的分工（设计稿 p18）：下拉只做"看一眼 + 就地取消/重试"，
/// 进度详情、阶段表、事件流与清空历史仍然在 p06；所以这里只投影
/// <see cref="DownloadQueueRowViewModel"/>（与 p06 同一份投影），不另造一套行类型。
/// </para>
/// </summary>
internal sealed partial class ShellViewModel
{
    private bool _isQueueMenuOpen;
    private DownloadQueueRowViewModel? _activeQueueTask;
    private IReadOnlyList<DownloadQueueRowViewModel> _waitingQueueTasks = [];
    private IReadOnlyList<DownloadQueueRowViewModel> _recentQueueTasks = [];
    private double _activeQueuePercent;
    private string _activeQueueMeta = string.Empty;
    private IRelayCommand? _toggleQueueMenuCommand;
    private IRelayCommand? _openQueuePageCommand;
    private IRelayCommand? _queueRowActionCommand;
    private RelayCommand? _cancelAllQueueCommand;
    private IRelayCommand? _closeMenusCommand;

    /// <summary>是否有任一下拉展开（窗口内浮层的可见性绑定它）。</summary>
    public bool IsAnyMenuOpen => IsQueueMenuOpen || IsAccountMenuOpen;

    /// <summary>收起两个下拉（点浮层外、按 Esc、换页）。</summary>
    public IRelayCommand CloseMenusCommand => _closeMenusCommand ??= new RelayCommand(CloseMenus);

    /// <summary>队列下拉是否展开（View 的 <c>Popup.IsOpen</c> 双向绑定它）。</summary>
    /// <remarks>
    /// 打开一个菜单会关掉另一个：两个浮层同时出现在页头右侧会互相盖住。
    /// 这个 setter 是 public 的，因为 <c>Popup</c> 在点外部关闭时会把 <see langword="false"/> 写回来。
    /// </remarks>
    public bool IsQueueMenuOpen
    {
        get => _isQueueMenuOpen;
        set
        {
            if (!SetProperty(ref _isQueueMenuOpen, value))
            {
                return;
            }

            if (value)
            {
                CloseAccountMenuSilently();
            }

            OnPropertyChanged(nameof(IsAnyMenuOpen));
        }
    }

    /// <summary>正在跑的那一个任务（没有时为 <see langword="null"/>）。</summary>
    public DownloadQueueRowViewModel? ActiveQueueTask
    {
        get => _activeQueueTask;
        private set => SetProperty(ref _activeQueueTask, value);
    }

    /// <summary>排队中的任务（按入队顺序）。</summary>
    public IReadOnlyList<DownloadQueueRowViewModel> WaitingQueueTasks
    {
        get => _waitingQueueTasks;
        private set => SetProperty(ref _waitingQueueTasks, value);
    }

    /// <summary>最近结束的两行（新的在前）：完成 / 失败 / 已取消。</summary>
    public IReadOnlyList<DownloadQueueRowViewModel> RecentQueueTasks
    {
        get => _recentQueueTasks;
        private set => SetProperty(ref _recentQueueTasks, value);
    }

    /// <summary>进行中任务的百分比（0–100），下拉里的细进度条用它。</summary>
    public double ActiveQueuePercent
    {
        get => _activeQueuePercent;
        private set => SetProperty(ref _activeQueuePercent, value);
    }

    /// <summary>进行中任务的一行明细：已下载/总计 · 速度 · 预计剩余（纯数据，按语言无关的 ` · ` 连接）。</summary>
    public string ActiveQueueMeta
    {
        get => _activeQueueMeta;
        private set => SetProperty(ref _activeQueueMeta, value);
    }

    /// <summary>是否有进行中的任务（决定下拉里那一块是否显示）。</summary>
    public bool HasActiveQueueTask => ActiveQueueTask is not null;

    /// <summary>是否有排队中的任务。</summary>
    public bool HasWaitingQueueTasks => WaitingQueueTasks.Count > 0;

    /// <summary>是否有最近结束的任务。</summary>
    public bool HasRecentQueueTasks => RecentQueueTasks.Count > 0;

    /// <summary>队列完全为空（下拉显示空状态，而不是一排假行）。</summary>
    public bool HasNoQueueTasks => !HasActiveQueueTask && !HasWaitingQueueTasks && !HasRecentQueueTasks;

    /// <summary>还有可以取消的任务时才可按「取消全部」。</summary>
    public bool CanCancelAllQueueTasks =>
        ActiveQueueTask is { CanAct: true, IsRetry: false }
        || WaitingQueueTasks.Any(static row => row.CanAct && !row.IsRetry);

    /// <summary>下拉头行右侧的计数文本。</summary>
    public string QueueMenuSummary
    {
        get
        {
            var running = ActiveQueueTask is null ? 0 : 1;
            var queued = WaitingQueueTasks.Count;
            return Loc.T("{0} 个进行中 · {1} 个排队", running, queued);
        }
    }

    /// <summary>打开 / 收起队列下拉。</summary>
    public IRelayCommand ToggleQueueMenuCommand =>
        _toggleQueueMenuCommand ??= new RelayCommand(ToggleQueueMenu);

    /// <summary>打开 p06 下载与队列（下拉头行、底行与行本体共用）。</summary>
    public IRelayCommand OpenQueuePageCommand =>
        _openQueuePageCommand ??= new RelayCommand(OpenQueuePage);

    /// <summary>行内动作：未结束的是「取消」，失败/已取消的是「重试」。</summary>
    public IRelayCommand QueueRowActionCommand =>
        _queueRowActionCommand ??= new RelayCommand<DownloadQueueRowViewModel>(QueueRowAction);

    /// <summary>取消所有未结束的任务（与 p06 的「取消全部」同一个动作）。</summary>
    public IRelayCommand CancelAllQueueCommand =>
        _cancelAllQueueCommand ??= new RelayCommand(CancelAllQueue);

    /// <summary>把当前队列投影成下拉需要的三段（进行中 / 排队 / 最近结束）。</summary>
    /// <remarks>
    /// 每次展开、每次队列变化都重算一次：队列行带着"那一刻的进度"，留着旧行会显示过期数字。
    /// 行投影复用 p06 的 <see cref="DownloadQueueRowViewModel.From"/>，所以两处的「失败：」前缀、
    /// 取消/重试的可执行性与文案完全一致。
    /// </remarks>
    private void RebuildQueueMenu()
    {
        var rows = _downloads.GetQueue()
            .Select(static item => DownloadQueueRowViewModel.From(item, DownloadQueueRowViewModel.Describe(item)))
            .ToList();

        ActiveQueueTask = rows.FirstOrDefault(static row => row.State == DownloadQueueState.Running);
        WaitingQueueTasks = [.. rows.Where(static row => row.State == DownloadQueueState.Queued)];

        // 最近结束的两行：新的在前。只留两行是有意的（设计稿 p18）——更多历史去 p06。
        RecentQueueTasks =
        [
            .. rows
                .Where(static row => row.State is DownloadQueueState.Completed or DownloadQueueState.Failed or DownloadQueueState.Cancelled)
                .TakeLast(2)
                .Reverse(),
        ];

        var progress = _downloads.GetProgress();
        ActiveQueuePercent = progress.Percent;
        ActiveQueueMeta = ComposeMeta(progress);

        // 空属性名通知：HasActiveQueueTask / QueueMenuSummary / CanCancelAllQueueTasks
        // 这些派生属性一并重算（与 p06 的 Refresh 同一条规矩）。
        // 「取消全部」的可执行性跟着队列走（没有可取消的任务时由命令自身禁用，不靠界面绑 IsEnabled）。
        _cancelAllQueueCommand?.NotifyCanExecuteChanged();

        OnPropertyChanged(string.Empty);
    }

    /// <summary>进行中任务的明细：`已下载/总计 · 速度 · 预计剩余`（纯数据，` · ` 只是分隔符）。</summary>
    private static string ComposeMeta(DownloadProgress progress)
    {
        var remaining = LocalizedText.Of(progress.RemainingText);

        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(progress.AmountText))
        {
            parts.Add(progress.AmountText);
        }

        if (!string.IsNullOrEmpty(progress.SpeedText))
        {
            parts.Add(progress.SpeedText);
        }

        if (!string.IsNullOrEmpty(remaining))
        {
            parts.Add(remaining);
        }

        return string.Join(" · ", parts);
    }

    private void ToggleQueueMenu()
    {
        if (IsQueueMenuOpen)
        {
            IsQueueMenuOpen = false;
            return;
        }

        // 展开时重新读一次队列：菜单关着的时候队列可能已经变了（这里不能等 Changed 的节流）。
        RebuildQueueMenu();
        IsQueueMenuOpen = true;
    }

    private void CloseMenus()
    {
        IsQueueMenuOpen = false;
        IsAccountMenuOpen = false;
    }

    private void OpenQueuePage()
    {
        IsQueueMenuOpen = false;
        _navigation.Navigate(PageId.Downloads);
    }

    private void QueueRowAction(DownloadQueueRowViewModel? row)
    {
        if (row is null || !row.CanAct)
        {
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

        // 不等节流器：用户刚点的那一下必须立刻看见（与 p06 的 QueueAction 同一条规矩）。
        RebuildQueueMenu();
    }

    private void CancelAllQueue()
    {
        var unfinished = new List<string>(1);
        if (ActiveQueueTask is { CanAct: true, IsRetry: false } active)
        {
            unfinished.Add(active.Id);
        }

        foreach (var row in WaitingQueueTasks)
        {
            if (row.CanAct && !row.IsRetry)
            {
                unfinished.Add(row.Id);
            }
        }

        foreach (var id in unfinished)
        {
            _downloads.Cancel(id);
        }

        RebuildQueueMenu();
    }

    /// <summary>
    /// 队列变化（后台线程）→ 只有在菜单展开时才重建下拉；
    /// 徽标与页头忙碌态仍然每次都更新（它们在菜单关着时也要对）。
    /// </summary>
    private void OnDownloadsChangedForMenu()
    {
        if (IsQueueMenuOpen)
        {
            RebuildQueueMenu();
        }
    }

    /// <summary>语言切换 / 首次初始化时也重算一次（行里的标题与明细是拼好的句子）。</summary>
    private void RefreshQueueMenuForLanguage() => RebuildQueueMenu();
}
