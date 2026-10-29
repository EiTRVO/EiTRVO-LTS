using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Dialogs;
using CommunityToolkit.Mvvm.Input;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p02 本地实例。对应 mockup/pages/p02-instances.html。
/// <para>
/// 这是设计稿里 **唯一** 带「启动游戏」的页面（见 mockup/pages/index.html 的连通性表：
/// 启动 → p14 正在启动，不经实例详情）。
/// </para>
/// <para>
/// 「启动游戏」从 M4-d 起**真的启动**：先把当前实例的启动会话挂上（内核编排），
/// 再切到 p14 —— p14 显示的是同一份会话快照，所以阶段、进度、失败原因都不是演示值。
/// 启动本身在后台跑，界面不阻塞。
/// </para>
/// </summary>
internal sealed partial class InstancesViewModel : PageViewModelBase
{
    private readonly IInstanceCatalog _instances;
    private readonly IGameLauncher _launcher;
    private readonly INavigationService _navigation;
    private readonly RenameInstanceDialogViewModel _renameDialog;
    private readonly DeleteConfirmDialogViewModel _deleteDialog;
    private bool _isRenameDialogOpen;
    private bool _isDeleteDialogOpen;

    private InstanceRowViewModel? _selectedInstance;

    /// <summary>
    /// 上次从内核读到的实例行。选中行只改 <see cref="InstanceRowViewModel.IsActive"/>，
    /// 不再为了刷新勾选标记重新扫描所有实例目录。
    /// </summary>
    private IReadOnlyList<InstanceRowViewModel> _rows = [];

    /// <summary>p02 的筛选词（空 = 不筛选）。</summary>
    private string _filterText = string.Empty;

    /// <summary>列表方向：false = 内核默认的「新 → 旧」，true = 反过来。</summary>
    private bool _sortAscending;

    /// <summary>构造本地实例 ViewModel。</summary>
    /// <param name="instances">实例目录。</param>
    /// <param name="launcher">启动编排（「启动游戏」用它）。</param>
    /// <param name="navigation">导航（启动后切到 p14）。</param>
    /// <param name="renameDialog">「重命名」对话框。</param>
    /// <param name="deleteDialog">「删除实例」确认框（从本页打开时背景保持本地实例页）。</param>
    public InstancesViewModel(
        IInstanceCatalog instances,
        IGameLauncher launcher,
        INavigationService navigation,
        RenameInstanceDialogViewModel renameDialog,
        DeleteConfirmDialogViewModel deleteDialog)
        : base(PageId.Instances, "本地实例")
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(renameDialog);
        ArgumentNullException.ThrowIfNull(deleteDialog);

        _instances = instances;
        _launcher = launcher;
        _navigation = navigation;
        _renameDialog = renameDialog;
        _deleteDialog = deleteDialog;
        _renameDialog.Closed += OnRenameClosed;
        _deleteDialog.Closed += OnDeleteClosed;

        ReloadRows();

        // 设计稿开局就把「当前实例」那一行画成高亮行。这里用「预先选中它」来复现：
        // 之后用户点别的行，高亮跟着走，而首列那枚对勾仍留在真正的当前实例上。
        _selectedInstance = _rows.FirstOrDefault(static row => row.IsActive);
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Instances;

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();
        ReloadRows();
        _selectedInstance = _rows.FirstOrDefault(static row => row.IsActive)
            ?? (_rows.Count > 0 ? _rows[0] : null);
        OnPropertyChanged(nameof(SelectedInstance));
        OnPropertyChanged(nameof(Instances));
        OnPropertyChanged(nameof(SectionSubtitle));
    }

    /// <inheritdoc />
    public override string SectionSubtitle => HasFilter
        ? Loc.T("共 {0} 个实例 · 筛选出 {1} 个", _rows.Count, Instances.Count)
        : Loc.T("共 {0} 个实例", _rows.Count);

    /// <summary>筛选框里有没有内容（空 = 全量列表）。</summary>
    public bool HasFilter => FilterText.Trim().Length > 0;

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>
    /// p02 的筛选词。按实例名做**本地**过滤（只影响本页表格，不改内核的实例列表）。
    /// </summary>
    /// <remarks>
    /// 设计稿的筛选框是控制台样式的静态框（p05 已在 M2.2 换成真输入框）。
    /// 这里同样做成真输入框：输入即过滤、清空即还原；筛选只影响本页，
    /// 「当前实例」与启动目标不因为"看不见某一行"而改变。
    /// </remarks>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Instances));
                OnPropertyChanged(nameof(HasFilter));
                OnPropertyChanged(nameof(SectionSubtitle));
                OnPropertyChanged(nameof(SortHintText));
            }
        }
    }

    /// <summary>排序方向（false = 内核默认的「新 → 旧」，true = 反过来）。</summary>
    public bool SortAscending
    {
        get => _sortAscending;
        private set
        {
            if (!SetProperty(ref _sortAscending, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Instances));
            OnPropertyChanged(nameof(SortHintText));
        }
    }

    /// <summary>筛选行右侧那句排序说明（设计稿是「按上次启动排序」，这里补上方向）。</summary>
    public string SortHintText => SortAscending
        ? Loc.T("按上次启动排序（旧 → 新）")
        : Loc.T("按上次启动排序（新 → 旧）");

    /// <summary>
    /// 表格行。首列那枚「当前实例」对勾按 <see cref="IInstanceCatalog.ActiveInstanceName"/> 现算，
    /// 所以它会跟着选中一起移动，而不是钉死在演示数据的初始值上。
    /// </summary>
    /// <remarks>
    /// 筛选与排序都只在**界面层**做：内核给出的顺序就是设计稿副标题写的「按上次启动排序」，
    /// 这里只决定"看不看得见"与"要不要反过来"。排序键名（而不是时间）：内核的
    /// <c>InstanceRecord</c> 只把「上次启动」以本地化文本交出来，没有可排序的原始时刻 ——
    /// 所以这里反转的是内核已经排好的顺序，方向因此仍然以「上次启动」为准，而不是按名字重排。
    /// </remarks>
    public IReadOnlyList<InstanceRowViewModel> Instances
    {
        get
        {
            IEnumerable<InstanceRowViewModel> rows = _rows;

            var keyword = FilterText.Trim();

            if (keyword.Length > 0)
            {
                rows = rows.Where(row => row.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
            }

            var filtered = rows.ToList();
            return SortAscending ? [.. filtered.AsEnumerable().Reverse()] : filtered;
        }
    }

    /// <summary>
    /// 重新从内核读一次实例行（只在激活页面、刷新、删除/重命名之后调用）。
    /// </summary>
    /// <remarks>
    /// 快速点选不同实例时**不能**每次都走 <c>GetInstances()</c>：它会同步枚举每个实例目录并计算占用空间，
    /// 实例多或目录大时会把 UI 线程卡住。选中行只改已有行的 <c>IsActive</c>，不再重扫磁盘。
    /// </remarks>
    private void ReloadRows()
    {
        var activeName = _instances.ActiveInstanceName;

        _rows =
        [
            .. _instances.GetInstances().Select(row => new InstanceRowViewModel(
                row.Name,
                LocalizedText.Of(row.GameVersion),
                LocalizedText.Of(row.LoaderDisplay),
                row.SizeOnDisk,
                LocalizedText.Of(row.LastLaunched),
                LocalizedText.Of(row.StatusText),
                row.StatusTone,
                isActive: string.Equals(row.Name, activeName, StringComparison.Ordinal))),
        ];
    }

    /// <summary>
    /// 当前选中的行。选中即切换「当前实例」，之后的页面都以它为准。
    /// 由 <c>DataGrid.SelectedItem</c> 双向绑定驱动（点击行 = 设置本属性）。
    /// </summary>
    public InstanceRowViewModel? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value) || value is null)
            {
                return;
            }

            _instances.ActiveInstanceName = value.Name;

            // 只更新可观察的 IsActive；不重建 ItemsSource，也不重新扫描实例目录。
            foreach (var row in _rows)
            {
                row.IsActive = string.Equals(row.Name, value.Name, StringComparison.Ordinal);
            }

            OnPropertyChanged(nameof(SectionSubtitle));
            OpenRenameCommand.NotifyCanExecuteChanged();
            OpenDeleteCommand.NotifyCanExecuteChanged();
        }
    }

    /// <inheritdoc />
    public override bool HasDialog => _isRenameDialogOpen || _isDeleteDialogOpen;

    /// <inheritdoc />
    public override object? Dialog => _isDeleteDialogOpen
        ? _deleteDialog
        : _isRenameDialogOpen ? _renameDialog : null;

    /// <summary>「删除」：在 p02 自己的页面上打开确认框，不切到实例详情背景。</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void OpenDelete()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        _deleteDialog.Open(fromInstances: true);
        _isDeleteDialogOpen = true;
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(Dialog));
    }

    private bool CanDelete() => SelectedInstance is not null;

    private void OnDeleteClosed(object? sender, EventArgs e)
    {
        _isDeleteDialogOpen = false;
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(Dialog));

        // 删除成功后列表少一行；先重读磁盘，再选回内核当前的「当前实例」，没有就选第一行。
        ReloadRows();
        var rows = Instances;
        _selectedInstance = rows.FirstOrDefault(static row => row.IsActive) ?? (rows.Count > 0 ? rows[0] : null);
        OnPropertyChanged(nameof(SelectedInstance));
        OnPropertyChanged(string.Empty);
        OpenRenameCommand.NotifyCanExecuteChanged();
        OpenDeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>「重命名」：打开对话框，真的移动实例目录。</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void OpenRename()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        _renameDialog.Reset(SelectedInstance.Name);
        _isRenameDialogOpen = true;
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(Dialog));
    }

    private bool CanRename() => SelectedInstance is not null;

    private void OnRenameClosed(object? sender, RenameInstanceClosedEventArgs e)
    {
        _isRenameDialogOpen = false;
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(Dialog));

        if (string.IsNullOrWhiteSpace(e.NewName))
        {
            return;
        }

        // 目录已经换成新名字：重读行、把新名字选为当前实例。
        ReloadRows();
        OnPropertyChanged(string.Empty);
        SelectedInstance = Instances.FirstOrDefault(row =>
            string.Equals(row.Name, e.NewName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>没有实例时「启动游戏」不可点（零实例是首次运行的正常状态）。</summary>
    public bool CanStartGame => _instances.ActiveInstanceName.Length > 0;

    /// <summary>切换「按上次启动排序」的方向（设计稿筛选行第一颗小方钮；Q13 指出它应是 Sort 字形）。</summary>
    [RelayCommand]
    private void ToggleSort() => SortAscending = !SortAscending;

    /// <summary>
    /// 重新扫描实例目录（设计稿筛选行第二颗小方钮）。
    /// </summary>
    /// <remarks>
    /// 只有用户显式刷新/重新进入本页时才重读磁盘；日常筛选、排序、选中都只动内存里的行。
    /// </remarks>
    [RelayCommand]
    private void Refresh()
    {
        ReloadRows();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// 启动当前实例。
    /// <para>
    /// 先切到 p14 再启动：p14 一进去就能看到"正在解析实例…"，而不是先停在 p02 上等；
    /// 启动是异步的，所以界面不会卡住。失败原因由会话快照带给 p14（不在这里弹框 ——
    /// ViewModel 不碰 UI 类型，这是 C1）。
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task StartGameAsync()
    {
        var instanceName = _instances.ActiveInstanceName;

        if (instanceName.Length == 0)
        {
            return;
        }

        _navigation.Navigate(PageId.Launching);

        await _launcher.LaunchAsync(instanceName).ConfigureAwait(true);
    }
}
