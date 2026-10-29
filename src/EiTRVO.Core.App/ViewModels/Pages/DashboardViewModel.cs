using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p01 仪表板。对应 mockup/pages/p01-dashboard.html。
/// <para>
/// 结构：区块标题 → 属性表（左半是当前实例、右半是本机信息）→
/// 实例快切列表 → 动作条 → 1px 分隔线 → 事件表（填满剩余高度）。
/// </para>
/// <para>
/// 所有「当前实例」相关的值都是**现算**的：在实例列表里换了实例再回到这一页，
/// 显示的就是新实例（见 <see cref="PageViewModelBase.OnActivated"/>）。
/// </para>
/// </summary>
internal sealed partial class DashboardViewModel : EventListPageViewModel
{
    private readonly IInstanceCatalog _instances;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IEventLog _events;
    private readonly INavigationService _navigation;
    private readonly IAccountCatalog _accounts;
    private readonly IJavaRuntimeCatalog _java;

    /// <summary>构造仪表板 ViewModel。</summary>
    /// <param name="instances">实例目录。</param>
    /// <param name="systemInfo">本机信息。</param>
    /// <param name="events">事件日志。</param>
    /// <param name="navigation">导航服务（快切列表点一行就进该实例的详情）。</param>
    /// <param name="accounts">账户目录（首次运行清单判断"有没有账户"）。</param>
    /// <param name="java">Java 检测结果（首次运行清单判断"检测过但没有 Java"）。</param>
    public DashboardViewModel(
        IInstanceCatalog instances,
        ISystemInfoProvider systemInfo,
        IEventLog events,
        INavigationService navigation,
        IAccountCatalog accounts,
        IJavaRuntimeCatalog java)
        : base(PageId.Dashboard, "仪表板")
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(systemInfo);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(java);

        _instances = instances;
        _systemInfo = systemInfo;
        _events = events;

        // M6-f：内核写事件（后台线程）→ FileEventLog 合并并切回 UI 线程 → 这里重读两格。
        _events.Changed += OnEventsChanged;
        _navigation = navigation;
        _accounts = accounts;
        _java = java;
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Dashboard;

    // 设计稿此处写的是「用于 EiTRVO Core」。但 AGENTS.md §0/§7 规定产品名只出现在窗口标题
    // 与深色标题栏左端，所以这里改回 Server Manager 的原意「用于 <当前对象>」（原图是「用于 <计算机名>」）。
    /// <summary>
    /// 首次运行清单：只列**现在真的挡住用户**的事（没有实例 / 没有账户 / 检测过但没有 Java）。
    /// </summary>
    /// <remarks>
    /// 每行带一个去向按钮，而不是只写一句提示 —— 首次运行的人需要知道"下一步点哪里"。
    /// 没有检测结果时**不列 Java**：把"还没查过"说成"没有 Java"会让人去处理一个不存在的故障。
    /// </remarks>
    public IReadOnlyList<FirstRunIssueViewModel> FirstRunIssues { get; private set; } = [];

    /// <summary>是否有需要注意的首次运行事项（没有时不占高度）。</summary>
    public bool HasFirstRunIssues => FirstRunIssues.Count > 0;

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();

        List<FirstRunIssueViewModel> issues = [];

        if (!HasInstance)
        {
            issues.Add(new(
                Loc.T("还没有实例"),
                Loc.T("先新建一个实例：游戏、加载器与 Java 都会在向导里处理。"),
                Loc.T("新建实例"),
                new RelayCommand(() => _navigation.Navigate(PageId.NewInstance))));
        }

        if (_accounts.GetAccounts().Count == 0)
        {
            issues.Add(new(
                Loc.T("还没有账户"),
                Loc.T("您需要登录一个有效的微软账号，否则启动器部分功能将受限"),
                Loc.T("添加账户"),
                new RelayCommand(() => _navigation.Navigate(PageId.Accounts))));
        }

        if (_java.HasDetectionResult && _java.GetInstallations().Count == 0)
        {
            issues.Add(new(
                Loc.T("没有检测到 Java"),
                Loc.T("启动器可以按版本自动匹配，也可以在「Java 与设置」里安装官方 JRE 或指定路径。"),
                Loc.T("Java 与设置"),
                new RelayCommand(() => _navigation.Navigate(PageId.JavaSettings))));
        }

        FirstRunIssues = issues;
        OnPropertyChanged(nameof(FirstRunIssues));
        OnPropertyChanged(nameof(HasFirstRunIssues));
    }

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T("用于 {0}", Detail.Name);

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>当前是否有实例上下文（没有实例时属性表显示占位值，而不是抛异常）。</summary>
    public bool HasInstance => _instances.ActiveInstanceName.Length > 0;

    /// <summary>当前实例的完整属性（属性表左半部分取自它）。</summary>
    /// <remarks>
    /// 没有实例时返回 <see cref="InstancePlaceholder.Detail"/> 的投影，**绝不去问内核"空名字的详情"**：
    /// 仪表板是启动页，首次运行时它必然在"零实例"状态下被渲染。
    /// 这里少一个判断的后果不是显示错一行字，而是新用户第一次打开就崩
    /// （`GetDetail("")` 抛 <c>KeyNotFoundException</c>；<c>SectionSubtitle</c> 与面包屑还会
    /// 在外壳导航时同步求值，所以崩的是整个窗口）。
    /// </remarks>
    public InstanceDetailPanelViewModel Detail =>
        HasInstance
            ? InstanceDetailPanelViewModel.From(_instances.GetDetail(_instances.ActiveInstanceName))
            : InstanceDetailPanelViewModel.Empty;

    /// <summary>本机信息（属性表右半部分）。</summary>
    public ISystemInfoProvider SystemInfo => _systemInfo;

    /// <summary>实例快切列表。高亮项 = 当前实例。</summary>
    public IReadOnlyList<InstanceOverviewRowViewModel> Overview =>
    [
        .. _instances.GetOverview().Select(item => new InstanceOverviewRowViewModel(
            item.Name,
            OverviewSummary(item),
            LocalizedText.Of(item.BadgeText),
            item.BadgeTone,
            IsActive: string.Equals(item.Name, _instances.ActiveInstanceName, StringComparison.Ordinal))),
    ];

    /// <summary>
    /// 快切行左侧的摘要：「加载器 · N 个模组 · 上次 &lt;时间&gt;」。
    /// </summary>
    /// <remarks>
    /// **内核只给原料**（加载器模板 / 模组数 / 时间模板），句子在这里拼 —— 理由见
    /// AGENTS.md §5.3 第 ⑤ 条：加载器那一段**自己是一个模板**，内核渲染不了它，
    /// 把它当参数传出去只会露出**键**。所以这里先渲染内层当参数，再把时间戳接在后面（第 ⑦ 条）。
    /// </remarks>
    private static string OverviewSummary(InstanceOverview item) =>
        LocalizedText.Concat(
            Loc.T(
                "{0} · {1} 个模组 · 上次",
                LocalizedText.Of(item.LoaderDisplay),
                item.ModCount.ToString(CultureInfo.InvariantCulture)),
            item.LastLaunched);

    /// <summary>事件表数据（筛选 / 排序后的结果，供既有测试与页面绑定）。</summary>
    public IReadOnlyList<EventEntry> Events => FilteredEvents;

    /// <inheritdoc />
    protected override IReadOnlyList<EventEntry> EventRows => _events.GetEvents();

    /// <summary>有新事件写入（通知已在 UI 线程上、且已按节流合并）。</summary>
    private void OnEventsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Events));
        OnPropertyChanged(nameof(FilteredEvents));
        OnPropertyChanged(nameof(EventTotal));
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(FilterHintText));
    }

    /// <summary>事件总数（不受筛选影响）。</summary>
    public int EventTotal => _events.TotalCount;

    /// <summary>事件表上方那行统计文本（沿用实例详情的口径：总数，不随筛选变化）。</summary>
    public string EventCountText => FormatCount(Loc.T("所有事件"), EventTotal, Loc.T("项"));

    /// <summary>
    /// 快切列表点一行：把「当前实例」切过去，再进它的详情。
    /// 连通性表对这一项的定义就是「选中并进入该实例」。
    /// </summary>
    /// <param name="instance">被点击的快切项。</param>
    [RelayCommand]
    private void OpenInstance(InstanceOverviewRowViewModel? instance)
    {
        if (instance is null)
        {
            return;
        }

        _instances.ActiveInstanceName = instance.Name;
        _navigation.Navigate(PageId.InstanceDetail);
    }
}

