using System.Text;
using System.Text.Json;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// 把共享版本目录里的版本**展开成自包含版本**并写进实例自己的目录。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-23 起实例不再依赖共享 <c>versions\&lt;版本 id&gt;\</c>：安装器先按原样装进共享目录，
/// 然后这里把继承链合并成**一份没有 <c>inheritsFrom</c> 的 version.json**（<c>id</c> = 实例名），
/// 连同客户端 JAR 与 natives 一起写进 <c>versions\&lt;实例名&gt;\</c>。
/// 库与资源仍然共享（<c>libraries\</c> / <c>assets\</c> 不复制）。
/// </para>
/// <para>
/// 合并之后，继承链上的共享版本目录如果**没有别的实例再引用**就删掉：
/// 装一个 1.12.2-Forge 最终只留下 <c>versions\1.12.2-Forge\</c> 一个目录。
/// 旧实例（只有 <c>instance.json</c>、没有 <c>version.json</c>）仍被扫描为消费者，相关共享目录会保留。
/// </para>
/// </remarks>
public static class InstalledVersionMirror
{
    /// <summary>实例版本 JSON 的文件名（与 <c>InstancePaths.VersionJsonFileName</c> 同值）。</summary>
    public const string VersionJsonFileName = "version.json";

    /// <summary>执行镜像（并清理不再被引用的共享版本目录）。</summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="sourceVersionId">共享版本目录名（安装器刚写好的那个）。</param>
    /// <param name="instanceName">实例名（同时是目标目录名与版本 JSON 的 id）。</param>
    /// <exception cref="ArgumentException">版本 id / 实例名不能用作目录名。</exception>
    /// <exception cref="DirectoryNotFoundException">共享版本目录不存在。</exception>
    /// <param name="versionCacheDirectory">
    /// 版本 JSON 缓存目录；为 <see langword="null"/> 时按游戏目录旁的 <c>cache\versions</c> 推断。
    /// </param>
    /// <exception cref="FileNotFoundException">共享版本目录里没有版本 JSON，或继承链解析失败。</exception>
    public static void Mirror(
        string gameDirectory,
        string sourceVersionId,
        string instanceName,
        string? versionCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        if (!PathSafety.IsSafePathSegment(sourceVersionId))
        {
            throw new ArgumentException($"版本 id 不能用作目录名：'{sourceVersionId}'。", nameof(sourceVersionId));
        }

        if (!PathSafety.IsSafePathSegment(instanceName))
        {
            throw new ArgumentException($"实例名不能用作目录名：'{instanceName}'。", nameof(instanceName));
        }

        var versionsDirectory = Path.GetFullPath(Path.Combine(gameDirectory, "versions"));
        var cacheVersionDirectory = ResolveCacheDirectory(gameDirectory, versionCacheDirectory);
        var sourceDirectory = PathSafety.CombineWithin(versionsDirectory, sourceVersionId);
        var targetDirectory = PathSafety.CombineWithin(versionsDirectory, instanceName);

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"共享版本目录不存在：'{sourceDirectory}'。");
        }

        var sourceJson = FindVersionJson(sourceDirectory, sourceVersionId)
            ?? throw new FileNotFoundException(
                $"共享版本目录里没有版本 JSON（{sourceDirectory}），无法写进实例目录。",
                sourceDirectory);

        var source = ReadJson(sourceJson, sourceVersionId);

        ResolvedVersion resolved;

        try
        {
            resolved = VersionInheritanceResolver.Resolve(
                source with { Id = sourceVersionId },
                parentId => ReadParent(versionsDirectory, cacheVersionDirectory, parentId));
        }
        catch (VersionResolutionException exception)
        {
            throw new FileNotFoundException(
                $"版本 {sourceVersionId} 的继承链解析失败：{exception.Message}",
                sourceJson);
        }

        // 自包含 JSON：id 换成实例名，继承标记已由 Merge 清掉。
        var merged = resolved.Merged with { Id = instanceName, InheritsFrom = null };

        Directory.CreateDirectory(targetDirectory);

        var jsonText = LauncherJson.Serialize(merged, LauncherJson.Indented);
        WriteText(Path.Combine(targetDirectory, instanceName + ".json"), jsonText);
        WriteText(Path.Combine(targetDirectory, VersionJsonFileName), jsonText);

        // 旧版 Forge 的合并 JSON 已经没有 inheritsFrom：原版目录只能从实例元数据的 gameVersion 找。
        var gameVersion = ReadInstanceGameVersion(targetDirectory);

        // 客户端 JAR：加载器版本目录里通常没有自己的客户端 JAR，要用链根（原版）的那个。
        var jar = FindClientJar(sourceDirectory, sourceVersionId)
                   ?? FindClientJar(Path.Combine(versionsDirectory, resolved.BaseVersionId), resolved.BaseVersionId)
                   ?? (string.IsNullOrWhiteSpace(gameVersion)
                       ? null
                       : FindClientJar(Path.Combine(versionsDirectory, gameVersion), gameVersion));

        var targetJar = Path.Combine(targetDirectory, instanceName + ".jar");

        if (jar is not null
            && !string.Equals(Path.GetFullPath(jar), Path.GetFullPath(targetJar), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(jar, targetJar, overwrite: true);
        }

        var natives = Path.Combine(sourceDirectory, "natives");
        if (!Directory.Exists(natives))
        {
            natives = Path.Combine(versionsDirectory, resolved.BaseVersionId, "natives");
        }

        if (!Directory.Exists(natives) && !string.IsNullOrWhiteSpace(gameVersion))
        {
            natives = Path.Combine(versionsDirectory, gameVersion, "natives");
        }

        if (Directory.Exists(natives))
        {
            CopyDirectory(natives, Path.Combine(targetDirectory, "natives"));
        }

        // 共享版本目录没有消费者时删掉：加载器那层与它继承的原版那层都检查。
        // 旧版 Forge 的 InstallResolvedAsync 产出的 JSON 已经自包含（没有 inheritsFrom），
        // 链里只剩 Forge 自己；这时原版目录要从 instance.json 的 gameVersion 补进来，
        // 否则会留下一个孤立的 versions\1.12.2（2026-10-24 用户实测）。
        var cleanupCandidates = resolved.Chain.ToList();

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            cleanupCandidates.Add(gameVersion);
        }

        foreach (var candidate in cleanupCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryRemoveUnreferenced(versionsDirectory, candidate, instanceName);
        }
    }

    /// <summary>读实例元数据里的游戏版本（清理共享原版目录时需要它）。</summary>
    private static string? ReadInstanceGameVersion(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "instance.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return document.RootElement.TryGetProperty("gameVersion", out var gameVersion)
                ? gameVersion.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把升级前留下的“共享版本 + 实例目录”整理成“一实例一份自包含版本”。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两种旧实例都要处理：有 <c>version.json</c> 的（它是共享版本 JSON 的副本，带 <c>inheritsFrom</c>）
    /// 以自己为源重新展开；只有 <c>instance.json</c> 的按元数据算出共享版本 id，再镜像过来。
    /// 全部处理完后扫一遍共享目录，把已经没有实例引用的删掉。
    /// </para>
    /// <para>
    /// 每个实例都单独 try/catch：一个坏实例不该让启动器的其它实例也整理不了。
    /// </para>
    /// </remarks>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="versionCacheDirectory">版本 JSON 缓存目录；为 <see langword="null"/> 时自动推断。</param>
    public static void MigrateExistingInstances(string gameDirectory, string? versionCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var versionsDirectory = Path.GetFullPath(Path.Combine(gameDirectory, "versions"));
        if (!Directory.Exists(versionsDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            var instanceName = Path.GetFileName(directory);

            if (!File.Exists(Path.Combine(directory, "instance.json")))
            {
                continue;
            }

            try
            {
                if (File.Exists(Path.Combine(directory, VersionJsonFileName)))
                {
                    // 已经是实例目录里的 version.json：以自己为源重新解析（旧副本可能还带 inheritsFrom）。
                    Mirror(gameDirectory, instanceName, instanceName, versionCacheDirectory);
                    continue;
                }

                var sourceId = LegacySourceId(Path.Combine(directory, "instance.json"));
                if (sourceId is not null
                    && Directory.Exists(Path.Combine(versionsDirectory, sourceId)))
                {
                    Mirror(gameDirectory, sourceId, instanceName, versionCacheDirectory);
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or DirectoryNotFoundException
                                               or FileNotFoundException)
            {
                // 这个实例先保持原样；下一次启动会再试。
            }
        }

        // 实例都已自包含后，把没人再引用的共享版本目录清掉。
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            if (File.Exists(Path.Combine(directory, "instance.json")))
            {
                continue;
            }

            TryRemoveUnreferenced(versionsDirectory, Path.GetFileName(directory), string.Empty);
        }
    }

    /// <summary>旧实例元数据里隐含的共享版本 id。</summary>
    private static string? LegacySourceId(string metadataPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath, Encoding.UTF8));
            var root = document.RootElement;

            var gameVersion = root.TryGetProperty("gameVersion", out var game) ? game.GetString() : null;
            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                return null;
            }

            var loaderText = root.TryGetProperty("loader", out var loader) ? loader.GetString() : null;
            var loaderVersion = root.TryGetProperty("loaderVersion", out var loaderValue) ? loaderValue.GetString() : null;

            return Enum.TryParse<LoaderKind>(loaderText, ignoreCase: true, out var parsedLoader)
                   && parsedLoader != LoaderKind.Vanilla
                   && !string.IsNullOrWhiteSpace(loaderVersion)
                ? LoaderNaming.VersionId(parsedLoader, loaderVersion, gameVersion)
                : gameVersion;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>版本 JSON 缓存目录：显式给的优先，否则在游戏目录旁找 <c>cache\versions</c>。</summary>
    private static string ResolveCacheDirectory(string gameDirectory, string? supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return Path.GetFullPath(supplied);
        }

        List<string> candidates =
        [
            Path.Combine(gameDirectory, "cache", "versions"),
        ];

        var parent = Path.GetDirectoryName(Path.GetFullPath(gameDirectory));
        if (!string.IsNullOrWhiteSpace(parent))
        {
            candidates.Add(Path.Combine(parent, "cache", "versions"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "cache", "versions"));

        return Path.GetFullPath(
            candidates.FirstOrDefault(Directory.Exists) ?? candidates[0]);
    }

    private static VersionJson ReadJson(string path, string versionId)
    {
        VersionJson? json;

        try
        {
            json = LauncherJson.Deserialize<VersionJson>(
                File.ReadAllText(path, Encoding.UTF8),
                LauncherJson.Indented);
        }
        catch (JsonException exception)
        {
            throw new FileNotFoundException($"版本 JSON 不是合法 JSON：'{path}'。", path, exception);
        }

        return json is null
            ? throw new FileNotFoundException($"版本 JSON 是空的：'{path}'。", path)
            : json with { Id = versionId };
    }

    private static VersionJson? ReadParent(string versionsDirectory, string cacheVersionDirectory, string parentId)
    {
        var path = FindVersionJson(PathSafety.CombineWithin(versionsDirectory, parentId), parentId)
                   ?? FindCachedJson(cacheVersionDirectory, parentId);

        return path is null ? null : ReadJson(path, parentId);
    }

    /// <summary>版本 JSON 的缓存（共享版本目录里可能只留下了 JAR，JSON 在 <c>cache\versions</c>）。</summary>
    private static string? FindCachedJson(string cacheVersionDirectory, string versionId)
    {
        if (!Directory.Exists(cacheVersionDirectory))
        {
            return null;
        }

        var flat = Path.Combine(cacheVersionDirectory, versionId + ".json");
        if (File.Exists(flat))
        {
            return flat;
        }

        var nested = Path.Combine(cacheVersionDirectory, versionId, versionId + ".json");
        if (File.Exists(nested))
        {
            return nested;
        }

        var neo = Path.Combine(cacheVersionDirectory, versionId, VersionJsonFileName);
        return File.Exists(neo) ? neo : null;
    }

    /// <summary>版本目录里实际存在的版本 JSON（<c>&lt;id&gt;.json</c> 优先，其次 <c>version.json</c>）。</summary>
    private static string? FindVersionJson(string directory, string versionId)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var primary = Path.Combine(directory, versionId + ".json");
        if (File.Exists(primary))
        {
            return primary;
        }

        var neo = Path.Combine(directory, VersionJsonFileName);
        return File.Exists(neo) ? neo : null;
    }

    /// <summary>
    /// 版本目录里的客户端 JAR：只认 <c>&lt;id&gt;.jar</c>。
    /// 加载器目录里的唯一 jar 可能是 universal / installer 产物，不能当客户端 JAR 复制。
    /// </summary>
    private static string? FindClientJar(string directory, string versionId)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var primary = Path.Combine(directory, versionId + ".jar");
        return File.Exists(primary) ? primary : null;
    }

    /// <summary>递归复制目录（覆盖同名文件）。</summary>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void WriteText(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// 如果没有任何实例再引用这个共享版本目录，就删掉它。
    /// </summary>
    /// <remarks>
    /// 判据分两种：**自包含实例**看它自己的 <c>version.json</c> 是否就是/继承自这个 id；
    /// **旧实例**（没有 version.json）按 <c>instance.json</c> 的游戏版本 / 加载器推出它要找的 id。
    /// 只要有一个消费者，目录就保留 —— 宁可多留，不能把别人的启动链删掉。
    /// </remarks>
    private static void TryRemoveUnreferenced(string versionsDirectory, string versionId, string instanceName)
    {
        if (string.Equals(versionId, instanceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            var name = Path.GetFileName(directory);

            if (string.Equals(name, versionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, instanceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var metadata = Path.Combine(directory, "instance.json");
            if (!File.Exists(metadata))
            {
                continue;
            }

            var versionJson = Path.Combine(directory, VersionJsonFileName);
            if (File.Exists(versionJson))
            {
                if (References(versionJson, versionId, versionsDirectory))
                {
                    return;
                }

                continue;
            }

            if (LegacyInstanceReferences(metadata, versionId))
            {
                return;
            }
        }

        try
        {
            var target = PathSafety.CombineWithin(versionsDirectory, versionId);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着：多一份共享目录不会让启动失败，删错才会。
        }
    }

    /// <summary>自包含版本的继承链里是否出现 <paramref name="versionId"/>（含自身）。</summary>
    private static bool References(string versionJson, string versionId, string versionsDirectory)
    {
        var current = versionJson;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var directory = Path.GetDirectoryName(current)!;
            var id = Path.GetFileName(directory);

            if (!seen.Add(id))
            {
                return false;
            }

            if (string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            VersionJson json;

            try
            {
                json = ReadJson(current, id);
            }
            catch (FileNotFoundException)
            {
                return false;
            }

            if (json.InheritsFrom is not { } parent)
            {
                return false;
            }

            // 父目录可能只剩 JAR（JSON 在 cache 里）：即便如此，这个 id 也是它声明的依赖，不能删。
            if (string.Equals(parent, versionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var parentJson = FindVersionJson(Path.Combine(versionsDirectory, parent), parent);
            if (parentJson is null)
            {
                return false;
            }

            current = parentJson;
        }
    }

    /// <summary>旧实例（没有 version.json）是否会找这个共享版本 id。</summary>
    private static bool LegacyInstanceReferences(string metadataPath, string versionId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath, Encoding.UTF8));
            var root = document.RootElement;

            var gameVersion = root.TryGetProperty("gameVersion", out var game) ? game.GetString() : null;
            if (string.Equals(gameVersion, versionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var loaderText = root.TryGetProperty("loader", out var loader) ? loader.GetString() : null;
            var loaderVersion = root.TryGetProperty("loaderVersion", out var loaderValue) ? loaderValue.GetString() : null;

            return !string.IsNullOrWhiteSpace(gameVersion)
                   && !string.IsNullOrWhiteSpace(loaderText)
                   && !string.IsNullOrWhiteSpace(loaderVersion)
                   && Enum.TryParse<LoaderKind>(loaderText, ignoreCase: true, out var parsedLoader)
                   && string.Equals(
                       LoaderNaming.VersionId(parsedLoader, loaderVersion, gameVersion),
                       versionId,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // 读不出来的旧实例：当作"可能引用"，保留目录（宁可少删）。
            return true;
        }
    }
}
