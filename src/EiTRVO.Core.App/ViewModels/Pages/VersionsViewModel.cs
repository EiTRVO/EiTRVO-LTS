using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Versions;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p05 所有版本。对应 mockup/pages/p05-versions.html。
/// <para>
/// M2 起这一页是**真实数据**：清单来自 <c>piston-meta</c>，行里的「本地状态」来自游戏目录。
/// 刷新发生在页面被导航到时（设计稿没有「刷新」按钮）；内核自己有新鲜度窗口，
/// 短时间内重复进出不会反复联网。首次运行（本机还没有缓存）时表是空的，
/// 刷新完成后会自动填上 —— 这就是这个页面需要异步刷新的原因。
/// </para>
/// <para>
/// Q12 决策 (b)：表格支持**选中一行**，动作以"所选版本"为准 ——
/// 「用所选版本新建实例」把该版本交给向导（内核的当前版本）。
/// **2026-10-21 用户要求**：左下角的「安装所选版本」已移除（安装统一走新建实例/向导），
/// 因此这里不再需要下载队列依赖。
/// </para>
/// </summary>
internal sealed partial class VersionsViewModel : PageViewModelBase
{
    private readonly IVersionCatalog _catalog;
    private readonly INavigationService _navigation;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly IAccountDownloadPolicy _downloadAccess;

    /// <summary>当前的类型筛选（见 <see cref="VersionKindFilter"/>）。</summary>
    // 用户 2026-10-20：默认只显示正式版；快照与全部仍可从「仅显示」切换。
    private VersionKindFilter _kindFilter = VersionKindFilter.Releases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SectionSubtitle))]
    [NotifyPropertyChangedFor(nameof(FilteredVersions))]
    private IReadOnlyList<MinecraftVersion> _versions;

    /// <summary>筛选器里的文本（按版本号做包含匹配，忽略大小写）。</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>表格实际显示的行：类型筛选 + 文本筛选之后的结果（已本地化）。</summary>
    /// <remarks>
    /// 计数与副标题用的是**总数**（设计稿的「共 917 个版本 · 快照 612 · 正式版 305」就是总数），
    /// 筛选只影响表里显示哪些行 —— 两件事不要混在一起。
    /// <para>
    /// M6-e-3e：内核行的「本地状态」是「键 + 参数」，界面不能直接绑（§5.3 第 ⑨ 条），
    /// 所以这里投影成 <see cref="VersionRowViewModel"/>；属性名与内核一致，XAML 一个字没改。
    /// </para>
    /// </remarks>
    public IReadOnlyList<VersionRowViewModel> FilteredVersions
    {
        get
        {
            IEnumerable<MinecraftVersion> rows = Versions;

            rows = _kindFilter switch
            {
                VersionKindFilter.Releases => rows.Where(static v => v.Kind == VersionKind.Release),
                VersionKindFilter.Snapshots => rows.Where(static v => v.Kind == VersionKind.Snapshot),
                _ => rows,
            };

            var text = FilterText.Trim();
            if (text.Length > 0)
            {
                rows = rows.Where(v => v.Id.Contains(text, StringComparison.OrdinalIgnoreCase));
            }

            return [.. rows.Select(VersionRowViewModel.From)];
        }
    }

    /// <summary>「仅显示：正式版」是否是当前选项（选中项用主色，未选中用灰色）。</summary>
    public bool IsShowingReleases => _kindFilter == VersionKindFilter.Releases;

    /// <summary>「仅显示：快照」是否是当前选项。</summary>
    public bool IsShowingSnapshots => _kindFilter == VersionKindFilter.Snapshots;

    /// <summary>「仅显示：全部」是否是当前选项。</summary>
    public bool IsShowingAll => _kindFilter == VersionKindFilter.All;

    /// <summary>
    /// 表格里被选中的那一行（<see langword="null"/> = 没选中，动作按钮会禁用）。
    /// <para>
    /// 刻意**不预选**任何行：设计稿的表格没有高亮行，一进页面就自带高亮属于加戏。
    /// </para>
    /// <para>
    /// M6-e-3e：类型是**投影行**（绑定给 <c>DataGrid.SelectedItem</c> 的就是它）；
    /// 动作用它按 <c>Id</c> 回到内核的规范行（见 <see cref="Target"/>）。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateInstanceCommand))]
    private VersionRowViewModel? _selectedVersion;

    /// <summary>构造版本清单 ViewModel。</summary>
    /// <param name="versions">版本清单。</param>
    /// <param name="navigation">导航服务（「用所选版本新建实例」的落点：向导）。</param>
    /// <param name="javaRuntimes">Java 运行时检测（「缺少 Java N」这一态的依据）。</param>
    /// <param name="downloadAccess">下载准入（没有正版账号时锁定「新建实例」）。</param>
    public VersionsViewModel(
        IVersionCatalog versions,
        INavigationService navigation,
        IJavaRuntimeCatalog javaRuntimes,
        IAccountDownloadPolicy? downloadAccess = null)
        : base(PageId.Versions, "所有版本")
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(javaRuntimes);

        _catalog = versions;
        _navigation = navigation;
        _javaRuntimes = javaRuntimes;
        _downloadAccess = downloadAccess ?? AllowAllDownloadPolicy.Instance;

        // 先用当前快照（真实实现里就是磁盘缓存）把表填上，避免进页面时先闪一下空表。
        _versions = versions.GetVersions();
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Versions;

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T(
        "共 {0} 个版本 · 快照 {1} · 正式版 {2}",
        _catalog.TotalCount.ToString("N0", CultureInfo.CurrentCulture),
        _catalog.SnapshotCount.ToString("N0", CultureInfo.CurrentCulture),
        _catalog.ReleaseCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();

        // 用户可能刚去账户页登录过正版账号；回到本页时要重新问一次下载准入。
        CreateInstanceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NewInstanceLockedReason));

        // 命令内部不 await（外壳的 OnActivated 是同步的）：刷新完成后由命令自己把新行写回属性。
        RefreshCommand.Execute(null);
    }

    /// <summary>
    /// 用某一行的版本新建实例（p04）。参数为 <see langword="null"/> 时用表格当前选中行。
    /// </summary>
    /// <param name="version">目标版本行（表格行链接会把它传进来）。</param>
    [RelayCommand(CanExecute = nameof(CanUseVersion))]
    private void CreateInstance(VersionRowViewModel? version)
    {
        if (Target(version) is not { } target)
        {
            return;
        }

        // 把"用户选的是哪个版本"交给内核：向导本来就是按内核的当前版本决定默认值的
        // （NewInstanceViewModel.UseCurrentVersion），所以这里不需要任何额外管道。
        _catalog.ActiveVersionId = target.Id;
        _navigation.Navigate(PageId.NewInstance);
    }

    /// <summary>动作的目标行：显式参数优先（表格行链接），否则用选中行。</summary>
    /// <remarks>
    /// 界面递过来的是**投影后的副本**（选中的是 <c>FilteredVersions</c> 里那一项），
    /// 所以按 <c>Id</c> 回到内核的规范行 —— 动作要的是内核那条记录，而不是一份显示副本。
    /// 按身份回选这条规矩与 p04 选加载器（按 <c>Kind</c>）同源。
    /// </remarks>
    private MinecraftVersion? Target(VersionRowViewModel? version) => Canonical(version ?? SelectedVersion);

    /// <summary>没有正版账号时，「用所选版本新建实例」锁定的原因（空串 = 已解锁）。</summary>
    public string NewInstanceLockedReason =>
        _downloadAccess.CanDownloadGameContent ? string.Empty : _downloadAccess.LockedReason;

    private bool CanUseVersion(VersionRowViewModel? version) =>
        _downloadAccess.CanDownloadGameContent && Target(version) is not null;

    private MinecraftVersion? Canonical(VersionRowViewModel? version) =>
        version is null
            ? null
            : Versions.FirstOrDefault(row => string.Equals(row.Id, version.Id, StringComparison.Ordinal));

    // ---------------------------------------------------------- 筛选行 ---

    /// <summary>「仅显示：正式版」。</summary>
    [RelayCommand]
    private void ShowReleases() => SetKindFilter(VersionKindFilter.Releases);

    /// <summary>「仅显示：快照」。</summary>
    [RelayCommand]
    private void ShowSnapshots() => SetKindFilter(VersionKindFilter.Snapshots);

    /// <summary>「仅显示：全部」。</summary>
    [RelayCommand]
    private void ShowAll() => SetKindFilter(VersionKindFilter.All);

    /// <summary>重置筛选（筛选器旁的撤销按钮）：清掉文本并回到「全部」。</summary>
    [RelayCommand]
    private void ResetFilter()
    {
        FilterText = string.Empty;
        SetKindFilter(VersionKindFilter.Releases);
    }

    private void SetKindFilter(VersionKindFilter filter)
    {
        _kindFilter = filter;

        OnPropertyChanged(nameof(IsShowingReleases));
        OnPropertyChanged(nameof(IsShowingSnapshots));
        OnPropertyChanged(nameof(IsShowingAll));
        OnFilterChanged();
    }

    /// <summary>筛选条件变了：重算显示行，并把已经看不见的选中行清掉。</summary>
    /// <remarks>
    /// 清掉选中行是必要的：真实界面里 WPF 会在选中项离开 <c>ItemsSource</c> 时把 <c>SelectedItem</c> 置空，
    /// 而 ViewModel 这边若还留着它，动作按钮就会**为一个看不见的行**保持可用 ——
    /// 点下去等于用了一个用户已经看不到的版本。这里把这个语义显式写出来，也就能被单测钉住。
    /// </remarks>
    private void OnFilterChanged()
    {
        // 按 Id 比（而不是比投影实例）：行的身份是版本号，换了语言之后同一行的
        // LocalStateText 会不一样，值相等就不成立了。
        if (SelectedVersion is { } selected && !FilteredVersions.Any(row => string.Equals(row.Id, selected.Id, StringComparison.Ordinal)))
        {
            SelectedVersion = null;
        }

        OnPropertyChanged(nameof(FilteredVersions));
    }

    /// <summary>筛选器文本变化（源生成器的钩子）。</summary>
    /// <param name="value">新文本。</param>
    partial void OnFilterTextChanged(string value) => OnFilterChanged();

    /// <summary>类型筛选的三个取值。</summary>
    private enum VersionKindFilter
    {
        /// <summary>不过滤。</summary>
        All,

        /// <summary>只看正式版。</summary>
        Releases,

        /// <summary>只看快照。</summary>
        Snapshots,
    }

    /// <summary>刷新版本清单（联网；失败时内核会沿用缓存并记一条事件）。</summary>
    /// <remarks>
    /// 这里**不写** <c>ConfigureAwait(false)</c>：await 之后要写绑定属性，
    /// 必须回到 UI 线程（否则 WPF 会因为跨线程改属性而抛异常）。
    /// </remarks>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _catalog.RefreshAsync();

        // 「缺少 Java 21」这一态要等运行时检测有结果之后才敢说，所以这里也要把检测推进一步。
        // 顺序在取行之前：GetVersions() 读的就是"当前有没有可用 Java"。
        await _javaRuntimes.RefreshAsync();

        // 刷新会把行**换成新对象**，绑定在 SelectedItem 上的选中行会因此丢掉。
        // 按 id 找回原来选的那一行：用户在表里点了 1.20.4，切走再回来不该变成没选中。
        var previousId = SelectedVersion?.Id;

        // 走生成属性而不是直接赋字段：计数与副标题要跟着一起通知。
        Versions = _catalog.GetVersions();

        SelectedVersion = previousId is null
            ? null
            : Versions.FirstOrDefault(row => string.Equals(row.Id, previousId, StringComparison.Ordinal)) is { } kernel
                ? VersionRowViewModel.From(kernel)
                : null;
    }
}
