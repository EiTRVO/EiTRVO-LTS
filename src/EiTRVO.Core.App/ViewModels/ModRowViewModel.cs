using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Content;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p07 模组表的一行：内核 <see cref="ModEntry.Note"/> 是「键 + 参数」或 <see langword="null"/>，
/// 这里拼成当前语言的备注（Q8）。
/// </summary>
/// <remarks>
/// 属性名与内核逐字一致（<c>Name</c> / <c>Version</c> / <c>Source</c> / <c>GameVersion</c> /
/// <c>Enablement</c> / <c>State</c> / <c>Note</c> / <c>IsActive</c>），所以 P07 的表格列与
/// 三个按枚举着色的 <c>DataTrigger</c> 一个字都不用改（AGENTS.md §5.3 第 ⑨ 条）。
/// <para>
/// M7-g-2：多一格 <see cref="FileName"/> —— 启用 / 禁用 / 删除要把它**原样交回内核**
/// （显示名会重名、版本也会相同，只有磁盘上的文件名唯一）。
/// </para>
/// </remarks>
/// <param name="Name">模组名。</param>
/// <param name="FileName">磁盘上的文件名（内核再校验一次它是单段名）。</param>
/// <param name="Version">已安装版本（数据）。</param>
/// <param name="Source">来源。</param>
/// <param name="GameVersion">适配的游戏版本（数据）。</param>
/// <param name="Enablement">启用状态。</param>
/// <param name="State">健康度。</param>
/// <param name="Note">备注（已本地化；没有备注时是空串）。</param>
/// <param name="IsActive">是否被选中。</param>
internal sealed record ModRowViewModel(
    string Name,
    string FileName,
    string Version,
    ContentSource Source,
    string GameVersion,
    ModEnablement Enablement,
    ModState State,
    string Note,
    bool IsActive)
{
    /// <summary>把一个内核模组行投影成可绑定的行。</summary>
    /// <param name="mod">内核行。</param>
    /// <returns>投影。</returns>
    public static ModRowViewModel From(ModEntry mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        return new ModRowViewModel(
            mod.Name,
            mod.FileName,
            mod.Version,
            mod.Source,
            mod.GameVersion,
            mod.Enablement,
            mod.State,
            LocalizedText.OrNull(mod.Note) ?? string.Empty,
            mod.IsActive);
    }
}
