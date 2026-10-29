using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Downloads;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p06 下载队列里的一行：内核给的是**键 + 参数**，这里拼成当前语言的句子（Q8）。
/// </summary>
/// <remarks>
/// 属性名与内核的 <see cref="DownloadQueueItem"/> 一致（<c>Title</c> / <c>Detail</c> / <c>State</c> / <c>IsActive</c>），
/// 所以 XAML 的绑定路径一个字都不用改。
/// <para>
/// **「失败：」这个前缀由这里加**（内核只说失败原因本身）：前缀是界面的话，不是内核的话 ——
/// 内核把"原因"和"前缀"一起拼出来，就等于又回到"内核拼句子"。
/// </para>
/// <para>
/// M7-g 起这一行还带着**行内动作**（<see cref="ActionText"/> / <see cref="CanAct"/> / <see cref="IsRetry"/>）：
/// 设计稿在这一格画的就是可点的徽标（运行中「暂停」、排队中「取消」），而 <c>IContentCatalog</c> 那边的
/// 动作靠 <see cref="Id"/> 点名（标题会重复，id 不会）。
/// </para>
/// </remarks>
/// <param name="Id">任务 id（点这个动作时交给内核的那个名字）。</param>
/// <param name="Title">队列行标题（已本地化）。</param>
/// <param name="Detail">进度 / 等待说明 / 失败原因（已本地化）。</param>
/// <param name="State">状态。</param>
/// <param name="IsActive">是否被选中。</param>
/// <param name="ActionText">行内动作的文案（<c>取消</c> / <c>重试</c>）。</param>
/// <param name="CanAct">这一行现在有没有动作可做（已完成的行没有）。</param>
/// <param name="IsRetry">动作是「重试」而不是「取消」（界面据此选颜色，命令据此分派）。</param>
/// <param name="ActionDetail">动作的悬停说明（说清这一按会发生什么）。</param>
internal sealed record DownloadQueueRowViewModel(
    string Id,
    string Title,
    string Detail,
    DownloadQueueState State,
    bool IsActive,
    string ActionText,
    bool CanAct,
    bool IsRetry,
    string ActionDetail)
{
    /// <summary>
    /// 队列行的明细（失败行由界面加「失败：」前缀）。
    /// </summary>
    /// <param name="item">内核给的队列行。</param>
    /// <returns>当前语言的明细。</returns>
    /// <remarks>
    /// **「失败：」这个前缀由界面加**（内核只给失败原因本身）：p06 与页头的队列下拉
    /// 共用这一处实现 —— 两处各写一份的话，同一个失败任务在两个地方会显示成两句话。
    /// </remarks>
    public static string Describe(DownloadQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var detail = LocalizedText.OrNull(item.Detail) ?? string.Empty;

        if (item.State != DownloadQueueState.Failed)
        {
            return detail;
        }

        // 失败原因缺失是"不该发生"的，但界面也不能因此空着 —— 空白的一行看起来像没失败。
        return string.IsNullOrEmpty(detail) ? Loc.T("失败") : Loc.T("失败：{0}", detail);
    }

    /// <summary>
    /// 内核的队列行 + 已本地化的明细 ⇒ 可绑定的行。
    /// </summary>
    /// <param name="item">内核行。</param>
    /// <param name="detail">已本地化的明细（失败行由调用方加过「失败：」前缀）。</param>
    /// <returns>投影。</returns>
    /// <remarks>
    /// 动作的三种取法刻意分开写：**已完成**没有任何动作（设计稿那一格写的就是状态词"已完成"），
    /// **排队中 / 正在执行**是「取消」，**失败 / 已取消**是「重试」。
    /// 把"已完成"也做成可点的重试，等于给用户一个把同一件事再做一遍的按钮 —— 那不是他会想要的东西。
    /// </remarks>
    public static DownloadQueueRowViewModel From(DownloadQueueItem item, string detail)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.State switch
        {
            DownloadQueueState.Failed or DownloadQueueState.Cancelled => new DownloadQueueRowViewModel(
                item.Id,
                LocalizedText.Of(item.Title),
                detail,
                item.State,
                item.IsActive,
                ActionText: Loc.T("重试"),
                CanAct: true,
                IsRetry: true,
                ActionDetail: Loc.T("把这一行原样再排一次队（已经校验通过的文件不会重新下载）。")),

            DownloadQueueState.Completed => new DownloadQueueRowViewModel(
                item.Id,
                LocalizedText.Of(item.Title),
                detail,
                item.State,
                item.IsActive,
                ActionText: string.Empty,
                CanAct: false,
                IsRetry: false,
                ActionDetail: string.Empty),

            _ => new DownloadQueueRowViewModel(
                item.Id,
                LocalizedText.Of(item.Title),
                detail,
                item.State,
                item.IsActive,
                ActionText: Loc.T("取消"),
                CanAct: true,
                IsRetry: false,
                ActionDetail: Loc.T("停止这个任务；已经做了一半的事按它自己的规则善后（安装会回滚，内容保留）。")),
        };
    }
}
