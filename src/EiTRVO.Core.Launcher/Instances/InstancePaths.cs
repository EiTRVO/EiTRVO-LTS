using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Instances;

/// <summary>
/// 实例目录的**唯一解析处**（对齐 Neo 26.1：一个实例 = 游戏目录 <c>versions\</c> 下的一个目录）。
/// </summary>
/// <remarks>
/// <para>
/// 旧布局把 <c>instance.json</c> / <c>overrides.json</c> 放在启动器数据目录的 <c>instances\&lt;实例&gt;</c>、
/// 把游戏数据放在 <c>&lt;游戏目录&gt;\versions\&lt;实例&gt;</c> —— 同一件事实有两份落盘位置，
/// 删除要搬两个地方、扫描要合并两个来源、安装完还要记得把版本文件写进另一个目录。
/// </para>
/// <para>
/// 现在只有一个目录：<c>&lt;游戏目录&gt;\versions\&lt;实例名&gt;</c>。实例元数据、启动参数覆盖、
/// 版本 JSON（<c>version.json</c>）、客户端 JAR 与 <c>mods\</c> / <c>saves\</c> 等内容都在里面，
/// 与 Neo 26.1 的隔离结构一致。旧布局只做**只读回退与复制迁移**（见
/// <see cref="FileInstanceCatalog"/>），从不删除用户文件。
/// </para>
/// </remarks>
public static class InstancePaths
{
    /// <summary>实例元数据文件名（<c>instance.json</c>）。</summary>
    public const string MetadataFileName = "instance.json";

    /// <summary>实例启动参数覆盖文件名（<c>overrides.json</c>）。</summary>
    public const string OverridesFileName = "overrides.json";

    /// <summary>实例版本 JSON 的文件名（Neo 26.1 的约定；读取时优先于 <c>&lt;实例名&gt;.json</c>）。</summary>
    public const string VersionJsonFileName = "version.json";

    /// <summary>游戏目录下的 <c>versions\</c>。</summary>
    /// <param name="settings">全局设置（决定游戏根目录）。</param>
    /// <returns>绝对路径。</returns>
    public static string VersionsDirectory(GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Path.Combine(settings.GameDirectory, "versions");
    }

    /// <summary>实例目录：<c>&lt;游戏目录&gt;\versions\&lt;实例名&gt;</c>。</summary>
    /// <param name="settings">全局设置（决定游戏根目录）。</param>
    /// <param name="instanceName">实例名。</param>
    /// <returns>绝对路径。</returns>
    /// <exception cref="ArgumentException">实例名不能用作单段目录名。</exception>
    public static string Directory(GlobalSettings settings, string instanceName)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!LauncherPaths.IsValidInstanceName(instanceName))
        {
            throw new ArgumentException($"实例名不能用作目录名：'{instanceName}'。", nameof(instanceName));
        }

        return Path.Combine(settings.GameDirectory, "versions", instanceName);
    }

    /// <summary>
    /// 实例的**游戏数据目录**（命令行里的 <c>${game_directory}</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Directory"/> 是同一个目录：实例目录里既放元数据也放 <c>mods\</c> / <c>saves\</c>。
    /// 保留这个成员名是因为内容扫描、删除回收、大小统计都按"游戏数据目录"这个语义调用它。
    /// </remarks>
    /// <param name="settings">全局设置。</param>
    /// <param name="instanceName">实例名。</param>
    /// <returns>绝对路径。</returns>
    public static string ContentDirectory(GlobalSettings settings, string instanceName) =>
        Directory(settings, instanceName);

    /// <summary>实例元数据文件：<c>&lt;实例目录&gt;\instance.json</c>。</summary>
    /// <param name="settings">全局设置。</param>
    /// <param name="instanceName">实例名。</param>
    /// <returns>绝对路径。</returns>
    public static string MetadataFile(GlobalSettings settings, string instanceName) =>
        Path.Combine(Directory(settings, instanceName), MetadataFileName);

    /// <summary>实例启动参数覆盖文件：<c>&lt;实例目录&gt;\overrides.json</c>。</summary>
    /// <param name="settings">全局设置。</param>
    /// <param name="instanceName">实例名。</param>
    /// <returns>绝对路径。</returns>
    public static string OverridesFile(GlobalSettings settings, string instanceName) =>
        Path.Combine(Directory(settings, instanceName), OverridesFileName);

    /// <summary>实例版本 JSON：<c>&lt;实例目录&gt;\version.json</c>。</summary>
    /// <param name="settings">全局设置。</param>
    /// <param name="instanceName">实例名。</param>
    /// <returns>绝对路径。</returns>
    public static string VersionJsonFile(GlobalSettings settings, string instanceName) =>
        Path.Combine(Directory(settings, instanceName), VersionJsonFileName);

    /// <summary>实例版本 JSON 的兼容文件名：<c>&lt;实例目录&gt;\&lt;实例名&gt;.json</c>。</summary>
    /// <param name="settings">全局设置。</param>
    /// <param name="instanceName">实例名。</param>
    /// <returns>绝对路径。</returns>
    public static string VersionJsonAliasFile(GlobalSettings settings, string instanceName) =>
        Path.Combine(Directory(settings, instanceName), instanceName + ".json");

    /// <summary>实例客户端 JAR：<c>&lt;实例目录&gt;\&lt;实例名&gt;.jar</c>。</summary>
    /// <param name="settings">全局设置。</param>
    /// <param name="instanceName">实例名。</param>
    /// <returns>绝对路径。</returns>
    public static string ClientJarFile(GlobalSettings settings, string instanceName) =>
        Path.Combine(Directory(settings, instanceName), instanceName + ".jar");
}
