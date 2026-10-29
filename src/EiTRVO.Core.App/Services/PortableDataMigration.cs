using System.IO;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 一次性把旧数据目录（<c>%APPDATA%\EiTRVO Core</c>）搬到启动器目录。
/// </summary>
/// <remarks>
/// 2026-10-20 用户要求数据目录跟启动器走。旧位置可能已经有账户 / 实例 / 日志，直接换根目录会让它们
/// 在界面上“消失”，所以首次启动做一次**只复制、不删除**的迁移；迁移完成后写标记，之后不再执行。
/// 复制到临时目录并成功后才逐项移入启动器目录，失败不会留下半套数据。
/// </remarks>
internal static class PortableDataMigration
{
    /// <summary>迁移完成标记；有它就说明这台机器已经选择过新目录。</summary>
    private const string MarkerFileName = "portable.migrated";

    /// <summary>迁移旧数据（默认源与目标）。</summary>
    /// <returns>是否发生了迁移。</returns>
    public static bool TryMigrate() =>
        TryMigrate(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EiTRVO Core"),
            AppContext.BaseDirectory);

    /// <summary>可测试的迁移入口。</summary>
    /// <param name="legacyRoot">旧数据根目录。</param>
    /// <param name="launcherRoot">启动器目录（新数据根目录）。</param>
    /// <returns>是否发生了迁移。</returns>
    public static bool TryMigrate(string legacyRoot, string launcherRoot)
    {
        var source = Path.GetFullPath(legacyRoot);
        var destination = Path.GetFullPath(launcherRoot);

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(source))
        {
            return false;
        }

        // 新目录已经有数据（或迁移过）就不再碰：用户可能已经用新目录跑过。
        if (File.Exists(Path.Combine(destination, MarkerFileName))
            || File.Exists(Path.Combine(destination, "settings.json"))
            || File.Exists(Path.Combine(destination, "accounts.json"))
            || Directory.Exists(Path.Combine(destination, "instances")))
        {
            return false;
        }

        var staging = Path.Combine(destination, ".migrate-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(staging);

            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, MarkerFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                var target = Path.Combine(staging, name);
                if (Directory.Exists(entry))
                {
                    CopyDirectory(entry, target);
                }
                else
                {
                    File.Copy(entry, target, overwrite: false);
                }
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                var target = Path.Combine(destination, Path.GetFileName(entry));
                if (Directory.Exists(target) || File.Exists(target))
                {
                    continue;
                }

                Directory.Move(entry, target);
            }

            File.WriteAllText(Path.Combine(destination, MarkerFileName), "Migrated from " + source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响启动；下次会重新尝试。
        }
    }
}
