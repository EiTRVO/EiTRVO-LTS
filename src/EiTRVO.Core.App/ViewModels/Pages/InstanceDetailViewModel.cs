using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p03 实例详情。对应 mockup/pages/p03-instance-detail.html。
/// <para>
/// 左导航高亮「本地实例」（它属于实例这个分域）。面包屑是两级
/// 「本地实例 › 1.20.4-Forge」，点前一级回实例列表。
/// </para>
/// <para>
/// 注意这一页 <b>没有</b>「启动游戏」按钮 —— 启动的唯一入口在 p02（见连通性表）。
/// </para>
/// <para>
/// 所有属性都从当前实例现算：在 p02 里换成 1.20.1-Fabric 再进这一页，显示的就是它。
/// </para>
/// </summary>
internal sealed partial class InstanceDetailViewModel : EventListPageViewModel
{
    private readonly IInstanceCatalog _instances;
    private readonly IEventLog _events;
    private readonly IShellService _shell;
    private readonly InstanceFileVerificationPresenter _verification;

    /// <summary>构造实例详情 ViewModel。</summary>
    /// <param name="instances">实例目录。</param>
    /// <param name="events">事件日志。</param>
    /// <param name="shell">外壳服务（打开游戏目录）。</param>
    /// <param name="verifier">文件校验服务。</param>
    public InstanceDetailViewModel(
        IInstanceCatalog instances,
        IEventLog events,
        IShellService shell,
        IInstanceFileVerifier? verifier = null)
        : base(PageId.InstanceDetail, "实例详情")
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(shell);

        _instances = instances;
        _events = events;

        // M6-f：事件表要跟着新事件走（通知已在 UI 线程上、且已合并过）。
        _events.Changed += OnEventsChanged;
        _shell = shell;
        _verification = new InstanceFileVerificationPresenter(
            verifier ?? new UnavailableInstanceFileVerifier(),
            () => HasInstance ? _instances.ActiveInstanceName : null,
            () => OnPropertyChanged(nameof(Detail)));
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Instances;

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T("用于 {0}", Detail.Name);

    // 这一块的标题是「属性」而不是页面名「实例详情」—— 与 Server Manager 的「属性」栏同名，
    // 也正是设计稿 p03 的写法。
    /// <inheritdoc />
    public override string SectionTitle => Loc.T("属性");

    // 面包屑末级是这个实例名（而不是页面名「实例详情」），所以这里不走 NestedCrumb 的默认末级。
    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb =>
    [
        new BreadcrumbPartViewModel(Loc.T("本地实例"), PageId.Instances, ShowSeparator: false),
        new BreadcrumbPartViewModel(Detail.Name, null, ShowSeparator: true),
    ];

    /// <summary>
    /// 当前是否存在实例上下文。
    /// <para>
    /// 「一个实例都没有」是首次运行的正常状态（接了真实实例目录之后，界面上不再有那 6 个演示实例）：
    /// 这时详情页展示占位值而不是抛异常 —— 这个属性还会走外壳的面包屑，抛异常会直接让导航崩掉。
    /// </para>
    /// </summary>
    public bool HasInstance => _instances.ActiveInstanceName.Length > 0;

    /// <summary>实例属性（当前实例）。</summary>
    public InstanceDetailPanelViewModel Detail
    {
        get
        {
            var detail = HasInstance
                ? InstanceDetailPanelViewModel.From(_instances.GetDetail(_instances.ActiveInstanceName))
                : InstanceDetailPanelViewModel.Empty;

            return _verification.StatusTextFor(_instances.ActiveInstanceName) is { } verification
                ? detail with { ResourceVerification = verification }
                : detail;
        }
    }

    /// <summary>「校验文件」：按版本 JSON 的 SHA-1 校验客户端 JAR 与库。</summary>
    public IAsyncRelayCommand VerifyFilesCommand => _verification.Command;

    /// <inheritdoc />
    /// <remarks>
    /// p03 是单例页面：从 A 实例切到 B 后再导航进来时，属性面板必须重新按当前实例求值，
    /// 不能保留 A 的 Detail 或校验结果。
    /// </remarks>
    public override void OnActivated()
    {
        base.OnActivated();

        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(SectionSubtitle));
        OnPropertyChanged(nameof(Breadcrumb));
    }

    /// <summary>实例操作入口（当前实例）。</summary>
    public IReadOnlyList<InstanceActionItemViewModel> Actions =>
        HasInstance
            ? InstanceActionItemViewModel.Map(_instances.GetActions(_instances.ActiveInstanceName))
            : [];

    /// <summary>该实例的事件。</summary>
    /// <summary>有新事件写入（通知已在 UI 线程上、且已按节流合并）。</summary>
    private void OnEventsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Events));
        OnPropertyChanged(nameof(EventTotal));
    }

    public IReadOnlyList<EventEntry> Events =>
        HasInstance ? _events.GetInstanceEvents(_instances.ActiveInstanceName) : [];

    /// <inheritdoc />
    protected override IReadOnlyList<EventEntry> EventRows => Events;

    /// <summary>事件总数。</summary>
    public int EventTotal => HasInstance ? _events.TotalCount : 0;

    /// <summary>事件表上方的统计文本。</summary>
    public string EventCountText => FormatCount(Loc.T("所有事件"), EventTotal, Loc.T("项"));

    /// <summary>用资源管理器打开游戏目录（连通性表：打开目录走外壳，不切页）。</summary>
    [RelayCommand]
    private void OpenGameFolder()
    {
        if (!HasInstance)
        {
            return;
        }

        _shell.OpenFolder(Detail.InstancePath);
    }
}
