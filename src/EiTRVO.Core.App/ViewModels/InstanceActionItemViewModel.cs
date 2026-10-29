using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 「实例操作」列表的一行（p03 / p13 的中间那块）。
/// <para>
/// 它存在的唯一理由：<see cref="InstanceAction"/> 里的目标是非 UI 层的
/// <see cref="InstanceActionTarget"/>，而导航需要 App 层的 <see cref="PageId"/> 与一个图标字形。
/// 这个「语义目标 → 页面 + 图标」的映射属于 View 层，所以放在这里而不是塞进 Model。
/// </para>
/// </summary>
/// <param name="Title">入口标题（已本地化）。</param>
/// <param name="Summary">入口摘要（已本地化）。</param>
/// <param name="Icon">Segoe MDL2 Assets 字形。</param>
/// <param name="Target">点击后跳转的页面。</param>
/// <param name="IsActive">
/// 是否高亮这一行。操作入口没有选中态，恒为 <see langword="false"/>；
/// 但 <c>PickerRowButton</c> 的模板里有 <c>DataTrigger Binding="{Binding IsActive}"</c>，
/// 缺少这个属性会让 WPF 为每一行记一条绑定错误（2026-10-24 实测 4 条）。
/// </param>
internal sealed record InstanceActionItemViewModel(
    string Title,
    string Summary,
    string Icon,
    PageId Target,
    bool IsActive = false)
{
    /// <summary>把数据源给出的操作入口映射成可导航的行。</summary>
    /// <param name="actions">数据源的入口列表。</param>
    /// <returns>可导航的行。</returns>
    public static IReadOnlyList<InstanceActionItemViewModel> Map(IReadOnlyList<InstanceAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return [.. actions.Select(Map)];
    }

    private static InstanceActionItemViewModel Map(InstanceAction action)
    {
        var (icon, target) = action.Target switch
        {
            InstanceActionTarget.Mods => ("\uE7FC", PageId.Mods),
            InstanceActionTarget.Resources => ("\uE8F1", PageId.Resources),
            InstanceActionTarget.InstanceSettings => ("\uE713", PageId.InstanceSettings),
            InstanceActionTarget.Downloads => ("\uE896", PageId.Downloads),
            _ => ("\uE946", PageId.Dashboard),
        };

        return new InstanceActionItemViewModel(
            LocalizedText.Of(action.Title),
            LocalizedText.Of(action.Summary),
            icon,
            target);
    }
}
