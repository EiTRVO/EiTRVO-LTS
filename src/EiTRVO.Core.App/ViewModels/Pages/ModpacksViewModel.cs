using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Versions;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p08 整合包。对应 mockup/pages/p08-modpacks.html。
/// <para>
/// 页面只保留「从 Modrinth 浏览」；「从 Modrinth 添加」也跳到这里，复用同一个浏览界面。
/// 2026-10-25 用户要求去掉「已安装的整合包」区块与标题旁的副标题。
/// </para>
/// <para>
/// M7-b 起浏览表是**真实 Modrinth 数据**：读取永远是同步的（读磁盘快照），
/// 联网发生在页面被导航到时（设计稿没有搜索框也没有刷新按钮，见 <c>ModrinthCatalog</c> 的窗口说明）。
/// </para>
/// <para>
/// M7-e-2 起「从文件导入 .mrpack」是**真的**：选文件 → 入队（扫描 → 建实例 → 装游戏与加载器 → 铺内容）
/// → 跳 p06 看进度。这一页自己不碰文件：清单要先被读懂才知道建什么实例，而那件事在队列里做。
/// </para>
/// </summary>
internal sealed partial class ModpacksViewModel : PageViewModelBase
{
    private readonly IContentCatalog _content;
    private readonly IDownloadCatalog _downloads;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IVersionCatalog _versions;
    private readonly IInstanceCatalog _instances;
    private readonly IAccountDownloadPolicy _downloadAccess;

    /// <summary>Modrinth 浏览结果。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalog))]
    [NotifyPropertyChangedFor(nameof(IsCatalogEmpty))]
    [NotifyPropertyChangedFor(nameof(CatalogEmptyText))]
    private IReadOnlyList<ModrinthEntry> _catalog;

    /// <summary>p08 的搜索词（空 = 不按关键词搜）。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>游戏版本筛选值（<see langword="null"/> = 任意版本）。</summary>
    [ObservableProperty]
    private string? _selectedGameVersion;

    /// <summary>加载器筛选值（Modrinth 的短名；<see langword="null"/> = 任意加载器）。</summary>
    [ObservableProperty]
    private string? _selectedLoader;

    /// <summary>上一次筛选查询的失败原因（空 = 没有失败；有内容时表格保留上一次结果）。</summary>
    [ObservableProperty]
    private string _catalogError = string.Empty;

    /// <summary>排序方式（对应 Modrinth search 的 index）。</summary>
    public IReadOnlyList<ChoiceOption<ModrinthSort>> SortChoices { get; } =
    [
        new(Loc.T("相关性"), ModrinthSort.Relevance),
        new(Loc.T("下载量"), ModrinthSort.Downloads),
        new(Loc.T("关注数"), ModrinthSort.Follows),
        new(Loc.T("最新发布"), ModrinthSort.Newest),
        new(Loc.T("最近更新"), ModrinthSort.Updated),
    ];

    /// <summary>当前排序方式；点标题栏的「排序」按顺序切换。</summary>
    [ObservableProperty]
    private ModrinthSort _selectedSort = ModrinthSort.Relevance;

    /// <summary>当前排序的显示文案（按钮旁边那行字）。</summary>
    public string SortText => SortChoices.First(choice => choice.Value == SelectedSort).Text;
    /// <summary>游戏版本下拉的选项（第一项是「任意版本」）。</summary>
    public IReadOnlyList<ChoiceOption<string?>> GameVersionChoices { get; }

    /// <summary>加载器下拉的选项（第一项是「任意加载器」）。</summary>
    public IReadOnlyList<ChoiceOption<string?>> LoaderChoices { get; }

    /// <summary>有没有可选的游戏版本（清单还没取到时只有「任意版本」，下拉如实置灰）。</summary>
    public bool HasGameVersionChoices => GameVersionChoices.Count > 1;

    /// <summary>
    /// 从 Modrinth 直接安装一行整合包（C）：先准备本地归档，再排进现有导入队列。
    /// </summary>
    /// <param name="entry">浏览表里的那一行。</param>
    /// <remarks>
    /// 2026-10-21：下载 `.mrpack` 也放进队列（<see cref="IDownloadCatalog.EnqueueModrinthModpack"/>），
    /// 所以点「安装」立刻跳 p06，进度、失败原因都在那里；不再让界面先静默下载几十 MB。
    /// 兼容性用当前筛选行的游戏版本 / 加载器，而不是"任意版本"。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanInstallFromModrinth))]
    private void InstallFromModrinth(ModrinthEntry? entry)
    {
        if (!_downloadAccess.CanDownloadGameContent)
        {
            CatalogError = _downloadAccess.LockedReason;
            return;
        }

        if (entry is not { CanInstall: true, ProjectId: { } projectId })
        {
            CatalogError = Loc.T("这个整合包缺少项目 id（旧缓存快照），请刷新页面后再试。");
            return;
        }

        CatalogError = string.Empty;

        var title = string.IsNullOrWhiteSpace(entry.Version)
            ? entry.Name
            : entry.Name + " " + entry.Version;

        _downloads.EnqueueModrinthModpack(
            new ModrinthModpackRequest(projectId, SelectedGameVersion, SelectedLoader),
            title);

        _navigation.Navigate(PageId.Downloads);
    }
    /// <summary>当前实例名不为空才允许导出（没有实例时按钮必须如实置灰）。</summary>
    public bool CanExport => !string.IsNullOrWhiteSpace(_instances.ActiveInstanceName);

    /// <summary>没有正版账号时，整合包导入 / 安装与下载服务一起锁定。</summary>
    public bool CanUseDownloadService => _downloadAccess.CanDownloadGameContent;

    /// <summary>锁定时给用户看的原因（空串 = 已解锁）。</summary>
    public string DownloadLockedReason => _downloadAccess.CanDownloadGameContent
        ? string.Empty
        : _downloadAccess.LockedReason;

    /// <summary>导出按钮的 ToolTip：把"导出的是哪个实例"写出来。</summary>
    public string ExportToolTip => _instances.ActiveInstanceName is { Length: > 0 } instance
        ? Loc.T("导出当前实例：{0}（内容全部放进 overrides，可再用「从文件导入」装回来）").Replace("{0}", instance, StringComparison.Ordinal)
        : Loc.T("没有当前实例，无法导出。");

    /// <summary>上一次导出成功后的去向提示（为空时不占高度）。</summary>
    [ObservableProperty]
    private string _exportNote = string.Empty;

    /// <summary>
    /// 把当前实例导出成 <c>.mrpack</c>（D：内容全部放 overrides）。
    /// </summary>
    /// <remarks>
    /// 选保存位置用系统对话框；打包本身在后台线程（几百 MB 的实例会让 UI 卡住）。
    /// 目标文件由内核先写 <c>.part</c>、成功后才改名，失败不会留下半截归档。
    /// </remarks>
    [RelayCommand]
    private async Task ExportInstanceAsync()
    {
        var instance = _instances.ActiveInstanceName;

        if (string.IsNullOrWhiteSpace(instance))
        {
            CatalogError = Loc.T("没有当前实例，无法导出。");
            return;
        }

        var path = _dialogs.PickSaveFile(
            Loc.T("导出当前实例"),
            Loc.T("Modrinth 整合包 (*.mrpack)|*.mrpack"),
            instance + ".mrpack");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        CatalogError = string.Empty;
        ExportNote = string.Empty;

        var result = await Task.Run(() => _content.ExportInstance(instance, path));

        if (result is { Succeeded: false, Message: { } failure })
        {
            CatalogError = LocalizedText.Of(failure);
            return;
        }

        ExportNote = result is { Message: { } note } ? LocalizedText.Of(note) : string.Empty;
    }
    /// <summary>点「排序」按顺序切换：相关性 → 下载量 → 关注数 → 最新发布 → 最近更新。</summary>
    [RelayCommand]
    private void CycleSort()
    {
        var index = SortChoices.ToList().FindIndex(choice => choice.Value == SelectedSort);
        SelectedSort = SortChoices[(index + 1) % SortChoices.Count].Value;
        OnPropertyChanged(nameof(SortText));

        // 排序变了就重跑一次查询（有搜索词 / 筛选时也一样）。
        SearchCommand.Execute(null);
    }
    /// <summary>搜索框 / 两个下拉是否不是默认状态（决定空状态说的是"没有匹配"还是"还没有数据"）。</summary>
    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(SearchText) || SelectedGameVersion is not null || SelectedLoader is not null;

    /// <summary>构造整合包 ViewModel。</summary>
    /// <param name="content">内容目录。</param>
    /// <param name="downloads">下载队列（导入整合包是一次队列任务）。</param>
    /// <param name="dialogs">文件选择（这是本页唯一需要系统对话框的地方）。</param>
    /// <param name="navigation">导航（入队后跳 p06）。</param>
    /// <param name="versions">版本清单（浏览筛选用它的正式版列表；没取到时就只有「任意版本」）。</param>
    /// <param name="instances">实例目录（「导出当前实例」需要当前实例名）。</param>
    /// <param name="downloadAccess">下载准入（没有正版账号时锁定整合包导入与下载）。</param>
    public ModpacksViewModel(
        IContentCatalog content,
        IDownloadCatalog downloads,
        IDialogService dialogs,
        INavigationService navigation,
        IVersionCatalog versions,
        IInstanceCatalog instances,
        IAccountDownloadPolicy? downloadAccess = null)
        : base(PageId.Modpacks, "整合包")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(instances);

        _content = content;
        _downloads = downloads;
        _dialogs = dialogs;
        _navigation = navigation;
        _versions = versions;
        _instances = instances;
        _downloadAccess = downloadAccess ?? AllowAllDownloadPolicy.Instance;

        _catalog = content.GetModrinthCatalog();

        var releases = versions
            .GetVersions()
            .Where(static version => version.Kind == VersionKind.Release)
            .Select(static version => version.Id)
            .ToArray();

        GameVersionChoices =
        [
            new(Loc.T("任意版本"), null),
            .. releases.Select(static id => new ChoiceOption<string?>(id, id)),
        ];

        LoaderChoices =
        [
            new(Loc.T("任意加载器"), null),
            new("Forge", "forge"),
            new("Fabric", "fabric"),
            new("NeoForge", "neoforge"),
            new("Quilt", "quilt"),
        ];
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Modpacks;

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>浏览表是否为空（决定空状态提示与表格谁显示，两者共用同一个单元格）。</summary>
    public bool IsCatalogEmpty => Catalog.Count == 0;

    /// <summary>浏览表里至少有一行（与 <see cref="IsCatalogEmpty"/> 互斥）。</summary>
    /// <remarks>
    /// 两个属性而不是"一个属性 + 取反转换器"：仓库里没有取反转换器，
    /// 而为此新加一个转换器不如直接多一个布尔属性（判据同 p07 的 <c>HasMods</c> / <c>HasNoMods</c>）。
    /// </remarks>
    public bool HasCatalog => Catalog.Count > 0;

    /// <summary>
    /// 空状态那句话。
    /// </summary>
    /// <remarks>
    /// **"还没查过"与"查过但没有结果"必须分开**（`IContentCatalog.HasModrinthCatalog`）：
    /// 把"还没联网"说成"Modrinth 没有整合包"就是在编一条用户会信的错误结论。
    /// 有筛选条件时又是第三种情况：那是"没有匹配"，不是"数据源没有内容"。
    /// </remarks>
    public string CatalogEmptyText => HasActiveFilter
        ? Loc.T("没有匹配的整合包：调整搜索词或筛选条件后再试。")
        : _content.HasModrinthCatalog
            ? Loc.T("Modrinth 没有返回任何整合包。")
            : Loc.T("还没有取得 Modrinth 数据：联网后重新进入本页会自动获取。");

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();

        // 每次进页面都从默认浏览视图开始：搜索词与筛选**不跨页面保留**，
        // 否则用户下次进来会看到一张"上次搜过"的表，与设计稿的默认视图不一致。
        SearchText = string.Empty;
        SelectedGameVersion = null;
        SelectedLoader = null;
        SelectedSort = ModrinthSort.Relevance;
        CatalogError = string.Empty;
        ExportNote = string.Empty;
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportToolTip));
        OnPropertyChanged(nameof(CanUseDownloadService));
        OnPropertyChanged(nameof(DownloadLockedReason));
        ImportMrpackCommand.NotifyCanExecuteChanged();
        InstallFromModrinthCommand.NotifyCanExecuteChanged();

        // 命令内部不 await（外壳的 OnActivated 是同步的）：刷新完成后由命令自己把新行写回属性。
        RefreshCommand.Execute(null);
    }

    /// <summary>
    /// 按当前搜索词 / 游戏版本 / 加载器查一次 Modrinth（S9-e）。
    /// </summary>
    /// <remarks>
    /// 结果只用于当前页面，不写浏览快照；失败时**保留上一次结果**并显示一句原因
    /// （把失败说成"没有匹配"会让用户以为 Modrinth 上没有这个包）。
    /// </remarks>
    [RelayCommand]
    private async Task SearchAsync()
    {
        CatalogError = string.Empty;

        var rows = await _content.SearchModrinthAsync(
            new ModrinthSearchQuery(SearchText, SelectedGameVersion, SelectedLoader, SelectedSort));

        if (rows is null)
        {
            CatalogError = Loc.T("Modrinth 请求失败：保留了上一次的结果。");
            return;
        }

        Catalog = rows;
        OnPropertyChanged(nameof(CatalogEmptyText));
    }

    /// <summary>
    /// 「从文件导入 .mrpack」：选一个包、把它排进队列，然后跳到 p06。
    /// </summary>
    /// <remarks>
    /// 三件刻意的事：
    /// <list type="number">
    /// <item><b>取消就什么都不做</b>：不建实例、不入队 —— "取消"在文件选择框上只有一个含义。</item>
    /// <item><b>只把路径交给队列</b>：实例名、游戏版本、加载器都在清单里，读清单是队列的第一步
    /// （设计稿 p08 承诺"先安全扫描，再建任务"）；这一层自己读清单等于把那份规则复制一份。</item>
    /// <item><b>入队后跳 p06</b>：导入是几百 MB 的下载，停在 p08 上用户只会觉得"点了没反应"。</item>
    /// </list>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUseDownloadService))]
    private void ImportMrpack()
    {
        if (!_downloadAccess.CanDownloadGameContent)
        {
            CatalogError = _downloadAccess.LockedReason;
            return;
        }

        var path = _dialogs.PickFile(
            Loc.T("选择整合包"),
            Loc.T("Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*"));

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _downloads.EnqueueMrpackImport(new MrpackInstallRequest(path));

        _navigation.Navigate(PageId.Downloads);
    }

    private bool CanInstallFromModrinth(ModrinthEntry? entry) =>
        _downloadAccess.CanDownloadGameContent && entry is { CanInstall: true };

    /// <summary>刷新 Modrinth 浏览列表（新鲜度窗口内是一次空操作）。</summary>
    /// <remarks>
    /// 这里**不写** <c>ConfigureAwait(false)</c>：await 之后要写绑定属性，必须回到 UI 线程。
    /// 有筛选条件时改为重跑一次筛选查询：默认列表的刷新解决不了"筛出来的是旧的"。
    /// </remarks>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (HasActiveFilter)
        {
            await SearchAsync();
            return;
        }

        await _content.RefreshModrinthAsync();

        Catalog = _content.GetModrinthCatalog();

        // 取到过 / 没取到过决定空状态那句话 —— 它可能在上面的列表没变的时候也要跟着变
        // （例如这次拿到了"0 条"的结果）。
        OnPropertyChanged(nameof(CatalogEmptyText));
    }

    partial void OnSearchTextChanged(string value) => NotifyFilterStateChanged();

    partial void OnSelectedGameVersionChanged(string? value) => NotifyFilterStateChanged();

    partial void OnSelectedLoaderChanged(string? value) => NotifyFilterStateChanged();

    /// <summary>筛选条件变了：空状态那句话与「是否在筛选」要跟着变（查询由搜索按钮 / 回车触发）。</summary>
    private void NotifyFilterStateChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(CatalogEmptyText));
    }
}
