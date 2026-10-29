using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p04 向导步骤条的一项：内核 <see cref="WizardStep.Title"/> 是「键 + 参数」，
/// 这里拼成当前语言的步骤名。
/// </summary>
/// <remarks>
/// 属性名与内核逐字一致（<c>Number</c> / <c>Title</c> / <c>State</c>），
/// 所以 P04 步骤条的 XAML 与三个 <c>DataTrigger</c>（按 <see cref="WizardStepState"/> 着色）都不用改。
/// 页头副标题那句「第 2 步，共 4 步 · 模组加载器」也从这里取渲染后的步骤名
/// （AGENTS.md §5.3 第 ⑤ 条：内层模板先拼再嵌）。
/// </remarks>
/// <param name="Number">步骤序号。</param>
/// <param name="Title">步骤名（已本地化）。</param>
/// <param name="State">状态。</param>
internal sealed record WizardStepViewModel(
    int Number,
    string Title,
    WizardStepState State)
{
    /// <summary>把一个内核步骤投影成可绑定的一项（状态由调用方按当前步骤重算）。</summary>
    /// <param name="step">内核步骤。</param>
    /// <param name="state">该步骤当前的状态。</param>
    /// <returns>投影。</returns>
    public static WizardStepViewModel From(WizardStep step, WizardStepState state)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new WizardStepViewModel(step.Number, LocalizedText.Of(step.Title), state);
    }
}
