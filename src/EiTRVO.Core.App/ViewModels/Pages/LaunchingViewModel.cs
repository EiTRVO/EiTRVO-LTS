using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Launch;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p14 正在启动。对应 mockup/pages/p14-dialog-launching.html。
/// <para>
/// 设计稿里它是「独立窗」但页面结构仍与其它页一致（深色页头 + 属性区 + 阶段列表 + 动作条），
/// 所以这里就是一个普通页面，只是内容在表达一次启动流程。
/// </para>
/// <para>
/// M4-d 起这一页读的是**真实的启动会话**（<see cref="IGameLauncher.GetSnapshot"/>）：
/// 4 步阶段、进度、失败原因、选中的 Java 全部来自内核编排，
/// 不再从下载队列借阶段表（那是安装流程的 3 步，与启动无关）。
/// </para>
/// <para>
/// 会话的变更在后台线程触发，经 <see cref="IUiRefreshThrottle"/> 合并后切回 UI 线程再改绑定属性。
/// </para>
/// </summary>
internal sealed partial class LaunchingViewModel : PageViewModelBase
{
    private readonly IGameLauncher _launcher;
    private readonly IInstanceCatalog _instances;
    private readonly IUiRefreshThrottle _throttle;

    private LaunchSnapshot _snapshot = LaunchSnapshot.Idle;
    private IReadOnlyList<LaunchStageRowViewModel> _stages = [];

    /// <summary>
    /// 上一次游戏进程**异常退出**时的说明（退出码 + 标准错误尾部）；正常退出或还没跑过时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 单独一个字段而不是塞进 <see cref="LaunchSnapshot"/>：启动信息在进程退出后必须清空
    /// （「正在启动」不是「启动结果」），但"游戏崩了"这件事不能跟着消失 ——
    /// 用户需要看到退出码与崩溃堆栈，否则界面上只会留下一次"什么都没发生"的启动。
    /// </remarks>
    private string? _exitFailure;

    /// <summary>构造正在启动 ViewModel。</summary>
    /// <param name="launcher">启动编排（快照与停止）。</param>
    /// <param name="instances">实例目录（面包屑与内存回退）。</param>
    /// <param name="throttle">刷新节流（会话在后台线程变化）。</param>
    public LaunchingViewModel(IGameLauncher launcher, IInstanceCatalog instances, IUiRefreshThrottle throttle)
        : base(PageId.Launching, "正在启动")
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(throttle);

        _launcher = launcher;
        _instances = instances;
        _throttle = throttle;

        _launcher.Changed += OnLauncherChanged;
        Refresh();
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Instances;

    /// <inheritdoc />
    public override string SectionSubtitle => Detail.Name;

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb =>
        NestedCrumb(Detail.Name, PageId.InstanceDetail);

    /// <summary>该实例的属性（面包屑、内存回退）。</summary>
    /// <remarks>
    /// 与 p03 / p13 / p17 一致：没有实例时用占位详情。
    /// <see cref="SectionSubtitle"/> 与 <see cref="Breadcrumb"/> 都读它，而面包屑是外壳**导航时同步求值**的 ——
    /// 少了这个判断，零实例下导航到本页会直接崩在导航过程中。
    /// </remarks>
    public InstanceDetailPanelViewModel Detail =>
        _instances.ActiveInstanceName.Length > 0
            ? InstanceDetailPanelViewModel.From(_instances.GetDetail(_instances.ActiveInstanceName))
            : InstanceDetailPanelViewModel.Empty;

    /// <summary>4 个启动阶段（真实进度；标题与说明已按当前语言拼好）。</summary>
    public IReadOnlyList<LaunchStageRowViewModel> Stages => _stages;

    /// <summary>总体进度百分比。</summary>
    public double Percent => _snapshot.Percent;

    /// <summary>会话是否正在准备或运行（决定「取消启动」是否可点）。</summary>
    public bool IsBusy =>
        _snapshot.State is LaunchSessionState.Preparing or LaunchSessionState.Running;

    /// <summary>
    /// 失败原因（已本地化）；没有失败时为 <see langword="null"/>。
    /// <para>
    /// 两种来源：启动阶段没起来（会话快照的 <see cref="LaunchSnapshot.Failure"/>），
    /// 或游戏进程起来过但异常退出（<see cref="_exitFailure"/>，退出码 + 标准错误尾部）。
    /// </para>
    /// </summary>
    public string? Failure => _exitFailure ?? LocalizedText.OrNull(_snapshot.Failure);

    /// <summary>是否有失败原因（控制红色提示条的显隐）。</summary>
    public bool HasFailure => !string.IsNullOrEmpty(Failure);

    /// <summary>「当前阶段」那行文本。</summary>
    public string CurrentStageText
    {
        get
        {
            var stage = _snapshot.CurrentStage;

            if (stage is not null)
            {
                return Loc.T("{0}（第 {1} 阶段，共 {2} 阶段）", LocalizedText.Of(stage.Title), stage.Order, _snapshot.Stages.Count);
            }

            return _snapshot.State switch
            {
                LaunchSessionState.Completed => Loc.T("已完成"),
                LaunchSessionState.Failed => Loc.T("已停止"),
                LaunchSessionState.Running => Loc.T("游戏正在运行"),
                _ => Loc.T("尚未开始"),
            };
        }
    }

    /// <summary>
    /// 「文件进度」那行：启动不下载文件，所以这里报的是**已校验就位的文件数**。
    /// </summary>
    /// <remarks>
    /// 与设计稿的差异（登记在 App README §5）：设计稿这一行是"已下载 / 总计"，
    /// 而启动时文件在安装阶段就已就位 —— 编一个下载数字出来等于骗人。
    /// </remarks>
    public string FileProgressText =>
        _snapshot.VerifiedFileCount > 0
            ? Loc.T("已校验 {0} 个文件", _snapshot.VerifiedFileCount)
            : Loc.T("尚未开始");

    /// <summary>
    /// 「速度」那行：启动不传输文件，没有速度可报。
    /// </summary>
    /// <remarks>如实说"启动不下载文件"，而不是拿一个假数字填满设计稿的格子。</remarks>
    public string SpeedText => _snapshot.State is LaunchSessionState.Idle ? Loc.T("尚未开始") : Loc.T("不适用（启动不下载文件）");

    /// <summary>「当前文件」那行：改成"当前在做的事"（校验文件 / 构建命令行 / 已启动）。</summary>
    public string CurrentFileText => LocalizedText.OrNull(_snapshot.Detail) ?? Loc.T("尚未开始");

    /// <summary>选中的 Java 运行时；还没选出来时给一句说明。</summary>
    public string JavaRuntimeText =>
        _snapshot.JavaRuntime.Length > 0 ? _snapshot.JavaRuntime : Loc.T("尚未选择");

    /// <summary>Java 是否已经过校验（决定「已校验」那三个字显不显示）。</summary>
    public bool IsJavaVerified => _snapshot.JavaRuntime.Length > 0;

    /// <summary>内存分配：会话里有就用会话的（已本地化），否则显示实例设置里的值。</summary>
    public string MemoryText => LocalizedText.OrNull(_snapshot.MemoryText) ?? Detail.MemoryText;

    /// <summary>取消启动 / 结束游戏。</summary>
    [RelayCommand]
    private void Cancel() => _launcher.RequestStop();

    /// <inheritdoc />
    public override void OnActivated()
    {
        base.OnActivated();
        Refresh();
    }

    private void OnLauncherChanged(object? sender, EventArgs e) => _throttle.Request(Refresh);

    private void Refresh()
    {
        _snapshot = _launcher.GetSnapshot();

        // 新的一次启动已经开始：上一次的异常退出提示到此为止（它属于上一次会话）。
        if (_snapshot.State is LaunchSessionState.Preparing or LaunchSessionState.Running)
        {
            _exitFailure = null;
        }

        // 游戏已经退出：这一页是「正在启动」，不是「启动结果」。退出码与时长已经写进实例元数据 / 事件日志，
        // 页面上继续留着"已完成 + 100%"只会让下一次启动前一直显示上一次的旧信息。
        // （只清"进程真的起来过又结束"的会话；准备阶段被取消的 Completed 没有 Result，仍显示"已取消"。）
        if (_snapshot is { State: LaunchSessionState.Completed, Result: not null })
        {
            // 正常退出（或被启动器结束）只清信息；**异常退出**必须留下可读的原因 ——
            // 这是 Neo 有、Core 之前漏掉的那一类提示（退出码 + stderr 尾部，Neo 用对话框，这里用 p14 的红色提示条）。
            // 注意用属性模式而不是 `_snapshot.Result`：MVVM 门禁把 `.Result` 这个字面量整体禁掉
            // （它同时是 Task.Result 的同步阻塞写法），写成 `is { Result: … }` 两件事都不会踩。
            _exitFailure = _snapshot is { Result: { WasKilled: false, ExitCode: not 0 } result }
                ? DescribeExit(result)
                : null;

            _snapshot = LaunchSnapshot.Idle;
        }

        // 阶段行要**在这里**投影一次（键 + 参数 → 当前语言的句子），而不是每次绑定读取时算：
        // 列表刷新很频繁，而绑定读到的应当是一个稳定的列表。
        _stages =
        [
            .. _snapshot.Stages.Select(static stage => new LaunchStageRowViewModel(
                stage.Order,
                LocalizedText.Of(stage.Title),
                LocalizedText.Of(stage.Detail),
                stage.State,
                stage.IsActive)),
        ];

        // 空属性名通知一次：阶段、百分比、各文本行一起重算。
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// 把一次异常退出的结局拼成可读的一句话（退出码 + 时长 + 标准错误尾部）。
    /// </summary>
    /// <remarks>
    /// 用的是事件表那条同款键（<c>游戏异常退出（退出码 {0}，运行 {1} 秒）{2}</c>），
    /// 尾部整段作为 <c>{2}</c> 填进去 —— 与 <c>log/events.jsonl</c> 里记的是同一件事，
    /// 用户对照两处时不会看到两种说法。尾部整段都是**数据**（JVM/模组输出），不做翻译。
    /// </remarks>
    /// <param name="result">进程结局。</param>
    /// <returns>提示文本。</returns>
    private static string DescribeExit(GameProcessResult result)
    {
        var seconds = Math.Max(0, (int)Math.Round(result.Duration.TotalSeconds));

        // 提示条只有那么大：尾部只留最后 8 行、总长压到 900 字符以内（与 Neo 的 1200 字符上限同一个理由：
        // 全量留给 log/events.jsonl 与实例的 logs/latest.log，界面上要给"看得见的退出码"留位置）。
        var lines = result.StandardErrorTail.TakeLast(8).ToArray();
        var tailText = string.Join('\n', lines);
        if (tailText.Length > 900)
        {
            tailText = tailText[^900..];
        }

        var tail = tailText.Length == 0
            ? string.Empty
            : "\n\n" + Loc.T("标准错误尾部：") + "\n" + tailText;

        return Loc.T("游戏异常退出（退出码 {0}，运行 {1} 秒）{2}", result.ExitCode, seconds, tail);
    }
}
