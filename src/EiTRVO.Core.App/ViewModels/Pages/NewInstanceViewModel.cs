using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p04 新建实例向导。对应 mockup/pages/p04-new-instance.html（图中停在第 2 步）。
/// <para>
/// 这是本次交付里少数带真实状态机逻辑的页面之一：步骤前进/后退、加载器选择、
/// 「下一步」的可用性都由 ViewModel 决定，View 只负责画。因此它有对应的单元测试。
/// </para>
/// <para>
/// M1 起最后一步的按钮真的会建实例：内核建立目录骨架 + <c>instance.json</c>，
/// 然后回到「本地实例」列表。**下载游戏文件仍属于 M2** —— 建实例与装游戏是两件事，
/// 新建出来的实例状态是「未安装」。
/// </para>
/// </summary>
internal sealed partial class NewInstanceViewModel : PageViewModelBase
{
    private readonly IVersionCatalog _versions;
    private readonly IInstanceCatalog _instances;
    private readonly INavigationService _navigation;
    private readonly IDownloadCatalog _downloads;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly IAccountDownloadPolicy _downloadAccess;
    private IReadOnlyList<LoaderOption> _allLoaders;

    /// <summary>估算的世代号：连续切版本时只让最后一次请求写回界面。</summary>
    private int _estimateGeneration;

    private string _downloadSizeText = string.Empty;
    private string _downloadBreakdownText = string.Empty;
    private string _diskFreeText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    [NotifyPropertyChangedFor(nameof(SectionSubtitle))]
    [NotifyPropertyChangedFor(nameof(Steps))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    [NotifyPropertyChangedFor(nameof(IsStep3))]
    [NotifyPropertyChangedFor(nameof(IsStep4))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadLockHint))]
    private int _currentStepNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Loaders))]
    [NotifyPropertyChangedFor(nameof(LoaderVersionOptions))]
    [NotifyPropertyChangedFor(nameof(ShowOptiFineComponent))]
    [NotifyPropertyChangedFor(nameof(ShowFabricApiComponent))]
    private LoaderOption? _selectedLoader;

    /// <summary>Forge 实例：是否同时把 OptiFine 作为模组放进 mods/（Neo 的共存做法）。</summary>
    [ObservableProperty]
    private bool _withOptiFine;

    /// <summary>Fabric 实例：是否同时安装 Fabric API。</summary>
    [ObservableProperty]
    private bool _withFabricApi;

    /// <summary>只有选中 Forge 时显示 OptiFine 可选组件。</summary>
    public bool ShowOptiFineComponent => SelectedLoader?.Kind == LoaderKind.Forge;

    /// <summary>只有选中 Fabric 时显示 Fabric API 可选组件。</summary>
    public bool ShowFabricApiComponent => SelectedLoader?.Kind == LoaderKind.Fabric;

    /// <summary>
    /// 选中的加载器版本。**纯版本号**（下拉框显示的可能带「（推荐）」，值不是）。
    /// </summary>
    /// <remarks>
    /// 与下拉框的绑定是 <c>SelectedValuePath="Value"</c>：显示文本与落盘值分开，
    /// 元数据里存的就是纯版本号，不需要再清洗（AGENTS.md §5.3 第 ⑩ 条）。
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _selectedLoaderVersion = string.Empty;

    /// <summary>第 3 步填写的实例名（默认是「版本-加载器」推导出的唯一名）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    [NotifyPropertyChangedFor(nameof(InstanceLocation))]
    private string _instanceName = string.Empty;

    /// <summary>实例名校验失败的原因（空串 = 合法）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _nameError = string.Empty;

    /// <summary>创建位置的相对提示（不暴露用户目录的绝对路径；游戏数据在游戏目录的 versions 下）。</summary>
    public string InstanceLocation => "versions\\" + InstanceName.Trim();

    /// <summary>
    /// 第 1 步选定的游戏版本。
    /// <para>
    /// 可空且可写：M2 起版本清单来自网络，**首次运行、离线且没有缓存时它是空的**。
    /// 那种情况下向导要如实显示"还没有版本清单"，而不是崩在 <c>First()</c> 上
    /// （这正是接真实数据时最容易踩的坑：以前样本数据永远有值）。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedVersionText))]
    [NotifyPropertyChangedFor(nameof(RequiredJavaText))]
    [NotifyPropertyChangedFor(nameof(HasVersion))]
    private MinecraftVersion? _selectedVersion;

    /// <summary>估算的下载总量（如 <c>412 MB</c>）；还没算出来时是「正在估算…」。</summary>
    public string DownloadSizeText
    {
        get => _downloadSizeText;
        private set => SetProperty(ref _downloadSizeText, value);
    }

    /// <summary>估算的分类明细（如「（库 268 MB · 资源 121 MB · natives 23 MB）。」）。</summary>
    public string DownloadBreakdownText
    {
        get => _downloadBreakdownText;
        private set => SetProperty(ref _downloadBreakdownText, value);
    }

    /// <summary>目标盘剩余空间（拿不到时为空串，界面不显示"空间充足"这种无依据的话）。</summary>
    public string DiskFreeText
    {
        get => _diskFreeText;
        private set => SetProperty(ref _diskFreeText, value);
    }

    /// <summary>构造向导 ViewModel。</summary>
    /// <param name="versions">版本清单。</param>
    /// <param name="instances">实例目录（最后一步真的建实例）。</param>
    /// <param name="navigation">导航服务（建完去下载页看进度）。</param>
    /// <param name="downloads">下载队列（最后一步把安装任务入队）。</param>
    /// <param name="javaRuntimes">Java 运行时检测（「所需 Java」那行要说实话）。</param>
    /// <param name="downloadAccess">下载准入（没有正版账号时不允许安装游戏与加载器）。</param>
    public NewInstanceViewModel(
        IVersionCatalog versions,
        IInstanceCatalog instances,
        INavigationService navigation,
        IDownloadCatalog downloads,
        IJavaRuntimeCatalog javaRuntimes,
        IAccountDownloadPolicy? downloadAccess = null)
        : base(PageId.NewInstance, "新建实例")
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(javaRuntimes);

        _versions = versions;
        _instances = instances;
        _navigation = navigation;
        _downloads = downloads;
        _javaRuntimes = javaRuntimes;
        _downloadAccess = downloadAccess ?? AllowAllDownloadPolicy.Instance;

        _allLoaders = [];
        _currentStepNumber = 2;
        _downloadSizeText = Loc.T("正在估算…");
        UseCurrentVersion();
        RefreshSuggestedName();
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Instances;

    /// <inheritdoc />
    public override string SectionSubtitle =>
        Loc.T("第 {0} 步，共 {1} 步 · {2}", CurrentStepNumber, Steps.Count, CurrentStepTitle);

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>
    /// 向导步骤（第 1 步被选中的版本来自版本清单，所以第 1 步天然是「已完成」）。
    /// </summary>
    /// <remarks>
    /// M6-e-3e：内核的 `WizardStep.Title` 是「键 + 参数」，界面不能直接绑（§5.3 第 ⑨ 条），
    /// 所以经 <see cref="WizardStepViewModel"/> 投影；属性名与内核一致，步骤条的 XAML 没改。
    /// </remarks>
    public IReadOnlyList<WizardStepViewModel> Steps =>
        [.. _versions.GetWizardSteps().Select(step => WizardStepViewModel.From(step, StateOf(step.Number)))];

    /// <summary>当前是否已经有可装的版本（没有清单时为 <see langword="false"/>）。</summary>
    public bool HasVersion => SelectedVersion is not null;

    /// <summary>「已选版本」那行的文本。设计稿形如「1.20.4（正式版，发布于 2023/12/07）」。</summary>
    public string SelectedVersionText => SelectedVersion is null
        ? Loc.T("还没有版本清单")
        : Loc.T("{0}（{1}，发布于 {2}）", SelectedVersion.Id, KindText(SelectedVersion.Kind), SelectedVersion.ReleaseDate);

    /// <summary>
    /// 加载器选项（显示名与说明已按当前语言拼好）。高亮项是「当前选中」而不是数据里的初始值，
    /// 所以每次读取时按当前选择重新投影一次。
    /// </summary>
    public IReadOnlyList<LoaderOptionViewModel> Loaders =>
        [.. _allLoaders.Select(l => LoaderOptionViewModel.From(l, IsSelected(l)))];

    /// <summary>这一项是不是当前选中的加载器。</summary>
    /// <remarks>
    /// 按 <see cref="LoaderKind"/> 认，**不按显示名**：显示名是文案（`原版` → `Vanilla`），
    /// 拿它当身份用就是"把显示文本当数据"（M6-e-3d 之前 `ParseLoaderDisplay` 正是这么做的）。
    /// </remarks>
    private bool IsSelected(LoaderOption loader) =>
        SelectedLoader is { } selected && loader.Kind == selected.Kind;

    /// <summary>当前加载器可选的版本（显示文本可能带「（推荐）」，值始终是纯版本号）。</summary>
    public IReadOnlyList<ChoiceOption<string>> LoaderVersionOptions =>
        SelectedLoader is { } selected ? LoaderOptionViewModel.BuildVersionOptions(selected) : [];

    /// <summary>当前步骤名（已本地化；页头副标题那句要嵌它 —— §5.3 第 ⑤ 条）。</summary>
    public string CurrentStepTitle =>
        _versions.GetWizardSteps().FirstOrDefault(s => s.Number == CurrentStepNumber) is { } step
            ? LocalizedText.Of(step.Title)
            : string.Empty;

    /// <summary>最后一步只在没有正版账号时显示锁定原因。</summary>
    public string DownloadActionHint => _downloadAccess.LockedReason;

    /// <summary>当前步骤是否为最后一步。</summary>
    public bool IsLastStep => CurrentStepNumber >= Steps.Count;

    /// <summary>当前停在第 1 步（游戏版本已选定）。</summary>
    public bool IsStep1 => CurrentStepNumber == 1;

    /// <summary>当前停在第 2 步（模组加载器）。</summary>
    public bool IsStep2 => CurrentStepNumber == 2;

    /// <summary>当前停在第 3 步（名称与目录）。</summary>
    public bool IsStep3 => CurrentStepNumber == 3;

    /// <summary>当前停在第 4 步（确认并安装）。</summary>
    public bool IsStep4 => CurrentStepNumber == 4;

    /// <summary>没有正版账号时，第四步保留一条锁定原因；正常情况不再显示说明条。</summary>
    public bool ShowDownloadLockHint => IsStep4 && !_downloadAccess.CanDownloadGameContent;

    /// <summary>
    /// 「所需 Java」那行的文本。
    /// <para>
    /// M3 起这句话是**真的**：原先无论本机有没有 Java 都写「Java 17（已检测到 17.0.9）」，
    /// 而那个版本号是写死的。现在按 <see cref="JavaPreflight"/> 的结论分三种说法，
    /// 并且「还没检测过」不许说成「没检测到」。
    /// </para>
    /// </summary>
    public string RequiredJavaText
    {
        get
        {
            if (SelectedVersion is null)
            {
                return string.Empty;
            }

            var check = JavaPreflight.Check(
                SelectedVersion.Id,
                javaOverride: null,
                _javaRuntimes.GetInstallations(),
                _javaRuntimes.HasDetectionResult);

            return check.Status switch
            {
                JavaCheckStatus.Satisfied => Loc.T(
                    "Java {0}（已检测到 {1}）",
                    check.RequiredMajor,
                    check.Selected?.VersionText ?? string.Empty),
                JavaCheckStatus.Missing => Loc.T("Java {0}（未检测到，启动前需要获取）", check.RequiredMajor),
                // 还没检测过时只说"需要哪个版本"。这一格与 p05 的「所需 Java」列一样，
                // 是内核给出的**数据**（"Java 17"在两种语言里写法相同），所以不走资源查找。
                _ => string.Create(CultureInfo.InvariantCulture, $"Java {check.RequiredMajor}"),
            };
        }
    }

    // ---------- 刷新 ----------

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();

        FinishCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DownloadActionHint));
        OnPropertyChanged(nameof(ShowDownloadLockHint));

        // 每次从别的页面进来都回到入口步骤：上一次安装完成后向导停在最后一步，
        // 直接复用会把"新建实例"变成"对旧实例再确认一次"。版本由 p05 的当前行决定，
        // 所以这里清空选中项、让 Refresh 重新按 ActiveVersionId 挑。
        CurrentStepNumber = 2;
        NameError = string.Empty;
        SelectedVersion = null;
        // 上一次向导里选过 Forge 也不该带进下一次：用户点「取消」后再进来，
        // 默认仍然是「原版」，否则会悄悄沿用已经放弃的选择。
        SelectedLoader = null;
        SelectedLoaderVersion = string.Empty;
        WithOptiFine = false;
        WithFabricApi = false;

        // 首次运行（本机既没有缓存也还没联网过）时清单是空的，进来才去取。
        // 与 p05 一样，命令内部不 await —— 外壳的 OnActivated 是同步的。
        RefreshCommand.Execute(null);
    }

    /// <summary>刷新版本清单与当前版本的加载器列表。</summary>
    /// <remarks>
    /// 不写 <c>ConfigureAwait(false)</c>：await 之后要写绑定属性，必须回到 UI 线程。
    /// </remarks>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _versions.RefreshAsync();

        // 先选版本、再取加载器，最后才刷新 Java 检测：Java 检测可能真的要跑进程，
        // 不能让它把"版本/加载器列表"一起拖住（用户会先看到一张空表，以为页面坏了）。
        var rows = _versions.GetVersions();
        if (SelectedVersion is null || !rows.Any(row => string.Equals(row.Id, SelectedVersion.Id, StringComparison.Ordinal)))
        {
            UseCurrentVersion();
        }

        // 体积估算与加载器列表**并行**：加载器元数据有时要等十几秒，而体积只需要版本 JSON
        // （本地有缓存时立即返回）。串行会让界面一直停在「正在估算…」。
        var estimateTask = SelectedVersion is { } selectedForEstimate
            ? UpdateDownloadEstimateAsync(selectedForEstimate.Id)
            : Task.CompletedTask;

        if (SelectedVersion is null)
        {
            ResetDownloadEstimate();
        }

        if (SelectedVersion is not null)
        {
            // 只取当前选中的那一家：Neo 26.1 也是点中加载器卡片后才取它的版本列表。
            // 其它四行保持可点击的占位项，谁失败都不影响这里。
            var loaderKind = SelectedLoader?.Kind ?? LoaderKind.Forge;
            await _versions.RefreshLoaderAsync(loaderKind, SelectedVersion.Id);
            ReloadLoaders();
        }

        // 「所需 Java」那行读的是检测结果，因此进向导页时也要让检测跟上（走新鲜度窗口，通常不跑进程）。
        await _javaRuntimes.RefreshAsync();
        OnPropertyChanged(nameof(RequiredJavaText));

        // 每次重新进页面都从建议名开始，避免沿用上一次（已创建）的名字。
        RefreshSuggestedName();

        // 体积估算已经在上面并行启动；这里只等它收尾，不让加载器元数据把提示拖住。
        await estimateTask;
    }

    /// <summary>重新估算所选版本的下载量（版本 JSON 本地没有时会联网取一次，只有几 KB）。</summary>
    /// <param name="versionId">版本 id。</param>
    private async Task UpdateDownloadEstimateAsync(string versionId)
    {
        var generation = ++_estimateGeneration;

        DownloadSizeText = Loc.T("正在估算…");
        DownloadBreakdownText = string.Empty;
        DiskFreeText = string.Empty;

        VersionDownloadEstimate? estimate;

        try
        {
            estimate = await _versions.EstimateDownloadAsync(versionId);
        }
        catch (Exception)
        {
            // 估算失败不该让向导崩掉：下面按"估不出来"如实显示。
            estimate = null;
        }

        // 用户在请求返回前又换了版本：这一次的结果已经过期，丢掉。
        if (generation != _estimateGeneration)
        {
            return;
        }

        if (estimate is null)
        {
            DownloadSizeText = "—";
            DownloadBreakdownText = Loc.T("（暂时无法获取版本信息，安装时以实际大小为准）。");
            return;
        }

        DownloadSizeText = FormatBytes(estimate.TotalBytes);

        var libraries = FormatBytes(estimate.ClientBytes + estimate.LibraryBytes);
        var assets = FormatBytes(estimate.AssetBytes);
        var natives = FormatBytes(estimate.NativeBytes);

        DownloadBreakdownText = SelectedLoader is { Kind: not LoaderKind.Vanilla }
            ? Loc.T("（库 {0} · 资源 {1} · natives {2}；不含加载器自身的库）。", libraries, assets, natives)
            : Loc.T("（库 {0} · 资源 {1} · natives {2}）。", libraries, assets, natives);

        if (estimate.FreeDiskBytes > 0)
        {
            DiskFreeText = estimate.FreeDiskBytes >= estimate.TotalBytes
                ? Loc.T("磁盘剩余 {0}，空间充足。", FormatBytes(estimate.FreeDiskBytes))
                : Loc.T("磁盘剩余 {0}，可能不够。", FormatBytes(estimate.FreeDiskBytes));
        }
    }

    /// <summary>没有选中版本时把体积提示恢复成"估不出来"。</summary>
    private void ResetDownloadEstimate()
    {
        _estimateGeneration++;
        DownloadSizeText = "—";
        DownloadBreakdownText = Loc.T("（暂时无法获取版本信息，安装时以实际大小为准）。");
        DiskFreeText = string.Empty;
    }

    /// <summary>字节数 → 界面上好读的 <c>412 MB</c> / <c>1.2 GB</c>。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>显示文本。</returns>
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 MB";
        }

        var megabytes = bytes / (1024.0 * 1024.0);

        return megabytes >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024:0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{megabytes:0.#} MB");
    }

    /// <summary>按清单里的「当前行」挑选版本，并取它的加载器列表。</summary>
    private void UseCurrentVersion()
    {
        var rows = _versions.GetVersions();

        MinecraftVersion? current = null;
        foreach (var row in rows)
        {
            if (row.IsActive)
            {
                current = row;
                break;
            }
        }

        // 没有「当前行」（清单非空但一行都没标）时退到第一行；清单为空时为 null。
        SelectedVersion = current ?? (rows.Count > 0 ? rows[0] : null);
        ReloadLoaders();
    }

    /// <summary>重取加载器列表，并尽量保住用户已经选好的那一项。</summary>
    private void ReloadLoaders()
    {
        var previous = SelectedLoader?.Kind;

        _allLoaders = SelectedVersion is null ? [] : _versions.GetLoaders(SelectedVersion.Id);

        // 优先保住用户的选择 → 原版 → 内核标的选中项 → 可自动安装的加载器 → 需人工完成
        // → 其它（Discouraged）。
        // 2026-10-25 用户要求默认选「原版」而不是 Forge：原版不改游戏本体，是安全默认值；
        // 想装加载器的人再主动选。只有在没有原版行时才退回其它优先顺序。
        SelectedLoader =
            FirstLoader(l => previous is { } kind && l.Kind == kind)
            ?? FirstLoader(static l => l.Kind == LoaderKind.Vanilla)
            ?? FirstLoader(static l => l.IsActive)
            ?? FirstLoader(static l => l is { Availability: LoaderAvailability.Recommended or LoaderAvailability.Available })
            ?? FirstLoader(static l => l.Availability == LoaderAvailability.NeedsManualStep)
            ?? FirstLoader(static l => l.Availability == LoaderAvailability.Discouraged)
            ?? FirstLoader(static _ => true);
    
        RefreshSuggestedName();
    }

    /// <summary>取第一个满足条件的加载器项（列表可索引，不用 LINQ 以免每行都装箱枚举）。</summary>
    /// <param name="predicate">条件。</param>
    /// <returns>找到的项；没有则为 <see langword="null"/>。</returns>
    private LoaderOption? FirstLoader(Func<LoaderOption, bool> predicate)
    {
        foreach (var loader in _allLoaders)
        {
            if (predicate(loader))
            {
                return loader;
            }
        }

        return null;
    }

    /// <summary>按当前版本与加载器刷新建议名（用户还没改过时也会跟着换）。</summary>
    private void RefreshSuggestedName()
    {
        if (SelectedVersion is null || SelectedLoader is null)
        {
            InstanceName = string.Empty;
            NameError = string.Empty;
            return;
        }

        InstanceName = InstanceNaming.SuggestUniqueName(
            SelectedVersion.Id,
            SelectedLoader.Kind,
            _instances.GetInstances().Select(static record => record.Name));

        ValidateName();
    }

    /// <summary>校验第 3 步的实例名（目录名合法 + 不重名），并把原因写进 <see cref="NameError"/>。</summary>
    private bool ValidateName()
    {
        var name = InstanceName.Trim();

        if (name.Length == 0)
        {
            NameError = Loc.T("实例名不能为空。");
            return false;
        }

        if (!LauncherPaths.IsValidInstanceName(name))
        {
            NameError = Loc.T("这个实例名不能用作目录名，请换一个。");
            return false;
        }

        if (_instances.GetInstances().Any(record => string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            NameError = Loc.T("已经有同名实例，请换一个名字。");
            return false;
        }

        NameError = string.Empty;
        return true;
    }

    partial void OnInstanceNameChanged(string value) => ValidateName();
    // ---------- 命令 ----------

    /// <summary>选择模组加载器。不可用的项不响应（设计稿里那一项是灰的）。</summary>
    /// <param name="loader">被点击的加载器。</param>
    [RelayCommand]
    private async Task SelectLoaderAsync(LoaderOptionViewModel? loader)
    {
        if (loader is null)
        {
            return;
        }

        // 界面上点到的往往是「投影后的副本」，所以按**种类**回到规范实例。
        // （旧实现是按显示名找 —— 那既脆弱又是"拿显示文本当数据用"，M6-e-3d 换掉了。）
        var canonical = _allLoaders.FirstOrDefault(l => l.Kind == loader.Kind);
        if (canonical is null || canonical.Availability == LoaderAvailability.Unavailable)
        {
            return;
        }

        SelectedLoader = canonical;

        // 占位行 / 上次失败的行：选中它之后**只取这一家**的版本，别动其它行。
        if (canonical.Versions.Count == 0 && SelectedVersion is not null)
        {
            await _versions.RefreshLoaderAsync(canonical.Kind, SelectedVersion.Id);
            ReloadLoaders();
        }
    }

    /// <summary>上一步。第 1 步时不可用。</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => CurrentStepNumber--;

    /// <summary>下一步。最后一步时不可用；还没有版本清单时也不可用（没东西可装）。</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (CurrentStepNumber == 3 && !ValidateName())
        {
            return;
        }

        CurrentStepNumber++;
    }

    /// <summary>
    /// 确认并安装：建出实例（M1 只落盘元数据与目录骨架），然后回「本地实例」列表。
    /// <para>
    /// 实例名由「游戏版本 + 加载器」推导（设计稿第 3 步「名称与目录」没有画输入控件，
    /// 而设计稿里的实例名正好是这个形状，如 <c>1.20.4-Forge</c>）；重名时内核补 <c>-2</c>。
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish()
    {
        // CanExecute 已经挡住这几种情况；这里再判一次是为了让编译器与读者都清楚"不能为空"。
        if (!_downloadAccess.CanDownloadGameContent
            || SelectedVersion is null
            || SelectedLoader is null)
        {
            return;
        }

        if (!ValidateName())
        {
            return;
        }

        var loader = SelectedLoader.Kind;
        var name = InstanceName.Trim();

        var loaderVersion = InstanceNaming.CleanLoaderVersion(SelectedLoaderVersion);

        _instances.CreateInstance(new InstanceCreationRequest(
            Name: name,
            GameVersion: SelectedVersion.Id,
            Loader: loader,
            LoaderVersion: loaderVersion,
            IsolatedDirectory: true));

        // 建实例只是落一份元数据；**装游戏**是几百 MB、几分钟到几十分钟的事。
        // 因此把它交给下载队列（入队即返回），并跳到「下载与队列」让用户看得见进度 ——
        // 停在向导页或直接回实例列表，用户都只会看到"什么都没发生"。
        _downloads.EnqueueInstall(new InstallRequest(
            InstanceName: name,
            VersionId: SelectedVersion.Id,
            Loader: loader,
            LoaderVersion: loaderVersion)
        {
            WithOptiFine = WithOptiFine,
            WithFabricApi = WithFabricApi,
        });

        _navigation.Navigate(PageId.Downloads);
    }

    private bool CanGoBack() => CurrentStepNumber > 1;

    private bool CanGoNext() =>
        !IsLastStep
        && SelectedVersion is not null
        && (CurrentStepNumber != 3 || (NameError.Length == 0 && InstanceName.Trim().Length > 0));

    private bool CanFinish() =>
        _downloadAccess.CanDownloadGameContent
        && IsLastStep
        && SelectedVersion is not null
        && SelectedLoader is not null;

    /// <summary>换加载器时，把版本选项重置为新加载器的第一个版本。</summary>
    /// <param name="value">新的加载器；没有清单时为 <see langword="null"/>。</param>
    partial void OnSelectedLoaderChanged(LoaderOption? value)
    {
        SelectedLoaderVersion = value is { Versions.Count: > 0 } ? value.Versions[0] : string.Empty;

        // 切到别的加载器时，上一个加载器的可选组件不再适用。
        if (value?.Kind != LoaderKind.Forge)
        {
            WithOptiFine = false;
        }

        if (value?.Kind != LoaderKind.Fabric)
        {
            WithFabricApi = false;
        }

        RefreshSuggestedName();
    }

    private WizardStepState StateOf(int number) => number < CurrentStepNumber
        ? WizardStepState.Done
        : number == CurrentStepNumber
            ? WizardStepState.Current
            : WizardStepState.Pending;

    private static string KindText(VersionKind kind) => kind == VersionKind.Snapshot ? Loc.T("快照") : Loc.T("正式版");
}
