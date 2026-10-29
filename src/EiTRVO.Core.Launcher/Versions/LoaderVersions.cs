using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 某个游戏版本可用的加载器版本列表。
/// </summary>
/// <param name="Kind">加载器种类。</param>
/// <param name="Versions">可用版本，**从新到旧**（界面下拉框第一项就是它）。</param>
/// <param name="Recommended">加载器自己标记的稳定版；没有标记时为 <see langword="null"/>。</param>
public sealed record LoaderVersionList(
    LoaderKind Kind,
    IReadOnlyList<string> Versions,
    string? Recommended)
{
    /// <summary>该游戏版本是否有对应的加载器版本。</summary>
    public bool IsAvailable => Versions.Count > 0;

    /// <summary>下拉框默认选中的那一项：加载器标记的稳定版，否则最新版。</summary>
    public string? Default =>
        Recommended is not null && Versions.Contains(Recommended, StringComparer.Ordinal)
            ? Recommended
            : Versions.Count > 0
                ? Versions[0]
                : null;

    /// <summary>构造一个"该游戏版本没有对应版本"的空列表。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>空列表。</returns>
    public static LoaderVersionList None(LoaderKind kind) => new(kind, [], null);
}
