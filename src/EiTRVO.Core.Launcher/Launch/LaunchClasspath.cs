using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 版本 JSON 的库列表 → 实际类路径（C-4）。
/// <para>
/// 这件事看着只是"拼一串文件路径"，但它有四个必须做对的地方，每一个做错的后果都是
/// "游戏起不来，而且看不出为什么"：
/// </para>
/// <list type="number">
/// <item><b>规则筛选</b>：macOS / Linux / 32 位专属的库不能进 Windows x64 的类路径。</item>
/// <item><b>去重保最高版本</b>：同一个构件出现两次时 JVM 会用先出现的那个 ——
/// asm / lwjgl 这类库在真实整合包里几乎一定重复，保错版本就是 NoSuchMethodError。</item>
/// <item><b>分类器不能被当成版本</b>：<c>lwjgl-3.3.2-natives-windows.jar</c> 与
/// <c>lwjgl-3.3.2.jar</c> 是两个不同的构件，按"去掉版本号"去重会把 natives 丢掉。</item>
/// <item><b>缺文件要如实上报</b>：Neo 遇到不存在的库只是 <c>continue</c>，于是"缺了 20 个库"
/// 与"一切都好"在界面上长得一模一样。现在缺什么就报什么，交给 M4-e 在启动前拦住。</item>
/// </list>
/// </summary>
public static class LaunchClasspath
{
    /// <summary>类路径解析结果。</summary>
    /// <param name="Entries">类路径条目（绝对路径，顺序即优先级）。</param>
    /// <param name="Problems">解析过程中发现的问题（缺文件、坐标不合法、路径越界）。</param>
    public sealed record ClasspathResult(IReadOnlyList<string> Entries, IReadOnlyList<LaunchProblem> Problems);

    /// <summary>解析版本 JSON 里的库列表。</summary>
    /// <param name="version">已合并继承链的版本。</param>
    /// <param name="gameDirectory">游戏根目录（<c>libraries</c> 在它下面）。</param>
    /// <param name="platform">平台（库规则求值用）。</param>
    /// <param name="fileExists">文件存在性判断（注入进来，单测不必真的建文件）。</param>
    /// <param name="modulePathArtifacts">已经在 module-path 上的构件名（<c>-p</c> 的值里认出来的）。</param>
    /// <returns>类路径与问题列表。</returns>
    public static ClasspathResult Build(
        ResolvedVersion version,
        string gameDirectory,
        GamePlatform platform,
        Func<string, bool> fileExists,
        IReadOnlySet<string> modulePathArtifacts)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(modulePathArtifacts);

        List<string> entries = [];
        List<LaunchProblem> problems = [];

        // 去重表：键 → (条目下标, 版本)。替换时**保持原位置**，因为位置就是 classpath 优先级。
        Dictionary<string, (int Index, string? Version)> chosen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var library in version.Merged.Libraries ?? [])
        {
            if (!LibraryFilter.Applies(library, platform))
            {
                continue;
            }

            var artifact = library.Downloads?.Artifact;
            var relative = artifact?.Path;
            var declaredArtifact = !string.IsNullOrWhiteSpace(relative);

            if (!declaredArtifact)
            {
                if (!MavenPath.TryFromName(library.Name, out var fromName))
                {
                    problems.Add(new LaunchProblem(
                        LaunchProblemKind.UnsafeLibraryPath,
                        library.Name ?? "(无坐标)",
                        "库既没有 downloads.artifact.path，也不是合法的 Maven 坐标，无法定位它的文件。"));

                    continue;
                }

                relative = fromName;
            }

            string fullPath;
            try
            {
                fullPath = PathSafety.CombineWithin(gameDirectory, "libraries", relative!);
            }
            catch (InvalidDataException exception)
            {
                problems.Add(new LaunchProblem(
                    LaunchProblemKind.UnsafeLibraryPath,
                    library.Name ?? relative!,
                    exception.Message));

                continue;
            }

            if (!fileExists(fullPath))
            {
                // 纯 natives 库（只有分类器、没有主构件）本来就没有主 JAR。
                // 1.12.2 及以前的 LWJGL 2 就是这种形状：把它当"缺失"会让每个老版本都报一堆假故障。
                var nativesOnly = !declaredArtifact
                    && (library.Natives is { Count: > 0 } || library.Downloads?.Classifiers is { Count: > 0 });

                if (!nativesOnly)
                {
                    problems.Add(new LaunchProblem(
                        LaunchProblemKind.MissingFile,
                        library.Name ?? fullPath,
                        $"库文件不存在：{fullPath}"));
                }

                continue;
            }

            var identity = MavenArtifactIdentity.FromRelativePath(relative!);

            // 已经在 module-path 上的构件不能再进 classpath：同一份类被两个加载器各加载一次会冲突。
            if (modulePathArtifacts.Contains(identity.Artifact))
            {
                continue;
            }

            if (chosen.TryGetValue(identity.Key, out var existing))
            {
                if (MavenVersion.Compare(identity.Version, existing.Version) > 0)
                {
                    entries[existing.Index] = fullPath;
                    chosen[identity.Key] = (existing.Index, identity.Version);
                }

                continue;
            }

            chosen[identity.Key] = (entries.Count, identity.Version);
            entries.Add(fullPath);
        }

        return new ClasspathResult(entries, problems);
    }
}
