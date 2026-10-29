using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>启动会话的状态。</summary>
public enum LaunchSessionState
{
    /// <summary>还没有启动过（首次打开 p14 就是它）。</summary>
    Idle,

    /// <summary>正在准备（解析、校验、选 Java、构建命令行）。</summary>
    Preparing,

    /// <summary>游戏进程已经在跑。</summary>
    Running,

    /// <summary>会话正常走完（进程退出，退出码见 <see cref="LaunchSnapshot.Result"/>）。</summary>
    Completed,

    /// <summary>没起来（准备阶段被拒绝、或进程创建失败）。</summary>
    Failed,
}

/// <summary>
/// 启动流程的四个阶段。
/// <para>
/// 与设计稿的 4 行一一对应，但**第 2、3 行的语义不同**（设计稿是"下载并校验资源 / 解压 natives"）：
/// 启动不会下载 —— 文件在安装时就已经就位；把这两行写成"下载/解压"，用户会看到两句永远不会动的话，
/// 而真正在做的事（校验本地文件、挑 Java）反而是隐形的。所以它们是「校验本地文件」与「选择 Java 运行时」。
/// 差异登记在 App README §5。
/// </para>
/// </summary>
public static class LaunchStagePlan
{
    /// <summary>阶段总数。</summary>
    public const int Count = 4;

    /// <summary>1. 解析实例与版本。</summary>
    public const int ResolveVersion = 1;

    /// <summary>2. 校验本地文件。</summary>
    public const int VerifyFiles = 2;

    /// <summary>3. 选择 Java 运行时。</summary>
    public const int SelectJava = 3;

    /// <summary>4. 构建参数并启动进程。</summary>
    public const int StartProcess = 4;

    /// <summary>阶段标题（键 + 参数：内核不拼句子，界面用 <c>Loc.T</c> 拼）。</summary>
    /// <param name="order">阶段序号（1 起）。</param>
    /// <returns>标题模板。</returns>
    public static TextTemplate Title(int order) => order switch
    {
        ResolveVersion => TextTemplate.Of("1. 解析实例与版本"),
        VerifyFiles => TextTemplate.Of("2. 校验本地文件"),
        SelectJava => TextTemplate.Of("3. 选择 Java 运行时"),
        StartProcess => TextTemplate.Of("4. 构建参数并启动进程"),
        _ => TextTemplate.Of("?"),
    };
}

/// <summary>
/// 一次启动会话的只读快照（界面只读它，不去翻内核内部状态）。
/// </summary>
/// <param name="State">会话状态。</param>
/// <param name="InstanceName">实例名。</param>
/// <param name="VersionId">要启动的版本 id。</param>
/// <param name="Detail">当前在做的事（一行，用于「当前文件」那格）；没有时为 <see langword="null"/>。</param>
/// <param name="Stages">四个阶段。</param>
/// <param name="Percent">总体进度（0–100，按已完成阶段数算）。</param>
/// <param name="JavaRuntime">选中的 Java 运行时文本；还没选出来时为空串。</param>
/// <param name="MemoryText">内存分配文本（键 + 参数）；还没算出来时为 <see langword="null"/>。</param>
/// <param name="VerifiedFileCount">已校验的就位文件数（客户端 JAR + 库）；还没查时为 0。</param>
/// <param name="ProcessId">游戏进程 id；没在跑时为 <see langword="null"/>。</param>
/// <param name="Elapsed">本次会话已过去的时长。</param>
/// <param name="Result">退出后的结局；还没结束时为 <see langword="null"/>。</param>
/// <param name="Failure">没起来的原因（键 + 参数）；正常时为 <see langword="null"/>。</param>
public sealed record LaunchSnapshot(
    LaunchSessionState State,
    string InstanceName,
    string VersionId,
    TextTemplate? Detail,
    IReadOnlyList<LaunchStage> Stages,
    double Percent,
    string JavaRuntime,
    TextTemplate? MemoryText,
    int VerifiedFileCount,
    int? ProcessId,
    TimeSpan Elapsed,
    GameProcessResult? Result,
    TextTemplate? Failure)
{
    /// <summary>首次打开 p14、还没点过启动时的空态。</summary>
    public static LaunchSnapshot Idle { get; } = new(
        State: LaunchSessionState.Idle,
        InstanceName: string.Empty,
        VersionId: string.Empty,
        Detail: null,
        Stages: [.. Enumerable.Range(1, LaunchStagePlan.Count).Select(order =>
            new LaunchStage(
                order,
                LaunchStagePlan.Title(order),
                TextTemplate.Of("等待中"),
                LaunchStageState.Pending,
                IsActive: false))],
        Percent: 0,
        JavaRuntime: string.Empty,
        MemoryText: null,
        VerifiedFileCount: 0,
        ProcessId: null,
        Elapsed: TimeSpan.Zero,
        Result: null,
        Failure: null);

    /// <summary>当前正在进行的阶段；全都完成时为 <see langword="null"/>。</summary>
    public LaunchStage? CurrentStage => Stages.FirstOrDefault(static stage => stage.State == LaunchStageState.Running);

    /// <summary>游戏是否正在跑。</summary>
    public bool IsRunning => State == LaunchSessionState.Running;

    /// <summary>会话是否已经结束（完成或失败）。</summary>
    public bool IsFinished => State is LaunchSessionState.Completed or LaunchSessionState.Failed;
}

/// <summary>一次启动调用的结果。</summary>
/// <param name="Started">游戏进程是否真的起来过。</param>
/// <param name="State">结束时的会话状态。</param>
/// <param name="Failure">没起来的原因（键 + 参数）；起来过则为 <see langword="null"/>。</param>
/// <param name="Result">进程结局；没起来时为 <see langword="null"/>。</param>
public sealed record LaunchOutcome(
    bool Started,
    LaunchSessionState State,
    TextTemplate? Failure,
    GameProcessResult? Result)
{
    /// <summary>是否"起来过、且正常退出"（被启动器结束不算失败，但也不算成功退出）。</summary>
    public bool Succeeded => Started && Result is { WasKilled: false, ExitCode: 0 };
}
