using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher;
using EiTRVO.Core.Launcher.Content;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p08「已安装的整合包」列表的一行：内核 <see cref="ModpackEntry"/> 的行摘要与状态是「键 + 参数」，
/// 这里拼成当前语言的句子（Q8）。
/// </summary>
/// <remarks>
/// 与 <see cref="InstanceDetailPanelViewModel"/> 同一个理由（AGENTS.md §5.3 第 ⑨ 条）：
/// <c>Meta</c> / <c>StatusText</c> 直接绑到界面上会显示**键**，参数全丢。
/// 属性名与 <see cref="ModpackEntry"/> 一致，所以 P08 的 XAML 一个字都不用改。
/// </remarks>
/// <param name="Name">整合包名。</param>
/// <param name="Version">版本（自带 <c>v</c> 前缀）。</param>
/// <param name="ModCount">模组数。</param>
/// <param name="Size">占用空间。</param>
/// <param name="InstanceName">对应实例名。</param>
/// <param name="StatusText">状态文本（已本地化）。</param>
/// <param name="StatusTone">状态色调。</param>
/// <param name="IsActive">是否被选中。</param>
/// <param name="Meta">行摘要（已本地化）。</param>
internal sealed record ModpackRowViewModel(
    string Name,
    string Version,
    int ModCount,
    string Size,
    string InstanceName,
    string StatusText,
    Tone StatusTone,
    bool IsActive,
    string Meta)
{
    /// <summary>把内核的一条整合包投影成可绑定的行。</summary>
    /// <param name="entry">内核条目。</param>
    /// <returns>投影。</returns>
    public static ModpackRowViewModel From(ModpackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ModpackRowViewModel(
            Name: entry.Name,
            Version: entry.Version,
            ModCount: entry.ModCount,
            Size: entry.Size,
            InstanceName: entry.InstanceName,
            StatusText: LocalizedText.Of(entry.StatusText),
            StatusTone: entry.StatusTone,
            IsActive: entry.IsActive,
            Meta: LocalizedText.Of(entry.Meta));
    }
}
