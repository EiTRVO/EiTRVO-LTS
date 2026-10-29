using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p07 模组统计（属性区四格 + 缺失依赖横幅）：内核的 <see cref="ModSummary.LoaderMatch"/> /
/// <see cref="ModSummary.GameVersionMatch"/> 是「键 + 参数」，这里拼成当前语言。
/// </summary>
/// <remarks>
/// 属性名与内核逐字一致 ⇒ P07 的 <c>{Binding Summary.Enabled}</c> /
/// <c>{Binding Summary.LoaderMatch}</c> 这些绑定一个字都不用改（AGENTS.md §5.3 第 ⑨ 条）。
/// 计数本来就是 <see cref="int"/>（数据），照搬。
/// </remarks>
/// <param name="Enabled">已启用数量。</param>
/// <param name="Disabled">已禁用数量。</param>
/// <param name="MissingDependency">缺失依赖数量。</param>
/// <param name="Updatable">可更新数量。</param>
/// <param name="LoaderMatch">加载器匹配情况（已本地化）。</param>
/// <param name="GameVersionMatch">游戏版本匹配情况（已本地化）。</param>
/// <param name="MissingDependencyDetail">缺失依赖详情（已本地化；没有时为 <see langword="null"/>）。</param>
internal sealed record ModSummaryViewModel(
    int Enabled,
    int Disabled,
    int MissingDependency,
    int Updatable,
    string LoaderMatch,
    string GameVersionMatch,
    string? MissingDependencyDetail)
{
    /// <summary>把一个内核统计投影成可绑定的属性区。</summary>
    /// <param name="summary">内核统计。</param>
    /// <returns>投影。</returns>
    public static ModSummaryViewModel From(ModSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ModSummaryViewModel(
            summary.Enabled,
            summary.Disabled,
            summary.MissingDependency,
            summary.Updatable,
            LocalizedText.Of(summary.LoaderMatch),
            LocalizedText.Of(summary.GameVersionMatch),
            LocalizedText.OrNull(summary.MissingDependencyDetail));
    }
}
