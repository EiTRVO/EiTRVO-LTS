using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p05 版本表的一行：内核 <see cref="MinecraftVersion"/> 的「本地状态」是「键 + 参数」，
/// 这里拼成当前语言的句子（Q8）。
/// </summary>
/// <remarks>
/// 属性名与内核逐字一致（<c>Id</c> / <c>Kind</c> / <c>ReleaseDate</c> / <c>RequiredJava</c> /
/// <c>LocalState</c> / <c>LocalStateText</c> / <c>IsActive</c>），所以 P05 的 XAML 一个字都不用改
/// （AGENTS.md §5.3 第 ⑨ 条：内核字段是模板时界面不能直接绑，必须经投影）。
/// </remarks>
/// <param name="Id">版本号。</param>
/// <param name="Kind">版本类型（表格里那一格的着色按它判断，所以原样留着）。</param>
/// <param name="ReleaseDate">发布日期文本（数据）。</param>
/// <param name="RequiredJava">所需 Java 文本（数据）。</param>
/// <param name="LocalState">本地状态（着色按它判断，所以原样留着）。</param>
/// <param name="LocalStateText">本地状态（已本地化）。</param>
/// <param name="IsActive">是否被选中。</param>
internal sealed record VersionRowViewModel(
    string Id,
    VersionKind Kind,
    string ReleaseDate,
    string RequiredJava,
    VersionLocalState LocalState,
    string LocalStateText,
    bool IsActive)
{
    /// <summary>这一行装好了才给「新建实例」快捷入口（未安装行不在表格里提供安装动作）。</summary>
    public bool CanCreateInstance => LocalState == VersionLocalState.Installed;
    /// <summary>把一个内核版本行投影成可绑定的行。</summary>
    /// <param name="version">内核行。</param>
    /// <returns>投影。</returns>
    public static VersionRowViewModel From(MinecraftVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new VersionRowViewModel(
            version.Id,
            version.Kind,
            version.ReleaseDate,
            version.RequiredJava,
            version.LocalState,
            LocalizedText.Of(version.LocalStateText),
            version.IsActive);
    }
}
