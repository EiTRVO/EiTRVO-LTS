using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Installer;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p09 存档表的一行：内核 <see cref="WorldSaveEntry.LastPlayed"/> 与
/// <see cref="WorldSaveEntry.GameMode"/> 是「键 + 参数」（`今天 16:01`、`生存`），
/// 这里拼成当前语言。
/// </summary>
/// <remarks>
/// 属性名与内核逐字一致（<c>Name</c> / <c>Folder</c> / <c>Size</c> / <c>LastPlayed</c> / <c>GameMode</c> /
/// <c>GameVersion</c> / <c>IsBackedUp</c> / <c>IsActive</c>），所以 P09 的五列表格、
/// 「已备份」触发器与 <c>SelectedItem</c> 绑定都不用改 —— 选中行的类型随之变成这个投影，
/// 由 ViewModel 自己持有（它只用来驱动高亮与三个备份 / 恢复命令）。
/// </remarks>
/// <param name="Name">存档名（显示名）。</param>
/// <param name="Folder">存档目录名（磁盘上的事实：备份文件名与恢复目标都按它定，M7-f）。</param>
/// <param name="Size">大小文本（数据）。</param>
/// <param name="LastPlayed">最后游玩（已本地化）。</param>
/// <param name="GameMode">游戏模式（已本地化）。</param>
/// <param name="GameVersion">存档版本（数据）。</param>
/// <param name="IsBackedUp">是否已备份。</param>
/// <param name="IsActive">是否被选中。</param>
internal sealed record WorldSaveRowViewModel(
    string Name,
    string Folder,
    string Size,
    string LastPlayed,
    string GameMode,
    string GameVersion,
    bool IsBackedUp,
    bool IsActive)
{
    /// <summary>
    /// 这一行对应的备份 / 恢复目标（目录名 + 显示名）。
    /// </summary>
    /// <remarks>
    /// 由投影自己给出，而不是让每个命令各自拼一次 <c>new WorldBackupTarget(Folder, Name)</c> ——
    /// 三个命令（备份所选 / 恢复所选 / 备份全部）里任何一处写反了参数顺序，
    /// 都会变成"备份了一个名字很像的目录"。
    /// </remarks>
    public WorldBackupTarget BackupTarget => new(Folder, Name);

    /// <summary>把一个内核存档行投影成可绑定的行。</summary>
    /// <param name="save">内核行。</param>
    /// <returns>投影。</returns>
    public static WorldSaveRowViewModel From(WorldSaveEntry save)
    {
        ArgumentNullException.ThrowIfNull(save);

        return new WorldSaveRowViewModel(
            save.Name,
            save.Folder,
            save.Size,
            LocalizedText.Of(save.LastPlayed),
            LocalizedText.Of(save.GameMode),
            save.GameVersion,
            save.IsBackedUp,
            save.IsActive);
    }
}
