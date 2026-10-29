using System.IO.Compression;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Security;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>解压结果。</summary>
/// <param name="Extracted">成功解压的条目数。</param>
/// <param name="SkippedUnsafe">因路径越界而被拒绝的条目数。</param>
/// <param name="SkippedExcluded">因排除清单（如 <c>META-INF/</c>）而跳过的条目数。</param>
public sealed record NativeExtractionResult(int Extracted, int SkippedUnsafe, int SkippedExcluded);

/// <summary>
/// natives 归档的解压（B-4 里唯一会"写进一个由远端内容决定的目录结构"的地方）。
/// <para>
/// Zip Slip 防护是这里存在的主要理由：归档条目名是**远端的**，一个 <c>../../evil.dll</c>
/// 条目就足以把文件写到游戏目录之外（老启动器被打过这个洞）。逐条目经
/// <see cref="PathSafety.CombineWithin"/> 校验，越界条目**跳过并记一条事件**，
/// 而不是让整次安装失败：natives 里少一个不该存在的文件并不影响游戏启动，
/// 但"因为压缩包里有个怪条目就装不上"会让人完全摸不着头脑。事件里带上条目名，可疑内容不会被藏起来。
/// </para>
/// </summary>
public sealed class NativeExtractor
{
    private const int MaxEntries = 10_000;
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造解压器。</summary>
    /// <param name="diagnostics">诊断事件。</param>
    public NativeExtractor(IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;
    }

    /// <summary>解压一个 natives 归档。</summary>
    /// <param name="archivePath">归档路径。</param>
    /// <param name="targetDirectory">目标目录。</param>
    /// <param name="exclude">排除的条目前缀。</param>
    /// <returns>解压结果。</returns>
    /// <exception cref="IOException">归档不存在或无法读取。</exception>
    public NativeExtractionResult Extract(
        string archivePath,
        string targetDirectory,
        IReadOnlyList<string>? exclude)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!File.Exists(archivePath))
        {
            throw new IOException($"natives 归档不存在：{Path.GetFileName(archivePath)}。");
        }

        Directory.CreateDirectory(targetDirectory);

        var extracted = 0;
        var unsafeEntries = 0;
        var excluded = 0;
        long totalBytes = 0;

        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            if (extracted + excluded + unsafeEntries >= MaxEntries)
            {
                throw new InvalidDataException(
                    $"natives 归档的条目超过 {MaxEntries} 个，已拒绝解压。");
            }

            if (entry.Length > MaxEntryBytes)
            {
                throw new InvalidDataException(
                    $"natives 归档里的 {entry.FullName} 解压后超过 {MaxEntryBytes / 1024 / 1024} MB，已拒绝解压。");
            }

            var name = entry.FullName.Replace('\\', '/');

            if (IsExcluded(name, exclude))
            {
                excluded++;
                continue;
            }

            string targetPath;
            try
            {
                targetPath = PathSafety.CombineWithin(targetDirectory, [.. name.Split('/')]);
            }
            catch (InvalidDataException)
            {
                unsafeEntries++;
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Downloader,
                    "{0} 里有越界条目，已跳过：{1}",
                    [Path.GetFileName(archivePath), name]);

                continue;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // ExtractToFile 不看大小：压缩包里的 Length 是远端声明的，伪造一个很小的值
            // 仍可能在解压时写出巨大文件。这里按实际读出的字节数再挡一次。
            try
            {
                using var input = entry.Open();
                using var output = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920);

                var buffer = new byte[81920];
                long entryBytes = 0;
                int read;

                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryBytes += read;
                    totalBytes += read;

                    if (entryBytes > MaxEntryBytes || totalBytes > MaxTotalBytes)
                    {
                        throw new InvalidDataException(
                            "natives 归档解压后超过安全上限，已拒绝解压（可能是 zip 炸弹）。");
                    }

                    output.Write(buffer, 0, read);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                TryDelete(targetPath);
                throw;
            }

            extracted++;
        }

        return new NativeExtractionResult(extracted, unsafeEntries, excluded);
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.Name.Length == 0 || entry.FullName.EndsWith('/');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 失败路径上的清理是尽力而为；原始异常更重要。
        }
    }

    private static bool IsExcluded(string name, IReadOnlyList<string>? exclude)
    {
        if (exclude is null || exclude.Count == 0)
        {
            return false;
        }

        foreach (var prefix in exclude)
        {
            if (!string.IsNullOrEmpty(prefix)
                && name.StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
