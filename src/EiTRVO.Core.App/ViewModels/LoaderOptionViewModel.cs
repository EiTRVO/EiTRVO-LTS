using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p04 新建实例向导第 2 步的一行加载器：内核 <see cref="LoaderOption"/> 的显示名与说明是「键 + 参数」，
/// 这里拼成当前语言的句子（Q8）；版本下拉的显示文本也在这里拼。
/// </summary>
/// <remarks>
/// 属性名与内核的 <see cref="LoaderOption"/> 一致（<c>Display</c> / <c>Detail</c> /
/// <c>Availability</c> / <c>IsActive</c>），所以 P04 的 XAML 一个字都不用改。
/// <para>
/// **M6-e-3d**：内核不再把「（推荐）」贴进版本串里（那会让版本段变成脏数据），
/// 而是单独给 <see cref="LoaderOption.RecommendedVersion"/> —— 显示文本在这里拼，
/// 下拉框的**值**始终是纯版本号（<see cref="ChoiceOption{T}"/> 的 <c>Text</c> / <c>Value</c> 分工）。
/// </para>
/// </remarks>
/// <param name="Kind">加载器种类（**身份**：界面按它回选）。</param>
/// <param name="Display">显示名（已本地化）。</param>
/// <param name="Detail">说明文本（已本地化）。</param>
/// <param name="Availability">可选性。</param>
/// <param name="IsActive">是否为当前选中项。</param>
/// <param name="VersionOptions">版本下拉的选项（显示文本可能带「（推荐）」，值始终是纯版本号）。</param>
internal sealed record LoaderOptionViewModel(
    LoaderKind Kind,
    string Display,
    string Detail,
    LoaderAvailability Availability,
    bool IsActive,
    IReadOnlyList<ChoiceOption<string>> VersionOptions)
{
    /// <summary>这一行能不能点（不可用的行连悬停/点击都不该有）。</summary>
    public bool IsSelectable => Availability != LoaderAvailability.Unavailable;

    /// <summary>把一个内核加载器项投影成界面上的行。</summary>
    /// <param name="option">内核项。</param>
    /// <param name="isActive">是否为当前选中项（由向导按 <see cref="Kind"/> 现算）。</param>
    /// <returns>投影。</returns>
    public static LoaderOptionViewModel From(LoaderOption option, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(option);

        return new LoaderOptionViewModel(
            option.Kind,
            LocalizedText.Of(option.Display),
            LocalizedText.Of(option.Detail),
            option.Availability,
            isActive,
            BuildVersionOptions(option));
    }

    /// <summary>版本下拉的选项。</summary>
    /// <param name="option">内核项。</param>
    /// <returns>「显示文本 + 纯版本号」的选项表。</returns>
    public static IReadOnlyList<ChoiceOption<string>> BuildVersionOptions(LoaderOption option) =>
        [.. option.Versions.Select(version => new ChoiceOption<string>(
            IsRecommended(option, version) ? Loc.T("{0}（推荐）", version) : version,
            version))];

    private static bool IsRecommended(LoaderOption option, string version) =>
        option.RecommendedVersion is { } recommended
        && string.Equals(version, recommended, StringComparison.Ordinal);
}
