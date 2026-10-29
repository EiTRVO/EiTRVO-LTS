using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>一个要解压的 natives 归档。</summary>
/// <param name="ArchivePath">归档（jar/zip）路径。</param>
/// <param name="TargetDirectory">解压目标目录。</param>
/// <param name="Exclude">不解压的条目前缀（如 <c>META-INF/</c>）。</param>
public sealed record NativeExtraction(
    string ArchivePath,
    string TargetDirectory,
    IReadOnlyList<string> Exclude);

/// <summary>老版本才需要的资源布局副本（源对象 → 目标路径）。</summary>
/// <param name="SourcePath">对象文件路径（<c>assets/objects/…</c>）。</param>
/// <param name="TargetPath">要铺到的位置。</param>
public sealed record AssetLayoutLink(string SourcePath, string TargetPath);

/// <summary>
/// 一次原版安装要做的全部事情（**只是计划**，不碰网络也不碰磁盘）。
/// <para>
/// 把"算出要下什么、下到哪"与"真的去下"分开，好处是这个计划可以被完整断言：
/// 目标路径、库规则筛选、natives 选择、资源对象展开，都能在离线单测里逐个钉住 ——
/// 而"装错一个库/漏一个 native"在真机上的表现往往只是启动时一句含糊的
/// <c>UnsatisfiedLinkError</c>。
/// </para>
/// </summary>
/// <param name="VersionId">版本 id。</param>
/// <param name="GameDirectory">游戏根目录。</param>
/// <param name="VersionJsonPath">版本 JSON 的落地路径。</param>
/// <param name="NativesDirectory">natives 解压目录。</param>
/// <param name="ClientJarPath">客户端 JAR 路径；该版本没声明时为 <see langword="null"/>。</param>
/// <param name="AssetIndexId">资源索引 id（如 <c>12</c>）。</param>
/// <param name="AssetIndexPath">资源索引文件路径。</param>
/// <param name="Files">要下载的文件。</param>
/// <param name="Natives">要解压的 natives。</param>
public sealed record InstallPlan(
    string VersionId,
    string GameDirectory,
    string VersionJsonPath,
    string NativesDirectory,
    string? ClientJarPath,
    string? AssetIndexId,
    string? AssetIndexPath,
    IReadOnlyList<DownloadItem> Files,
    IReadOnlyList<NativeExtraction> Natives)
{
    /// <summary>预计下载的总字节数（未知大小按 0 计）。</summary>
    public long TotalBytes => DiskSpaceChecker.TotalBytes(Files);
}

/// <summary>
/// 把一份（已合并继承链的）版本 JSON 变成安装计划。
/// <para>
/// 路径一律经 <see cref="PathSafety.CombineWithin"/> 拼装：库路径、资源对象哈希、资源名都来自
/// **远端清单**，其中任何一个含 <c>..</c> 都会让文件落到游戏目录之外（另外还挡 NTFS 重解析点）。
/// 越界时**抛异常而不是跳过**：一份带穿越路径的清单只能是被改过的，静默跳过会装出一个
/// "看起来装完了、其实少一半"的游戏。
/// </para>
/// </summary>
public static class InstallPlanner
{
    /// <summary>资源对象的下载地址前缀。</summary>
    public const string AssetBaseUrl = "https://resources.download.minecraft.net/";

    /// <summary>生成安装计划。</summary>
    /// <param name="version">已解析（继承链已合并）的版本。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="platform">当前平台（决定库与 natives 的筛选）。</param>
    /// <returns>安装计划。</returns>
    public static InstallPlan Plan(ResolvedVersion version, string gameDirectory, GamePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(platform);

        var json = version.Merged;
        var id = string.IsNullOrWhiteSpace(json.Id) ? version.BaseVersionId : json.Id;

        if (!PathSafety.IsSafePathSegment(id))
        {
            throw new InvalidDataException($"版本 id 不能用作目录名：'{id}'。");
        }

        var versionJsonPath = PathSafety.CombineWithin(gameDirectory, "versions", id, id + ".json");
        var nativesDirectory = PathSafety.CombineWithin(gameDirectory, "versions", id, "natives");

        List<DownloadItem> files = [];
        List<NativeExtraction> natives = [];

        // ---- 客户端 JAR ----
        string? clientJarPath = null;
        var client = json.Downloads?.Client;
        if (client?.Url is not null)
        {
            clientJarPath = PathSafety.CombineWithin(gameDirectory, "versions", id, id + ".jar");
            files.Add(new DownloadItem(
                DownloadKind.ClientJar, id, LegacyMojangUrls.Normalize(client.Url), client.Sha1 ?? string.Empty, client.Size, clientJarPath));
        }

        // ---- 日志配置（Log4j 的 xml；不放它出来，老版本启动会缺日志配置）----
        // 落盘位置与 M4 启动时 -Dlog4j.configurationFile 指向的位置必须是同一个地方，
        // 规则只留一份（Versions/LoggingConfigLayout）。
        var loggingPath = LoggingConfigLayout.TryGetPath(json, gameDirectory);
        if (loggingPath is not null)
        {
            var loggingFile = json.Logging!.Client!.File!;
            files.Add(new DownloadItem(
                DownloadKind.Logging,
                id,
                LegacyMojangUrls.Normalize(loggingFile.Url!),
                loggingFile.Sha1 ?? string.Empty,
                loggingFile.Size,
                loggingPath));
        }

        // ---- 资源索引（对象要等它下下来才知道）----
        string? assetIndexId = null;
        string? assetIndexPath = null;
        var assetIndex = json.AssetIndex;
        assetIndexId = assetIndex?.Id ?? json.Assets;

        if (assetIndex?.Url is not null && !string.IsNullOrWhiteSpace(assetIndexId) && PathSafety.IsSafePathSegment(assetIndexId))
        {
            assetIndexPath = PathSafety.CombineWithin(gameDirectory, "assets", "indexes", assetIndexId + ".json");
            files.Add(new DownloadItem(
                DownloadKind.AssetIndex, id, LegacyMojangUrls.Normalize(assetIndex.Url), assetIndex.Sha1 ?? string.Empty, assetIndex.Size, assetIndexPath));
        }

        // ---- 库与 natives ----
        foreach (var library in json.Libraries ?? [])
        {
            if (!LibraryFilter.Applies(library, platform))
            {
                continue;
            }

            AddLibrary(files, library, id, gameDirectory);
            AddNative(files, natives, library, id, gameDirectory, platform, nativesDirectory);
        }

        return new InstallPlan(
            VersionId: id,
            GameDirectory: Path.GetFullPath(gameDirectory),
            VersionJsonPath: versionJsonPath,
            NativesDirectory: nativesDirectory,
            ClientJarPath: clientJarPath,
            AssetIndexId: assetIndexId,
            AssetIndexPath: assetIndexPath,
            Files: files,
            Natives: natives);
    }

    /// <summary>把资源索引里的对象展开成下载项（按哈希去重）。</summary>
    /// <param name="index">资源索引内容。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="versionId">版本 id（镜像路线要用）。</param>
    /// <returns>下载项。</returns>
    public static IReadOnlyList<DownloadItem> PlanAssetObjects(
        AssetIndexContents index,
        string gameDirectory,
        string versionId)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        List<DownloadItem> items = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (index.Objects is null)
        {
            return items;
        }

        foreach (var (_, asset) in index.Objects)
        {
            var hash = asset.Hash;
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2 || !seen.Add(hash))
            {
                // 同一个哈希会被多个资源名共用（例如两个语言文件内容一样），只下一次。
                continue;
            }

            if (!IsHex(hash))
            {
                continue;
            }

            items.Add(new DownloadItem(
                DownloadKind.Asset,
                versionId,
                AssetBaseUrl + hash[..2] + "/" + hash,
                hash,
                asset.Size,
                PathSafety.CombineWithin(gameDirectory, "assets", "objects", hash[..2], hash)));
        }

        return items;
    }

    /// <summary>老版本（1.6 及以前）需要的资源布局副本。</summary>
    /// <param name="index">资源索引内容。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="assetIndexId">资源索引 id（虚拟布局的目录名）。</param>
    /// <returns>要复制的对象清单。</returns>
    public static IReadOnlyList<AssetLayoutLink> PlanAssetLayout(
        AssetIndexContents index,
        string gameDirectory,
        string assetIndexId)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        if (!index.Virtual && !index.MapToResources)
        {
            return [];
        }

        if (index.Objects is null)
        {
            return [];
        }

        List<AssetLayoutLink> links = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, asset) in index.Objects)
        {
            var hash = asset.Hash;
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2 || !IsHex(hash) || !seen.Add(hash))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var source = PathSafety.CombineWithin(gameDirectory, "assets", "objects", hash[..2], hash);

            if (index.Virtual && !string.IsNullOrWhiteSpace(assetIndexId))
            {
                links.Add(new AssetLayoutLink(source, PathSafety.CombineWithin(gameDirectory, "assets", "virtual", assetIndexId, name)));
            }

            if (index.MapToResources)
            {
                links.Add(new AssetLayoutLink(source, PathSafety.CombineWithin(gameDirectory, "resources", name)));
            }
        }

        return links;
    }

    /// <summary>一个库的主构件。</summary>
    private static void AddLibrary(List<DownloadItem> files, Library library, string versionId, string gameDirectory)
    {
        var artifact = library.Downloads?.Artifact;
        var relative = artifact?.Path;

        if (relative is null && artifact?.Url is not null && MavenPath.TryFromName(library.Name, out var fromName))
        {
            relative = fromName;
        }

        if (artifact?.Url is not null && !string.IsNullOrWhiteSpace(relative))
        {
            files.Add(new DownloadItem(
                DownloadKind.Library,
                versionId,
                LegacyMojangUrls.Normalize(artifact.Url),
                artifact.Sha1 ?? string.Empty,
                artifact.Size,
                PathSafety.CombineWithin(gameDirectory, "libraries", relative)));

            return;
        }

        // 旧版 Forge（≤1.12.2）与 Fabric / Quilt 的 profile 把仓库基址写在库对象自身，
        // 而不是 downloads/artifact 里。其中有校验值就校验（Fabric 的 asm 等就带 sha1），
        // 没有（Quilt 的 profile 全部没有）就如实按"不可校验"处理，绝不假装校验过。
        if (library.Url is not null && MavenPath.TryFromName(library.Name, out var mavenPath))
        {
            files.Add(new DownloadItem(
                DownloadKind.Library,
                versionId,
                LegacyMojangUrls.Normalize(library.Url.TrimEnd('/') + "/" + mavenPath),
                Sha1: library.Sha1 ?? string.Empty,
                Size: library.Size,
                TargetPath: PathSafety.CombineWithin(gameDirectory, "libraries", mavenPath)));
        }
    }

    /// <summary>一个库的 natives 分类器（要下载 + 要解压）。</summary>
    private static void AddNative(
        List<DownloadItem> files,
        List<NativeExtraction> extractions,
        Library library,
        string versionId,
        string gameDirectory,
        GamePlatform platform,
        string nativesDirectory)
    {
        if (library.Natives is null || library.Downloads?.Classifiers is null)
        {
            return;
        }

        if (!library.Natives.TryGetValue(platform.OsName, out var classifier) || string.IsNullOrWhiteSpace(classifier))
        {
            return;
        }

        if (!library.Downloads.Classifiers.TryGetValue(classifier, out var native) || native.Url is null)
        {
            return;
        }

        var relative = native.Path;
        if (string.IsNullOrWhiteSpace(relative)
            && MavenPath.TryFromName(library.Name, out var basePath))
        {
            // 分类器路径一般由清单直接给出；没给时按 Maven 规则补（坐标 + 分类器）。
            var name = library.Name ?? string.Empty;
            var parts = name.Split(':');
            relative = parts.Length >= 3
                ? MavenPath.FromName(parts[0] + ":" + parts[1] + ":" + parts[2] + ":" + classifier)
                : basePath;
        }

        if (string.IsNullOrWhiteSpace(relative))
        {
            return;
        }

        var archivePath = PathSafety.CombineWithin(gameDirectory, "libraries", relative);

        files.Add(new DownloadItem(
            DownloadKind.Library,
            versionId,
            LegacyMojangUrls.Normalize(native.Url),
            native.Sha1 ?? string.Empty,
            native.Size,
            archivePath));

        extractions.Add(new NativeExtraction(
            archivePath,
            nativesDirectory,
            library.Extract?.Exclude ?? []));
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
