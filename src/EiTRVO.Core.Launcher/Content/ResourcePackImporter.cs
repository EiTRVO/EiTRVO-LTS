using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content;

/// <summary>
/// 本地资源包导入器。只接受单个 <c>.zip</c>，校验通过后原样复制到实例的 <c>resourcepacks\</c>。
/// </summary>
/// <remarks>
/// <para>
/// 资源包不是“解压后装进游戏”的：Minecraft 自己会读这个 zip。但 zip 仍可能带
/// 路径穿越条目（将来任何解压实现都会中招）与压缩炸弹（游戏读它时撑爆内存），
/// 所以导入前先把整份归档**只读地**过一遍：
/// </para>
/// <list type="bullet">
/// <item>条目名不许绝对路径、盘符、<c>..</c>、重复；落地路径必须仍在 <c>resourcepacks\</c> 下；</item>
/// <item>拒绝 Unix 符号链接条目；</item>
/// <item>条目数 ≤ 10000、单条 ≤ 256 MB、总解压量 ≤ 512 MB、压缩比 ≤ 200；</item>
/// <item>逐条真正解压到计数流，实际解压量超过声明值或上限就拒绝（防伪造中央目录长度）；</item>
/// <item>根目录必须有 <c>pack.mcmeta</c>，且 <c>pack.pack_format</c> 是正整数。</item>
/// </list>
/// </remarks>
public sealed class ResourcePackImporter : IResourcePackImporter
{
    private const int MaxEntries = 10_000;
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private const long MaxSourceBytes = 512L * 1024 * 1024;
    private const long MaxCompressionRatio = 200;
    private const long MaxPackMetaBytes = 1024 * 1024;
    private const string ResourcePacksDirectoryName = "resourcepacks";

    private readonly IInstanceCatalog _instances;

    /// <summary>构造资源包导入器。</summary>
    /// <param name="instances">实例目录（目标 <c>resourcepacks\</c> 从实例路径解析）。</param>
    public ResourcePackImporter(IInstanceCatalog instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        _instances = instances;
    }

    /// <inheritdoc />
    public async Task<ContentActionResult> ImportAsync(
        string instanceName,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        return await Task.Run(
            () => Import(instanceName, sourcePath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private ContentActionResult Import(
        string instanceName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            return Failed(TextTemplate.Of("找不到要导入的文件：{0}。", Path.GetFileName(source)));
        }

        if (!string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(TextTemplate.Of("资源包必须是 .zip 文件（当前是 {0}）。", Path.GetExtension(source)));
        }

        if (new FileInfo(source).Length > MaxSourceBytes)
        {
            return Failed(TextTemplate.Of(
                "资源包文件超过 {0} MB，已拒绝。",
                (MaxSourceBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture)));
        }

        InstanceLaunchInfo launch;
        try
        {
            launch = _instances.GetLaunchInfo(instanceName);
        }
        catch (KeyNotFoundException)
        {
            return Failed(TextTemplate.Of("实例 {0} 已经不在了。", instanceName));
        }

        var resourcePacksDirectory = Path.Combine(launch.InstancePath, ResourcePacksDirectoryName);
        var fileName = Path.GetFileName(source);

        if (!PathSafety.IsSafePathSegment(fileName))
        {
            return Failed(TextTemplate.Of("资源包文件名不可用：{0}。", fileName));
        }

        var destination = Path.Combine(resourcePacksDirectory, fileName);
        if (!PathSafety.IsContained(resourcePacksDirectory, destination))
        {
            return Failed(TextTemplate.Of("资源包目标路径越出了 resourcepacks 目录。"));
        }

        if (File.Exists(destination))
        {
            return Failed(TextTemplate.Of("同名资源包已经存在：{0}。", fileName));
        }

        ContentActionResult? validation;
        try
        {
            validation = ValidateArchive(source, resourcePacksDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or IOException
                                           or NotSupportedException
                                           or ArgumentException
                                           or JsonException)
        {
            // 损坏 / 不支持的 zip 实现细节不该把整页崩掉，也不该落一个半成品。
            return Failed(TextTemplate.Of("资源包不是有效的 zip 文件（{0}）。", exception.Message));
        }

        if (validation is not null)
        {
            return validation;
        }

        string temporary = Path.Combine(
            resourcePacksDirectory,
            fileName + "." + Guid.NewGuid().ToString("N") + ".part");

        try
        {
            Directory.CreateDirectory(resourcePacksDirectory);
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            TryDelete(temporary);
            return Failed(TextTemplate.Of("导入资源包失败（{0}）。", exception.Message));
        }

        return new ContentActionResult(
            true,
            TextTemplate.Of("已把资源包 {0} 导入到当前实例。", fileName));
    }

    /// <summary>只读检查整份归档；通过返回 <see langword="null"/>，否则返回失败结果。</summary>
    private static ContentActionResult? ValidateArchive(
        string source,
        string resourcePacksDirectory,
        CancellationToken cancellationToken)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(source);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return Failed(TextTemplate.Of("资源包不是有效的 zip 文件（{0}）。", exception.Message));
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxEntries)
            {
                return Bomb();
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;
            var hasPackMeta = false;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalized = entry.FullName.Replace('\\', '/');
                var pathIssue = ValidateEntryPath(normalized, resourcePacksDirectory);
                if (pathIssue is not null)
                {
                    return Failed(TextTemplate.Of("资源包里的路径不安全：{0}。", pathIssue));
                }

                if (!names.Add(normalized))
                {
                    return Failed(TextTemplate.Of("资源包里有重复的条目：{0}。", normalized));
                }

                if (IsSymbolicLink(entry))
                {
                    return Failed(TextTemplate.Of("资源包里有符号链接条目：{0}。", normalized));
                }

                if (entry.Length > MaxEntryBytes)
                {
                    return Bomb();
                }

                if (!TryAddCapped(ref declaredTotal, entry.Length, MaxTotalBytes))
                {
                    return Bomb();
                }

                if (entry.Length > 0)
                {
                    if (entry.CompressedLength == 0)
                    {
                        return Bomb();
                    }

                    if (entry.Length / entry.CompressedLength > MaxCompressionRatio)
                    {
                        return Bomb();
                    }
                }

                if (string.Equals(normalized, "pack.mcmeta", StringComparison.Ordinal))
                {
                    hasPackMeta = true;
                }
            }

            if (!hasPackMeta)
            {
                return Failed(TextTemplate.Of("这不是资源包：根目录缺少 pack.mcmeta。"));
            }

            // 真实解压复核：中央目录里的 Length 是远端数据，不能只信它。
            long actualTotal = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                {
                    continue;
                }

                using var stream = entry.Open();
                var buffer = new byte[81_920];
                long actual = 0;
                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    actual += read;

                    if (actual > entry.Length)
                    {
                        return Bomb();
                    }

                    if (!TryAddCapped(ref actualTotal, read, MaxTotalBytes))
                    {
                        return Bomb();
                    }
                }

                if (actual != entry.Length)
                {
                    return Bomb();
                }
            }

            return ValidatePackMeta(archive);
        }
    }

    private static ContentActionResult? ValidatePackMeta(ZipArchive archive)
    {
        var packMeta = archive.Entries.FirstOrDefault(static entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), "pack.mcmeta", StringComparison.Ordinal));

        if (packMeta is null || packMeta.Length > MaxPackMetaBytes)
        {
            return Failed(TextTemplate.Of("pack.mcmeta 缺失或过大。"));
        }

        try
        {
            using var stream = packMeta.Open();
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("pack", out var pack)
                || pack.ValueKind != JsonValueKind.Object
                || !pack.TryGetProperty("pack_format", out var format)
                || format.ValueKind != JsonValueKind.Number
                || !format.TryGetInt32(out var version)
                || version <= 0)
            {
                return Failed(TextTemplate.Of("pack.mcmeta 里没有有效的 pack.pack_format。"));
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            return Failed(TextTemplate.Of("pack.mcmeta 解析失败（{0}）。", exception.Message));
        }

        return null;
    }

    /// <summary>条目路径检查：绝对路径、盘符、<c>..</c> 与最终落地位置。</summary>
    private static string? ValidateEntryPath(string normalized, string root)
    {
        // 目录条目以 "/" 结尾是合法的；路径检查针对的是去掉尾斜杠后的实际路径。
        var path = normalized.EndsWith('/') ? normalized[..^1] : normalized;

        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/')
            || path.Contains(':', StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            return normalized;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0
                || string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return normalized;
            }
        }

        var destination = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        return PathSafety.IsContained(root, destination) ? null : normalized;
    }

    /// <summary>Unix 的符号链接在 zip 里用外部属性的高 16 位标记（S_IFLNK = 0xA000）。</summary>
    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    /// <summary>加上一个非负长度并检查上限；溢出也算超限。</summary>
    private static bool TryAddCapped(ref long total, long value, long cap)
    {
        if (value < 0 || total > cap - value)
        {
            return false;
        }

        total += value;
        return true;
    }

    private static ContentActionResult Failed(TextTemplate reason) => new(false, reason);

    private static ContentActionResult Bomb() =>
        Failed(TextTemplate.Of("资源包里的解压数据超过安全上限，已拒绝（可能是压缩炸弹）。"));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时文件删不掉不影响结果；目标文件从未动过。
        }
    }
}
