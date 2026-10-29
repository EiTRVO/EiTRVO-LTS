using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p07 模组管理。对应 mockup/pages/p07-mods.html。
/// <para>
/// 绑定的是**当前实例**的 mods 目录，所以面包屑是「1.20.4-Forge › 模组管理」，
/// 左导航高亮「模组管理」。换个实例再进来，看到的就是那个实例的模组。
/// </para>
/// <para>
/// M7-c 起「来源 / 状态 / 备注」三列是**对过账的真值**：页面被导航到时刷新一次
/// （算 SHA-1 → 问 Modrinth 要来源与更新），结果落 <c>cache\content\</c>，
/// 所以下次进页面同步读到的就已经是对的。
/// </para>
/// </summary>
internal sealed partial class ModsViewModel : PageViewModelBase
{
    private readonly IContentCatalog _content;
    private readonly IInstanceCatalog _instances;
    private readonly IShellService _shell;
    private readonly IDownloadCatalog _downloads;
    private readonly INavigationService _navigation;
    private readonly ModrinthDownloadDialogViewModel? _modrinthDialog;

    private ModRowViewModel? _selectedMod;

    /// <summary>「从 Modrinth 添加」的对话框是否打开。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    private bool _isModrinthDownloadOpen;

    /// <summary>p07 的筛选词（空 = 不筛选；按模组名匹配）。</summary>
    private string _filterText = string.Empty;

    /// <summary>来源筛选（设计稿筛选行右侧那三个词：全部 / Modrinth / 本地文件）。</summary>
    private ModSourceFilter _sourceFilter = ModSourceFilter.All;

    /// <summary>按模组名排序的方向（false = 升序）。</summary>
    private bool _sortDescending;

    /// <summary>上一次内容动作失败的原因（为空时不占高度）。</summary>
    /// <remarks>
    /// 与 p11 的 <c>ActionError</c> 同一个理由：改名 / 删除**直接改用户的磁盘**，
    /// 失败原因（文件被游戏占用、目标名已存在、文件已经不在了）必须看得见 ——
    /// 什么都显示只会让用户以为"点了没生效"然后再点一次。
    /// </remarks>
    [ObservableProperty]
    private string _actionError = string.Empty;

    /// <summary>上一次内容动作成功之后要交代的话（例如"被移进了哪个备份目录"）。</summary>
    [ObservableProperty]
    private string _actionNote = string.Empty;

    /// <summary>
    /// 模组表格行。
    /// </summary>
    /// <remarks>
    /// **现算而不是缓存**：投影里每一句都是按**当前语言**拼好的字符串 —— 缓存下来就会在切语言之后
    /// 停在旧语言（M7-c 把它改成可观察属性时正是这么错的，被 `ContentTextTests` 抓住）。
    /// 内核那边的扫描结果有自己的内容戳缓存，所以这里"现算"很便宜。
    /// <para>
    /// M6-e-3f：内核的 `Note` 是「键 + 参数」（没有备注时是 `null`），界面不能直接绑
    /// （§5.3 第 ⑨ 条），所以经 <see cref="ModRowViewModel"/> 投影；属性名与内核一致，XAML 没改。
    /// </para>
    /// </remarks>
    public IReadOnlyList<ModRowViewModel> Mods => VisibleMods();

    /// <summary>筛选框里有没有内容，或「来源」是否不是「全部」。</summary>
    public bool HasActiveFilter => FilterText.Trim().Length > 0 || _sourceFilter != ModSourceFilter.All;

    /// <summary>来源筛选：全部。</summary>
    public bool IsSourceAll => _sourceFilter == ModSourceFilter.All;

    /// <summary>来源筛选：Modrinth。</summary>
    public bool IsSourceModrinth => _sourceFilter == ModSourceFilter.Modrinth;

    /// <summary>来源筛选：本地文件（含整合包导入，二者都来自本机磁盘）。</summary>
    public bool IsSourceLocal => _sourceFilter == ModSourceFilter.Local;

    /// <summary>
    /// p07 的筛选词。按模组名做**本地**过滤（只影响本页表格，不改磁盘）。
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty))
            {
                NotifyModListChanged();
            }
        }
    }

    /// <summary>
    /// 表格空状态的文案：**没有模组**与**筛选后没有匹配**是两件事，不能说成同一句话。
    /// </summary>
    public string EmptyStateText => _content.GetMods(_instances.ActiveInstanceName).Count == 0
        ? Loc.T("该实例还没有模组：可以用「从 Modrinth 添加」，或直接把 .jar 放进 mods 目录。")
        : Loc.T("没有匹配的模组：调整筛选词或「来源」后再试。");

    /// <summary>模组统计（属性区 + 缺失依赖横幅）；加载器/版本匹配那两格是「键 + 参数」，这里拼好。</summary>
    /// <remarks>与 <see cref="Mods"/> 同理：现算，免得把"当时的语言"缓存进去。</remarks>
    public ModSummaryViewModel Summary =>
        ModSummaryViewModel.From(_content.GetModSummary(_instances.ActiveInstanceName));

    /// <summary>构造模组管理 ViewModel。</summary>
    /// <param name="content">内容目录。</param>
    /// <param name="instances">实例目录（取实例路径以拼出 mods 路径）。</param>
    /// <param name="shell">外壳服务（打开目录）。</param>
    /// <param name="downloads">下载队列（「更新所选」把任务排进去）。</param>
    /// <param name="navigation">导航（排完队跳到「下载与队列」看进度）。</param>
    /// <param name="modrinthDialog">「从 Modrinth 添加」对话框（下载模组）。</param>
    public ModsViewModel(
        IContentCatalog content,
        IInstanceCatalog instances,
        IShellService shell,
        IDownloadCatalog downloads,
        INavigationService navigation,
        ModrinthDownloadDialogViewModel? modrinthDialog = null)
        : base(PageId.Mods, "模组管理")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(navigation);


        _content = content;
        _instances = instances;
        _shell = shell;
        _downloads = downloads;
        _navigation = navigation;
        _modrinthDialog = modrinthDialog;
        if (_modrinthDialog is not null)
        {
            _modrinthDialog.Closed += (_, _) => IsModrinthDownloadOpen = false;
        }
    }

    /// <inheritdoc />
    public override bool HasDialog => IsModrinthDownloadOpen;

    /// <inheritdoc />
    public override object? Dialog => _modrinthDialog;

    /// <summary>
    /// 当前实例是否允许安装 Modrinth 模组：原版实例没有加载器，不能装模组。
    /// </summary>
    public bool CanOpenModrinth => HasInstance && !IsVanillaInstance;

    /// <summary>「从 Modrinth 添加」按钮的说明（原版实例给明确原因）。</summary>
    public string OpenModrinthDetail => !HasInstance
        ? Loc.T("先在「本地实例」里选一个实例。")
        : IsVanillaInstance
            ? Loc.T("原版实例不能安装模组；请先创建一个带加载器的实例。")
            : Loc.T("搜索 Modrinth 上的模组；下载会按当前实例的游戏版本与加载器排进「下载与队列」。");

    /// <summary>打开「从 Modrinth 添加」对话框（模组）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenModrinth))]
    private void OpenModrinth()
    {
        if (!CanOpenModrinth)
        {
            return;
        }

        _modrinthDialog?.Open(ModrinthContentKind.Mod, _instances.ActiveInstanceName);
        IsModrinthDownloadOpen = true;
    }

    /// <summary>
    /// 原版实例不能装模组；查询失败（实例已不存在）按非原版处理，由后续动作如实报错。
    /// </summary>
    private bool IsVanillaInstance
    {
        get
        {
            if (!HasInstance)
            {
                return false;
            }

            try
            {
                return _instances.GetLaunchInfo(_instances.ActiveInstanceName).Loader == LoaderKind.Vanilla;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>把缺失的必需依赖排进下载队列（p07 的「修复依赖」）。</summary>
    [RelayCommand]
    private async Task FixDependenciesAsync()
    {
        ActionError = string.Empty;
        ActionNote = string.Empty;

        var result = await _content.PrepareDependencyFixAsync(_instances.ActiveInstanceName);

        if (!result.Succeeded || result.Request is null)
        {
            ActionError = result.Failure is { } failure
                ? LocalizedText.Of(failure)
                : Loc.T("修复依赖失败，稍后再试。");
            return;
        }

        _downloads.EnqueueContentDownload(result.Request);
        _navigation.Navigate(PageId.Downloads);
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Mods;

    /// <inheritdoc />
    public override string SectionSubtitle =>
        HasActiveFilter
            ? Loc.T("用于 {0} · 共 {1} 个 · 筛选出 {2} 个", _instances.ActiveInstanceName, Summary.Enabled + Summary.Disabled, Mods.Count)
            : Loc.T("用于 {0} · 共 {1} 个", _instances.ActiveInstanceName, Summary.Enabled + Summary.Disabled);

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb =>
        NestedCrumb(_instances.ActiveInstanceName, PageId.InstanceDetail);

    /// <summary>该实例一个模组都没有（原版）——表格给个空状态提示。</summary>
    public bool HasNoMods => Mods.Count == 0;

    /// <summary>
    /// 当前列表（已应用筛选词、来源筛选与排序）。
    /// </summary>
    /// <remarks>
    /// 「来源」筛选把 <see cref="ContentSource.LocalFile"/> 与 <see cref="ContentSource.Modpack"/>
    /// 都算作「本地文件」：设计稿只画了「全部 | Modrinth | 本地文件」三个词，而整合包导入的内容
    /// 同样来自用户自己的磁盘；不能为了凑枚举值再发明一个设计稿没画的分组。
    /// </remarks>
    private List<ModRowViewModel> VisibleMods()
    {
        IEnumerable<ModRowViewModel> rows = _content.GetMods(_instances.ActiveInstanceName).Select(ModRowViewModel.From);

        var keyword = FilterText.Trim();

        if (keyword.Length > 0)
        {
            rows = rows.Where(row => row.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        }

        rows = _sourceFilter switch
        {
            ModSourceFilter.Modrinth => rows.Where(static row => row.Source == ContentSource.Modrinth),
            ModSourceFilter.Local => rows.Where(static row => row.Source != ContentSource.Modrinth),
            _ => rows,
        };

        rows = _sortDescending
            ? rows.OrderByDescending(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase);

        return [.. rows];
    }

    /// <summary>筛选词 / 来源 / 排序改了之后，让所有"现算"的绑定重新求值。</summary>
    private void NotifyModListChanged()
    {
        OnPropertyChanged(nameof(Mods));
        OnPropertyChanged(nameof(HasNoMods));
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(SectionSubtitle));
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    /// <summary>切换按模组名的排序方向（Q13：原 E70B 是字形用错，设计稿那一格是排序）。</summary>
    [RelayCommand]
    private void ToggleSort()
    {
        _sortDescending = !_sortDescending;
        NotifyModListChanged();
    }

    /// <summary>「来源：全部」。</summary>
    [RelayCommand]
    private void ShowAllSources() => SetSourceFilter(ModSourceFilter.All);

    /// <summary>「来源：Modrinth」。</summary>
    [RelayCommand]
    private void ShowModrinthSources() => SetSourceFilter(ModSourceFilter.Modrinth);

    /// <summary>「来源：本地文件」。</summary>
    [RelayCommand]
    private void ShowLocalSources() => SetSourceFilter(ModSourceFilter.Local);

    private void SetSourceFilter(ModSourceFilter filter)
    {
        if (_sourceFilter == filter)
        {
            return;
        }

        _sourceFilter = filter;
        OnPropertyChanged(nameof(IsSourceAll));
        OnPropertyChanged(nameof(IsSourceModrinth));
        OnPropertyChanged(nameof(IsSourceLocal));
        NotifyModListChanged();
    }

    /// <summary>
    /// 表格里至少有一行。
    /// </summary>
    /// <remarks>
    /// p07 的空状态与表格**互斥显示**：两者共用同一个 216px 单元格，都显示的话要么提示被
    /// 不透明的 DataGrid 整块盖住（原来的写法），要么用它盖住表头（改一次就知道难看）。
    /// </remarks>
    public bool HasMods => Mods.Count > 0;

    /// <summary>是否有缺失依赖（决定红色横幅是否显示）。</summary>
    public bool HasMissingDependency => Summary.MissingDependency > 0;

    /// <summary>
    /// 横幅的引导句「N 个模组缺失依赖」。
    /// </summary>
    /// <remarks>
    /// M7-a：这句话原来在设计稿的静态文案里写死成「1 个模组缺失依赖」，而横幅此时接的是
    /// **真实扫描结果**（<see cref="MissingDependencyDetail"/> 里是真实文件名与依赖 id）——
    /// 数字写死会让"3 个模组缺依赖"的实例上写着 1，把一条真实故障说小。
    /// </remarks>
    public string MissingDependencyCountText => Loc.T("{0} 个模组缺失依赖", Summary.MissingDependency);

    /// <summary>缺失依赖的详情文本（投影已经拼好；没有详情时是空串）。</summary>
    public string MissingDependencyDetail => Summary.MissingDependencyDetail ?? string.Empty;

    /// <summary>当前是否有实例上下文。</summary>
    public bool HasInstance => _instances.ActiveInstanceName.Length > 0;

    /// <summary>
    /// 模组表当前选中的行（由 <c>DataGrid.SelectedItem</c> 双向绑定驱动）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「所选」在这里就是**高亮的那一行**：设计稿的模组表没有勾选框（首列是启用状态图标），
    /// 所以一次只对一行做动作 —— 多选要先给表格加一列勾选框或一条
    /// <c>SelectedItems</c> 附加行为，而设计稿两样都没画。M7-d 的「更新所选」也是这条思路
    /// （它的"所选"= 这一页里所有可更新的模组，因为那件事本来就该成批做）。
    /// </para>
    /// <para>
    /// 选中行是**投影对象**（每次 <see cref="Mods"/> 求值都会换新对象），所以动作做完刷新之后
    /// 用文件名把它挑回来 —— 否则启用 / 禁用一次，高亮就没了。
    /// </para>
    /// </remarks>
    public ModRowViewModel? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                // 三颗按钮的可执行性都是"现算"的，它们不跟着属性名通知走 ——
                // 必须显式再问一次，否则选中一行后按钮仍然是灰的。
                EnableSelectedCommand.NotifyCanExecuteChanged();
                DisableSelectedCommand.NotifyCanExecuteChanged();
                DeleteSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 「一键更新」要做什么（悬停提示）：数字是真的，没有可更新的时候说清是 0。
    /// </summary>
    /// <remarks>
    /// 按钮文案本身是固定的一句（<c>{loc:Text 一键更新}</c>，XAML 里直接写），所以它不在这里；
    /// 那个「（5）」是设计稿写死的数字，真值走这条提示 —— 有意偏差登记在
    /// <c>tools\icon-label-renames.txt</c>（图标门禁按文案对账）。
    /// </remarks>
    public string UpdateSelectedDetail => !HasInstance
        ? Loc.T("先在「本地实例」里选一个实例。")
        : Summary.Updatable > 0
            ? Loc.T("把这一页里 {0} 个非最新模组一键更新到最新版本（禁用的仍保持禁用，旧的会移进备份目录）。", Summary.Updatable)
            : Loc.T("当前没有可更新的模组。");

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();

        // 每次进这一页都从干净的一次开始：上一次的失败原因 / 备份去向留在页面上会与新的内容混在一起。
        ActionError = string.Empty;
        ActionNote = string.Empty;

        // 切换实例后再进这一页，原版/带加载器的可执行性可能已经变了。
        OpenModrinthCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenModrinth));
        OnPropertyChanged(nameof(OpenModrinthDetail));

        // 命令内部不 await（外壳的 OnActivated 是同步的）：刷新完成后由命令自己通知绑定重新求值。
        RefreshCommand.Execute(null);
    }

    /// <summary>
    /// 打开 mods 目录。
    /// 连通性表对这一项的行为说明是「外壳打开资源管理器（同一入口）」，
    /// 所以它走 <see cref="IShellService"/>，不切页。
    /// </summary>
    [RelayCommand]
    private void OpenModsFolder()
    {
        // 没有实例时不把空路径交给资源管理器（与 p03 的「打开目录」同一规则）。
        if (!HasInstance)
        {
            return;
        }

        _shell.OpenFolder(Path.Combine(_instances.GetDetail(_instances.ActiveInstanceName).InstancePath, "mods"));
    }

    /// <summary>
    /// 更新这一页里所有可更新的模组（M7-d）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 设计稿的「更新所选（5）」在 HTML 里只是一行静态按钮（那一页的表根本没有勾选框），
    /// 所以"所选"在这里落地为**这一页里所有可更新的模组**：数字来自对账结果，
    /// 一个都没有时按钮直接不可按（而不是按下去什么都不发生）。
    /// </para>
    /// <para>
    /// 排完队**跳到「下载与队列」**（与 p04 建完实例后同一条理由）：一次更新要下几十上百 MB，
    /// 停在模组页只会让人以为"点了没反应"；进度、失败原因与"旧的移到哪去了"都在那一页读得到。
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUpdateSelected))]
    private void UpdateSelected()
    {
        var request = _content.CreateModUpdateRequest(_instances.ActiveInstanceName);

        if (request is null)
        {
            // 按钮本来就不可按（见 CanUpdateSelected）；真走到这里说明状态在两次求值之间变了。
            return;
        }

        _downloads.EnqueueContentDownload(request);
        _navigation.Navigate(PageId.Downloads);
    }

    /// <summary>有没有可更新的模组（没有时「更新所选」是灰的）。</summary>
    private bool CanUpdateSelected() =>
        HasInstance && _content.CreateModUpdateRequest(_instances.ActiveInstanceName) is not null;

    /// <summary>启用选中的模组（M7-g-2：去掉文件名末尾的 <c>.disabled</c>）。</summary>
    /// <remarks>
    /// 只有**已禁用**的那一行能让这颗按钮可按：对已经启用的模组按"启用"是一个不存在的动作，
    /// 让按钮亮着再无声无息地什么都不做，是这一页最容易被做成假按钮的地方。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanEnableSelected))]
    private void EnableSelected() => ApplyModAction(
        _selectedMod,
        static (content, instance, fileName) => content.SetModEnabled(instance, fileName, enabled: true));

    /// <summary>禁用选中的模组（M7-g-2：给文件名加 <c>.disabled</c>，文件不删）。</summary>
    [RelayCommand(CanExecute = nameof(CanDisableSelected))]
    private void DisableSelected() => ApplyModAction(
        _selectedMod,
        static (content, instance, fileName) => content.SetModEnabled(instance, fileName, enabled: false));

    /// <summary>删除选中的模组（M7-g-2：移进 <c>backups\mods\&lt;时间戳&gt;\</c>，不是真删）。</summary>
    /// <remarks>
    /// **没有模态确认框**：文件只是被移走，用户随时能拿回来 —— 没有一步不可逆。
    /// 恢复默认的按钮 ToolTip 会把这句话说清楚，动作完成后还会把"移到了哪里"显示在表格下面。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected() => ApplyModAction(
        _selectedMod,
        static (content, instance, fileName) => content.DeleteMod(instance, fileName));

    /// <summary>选中了一行、并且它确实是**禁用**状态时才能启用。</summary>
    private bool CanEnableSelected() =>
        HasInstance && _selectedMod is { Enablement: ModEnablement.Disabled };

    /// <summary>选中了一行、并且它是启用状态（含"未验证来源"）时才能禁用。</summary>
    private bool CanDisableSelected() =>
        HasInstance && _selectedMod is { Enablement: ModEnablement.Enabled or ModEnablement.Unverified };

    /// <summary>选中了一行就能删除（删的是文件，与启用状态无关）。</summary>
    private bool CanDeleteSelected() => HasInstance && _selectedMod is not null;

    /// <summary>
    /// 执行一次模组动作、把结果落到界面，并让整页重新求值。
    /// </summary>
    /// <param name="row">选中的行（动作的目标）。</param>
    /// <param name="action">内核动作（参数：内容目录、实例名、文件名）。</param>
    private void ApplyModAction(
        ModRowViewModel? row,
        Func<IContentCatalog, string, string, ContentActionResult> action)
    {
        if (row is null || !HasInstance)
        {
            // 按钮本来就不可按（见三个 CanExecute）；真走到这里说明状态在两次求值之间变了。
            return;
        }

        var result = action(_content, _instances.ActiveInstanceName, row.FileName);

        ActionError = result is { Succeeded: false, Message: { } failure }
            ? LocalizedText.Of(failure)
            : string.Empty;

        ActionNote = result is { Succeeded: true, Message: { } note }
            ? LocalizedText.Of(note)
            : string.Empty;

        // 行与统计都是**现算**的，而目录内容已经变了 —— 让本页所有绑定重新求值。
        OnPropertyChanged(string.Empty);

        // 动作之后**不把同一行重新选回来**：启用 / 禁用会改名（.jar ↔ .jar.disabled），
        // 而"按显示名挑回来"在两个模组同名时可能挑到**另一个文件** ——
        // 紧接着再按一次删除，删掉的就是用户没选的那个。宁可让高亮消失，让他再点一次。
        SelectedMod = null;

        // 可执行性也是现算的（选中的那一行可能已经从"已启用"变成"已禁用"）。
        EnableSelectedCommand.NotifyCanExecuteChanged();
        DisableSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 与 Modrinth 对一次账（算哈希 + 查来源 + 查更新），然后让本页的绑定重新求值。
    /// </summary>    /// <remarks>
    /// 这里**不写** <c>ConfigureAwait(false)</c>：await 之后要通知绑定，必须回到 UI 线程；
    /// 算哈希那一段在 <see cref="IContentCatalog.RefreshModsAsync"/> 内部已经放到后台线程了。
    /// </remarks>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!HasInstance)
        {
            return;
        }

        await _content.RefreshModsAsync(_instances.ActiveInstanceName);

        // 空属性名 = 让本页所有绑定重新求值（与 `PageViewModelBase.OnActivated` 同一手法）：
        // 行与统计都是**现算**的，刷新之后它们不会自己知道内核那边变了。
        OnPropertyChanged(string.Empty);

        // 命令的可执行性也是"现算"的，但它不会跟着空属性名通知走 —— 必须显式再问一次，
        // 否则刚对账出"有 5 个可更新"，按钮仍然是灰的。
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        EnableSelectedCommand.NotifyCanExecuteChanged();
        DisableSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// p07 的「来源」筛选三态（设计稿筛选行右侧：全部 / Modrinth / 本地文件）。
/// <para>
/// 定义在 App 层而不是拆进内核：这是**界面上的分组**，不是内容目录的概念；
/// 内核只认 <see cref="ContentSource"/> 的三值，界面把 <see cref="ContentSource.Modpack"/>
/// 归进「本地文件」这一个画得出来的分组里。
/// </para>
/// </summary>
internal enum ModSourceFilter
{
    /// <summary>不按来源筛。</summary>
    All,

    /// <summary>只看 Modrinth 来源。</summary>
    Modrinth,

    /// <summary>只看本地文件（含整合包导入）。</summary>
    Local,
}
