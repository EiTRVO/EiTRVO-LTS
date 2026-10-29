using System.IO;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 决定启动器的数据根目录：便携运行时仍在 exe 旁边；MSIX 安装后安装目录只读，
/// 数据必须落到当前用户的 <c>%LOCALAPPDATA%</c>。
/// </summary>
/// <remarks>
/// 判断依据与默认游戏目录共用 <see cref="PackagedEnvironment"/>，避免便携版和 MSIX 版
/// 各自维护一套“当前是不是在包里”的实现。
/// </remarks>
internal static class PackagedDataRoot
{
    /// <summary>解析本次运行的数据根目录。</summary>
    /// <returns>
    /// 便携模式返回 exe 目录；MSIX 模式返回包内
    /// <c>%LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;\LocalState\EiTRVO LTS</c>。
    /// </returns>
    public static string Resolve() => PackagedEnvironment.ResolveDataRoot();

    /// <summary>
    /// 解析由启动器下载的 Java 运行时目录。
    /// </summary>
    /// <returns><c>&lt;Documents&gt;\EiTRVO LTS\runtime</c>。</returns>
    /// <remarks>
    /// 与默认游戏目录放在同一个 Documents 根下，避免 Java 运行时藏在 MSIX 包的
    /// LocalState 里；Java 检测与安装共用这一条路径。
    /// </remarks>
    public static string ResolveJavaRuntimeDirectory() =>
        Path.Combine(
            SettingsDefaults.DefaultDocumentsDirectory,
            "EiTRVO LTS",
            "runtime");

    /// <summary>
    /// 解析账户文件目录。
    /// </summary>
    /// <returns>
    /// MSIX 模式返回 <c>&lt;Documents&gt;\EiTRVO LTS</c>；便携模式返回
    /// <see langword="null"/>，由 <see cref="LauncherPaths"/> 回落到数据根目录。
    /// </returns>
    public static string? ResolveAccountsDirectory() =>
        PackagedEnvironment.IsPackaged
            ? Path.Combine(SettingsDefaults.DefaultDocumentsDirectory, "EiTRVO LTS")
            : null;

    /// <summary>
    /// 把旧版 MSIX 包目录里的 <c>accounts.json</c> 复制到 Documents 下的新位置。
    /// </summary>
    /// <param name="target">新的账户文件路径。</param>
    /// <remarks>
    /// 只在新位置**没有**账户文件时迁移，选择旧文件中最后写入的一份复制；
    /// 旧文件原样保留，不删除、不覆盖，避免迁移失败时丢失用户唯一的凭据。
    /// 失败不阻断启动，用户仍可用旧文件手工恢复。
    /// </remarks>
    public static void MigrateLegacyAccountFile(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || File.Exists(target))
        {
            return;
        }

        var candidates = new List<string>();

        var redirectedLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(redirectedLocal))
        {
            candidates.Add(Path.Combine(redirectedLocal, PackagedEnvironment.DataFolderName, "accounts.json"));
        }

        candidates.Add(Path.Combine(PackagedEnvironment.ResolveDataRoot(), "accounts.json"));

        var source = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (source is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(source, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迁移是尽力而为：旧文件仍在，绝不能因为它失败而影响启动。
        }
    }
}
