using EiTRVO.Core.Launcher.Downloads;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p14「阶段」表里的一行：内核给的是**键 + 参数**（<see cref="LaunchStage"/>），
/// 这里把标题与说明拼成当前语言的句子（Q8：句子只在 App 层拼）。
/// </summary>
/// <remarks>
/// 属性名与内核的 <see cref="LaunchStage"/> 一致（<c>Title</c> / <c>Detail</c> / <c>State</c>），
/// 所以 XAML 的绑定路径一个字都不用改 —— 换掉的只是"谁来拼那句话"。
/// </remarks>
/// <param name="Order">阶段序号（1 起）。</param>
/// <param name="Title">阶段名（已本地化）。</param>
/// <param name="Detail">阶段说明 / 进度（已本地化）。</param>
/// <param name="State">状态。</param>
/// <param name="IsActive">是否为当前正在进行的阶段。</param>
internal sealed record LaunchStageRowViewModel(
    int Order,
    string Title,
    string Detail,
    LaunchStageState State,
    bool IsActive);
