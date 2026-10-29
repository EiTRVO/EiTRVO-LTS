using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p17 实例设置（<b>逐实例</b>）。对应 mockup/pages/p17-instance-settings.html。
/// <para>
/// 与 p10 的分工：这里改的只影响当前实例，未覆盖的项继承全局默认值（p10）。
/// 因此这一页额外提供「重置为全局默认」，并且左导航 <b>不高亮</b> 任何一项 ——
/// 它不是任何导航分域的目的地，只能从 p02 / p03 带上下文进入（NavKey.None）。
/// </para>
/// </summary>
internal sealed partial class InstanceSettingsViewModel : PageViewModelBase
{
    private readonly ISettingsCatalog _settings;
    private readonly IInstanceCatalog _instances;
    private readonly IEventLog _events;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly GlobalSettings _global;
    private InstanceOverrides _baseline;

    [ObservableProperty]
    private string _javaOverride = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    private int _maxMemoryMb;

    [ObservableProperty]
    private string _jvmArguments = string.Empty;

    [ObservableProperty]
    private int _windowWidth;

    [ObservableProperty]
    private int _windowHeight;

    [ObservableProperty]
    private WindowLaunchMode _launchMode;

    [ObservableProperty]
    private string _extraGameArguments = string.Empty;

    [ObservableProperty]
    private bool _minimizeAfterLaunch;
    private readonly InstanceFileVerificationPresenter _verification;

    /// <summary>构造实例设置 ViewModel。</summary>
    /// <param name="settings">设置读写。</param>
    /// <param name="instances">实例目录。</param>
    /// <param name="events">事件日志（该实例的配置变更记录）。</param>
    /// <param name="javaRuntimes">Java 运行时检测（「所需 Java」与覆盖选项都要真的）。</param>
    /// <param name="systemInfo">本机信息（内存提示里的物理内存总量）。</param>
    /// <param name="verifier">文件校验服务。</param>
    public InstanceSettingsViewModel(
        ISettingsCatalog settings,
        IInstanceCatalog instances,
        IEventLog events,
        IJavaRuntimeCatalog javaRuntimes,
        ISystemInfoProvider systemInfo,
        IInstanceFileVerifier? verifier = null)
        : base(PageId.InstanceSettings, "实例设置")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(javaRuntimes);
        ArgumentNullException.ThrowIfNull(systemInfo);

        _settings = settings;
        _instances = instances;
        _events = events;

        // M6-f：配置变更记录要跟着新事件走（通知已在 UI 线程上、且已合并过）。
        _events.Changed += OnEventsChanged;
        _javaRuntimes = javaRuntimes;
        _systemInfo = systemInfo;
        _verification = new InstanceFileVerificationPresenter(
            verifier ?? new UnavailableInstanceFileVerifier(),
            () => HasInstance ? _instances.ActiveInstanceName : null,
            () => OnPropertyChanged(nameof(Detail)));
        _global = settings.GetGlobal();
        _baseline = settings.GetInstanceOverrides(instances.ActiveInstanceName);

        Load(_baseline);
    }

    /// <summary>
    /// 换了实例再进来时，草稿基线要按**新实例**重取一遍，
    /// 否则「有未保存的修改」会把上一个实例的覆盖项算进来。
    /// </summary>
    public override void OnActivated()
    {
        _baseline = _settings.GetInstanceOverrides(_instances.ActiveInstanceName);
        Load(_baseline);
        RefreshJavaCommand.Execute(null);
        base.OnActivated();
    }

    /// <summary>进页面时刷新 Java 检测（走新鲜度窗口），再通知依赖它的两行文案重算。</summary>
    [RelayCommand]
    private async Task RefreshJavaAsync()
    {
        await _javaRuntimes.RefreshAsync();
        OnPropertyChanged(nameof(JavaOverrideOptions));
        OnPropertyChanged(nameof(RequiredJavaHint));
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.None;

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T("用于 {0} · 仅影响该实例", Detail.Name);

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb =>
        NestedCrumb(Detail.Name, PageId.InstanceDetail);

    /// <summary>当前是否存在实例上下文（没有实例时这一页只展示占位值，不抛异常）。</summary>
    public bool HasInstance => _instances.ActiveInstanceName.Length > 0;

    /// <summary>实例属性（Java 运行时、隔离目录、路径等；当前实例，现算）。</summary>
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

    /// <summary>
    /// 该实例**声明**的游戏版本（原始值，不是给人看的文本）。
    /// </summary>
    /// <remarks>
    /// 展示文本在 <see cref="Detail"/> 上（没有版本时是「未指定」那句话），而 Java 判定要的是版本号本身 ——
    /// 拿显示文本去判版本就是"把文案当数据用"，这也正是 <see cref="InstanceLaunchInfo"/> 存在的理由。
    /// 没有实例时是空串（首次运行读属性不该抛异常）。
    /// </remarks>
    private string DeclaredGameVersion =>
        HasInstance ? _instances.GetLaunchInfo(_instances.ActiveInstanceName).GameVersion : string.Empty;

    /// <summary>该实例的配置变更记录。</summary>
    /// <summary>有新事件写入（通知已在 UI 线程上、且已按节流合并）。</summary>
    private void OnEventsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ChangeEvents));

    public IReadOnlyList<EventEntry> ChangeEvents =>
        HasInstance ? _events.GetSettingsEvents(_instances.ActiveInstanceName) : [];

    /// <inheritdoc />
    public override bool CanSearchEvents => true;

    /// <inheritdoc />
    public override IReadOnlyList<EventEntry> SearchableEvents => ChangeEvents;

    /// <summary>
    /// 实例 Java 覆盖选项。第一项表示"继承全局默认"，其余是本机**真实检测到**的运行时；
    /// 值与 p10 一样是运行时根目录（见那里的说明）。存着但已检测不到的值会如实标成「已失效」。
    /// </summary>
    public IReadOnlyList<ChoiceOption<string>> JavaOverrideOptions
    {
        get
        {
            List<ChoiceOption<string>> options =
            [
                new(Loc.T("自动（按游戏版本匹配）"), string.Empty),
            ];

            foreach (var installation in _javaRuntimes.GetInstallations())
            {
                options.Add(new(
                    string.Create(CultureInfo.InvariantCulture, $"{installation.VersionText}（{installation.Vendor}）"),
                    installation.HomeDirectory));
            }

            // 空值判定用 IsNullOrEmpty 而不是 Length：绑定可能写进 null（见 OnJavaOverrideChanged）。
            if (!string.IsNullOrEmpty(JavaOverride)
                && !options.Any(option => string.Equals(option.Value, JavaOverride, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new(Loc.T("（已失效）{0}", JavaOverride), JavaOverride));
            }

            return options;
        }
    }

    /// <summary>启动方式选项。</summary>
    public IReadOnlyList<ChoiceOption<WindowLaunchMode>> LaunchModeOptions { get; } =
    [
        new(Loc.T("窗口化"), WindowLaunchMode.Windowed),
        new(Loc.T("全屏"), WindowLaunchMode.Fullscreen),
        new(Loc.T("无边框全屏"), WindowLaunchMode.Borderless),
    ];

    /// <summary>内存提示：物理内存 · 全局默认 · 本实例覆盖。</summary>
    public string MemoryHint =>
        Loc.T("物理内存 {0} · 全局默认 {1} MB · 本实例覆盖为 {2} MB", _systemInfo.TotalMemory, _global.MaxMemoryMb, MaxMemoryMb);

    /// <summary>
    /// 该实例需要哪个 Java，以及**当前能不能满足**。
    /// <para>
    /// 原文案是「该版本需要 Java {0}；全局检测结果与之匹配」—— 后半句是写死的，
    /// 就算本机一个 Java 都没有也照样这么说。现在按真实的检测结论分四种说法。
    /// </para>
    /// </summary>
    public string RequiredJavaHint
    {
        get
        {
            var required = JavaRequirement.RequiredMajor(DeclaredGameVersion);

            // 实例没覆盖时，全局的 Java 覆盖是**默认值**（Q14）—— 它失效时要回落，不许把实例卡死。
            var scope = !string.IsNullOrEmpty(JavaOverride)
                ? JavaOverrideScope.Instance
                : string.IsNullOrEmpty(_global.JavaOverride) ? JavaOverrideScope.None : JavaOverrideScope.Global;

            var check = JavaPreflight.Check(
                DeclaredGameVersion,
                EffectiveJavaOverride,
                _javaRuntimes.GetInstallations(),
                _javaRuntimes.HasDetectionResult,
                declaredMajor: null,
                overrideScope: scope);

            if (check.FellBackFromOverride)
            {
                return check.Selected is { } fallback
                    ? Loc.T("该版本需要 Java {0}；全局默认的 Java 不可用，已按版本自动匹配到 {1}", required, fallback.VersionText)
                    : Loc.T("该版本需要 Java {0}；全局默认的 Java 不可用，也没有可自动匹配的运行时", required);
            }

            return check.Status switch
            {
                JavaCheckStatus.Satisfied => Loc.T(
                    "该版本需要 Java {0}；已检测到 {1}",
                    required,
                    check.Selected?.VersionText ?? string.Empty),
                JavaCheckStatus.Missing => Loc.T("该版本需要 Java {0}；未检测到可用的运行时", required),
                JavaCheckStatus.OverrideInvalid => Loc.T("该版本需要 Java {0}；指定的运行时不可用", required),
                _ => Loc.T("该版本需要 Java {0}；尚未检测", required),
            };
        }
    }

    /// <summary>实际生效的 Java 覆盖：实例自己的，否则全局的（空串表示自动匹配）。</summary>
    /// <remarks>空值判定用 <see cref="string.IsNullOrEmpty(string)"/>：绑定可能把 null 写进这个属性（见 OnJavaOverrideChanged）。</remarks>
    private string EffectiveJavaOverride =>
        !string.IsNullOrEmpty(JavaOverride) ? JavaOverride : _global.JavaOverride ?? string.Empty;

    /// <summary>
    /// 把绑定写进来的 <see langword="null"/> 归一成空串。
    /// <para>
    /// p17 与 p10 是同一个缺陷类：<c>SelectedValue</c> 双向绑定的 ComboBox 在选项重建时会把
    /// <c>SelectedValue</c> 清成 null 并写回这个 <c>string</c> 属性 —— 而生成器不会拦。
    /// 归一之后 <see cref="Build"/>、<see cref="JavaOverrideOptions"/>、<see cref="EffectiveJavaOverride"/>
    /// 三处都不会再踩到 null。
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

    /// <summary>是否有未保存的修改。</summary>
    public bool IsDirty => Build() != _baseline;

    /// <summary>配置变更记录上方那行统计文本。</summary>
    public string ChangeCountText => FormatCount(Loc.T("该实例的配置变更记录"), ChangeEvents.Count, Loc.T("条"));

    /// <summary>保存本实例的覆盖项。</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        if (!HasInstance)
        {
            return;
        }

        _settings.SaveInstanceOverrides(Build());
        _baseline = _settings.GetInstanceOverrides(EditableInstanceName);
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>清除本实例的所有覆盖项，回到全局默认。</summary>
    [RelayCommand]
    private void ResetToGlobal()
    {
        if (!HasInstance)
        {
            return;
        }

        _baseline = new InstanceOverrides(
            InstanceName: EditableInstanceName,
            JavaOverride: null,
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: _global.WindowWidth,
            WindowHeight: _global.WindowHeight,
            LaunchMode: _global.Fullscreen ? WindowLaunchMode.Fullscreen : WindowLaunchMode.Windowed,
            ExtraGameArguments: _global.ExtraGameArguments,
            MinimizeAfterLaunch: _global.MinimizeAfterLaunch);

        _settings.SaveInstanceOverrides(_baseline);
        Load(_baseline);
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);

        // 必须把本方法自己会触发的属性名一并列进来，否则「通知 A → 进本方法 → 再通知 A」
        // 会无限递归 —— 实测就是这里把进程打成了 StackOverflowException，p17 连窗都起不来。
        if (e.PropertyName is nameof(IsDirty) or nameof(MemoryHint))
        {
            return;
        }

        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 这一页读写覆盖项时用的实例名。
    /// <para>
    /// 有实例时它就是当前实例；没有实例时是空串 —— **不能用 <see cref="Detail"/> 的名字**：
    /// 占位详情的名字是「（没有实例）」，拿它当基线会让页面一打开就显示"有未保存的修改"。
    /// </para>
    /// </summary>
    private string EditableInstanceName => _instances.ActiveInstanceName;

    private void Load(InstanceOverrides overrides)
    {
        JavaOverride = overrides.JavaOverride ?? string.Empty;
        MaxMemoryMb = overrides.MaxMemoryMb ?? _global.MaxMemoryMb;
        JvmArguments = overrides.JvmArguments ?? _global.JvmArguments;
        WindowWidth = overrides.WindowWidth;
        WindowHeight = overrides.WindowHeight;
        LaunchMode = overrides.LaunchMode;
        ExtraGameArguments = overrides.ExtraGameArguments;
        MinimizeAfterLaunch = overrides.MinimizeAfterLaunch;
    }

    private InstanceOverrides Build() => new(
        InstanceName: EditableInstanceName,
        JavaOverride: string.IsNullOrEmpty(JavaOverride) ? null : JavaOverride,
        // 「等于全局默认」在语义上就是「未覆盖」，所以这里回写成 null（界面上「留空则继承全局」
        // 也是同一个意思）。否则「重置为全局默认」之后 Build() 会产出一串具体值、
        // 与含 null 的基线不相等，页面会立刻显示成「有未保存的修改」——测试正是这样抓到它的。
        MaxMemoryMb: MaxMemoryMb == _global.MaxMemoryMb ? null : MaxMemoryMb,
        JvmArguments: string.IsNullOrEmpty(JvmArguments)
                      || string.Equals(JvmArguments, _global.JvmArguments, StringComparison.Ordinal)
            ? null
            : JvmArguments,
        WindowWidth: WindowWidth,
        WindowHeight: WindowHeight,
        LaunchMode: LaunchMode,
        ExtraGameArguments: ExtraGameArguments,
        MinimizeAfterLaunch: MinimizeAfterLaunch);
}
