using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Downloads;

/// <summary>队列项状态。</summary>
public enum DownloadQueueState
{
    /// <summary>正在传输。</summary>
    Running,

    /// <summary>排队等待。</summary>
    Queued,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>失败（校验不通过、镜像也不可用等）。</summary>
    Failed,

    /// <summary>
    /// 被用户取消（M7-g）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Failed"/> 分开：**失败是"它坏了"，取消是"我不想要了"**。
    /// 合成一种状态的话，p06 会把用户自己点的取消写成一行红色失败原因，
    /// 而且「重试」也就无从判断该不该出现（两者都可以重试，但界面要说对话）。
    /// </remarks>
    Cancelled,
}

/// <summary>下载队列中的一项。</summary>
/// <param name="Id">
/// 任务 id（p06 的行内动作靠它点名 —— 标题会重复，id 不会）。
/// </param>
/// <param name="Title">任务标题，如 <c>安装 1.21.4-Vanilla</c>（键 + 参数）。</param>
/// <param name="Detail">进度或等待说明（键 + 参数）。</param>
/// <param name="State">状态。</param>
/// <param name="IsActive">是否被选中。</param>
public sealed record DownloadQueueItem(
    string Id,
    TextTemplate Title,
    TextTemplate? Detail,
    DownloadQueueState State,
    bool IsActive);

/// <summary>下载总体进度（props 区与进度窗共用）。</summary>
/// <param name="CurrentTask">当前任务标题（键 + 参数）。</param>
/// <param name="Percent">完成百分比（0–100）。</param>
/// <param name="AmountText">已下载 / 总计。</param>
/// <param name="SpeedText">当前速度。</param>
/// <param name="FilesText">剩余文件。</param>
/// <param name="RemainingText">预计剩余（键 + 参数：`即将完成` / `约 {0} 秒` / `—`）。</param>
/// <param name="ConnectionsText">并发连接数。</param>
/// <param name="DataSource">数据源主机。</param>
public sealed record DownloadProgress(
    TextTemplate CurrentTask,
    double Percent,
    string AmountText,
    string SpeedText,
    string FilesText,
    TextTemplate RemainingText,
    string ConnectionsText,
    string DataSource)
{
    /// <summary>
    /// 进度是否**不确定**（官方安装器在子进程里下载库，我们拿不到总量）。
    /// 界面在这种时候显示不确定进度条，而不是一根停在 0% 的条。
    /// </summary>
    public bool IsIndeterminate { get; init; }
}

/// <summary>启动流程的 4 个阶段。</summary>
public enum LaunchStageState
{
    /// <summary>已完成。</summary>
    Completed,

    /// <summary>进行中。</summary>
    Running,

    /// <summary>待执行。</summary>
    Pending,

    /// <summary>在这个阶段失败了（后面的阶段不会执行）。</summary>
    /// <remarks>
    /// 单独一个状态而不是"退回待执行"：启动失败时用户最需要知道的是**卡在哪一步**，
    /// 而一个停在"待执行"的列表看起来像"什么都没发生"（M4-d 新增）。
    /// </remarks>
    Failed,
}

/// <summary>启动流程的一个阶段。</summary>
/// <param name="Order">阶段序号。</param>
/// <param name="Title">阶段名（模板键 + 参数，句子由 App 层拼）。</param>
/// <param name="Detail">阶段说明 / 进度（同上）。</param>
/// <param name="State">状态。</param>
/// <param name="IsActive">是否为当前正在进行的阶段（列表高亮行）。</param>
public sealed record LaunchStage(
    int Order,
    TextTemplate Title,
    TextTemplate Detail,
    LaunchStageState State,
    bool IsActive);
