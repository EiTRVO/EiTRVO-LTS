using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Content;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p09 资源包列表的一行：内核 <see cref="ResourcePackEntry.Detail"/> 是「键 + 参数」
/// （`32× · 18.4 MB · 优先级 1` 里的 `优先级` 是文案），这里拼成当前语言。
/// </summary>
/// <remarks>
/// 属性名与内核逐字一致（<c>Name</c> / <c>Detail</c> / <c>Priority</c> / <c>IsEnabled</c> /
/// <c>IsActive</c>），所以 P09 的列表项模板与两个 <c>DataTrigger</c> 一个字都不用改。
/// <para>
/// M7-g-2b：多一格 <see cref="FileName"/> —— 启用 / 停用 / 上移 / 下移 / 删除要把它**原样交回内核**
/// （显示名会重名，只有磁盘上的文件名唯一，它同时决定 <c>options.txt</c> 里的 id）。
/// </para>
/// </remarks>
/// <param name="Name">资源包名。</param>
/// <param name="FileName">磁盘上的文件名（zip 带扩展名，文件夹就是目录名）。</param>
/// <param name="Detail">说明（已本地化）。</param>
/// <param name="Priority">优先级序号（<c>1</c> = 最高）。</param>
/// <param name="IsEnabled">是否启用。</param>
/// <param name="IsActive">是否被选中。</param>
internal sealed record ResourcePackRowViewModel(
    string Name,
    string FileName,
    string Detail,
    int Priority,
    bool IsEnabled,
    bool IsActive)
{
    /// <summary>把一个内核资源包行投影成可绑定的行。</summary>
    /// <param name="pack">内核行。</param>
    /// <returns>投影。</returns>
    public static ResourcePackRowViewModel From(ResourcePackEntry pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        return new ResourcePackRowViewModel(
            pack.Name,
            pack.FileName,
            LocalizedText.Of(pack.Detail),
            pack.Priority,
            pack.IsEnabled,
            pack.IsActive);
    }
}
