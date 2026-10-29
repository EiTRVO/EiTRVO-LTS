using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 窗口外壳的 ViewModel：左导航、面包屑、深色页头的四个全局工具、菜单栏。
/// <para>
/// 它不知道任何具体页面的内容 —— 页面内容由 <see cref="CurrentPage"/> 指向的
/// 页面 ViewModel 提供，View 用 <c>DataTemplate</c> 把它渲染成对应的 UserControl。
/// </para>
/// </summary>
internal sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IAccountCatalog _accounts;
    private readonly IDownloadCatalog _downloads;
    private readonly ILocalizationService _localization;
    private readonly IUiRefreshThrottle _throttle;
    private readonly IHelpWindowService _help;
    private readonly IEventSearchWindowService _eventSearch;

    private PageViewModelBase? _currentPage;
    private IReadOnlyList<BreadcrumbPartViewModel> _breadcrumb = [];
    private IRelayCommand? _openHelpCommand;

    /// <summary>构造外壳 ViewModel。</summary>
    /// <param name="navigation">导航服务。</param>
    /// <param name="accounts">账户目录（页头「账户」按钮的标签、账户下拉与状态）。</param>
    /// <param name="downloads">下载队列（页头「队列」徽标与队列下拉）。</param>
    /// <param name="localization">界面语言服务（切换语言后左导航、面包屑、下拉与当前页要跟着变）。</param>
    /// <param name="throttle">刷新节流（队列在后台线程报告进度）。</param>
    /// <param name="help">帮助与支持中心（独立窗口）。</param>
    /// <param name="eventSearch">事件搜索窗口。</param>
    public ShellViewModel(
        INavigationService navigation,
        IAccountCatalog accounts,
        IDownloadCatalog downloads,
        ILocalizationService localization,
        IUiRefreshThrottle throttle,
        IHelpWindowService help,
        IEventSearchWindowService eventSearch)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(throttle);
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(eventSearch);

        _navigation = navigation;
        _accounts = accounts;
        _downloads = downloads;
        _localization = localization;
        _throttle = throttle;
        _help = help;
        _eventSearch = eventSearch;

        NavigationGroups = BuildNavigation();
        NavigateCommand = new RelayCommand<object?>(OnNavigate);

        _navigation.Navigated += OnNavigated;
        _localization.PropertyChanged += OnLocalizationChanged;

        // 徽标跟着真实队列走：安装任务入队/完成时它必须变，否则页头会一直显示进应用那一刻的数字。
        _downloads.Changed += OnDownloadsChanged;

        // 两个下拉的初始快照（菜单关着，但标签与“有没有任务”在展开前就要是对的）。
        RebuildQueueMenu();
        RebuildAccountMenu();

        _navigation.Navigate(PageId.Dashboard);
    }

    /// <summary>左导航的分组结构（与设计稿的 10 项 + 2 个分组标题一致）。</summary>
    public IReadOnlyList<NavigationGroupViewModel> NavigationGroups { get; }

    /// <summary>导航命令。参数是 <see cref="PageId"/>（导航项、菜单栏、面包屑共用）。</summary>
    public IRelayCommand<object?> NavigateCommand { get; }

    /// <summary>打开帮助与支持中心（独立窗口；已打开则前置）。</summary>
    public IRelayCommand OpenHelpCommand => _openHelpCommand ??= new RelayCommand(_help.Show);

    /// <summary>
    /// Ctrl+F：只在当前页面有事件区块时打开事件搜索窗口；其它页面无反应。
    /// </summary>
    [RelayCommand]
    private void OpenEventSearch()
    {
        if (CurrentPage.HasDialog || !CurrentPage.CanSearchEvents)
        {
            return;
        }

        _eventSearch.Show(CurrentPage.SearchableEvents);
    }

    /// <summary>当前页面 ViewModel。</summary>
    public PageViewModelBase CurrentPage =>
        _currentPage ?? throw new InvalidOperationException("外壳尚未完成首次导航。");

    /// <summary>当前页面的面包屑。</summary>
    public IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb
    {
        get => _breadcrumb;
        private set => SetProperty(ref _breadcrumb, value);
    }

    /// <summary>队列工具按钮的徽标文本；未完成任务数为 0 时为空串（View 会隐藏徽标）。</summary>
    public string QueueBadge =>
        _downloads.PendingTaskCount > 0
            ? _downloads.PendingTaskCount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>队列工具按钮的提词。</summary>
    public string QueueTooltip => _downloads.PendingTaskCount > 0
        ? Loc.T("下载与队列 — {0} 个未完成任务。点击展开队列菜单，可就地取消或重试", _downloads.PendingTaskCount)
        : Loc.T("下载与队列 — 当前没有未完成任务。点击展开队列菜单查看历史");

    /// <summary>账户工具按钮的标签：未登录显示「登录」，已登录显示玩家名。</summary>
    public string AccountLabel => _accounts.GetDefaultAccount()?.PlayerName ?? Loc.T("登录");

    /// <summary>
    /// 是否**真的**有任务在进行 —— 页头那道扫光只在忙碌时动（2026-10-06）。
    /// </summary>
    /// <remarks>
    /// 判据与队列徽标同源（<see cref="IDownloadCatalog.PendingTaskCount"/>），不另立一个状态：
    /// 空闲时页头必须**完全静止** —— 一条永远在动的装饰条既在骗人（看起来总有事情在做），
    /// 又会把用户从事件表上引开，所以它只在有真实进度可表示时才出现。
    /// 系统关闭"显示动画"（辅助功能 / 省电）时由 View 那边直接不显示
    /// （<c>MainWindow.xaml</c> 里的 <c>SystemParameters.ClientAreaAnimation</c> 触发器），
    /// 这一层不掺进辅助功能的判断。
    /// </remarks>
    public bool IsBusy => _downloads.PendingTaskCount > 0;

    /// <summary>队列变更（后台线程）→ 合并成有上限的刷新，再切回 UI 线程刷新徽标、提词与页头忙碌态。</summary>
    private void OnDownloadsChanged(object? sender, EventArgs e) => _throttle.Request(() =>
    {
        OnPropertyChanged(nameof(QueueBadge));
        OnPropertyChanged(nameof(QueueTooltip));
        OnPropertyChanged(nameof(IsBusy));

        // 下拉展开着的时候，里面那三段也要跟着变；关着就不用做无用的重建。
        OnDownloadsChangedForMenu();
    });

    /// <summary>账户工具按钮的提词。</summary>
    public string AccountTooltip => _accounts.GetDefaultAccount() is null
        ? Loc.T("账户 — 尚未登录，点击展开账户菜单")
        : Loc.T("账户 — 当前为 {0}，点击展开账户菜单", AccountLabel);

    /// <summary>
    /// 账户工具按钮的点击目标。
    /// 未登录时直达「添加账户」（p15，登录动作）；已登录时进「账户」页（p11，切换/管理）。
    /// 规范里写明「不做下拉菜单：账户可能只有一个，下拉是多余层级」（p16 §2）。
    /// </summary>
    public PageId AccountTarget =>
        _accounts.GetDefaultAccount() is null ? PageId.AccountLogin : PageId.Accounts;

    /// <summary>账户工具按钮是否可用。
    /// 规范（p16 §2）只允许一种禁用：设备代码流进行中防重复发起。
    /// 由于登录在本设计稿里是一个独立页面（p15），所以判据是「当前是否已经在 p15」。
    /// </summary>
    public bool IsAccountToolEnabled => CurrentPage.Id != PageId.AccountLogin;

    // 「设置」「帮助」两个按钮的提词是固定文案，直接写在 MainWindow.xaml 的 ToolTip 上，
    // 不在这里多加两个只有一行 return 的成员（那也正好是 CA1822 想让我改成 static 的东西）。

    private static List<NavigationGroupViewModel> BuildNavigation() =>
    [
        new(
            null,
            [
                new NavigationItemViewModel(NavKey.Dashboard, PageId.Dashboard, "\uEC4A", "仪表板"),
                new NavigationItemViewModel(NavKey.Instances, PageId.Instances, "\uE968", "本地实例"),
                new NavigationItemViewModel(NavKey.Versions, PageId.Versions, "\uE81E", "所有版本"),
                new NavigationItemViewModel(NavKey.Downloads, PageId.Downloads, "\uE896", "下载与队列"),
            ]),
        new(
            "资源与配置",
            [
                new NavigationItemViewModel(NavKey.Mods, PageId.Mods, "\uE7FC", "模组管理"),
                new NavigationItemViewModel(NavKey.Resources, PageId.Resources, "\uE8F1", "资源包 / 存档"),
                new NavigationItemViewModel(NavKey.Modpacks, PageId.Modpacks, "\uE7B8", "整合包"),
                new NavigationItemViewModel(NavKey.JavaSettings, PageId.JavaSettings, "\uE713", "Java 与设置"),
                new NavigationItemViewModel(NavKey.Accounts, PageId.Accounts, "\uE77B", "账户"),
            ]),
        new(
            "其他",
            [
                new NavigationItemViewModel(NavKey.About, PageId.About, "\uE946", "关于"),
            ]),
    ];

    /// <summary>
    /// 语言变了：左导航、分组标题、面包屑、页头提示与当前页面全部重新求值。
    /// <para>
    /// 之所以要在这里集中做，是因为这些文案分属不同的对象：导航项与分组标题是
    /// <c>Loc.T</c> 现算的，面包屑缓存了一份列表，页面则各自有一堆文本属性。
    /// </para>
    /// </summary>
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var group in NavigationGroups)
        {
            group.RefreshTitle();
            foreach (var item in group.Items)
            {
                item.RefreshText();
            }
        }

        // 面包屑是缓存过的列表，要重新向当前页要一份（它的各级文本是现算的）。
        if (_currentPage is not null)
        {
            Breadcrumb = _currentPage.Breadcrumb;
        }

        OnPropertyChanged(string.Empty);
        _currentPage?.OnLanguageChanged();

        // 两个下拉里的行是拼好的句子，切语言后要重新求值一次（与当前页同一处理）。
        RefreshQueueMenuForLanguage();
        RebuildAccountMenu();
    }

    private void OnNavigate(object? parameter)
    {
        if (parameter is PageId page)
        {
            _navigation.Navigate(page);
        }
    }

    private void OnNavigated(object? sender, PageViewModelBase page)
    {
        _currentPage = page;
        Breadcrumb = page.Breadcrumb;

        // 换页时收起两个下拉：菜单属于“当前上下文”，导航之后它的目标已经变了。
        IsQueueMenuOpen = false;
        IsAccountMenuOpen = false;

        foreach (var group in NavigationGroups)
        {
            foreach (var item in group.Items)
            {
                item.IsActive = page.NavKey != NavKey.None && item.Key == page.NavKey;
            }
        }

        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(IsAccountToolEnabled));

        // 让页面按「当前实例」重新求值一次 —— 在列表里换了实例再进详情/设置时才不会显示旧数据。
        page.OnActivated();
    }
}
