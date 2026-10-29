using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p10 Java 与设置（<b>全局</b>）。对应 mockup/pages/p10-java-settings.html。
/// <para>
/// 与 p17「实例设置」的分工是本项目最容易搞混的一点：
/// 这里改的是 <b>所有实例默认继承</b> 的全局值；单个实例的覆盖值在 p17。
/// 页头齿轮按钮永远进这一页（规范 p16 §2 第 ③ 条）。
/// </para>
/// <para>
/// 「保存设置」只在真的改过东西时可用 —— 用整条 <see cref="GlobalSettings"/> 记录做值比较，
/// 而不是给十几个属性各挂一个脏标记。
/// </para>
/// <para>
/// M3 起 Java 运行时表是**真实检测结果**：把 <see cref="IJavaRuntimeCatalog"/> 报出的事实
/// 按当前游戏版本投影成行（可用 / 当前不适用 / 未检测到）。投影放在 ViewModel 里而不是内核里，
/// 是因为"可用"这个词取决于当前选中的版本 —— 那是界面上下文，不是运行时的属性。
/// </para>
/// </summary>
internal sealed partial class JavaSettingsViewModel : PageViewModelBase
{
    private readonly ISettingsCatalog _settings;
    private readonly ILocalizationService _localization;
    private readonly IVersionCatalog _versions;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IDownloadCatalog _downloads;
    private readonly INavigationService _navigation;
    private readonly IDialogService? _dialogs;
    private readonly JavaRuntimeWarningDialogViewModel? _warningDialog;
    private JavaRuntimeInspection? _pendingCustomJava;
    private bool _isWarningDialogOpen;
    private GlobalSettings _baseline;

    private JavaRuntime? _selectedRuntime;

    [ObservableProperty]
    private IReadOnlyList<JavaRuntime> _runtimes;

    /// <summary>是否强制使用表格当前选中的 Java 启动，忽略版本与实例设置。</summary>
    [ObservableProperty]
    private bool _forceSelectedJava;

    /// <summary>强制启动时使用的 Java 根目录（随选中行更新，保存到全局设置）。</summary>
    [ObservableProperty]
    private string _forcedJavaHome = string.Empty;

    [ObservableProperty]
    private string _javaOverride = string.Empty;

    [ObservableProperty]
    private bool _autoFetchJava;

    [ObservableProperty]
    private int _maxMemoryMb;

    [ObservableProperty]
    private string _jvmArguments = string.Empty;

    [ObservableProperty]
    private int _windowWidth;

    [ObservableProperty]
    private int _windowHeight;

    [ObservableProperty]
    private bool _fullscreen;

    [ObservableProperty]
    private string _extraGameArguments = string.Empty;

    [ObservableProperty]
    private string _gameDirectory = string.Empty;

    [ObservableProperty]
    private DownloadSourcePolicy _downloadSource;

    [ObservableProperty]
    private int _concurrentDownloads;

    [ObservableProperty]
    private bool _minimizeAfterLaunch;

    /// <summary>界面语言标签（zh-Hans / en），由 <see cref="LanguageOptions"/> 双向绑定。</summary>
    [ObservableProperty]
    private string _language = string.Empty;

    [ObservableProperty]
    private bool _keepLogsOnCrash;

    /// <inheritdoc />
    public override bool HasDialog => _isWarningDialogOpen;

    /// <inheritdoc />
    public override object? Dialog => _isWarningDialogOpen ? _warningDialog : null;

    /// <summary>浏览并添加用户自有的 Java；风险项先弹窗确认。</summary>
    [RelayCommand]
    private async Task BrowseCustomJavaAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        var path = _dialogs.PickFile(
            Loc.T("选择 Java 可执行文件"),
            Loc.T("Java 可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"));

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var inspection = await _javaRuntimes.InspectCustomAsync(path);

            if (inspection.Risk == JavaRuntimeRisk.None)
            {
                _javaRuntimes.AddCustom(inspection.Installation);
                ReloadRuntimes();
                return;
            }

            _pendingCustomJava = inspection;
            ShowWarning(path, DescribeRisk(inspection.Risk), canAdd: true);
        }
        catch (JavaRuntimeInspectionException)
        {
            ShowWarning(path, Loc.T("无法读取这个文件的 Java 版本信息，它可能不是有效的 Java。"), canAdd: false);
        }
    }

    private void ShowWarning(string path, string riskText, bool canAdd)
    {
        if (_warningDialog is null)
        {
            return;
        }

        _warningDialog.Show(path, riskText, canAdd);
        _isWarningDialogOpen = true;
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(Dialog));
    }

    private void OnWarningClosed(object? sender, bool accepted)
    {
        _isWarningDialogOpen = false;
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(Dialog));

        var pending = _pendingCustomJava;
        _pendingCustomJava = null;

        if (!accepted || pending is null)
        {
            return;
        }

        _javaRuntimes.AddCustom(pending.Installation);
        ReloadRuntimes();
    }

    private static string DescribeRisk(JavaRuntimeRisk risk)
    {
        List<string> reasons = [];

        if (risk.HasFlag(JavaRuntimeRisk.UnknownVendor))
        {
            reasons.Add(Loc.T("无法识别这个 Java 的供应商，它可能不是可信的发行版。"));
        }

        if (risk.HasFlag(JavaRuntimeRisk.UserWritableLocation))
        {
            reasons.Add(Loc.T("该文件位于临时目录、下载目录或桌面等用户可写位置，可能被替换。"));
        }

        return string.Join(Environment.NewLine, reasons);
    }

    /// <summary>选择游戏目录（浏览按钮）。</summary>
    [RelayCommand]
    private void BrowseGameDirectory()
    {
        var folder = _dialogs?.PickFolder(Loc.T("选择游戏目录"));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            GameDirectory = folder;
        }
    }

    /// <summary>构造 Java 与设置 ViewModel。</summary>
    /// <param name="settings">设置读写。</param>
    /// <param name="versions">版本清单（只为取"当前版本"，用来判断哪个运行时可用）。</param>
    /// <param name="javaRuntimes">Java 运行时检测。</param>
    /// <param name="systemInfo">本机信息（内存提示里要写物理内存总量与推荐区间）。</param>
    /// <param name="downloads">下载队列（「获取 Java 25」入队）。</param>
    /// <param name="navigation">导航服务（入队后跳到下载页看进度）。</param>
    /// <param name="localization">界面语言服务（这一页负责切换语言）。</param>
    /// <param name="dialogs">目录选择框（游戏目录的「浏览」按钮）。</param>
    /// <param name="warningDialog">Java 安全风险确认框。</param>
    public JavaSettingsViewModel(
        ISettingsCatalog settings,
        IVersionCatalog versions,
        IJavaRuntimeCatalog javaRuntimes,
        ISystemInfoProvider systemInfo,
        IDownloadCatalog downloads,
        INavigationService navigation,
        ILocalizationService localization,
        IDialogService? dialogs = null,
        JavaRuntimeWarningDialogViewModel? warningDialog = null)
        : base(PageId.JavaSettings, "Java 与设置")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(javaRuntimes);
        ArgumentNullException.ThrowIfNull(systemInfo);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(localization);

        _settings = settings;
        _localization = localization;
        _versions = versions;
        _javaRuntimes = javaRuntimes;
        _systemInfo = systemInfo;
        _downloads = downloads;
        _navigation = navigation;
        _dialogs = dialogs;
        _warningDialog = warningDialog;
        if (_warningDialog is not null)
        {
            _warningDialog.Closed += OnWarningClosed;
        }

        _runtimes = BuildRuntimes();
        // 实际生效的界面语言来自 appsettings.json（见 App.OnStartup），域设置里的 Language
        // 只是持久化副本、可能还没跟上。这里统一成实际值，否则开局就会显示「有未保存的修改」，
        // 而且一进这一页就会把界面语言又拨回旧值。
        _baseline = settings.GetGlobal() with { Language = _localization.CurrentLanguage };

        // 设计稿把第一条运行时画成高亮行：高亮的是"当前版本会用的那一个"。
        _selectedRuntime = _runtimes.FirstOrDefault(static runtime => runtime.IsActive);

        Load(_baseline);

        if (ForceSelectedJava && ForcedJavaHome.Length > 0)
        {
            _selectedRuntime = Runtimes.FirstOrDefault(
                                   runtime => string.Equals(runtime.Path, ForcedJavaHome, StringComparison.OrdinalIgnoreCase))
                               ?? _selectedRuntime;
        }
    }

    /// <summary>页面被导航到时刷新检测结果（走新鲜度窗口；刚检测过就不会重复跑进程）。</summary>
    public override void OnActivated()
    {
        RefreshCommand.Execute(null);
        base.OnActivated();
    }


    /// <inheritdoc />
    public override NavKey NavKey => NavKey.JavaSettings;

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T("全局默认 + 实例覆盖");

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>Java 运行时表当前选中的行（由 DataGrid.SelectedItem 双向绑定驱动）。</summary>
    public JavaRuntime? SelectedRuntime
    {
        get => _selectedRuntime;
        set
        {
            if (!SetProperty(ref _selectedRuntime, value))
            {
                return;
            }

            if (ForceSelectedJava && value is { Path.Length: > 0 } selected)
            {
                ForcedJavaHome = selected.Path;
            }

            OnPropertyChanged(nameof(CanForceSelectedJava));
        }
    }

    /// <summary>有选中行时才允许打开强制启动开关。</summary>
    public bool CanForceSelectedJava => SelectedRuntime is not null;

    /// <summary>并发下载数选项。</summary>
    public IReadOnlyList<ChoiceOption<int>> ConcurrentDownloadOptions { get; } =
    [
        new("64", 64),
        new("32", 32),
        new("16", 16),
    ];

    /// <summary>
    /// 界面语言选项。名字一律用**该语言自己**书写，因此不走资源查找
    /// （否则切成英文之后，用户就找不到自己刚才是从哪一项切过来的了）。
    /// </summary>
    public IReadOnlyList<ChoiceOption<string>> LanguageOptions =>
    [
        .. _localization.Languages.Select(static info => new ChoiceOption<string>(info.DisplayName, info.Tag)),
    ];

    /// <summary>
    /// 实例 Java 覆盖选项（未覆盖时是第一项，值为空串 = 自动按版本匹配）。
    /// <para>
    /// 值用**运行时根目录**而不是显示文本：设置里存的是用来启动的那条路径，
    /// 存一句"17.0.9（Temurin）"在真正启动时毫无用处（而且用户改个目录名就全对不上了）。
    /// 检测结果里没有的旧值会作为"已失效"选项保留，避免打开这一页就把用户的选择悄悄清掉。
    /// </para>
    /// </summary>
    public IReadOnlyList<ChoiceOption<string>> JavaOverrideOptions
    {
        get
        {
            List<ChoiceOption<string>> options =
            [
                new(T("使用全局默认（自动按版本匹配）"), string.Empty),
            ];

            foreach (var installation in _javaRuntimes.GetInstallations())
            {
                options.Add(new(
                    string.Create(CultureInfo.InvariantCulture, $"{installation.VersionText}（{installation.Vendor}）"),
                    installation.HomeDirectory));
            }

            // 存着但已经检测不到（卸载了 / 换盘了）：如实标出来，别当成"没设置过"。
            // 空值判定用 IsNullOrEmpty 而不是 Length：绑定可能写进 null（见 OnJavaOverrideChanged）。
            if (!string.IsNullOrEmpty(JavaOverride)
                && !options.Any(option => string.Equals(option.Value, JavaOverride, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new(T("（已失效）{0}", JavaOverride), JavaOverride));
            }

            return options;
        }
    }

    /// <summary>
    /// 这一页自己的文案查找。走实例而不用静态的 <c>Loc.T</c>，
    /// 因为「下载源 / 实例 Java 覆盖 / 内存提示」这几项的取值**取决于当前语言** ——
    /// 写成实例方法，这层依赖在签名上就是显式的（也顺带满足 CA1822）。
    /// </summary>
    private string T(string key) => _localization.Translate(key);

    private string T(string key, params object?[] arguments) => _localization.Translate(key, arguments);

    /// <summary>
    /// 语言一改**立即生效**，不需要先按「保存设置」：用户选了 English 就应该马上看到英文，
    /// 否则他得先猜「保存设置」按钮是干什么的。保存按钮仍然负责把选择写进全局设置（持久化）。
    /// </summary>
    /// <param name="value">新的语言标签。</param>
    partial void OnLanguageChanged(string value)
    {
        // 空值不是"切换语言"，而是"没有语言可切"：WPF 的双向绑定会在选项重建时写 null
        // （见下面 OnJavaOverrideChanged 的说明）。此时必须**保持当前语言**并把它写回属性 ——
        // 否则 SetLanguage 的 Normalize(null) 会把界面悄悄切回中文。
        if (string.IsNullOrWhiteSpace(value))
        {
            Language = _localization.CurrentLanguage;
            return;
        }

        _localization.SetLanguage(value);
        OnPropertyChanged(nameof(JavaOverrideOptions));
        OnPropertyChanged(nameof(MemoryHint));

        // 表格里有一行是**拼出来的**（缺少所需 Java 那条，含「需要」这类文案），
        // 换语言之后必须重建，否则它会停在旧语言 —— 与 §5.3 第四条红线同一类问题。
        ReloadRuntimes();
    }

    /// <summary>
    /// 把绑定写进来的 <see langword="null"/> 归一成空串。
    /// <para>
    /// <b>这不是多余的防御</b>：p10 的「实例 Java 覆盖」是 <c>SelectedValue</c> 双向绑定的 ComboBox，
    /// 而它的**第一项显示文本随语言变**（"使用全局默认…"）—— 于是切换语言会重建选项、WPF 把
    /// <c>SelectedValue</c> 清成 null 并**写回这个 string 属性**。生成器不会拦（编译器以为非空类型
    /// 不可能拿到 null），随后 <see cref="Build"/> 里的 <c>JavaOverride.Length</c> 就抛
    /// <see cref="NullReferenceException"/> —— 2026-10-04 的实机崩溃正是这一条。
    /// 归一之后，"这个属性永远不是 null"从编译器的假设变成事实。
    /// </para>
    /// </summary>
    /// <param name="value">绑定写进来的值（可能是 null）。</param>
    partial void OnJavaOverrideChanged(string value)
    {
        if (value is null)
        {
            JavaOverride = string.Empty;
        }
    }

    /// <summary>打开强制开关时把当前选中行记下来；关闭时保留路径供下次打开。</summary>
    partial void OnForceSelectedJavaChanged(bool value)
    {
        if (value && SelectedRuntime is { Path.Length: > 0 } selected)
        {
            ForcedJavaHome = selected.Path;
        }
    }

    /// <summary>是否有未保存的修改。整条记录做值比较，<see cref="GlobalSettings"/> 是 record，天然值相等。</summary>
    public bool IsDirty => Build() != _baseline;

    /// <summary>
    /// 最大内存的物理上限文案（设计稿：物理内存 32.0 GB · 推荐 8–16 GB · 安全区上限 16384 MB）。
    /// <para>
    /// 三个数字全部来自 <see cref="MemoryRecommendations"/>：设计稿那句里的 8 / 16 / 16384
    /// 正是 32 GB 机器按该规则算出来的结果，所以它能逐字复现设计稿，同时在 8 GB 机器上会
    /// 如实变成「推荐 2–4 GB」—— 原先这三个数字是写死的，等于对每台机器都说"你有 32 GB"。
    /// </para>
    /// </summary>
    public string MemoryHint
    {
        get
        {
            var memory = _systemInfo.Memory;
            return T(
                "物理内存 {0} · 推荐 {1} · 安全区上限 {2} MB",
                _systemInfo.TotalMemory,
                GigabyteRange(memory.DefaultMb, memory.SafeZoneMb),
                memory.SafeZoneMb.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>推荐区间文本：<c>8–16 GB</c> / <c>512 MB</c>（单位只写一次，与设计稿一致）。</summary>
    private static string GigabyteRange(int fromMb, int toMb)
    {
        if (fromMb == toMb)
        {
            return Size(fromMb);
        }

        return toMb >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{fromMb / 1024.0:0.#}–{toMb / 1024.0:0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{fromMb}–{toMb} MB");
    }

    /// <summary>MB → <c>8 GB</c> / <c>512 MB</c>（小内存机器上"推荐 0.5 GB"不如直接写 512 MB）。</summary>
    private static string Size(int megabytes) => megabytes >= 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024.0:0.#} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{megabytes} MB");

    /// <summary>保存设置。写入设置目录后刷新基线，按钮随即回到禁用态。</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        _settings.SaveGlobal(Build());

        // 保存之后基线要重新取，并且同样按「实际生效的语言」归一化，
        // 否则刚保存完又会立刻显示成「有未保存的修改」。
        _baseline = _settings.GetGlobal() with { Language = _localization.CurrentLanguage };
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 页面导航时的刷新：按新鲜度窗口跑一次检测，然后把表格重建一遍。
    /// </summary>
    /// <remarks>
    /// 不写 <c>ConfigureAwait(false)</c>：await 之后要写绑定属性，必须回到 UI 线程。
    /// </remarks>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _javaRuntimes.RefreshAsync();
        ReloadRuntimes();
    }

    /// <summary>重新检测 Java 运行时（对应设计稿的「重新检测 Java」）。**强制**重跑，不吃新鲜度窗口。</summary>
    [RelayCommand]
    private async Task RedetectAsync()
    {
        await _javaRuntimes.RedetectAsync();
        ReloadRuntimes();
    }

    /// <summary>
    /// 「获取 Java 25」：真的去取 Mojang 官方运行时（§12.7）。
    /// </summary>
    /// <remarks>
    /// 取哪一代由**当前游戏版本**决定（它的版本 JSON 里声明的 <c>javaVersion</c>，没有就用规则表）；
    /// 没有选过版本时取官方索引里最新的一代 —— 按钮上写的就是最新一代（Java 25）。
    /// 入队立即返回，随后跳到「下载与队列」：一次运行时下载约 100 MB、几十秒到几分钟，
    /// 停在本页用户只会看到"什么都没发生"（这正是 M3 的残留）。
    /// </remarks>
    [RelayCommand]
    private void FetchJava()
    {
        var gameVersion = _versions.ActiveVersionId;

        _downloads.EnqueueJavaRuntime(new JavaRuntimeRequest(
            string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion));

        _navigation.Navigate(PageId.Downloads);
    }

    /// <summary>把这一页的所有改动丢弃，回到上次保存的全局值。</summary>
    [RelayCommand]
    private void ResetToDefault()
    {
        Load(_baseline);
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(IsDirty))
        {
            return;
        }

        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 重建运行时表：把检测到的事实按**当前游戏版本**投影成行。
    /// </summary>
    private void ReloadRuntimes()
    {
        var previouslySelected = _selectedRuntime?.Path;

        Runtimes = BuildRuntimes();

        // 用户手动选过哪一行就尽量留在那一行（按路径认；路径也认不出时用"当前版本会用的那一个"）。
        _selectedRuntime = Runtimes.FirstOrDefault(
                               runtime => ForceSelectedJava
                                          && string.Equals(runtime.Path, ForcedJavaHome, StringComparison.OrdinalIgnoreCase))
                           ?? Runtimes.FirstOrDefault(
                               runtime => string.Equals(runtime.Path, previouslySelected, StringComparison.OrdinalIgnoreCase))
                           ?? Runtimes.FirstOrDefault(static runtime => runtime.IsActive);

        OnPropertyChanged(nameof(SelectedRuntime));
        OnPropertyChanged(nameof(CanForceSelectedJava));
        OnPropertyChanged(nameof(JavaOverrideOptions));
    }

    /// <summary>把检测结果投影成表格行。</summary>
    private List<JavaRuntime> BuildRuntimes()
    {
        var installations = _javaRuntimes.GetInstallations();
        var gameVersion = _versions.ActiveVersionId;
        var hasContext = gameVersion.Length > 0;

        // 「当前版本会用的那一个」= 自动匹配的结果（M4 启动时选的是同一个）。
        var preferred = hasContext ? JavaPreflight.Select(installations, gameVersion) : null;

        List<JavaRuntime> rows = [];
        foreach (var installation in installations)
        {
            var usable = hasContext
                ? JavaRequirement.IsSatisfiedBy(installation.MajorVersion, gameVersion)
                : installation.MajorVersion >= JavaRequirement.OldestUsableMajor;

            rows.Add(new JavaRuntime(
                Version: installation.VersionText,
                Bits: LocalizedText.Of(installation.BitsText),
                Vendor: installation.Vendor,
                Path: installation.HomeDirectory,
                SupportedRange: installation.SupportedGameRange,
                State: usable ? JavaRuntimeState.Ready : JavaRuntimeState.Disabled,
                IsActive: preferred is not null && string.Equals(
                    preferred.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
        }

        var placeholder = MissingRow(gameVersion, hasContext);
        if (placeholder is not null)
        {
            rows.Add(placeholder);
        }

        return rows;
    }

    /// <summary>
    /// 缺 Java 时补的那一行（设计稿的第四行：版本号 + 未检测到 + 「1.20.5+ 需要」）。
    /// <para>
    /// 三种情况分得很清，因为它们的说法完全不同：
    /// ① 还没检测过 → 不能说"缺少"，只能说"尚未检测"；
    /// ② 检测过、当前版本有能用的 Java → 不要这一行；
    /// ③ 检测过、没有能用的 Java → 如实写"未检测到"并标出所需版本。
    /// </para>
    /// </summary>
    private JavaRuntime? MissingRow(string gameVersion, bool hasContext)
    {
        if (!_javaRuntimes.HasDetectionResult)
        {
            return new JavaRuntime(
                Version: "—",
                Bits: "—",
                Vendor: "—",
                Path: T("尚未检测"),
                SupportedRange: T("未检测"),
                State: JavaRuntimeState.Missing,
                IsActive: false);
        }

        if (!hasContext)
        {
            return null;
        }

        var check = JavaPreflight.Check(
            gameVersion,
            javaOverride: null,
            _javaRuntimes.GetInstallations(),
            hasDetectionResult: true);

        if (check.IsSatisfied)
        {
            return null;
        }

        return new JavaRuntime(
            Version: check.RequiredMajor.ToString(CultureInfo.InvariantCulture),
            Bits: "—",
            Vendor: "—",
            Path: T("未检测到"),
            SupportedRange: T("{0} 需要", JavaRequirement.SupportedGameRange(check.RequiredMajor)),
            State: JavaRuntimeState.Missing,
            IsActive: false);
    }

    private void Load(GlobalSettings settings)
    {
        // 空串 = 使用全局默认（自动按版本匹配）。设置里存的是运行时根目录。
        JavaOverride = settings.JavaOverride ?? string.Empty;
        ForceSelectedJava = settings.ForceSelectedJava;
        ForcedJavaHome = settings.ForcedJavaHome ?? string.Empty;
        AutoFetchJava = settings.AutoFetchJava;
        MaxMemoryMb = settings.MaxMemoryMb;
        JvmArguments = settings.JvmArguments;
        WindowWidth = settings.WindowWidth;
        WindowHeight = settings.WindowHeight;
        Fullscreen = settings.Fullscreen;
        ExtraGameArguments = settings.ExtraGameArguments;
        GameDirectory = settings.GameDirectory;
        // 2026-10-23：只剩官方源；旧设置里的 Mirror / Automatic 一律按官方处理。
        DownloadSource = DownloadSourcePolicy.Official;
        ConcurrentDownloads = settings.ConcurrentDownloads;
        MinimizeAfterLaunch = settings.MinimizeAfterLaunch;
        Language = _localization.CurrentLanguage;
        KeepLogsOnCrash = settings.KeepLogsOnCrash;
    }

    private GlobalSettings Build() => new(
        GameDirectory: GameDirectory,
        DownloadSource: DownloadSource,
        ConcurrentDownloads: ConcurrentDownloads,
        MinimizeAfterLaunch: MinimizeAfterLaunch,
        Language: Language,
        KeepLogsOnCrash: KeepLogsOnCrash,
        MaxMemoryMb: MaxMemoryMb,
        JvmArguments: JvmArguments,
        WindowWidth: WindowWidth,
        WindowHeight: WindowHeight,
        Fullscreen: Fullscreen,
        ExtraGameArguments: ExtraGameArguments,
        AutoFetchJava: AutoFetchJava,
        JavaOverride: string.IsNullOrEmpty(JavaOverride) ? null : JavaOverride,
        ForceSelectedJava: ForceSelectedJava && !string.IsNullOrWhiteSpace(ForcedJavaHome),
        ForcedJavaHome: string.IsNullOrWhiteSpace(ForcedJavaHome) ? null : ForcedJavaHome);
}
