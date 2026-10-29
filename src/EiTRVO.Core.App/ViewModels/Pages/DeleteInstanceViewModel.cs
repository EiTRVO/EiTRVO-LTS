using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p13 删除实例。对应 mockup/pages/p13-dialog-delete-instance.html。
/// <para>
/// 设计稿把这一页画成「实例详情的属性区 + 一个居中的破坏性确认框」，
/// 所以页面内容与 p03 同源（共用 <c>InstanceOverviewPanel</c>），
/// 确认框由 <see cref="Dialog"/> 提供、由外壳画在遮罩之上。
/// </para>
/// </summary>
internal sealed partial class DeleteInstanceViewModel : EventListPageViewModel
{
    private readonly IInstanceCatalog _instances;
    private readonly IEventLog _events;
    private readonly IShellService _shell;
    private readonly Dialogs.DeleteConfirmDialogViewModel _dialog;
    private readonly InstanceFileVerificationPresenter _verification;

    /// <summary>构造删除实例页面。</summary>
    /// <param name="instances">实例目录。</param>
    /// <param name="events">事件日志。</param>
    /// <param name="shell">外壳服务（打开游戏目录）。</param>
    /// <param name="dialog">该页面自带的确认框。</param>
    /// <param name="verifier">文件校验服务。</param>
    public DeleteInstanceViewModel(
        IInstanceCatalog instances,
        IEventLog events,
        IShellService shell,
        Dialogs.DeleteConfirmDialogViewModel dialog,
        IInstanceFileVerifier? verifier = null)
        : base(PageId.DeleteInstance, "删除实例")
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(dialog);

        _instances = instances;
        _events = events;

        // M6-f：事件表要跟着新事件走（通知已在 UI 线程上、且已合并过）。
        _events.Changed += OnEventsChanged;
        _shell = shell;
        _dialog = dialog;
        Dialog = dialog;
        _verification = new InstanceFileVerificationPresenter(
            verifier ?? new UnavailableInstanceFileVerifier(),
            () => HasInstance ? _instances.ActiveInstanceName : null,
            () => OnPropertyChanged(nameof(Detail)));
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Instances;

    /// <inheritdoc />
    public override string SectionTitle => Loc.T("属性");

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T("用于 {0}", Detail.Name);

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb =>
        NestedCrumb(Detail.Name, PageId.InstanceDetail);

    /// <inheritdoc />
    public override bool HasDialog => true;

    /// <inheritdoc />
    public override object? Dialog { get; }

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();

        // 同一个确认框单例也会被 p02 借去用；从实例详情进来时必须恢复「取消回详情」语义，
        // 否则上一次从 p02 打开留下的模式会让这里的取消只收框、不回到详情。
        _dialog.Open(fromInstances: false);
    }

    /// <summary>实例属性（与 p03 同源，供共用的面板使用）。</summary>
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

    /// <summary>当前是否存在实例上下文（没有实例时这一页只展示占位值，不抛异常）。</summary>
    public bool HasInstance => _instances.ActiveInstanceName.Length > 0;

    /// <summary>实例操作入口。</summary>
    public IReadOnlyList<InstanceActionItemViewModel> Actions => HasInstance
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

    /// <summary>用资源管理器打开游戏目录。</summary>
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
