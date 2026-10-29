using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// 事件表「筛选器」与「全部级别」的共用实现（p06 的事件流、p03/p13 的事件区块）。
/// </summary>
/// <remarks>
/// 设计稿里 p03/p13 的事件区块有「筛选器 + 全部级别」、p06 只有「筛选器」（p01/p17 两页都没有筛选行），
/// 所以三个有筛选行的页面共用这一层。**筛选发生在界面层**（内核只给"这个实例的 / 这个实例设置变更的"），
/// 与 §12.9 Q13 定的"排序在界面层、内核保持默认顺序"是同一条分工。
/// <para>
/// 没有筛选条件时返回的**就是原列表本身**，不做复制 —— 事件表在下载期间每次队列变化都会重算，
/// 白拷一遍两百行没有意义。
/// </para>
/// <para>
/// 级别选项**每次读属性时重建**（`Loc.T` 在那一刻求值），并在 <see cref="OnLanguageChanged"/> 里通知一次：
/// 否则切语言后下拉框里的「全部级别 / 信息 / 成功…」会留在旧语言（`AGENTS.md` §5.3 那条红线的同类问题）。
/// 选中的级别按**值**记住，所以重建选项不会把用户的选择丢掉。
/// </para>
/// </remarks>
internal abstract partial class EventListPageViewModel : PageViewModelBase
{
    private EventLevel? _selectedLevel;
    private IReadOnlyList<ChoiceOption<EventLevel?>> _eventLevelChoices = [];

    /// <summary>排序方向：false = 内核默认的「新 → 旧」，true = 反过来。</summary>
    private bool _eventSortAscending;

    /// <summary>构造事件表页面。</summary>
    /// <param name="page">页面标识。</param>
    /// <param name="titleKey">页面标题键（面包屑与区块标题用）。</param>
    protected EventListPageViewModel(PageId page, string titleKey)
        : base(page, titleKey)
    {
        RebuildLevelChoices();
    }

    /// <summary>筛选框里的关键字（空 = 不按关键字过滤）。</summary>
    [ObservableProperty]
    private string _eventFilterText = string.Empty;

    /// <summary>级别下拉的选项（第一项是「全部级别」）。</summary>
    public IReadOnlyList<ChoiceOption<EventLevel?>> EventLevelChoices => _eventLevelChoices;

    /// <summary>
    /// 级别下拉的当前选择。
    /// </summary>
    /// <remarks>
    /// 按**值**存取（不是按实例）：<see cref="EventLevelChoices"/> 每次重建，绑一个具体实例会在切语言后丢选择。
    /// </remarks>
    public ChoiceOption<EventLevel?> EventLevelChoice
    {
        get => EventLevelChoices.FirstOrDefault(option => option.Value == _selectedLevel) ?? EventLevelChoices[0];
        set
        {
            var level = value?.Value;

            if (level == _selectedLevel)
            {
                return;
            }

            _selectedLevel = level;
            OnPropertyChanged();
            NotifyFilterChanged();
        }
    }

    /// <summary>内核给的原始事件行（未排序；内核的默认顺序是由新到旧）。</summary>
    protected abstract IReadOnlyList<EventEntry> EventRows { get; }

    /// <summary>筛选之后要显示的事件行（再按当前排序方向排列）。</summary>
    public IReadOnlyList<EventEntry> FilteredEvents
    {
        get
        {
            var rows = EventRows;

            var filtered = EventFilterText.Length == 0 && _selectedLevel is null
                ? rows
                : [.. rows.Where(Matches)];

            return _eventSortAscending ? [.. filtered.Reverse()] : filtered;
        }
    }

    /// <summary>当前排序方向（false = 内核默认的「新 → 旧」）。</summary>
    public bool IsEventSortAscending => _eventSortAscending;

    /// <inheritdoc />
    public override bool CanSearchEvents => true;

    /// <inheritdoc />
    public override IReadOnlyList<EventEntry> SearchableEvents => EventRows;

    /// <summary>
    /// 切换事件排序方向（设计稿筛选行的第一颗小方钮；Q13：原 E70B 是字形用错，设计稿那一格是排序）。
    /// </summary>
    [RelayCommand]
    private void ToggleEventSort()
    {
        _eventSortAscending = !_eventSortAscending;

        OnPropertyChanged(nameof(IsEventSortAscending));
        OnPropertyChanged(nameof(FilteredEvents));
    }

    /// <summary>
    /// 立即重读事件（设计稿筛选行的第二颗小方钮）。
    /// </summary>
    /// <remarks>
    /// 事件行本身是"现算"的（每次读 <see cref="EventRows"/> 都从事件日志取一次快照），
    /// 而 <c>FileEventLog</c> 按文件戳缓存 —— 所以"重读"只需让整页绑定重新求值：
    /// 文件没变时拿到的还是同一份缓存，文件变了就是新的快照，不会为了画同一张表去白读磁盘。
    /// </remarks>
    [RelayCommand]
    private void RefreshEvents() => OnPropertyChanged(string.Empty);

    /// <summary>
    /// 筛选生效时显示在计数行右边的一句说明（空串 = 没有筛选）。
    /// </summary>
    /// <remarks>
    /// 设计稿的计数行写的是「所有事件 | 共 N 个」；一旦筛掉了东西就**必须说出来** ——
    /// 否则用户会以为日志真的只有这么多条，而被筛掉的往往正是他要找的那条。
    /// </remarks>
    public string FilterHintText => EventFilterText.Length == 0 && _selectedLevel is null
        ? string.Empty
        : Loc.T("已筛出 {0} / {1} 条", FilteredEvents.Count, EventRows.Count);

    /// <inheritdoc />
    /// <remarks>切语言时重建下拉选项（`Loc.T` 在那一刻求值），选中的级别按值保留。</remarks>
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        RebuildLevelChoices();

        OnPropertyChanged(nameof(EventLevelChoices));
        OnPropertyChanged(nameof(EventLevelChoice));
        OnPropertyChanged(nameof(FilterHintText));
    }

    /// <summary>重建级别选项（构造时与切语言时各一次）。</summary>
    private void RebuildLevelChoices() =>
        _eventLevelChoices =
        [
            new(Loc.T("全部级别"), null),
            new(Loc.T("信息"), EventLevel.Info),
            new(Loc.T("成功"), EventLevel.Success),
            new(Loc.T("警告"), EventLevel.Warning),
            new(Loc.T("错误"), EventLevel.Error),
        ];

    /// <summary>筛选条件变了：重算一次。</summary>
    partial void OnEventFilterTextChanged(string value) => NotifyFilterChanged();

    /// <summary>这一行是否命中当前筛选条件。</summary>
    /// <param name="entry">事件行。</param>
    /// <returns>是否显示。</returns>
    private bool Matches(EventEntry entry)
    {
        if (_selectedLevel is { } level && entry.Level != level)
        {
            return false;
        }

        if (EventFilterText.Length == 0)
        {
            return true;
        }

        // 关键字同时在消息、来源名与实例名里找：用户在筛选框里可能记得的是"哪台机器"而不是"哪句话"。
        var keyword = EventFilterText.Trim();

        return entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || entry.SourceText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || entry.InstanceName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyFilterChanged()
    {
        OnPropertyChanged(nameof(FilteredEvents));
        OnPropertyChanged(nameof(FilterHintText));
    }
}
