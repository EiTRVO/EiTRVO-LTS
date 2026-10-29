using System.Text.Json;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// 官方 Java 运行时清单解析失败：响应不是合法 JSON、缺少必需字段、或出现本启动器不认识的条目类型。
/// <para>
/// 单独一个异常类型（与 <c>VersionDataException</c> 并列）：调用方要据它给出
/// "官方运行时清单读不出来，请稍后重试"这类可读结论，而不是漏出一个 <see cref="JsonException"/>。
/// </para>
/// </summary>
public sealed class JavaRuntimeDataException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="message">消息。</param>
    /// <param name="innerException">底层异常。</param>
    public JavaRuntimeDataException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>官方运行时清单里的平台标识（<c>windows-x64</c> 等；与官方启动器一致）。</summary>
/// <remarks>
/// 与 <see cref="Versions.GamePlatform"/> 不是一回事：那个是"求值版本 JSON 里的规则"用的
/// （<c>windows</c> / <c>x86_64</c>），这个是官方运行时清单自己的键名。两套命名都属于远端协议，
/// 硬凑成一套只会让两边都读不懂。
/// </remarks>
public static class JavaRuntimePlatform
{
    /// <summary>Windows x64（本产品的目标平台）。</summary>
    public const string WindowsX64 = "windows-x64";

    /// <summary>Windows x86。</summary>
    public const string WindowsX86 = "windows-x86";

    /// <summary>Windows arm64。</summary>
    public const string WindowsArm64 = "windows-arm64";

    /// <summary>当前进程对应的平台标识。</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 产品只支持 Windows（AGENTS §0）。非 Windows 上给出清单里真实存在的键，
            // 让"取不到 windows-x64"这件事表现为一条明确的失败，而不是一个拼错的键。
            return OperatingSystem.IsMacOS() ? "mac-os" : "linux";
        }

        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => WindowsArm64,
            System.Runtime.InteropServices.Architecture.X86 => WindowsX86,
            _ => WindowsX64,
        };
    }
}

/// <summary>官方运行时清单里一条可用版本（索引里的一组 <c>manifest</c> + <c>version</c>）。</summary>
/// <param name="Platform">平台标识（<c>windows-x64</c>）。</param>
/// <param name="Component">组件名（<c>java-runtime-gamma</c>），也是落地目录名。</param>
/// <param name="VersionName">该组件的版本名（<c>17.0.15</c> / <c>8u51-cacert462b08</c>）。</param>
/// <param name="Released">官方发布时间（相同主版本的组件之间用来挑更新的那个）。</param>
/// <param name="ManifestSha1">该组件在該平台上的文件清单的 SHA-1（下载后必须核对）。</param>
/// <param name="ManifestSize">文件清单的字节数（核对 SHA-1 之前先看它，能省一次白读）。</param>
/// <param name="ManifestUrl">文件清单地址。</param>
public sealed record JavaRuntimeCandidate(
    string Platform,
    string Component,
    string VersionName,
    DateTimeOffset? Released,
    string ManifestSha1,
    long ManifestSize,
    string ManifestUrl)
{
    /// <summary>版本名开头的主版本号（<c>17.0.15</c> → 17、<c>8u51-cacert462b08</c> → 8）。</summary>
    /// <remarks>
    /// 官方清单里没有"这个组件是 Java 几"的字段，只有版本名。开头连续数字就是主版本号，
    /// 这与 <c>java -version</c> 的输出无关（那是 <see cref="JavaVersionParser"/> 的事）。
    /// 读不出来时为 0，调用方据此把它当成"版本未知"，而不是当成 Java 0。
    /// </remarks>
    public int MajorVersion
    {
        get
        {
            var major = 0;

            foreach (var character in VersionName)
            {
                if (!char.IsAsciiDigit(character))
                {
                    break;
                }

                major = major * 10 + (character - '0');
                if (major > 999999)
                {
                    return 0;
                }
            }

            return major;
        }
    }

    /// <summary>是否为快照组件（<c>java-runtime-gamma-snapshot</c>）。</summary>
    public bool IsSnapshot => Component.EndsWith("-snapshot", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 官方运行时**索引**（<c>/v1/products/java-runtime/&lt;sha&gt;/all.json</c>）的解析结果。
/// <para>
/// 远端形状是"平台 → 组件 → 可用版本数组"，三层嵌套的字典；这里在解析时**摊平**成一维列表。
/// 理由是索引的用法只有两种（按平台挑一个组件、按平台列出全部），而三层字典会让每个调用点
/// 都得写三重索引与空值判断 —— 选择规则反而不容易测。
/// </para>
/// </summary>
public sealed record JavaRuntimeIndex(IReadOnlyList<JavaRuntimeCandidate> Candidates)
{
    /// <summary>不是 Java 运行时的组件：官方启动器的 Windows 原生启动壳（清单里只有一个 <c>MinecraftJava.exe</c>）。</summary>
    public const string NativeLauncherComponent = "minecraft-java-exe";

    /// <summary>解析索引文本。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>索引。</returns>
    /// <exception cref="JavaRuntimeDataException">文本不是合法 JSON、结构不认识、或一条可用版本都没有。</exception>
    public static JavaRuntimeIndex Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        Dictionary<string, Dictionary<string, List<RawRelease>>>? raw;

        try
        {
            raw = LauncherJson.Deserialize<Dictionary<string, Dictionary<string, List<RawRelease>>>>(
                json,
                LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new JavaRuntimeDataException("官方 Java 运行时索引不是合法 JSON。", ex);
        }

        if (raw is null || raw.Count == 0)
        {
            throw new JavaRuntimeDataException("官方 Java 运行时索引为空。");
        }

        List<JavaRuntimeCandidate> candidates = [];

        foreach (var (platform, components) in raw)
        {
            if (components is null)
            {
                continue;
            }

            foreach (var (component, releases) in components)
            {
                if (releases is null || component.Length == 0)
                {
                    continue;
                }

                foreach (var release in releases)
                {
                    if (release?.Manifest?.Url is not { Length: > 0 } url)
                    {
                        // 没有清单地址的条目**没有**任何可用的东西可以下载：跳过它，
                        // 而不是让它进候选列表再在下载时失败（那时失败原因会指向下载，而不是这份索引）。
                        continue;
                    }

                    candidates.Add(new JavaRuntimeCandidate(
                        Platform: platform,
                        Component: component,
                        VersionName: release.Version?.Name ?? string.Empty,
                        Released: release.Version?.Released,
                        ManifestSha1: release.Manifest.Sha1 ?? string.Empty,
                        ManifestSize: release.Manifest.Size,
                        ManifestUrl: url));
                }
            }
        }

        if (candidates.Count == 0)
        {
            throw new JavaRuntimeDataException("官方 Java 运行时索引里没有任何可用组件。");
        }

        return new JavaRuntimeIndex(candidates);
    }

    /// <summary>某个平台上的全部候选（含不认识的组件名，筛选是 <see cref="Select"/> 的事）。</summary>
    /// <param name="platform">平台标识。</param>
    /// <returns>候选。</returns>
    public IReadOnlyList<JavaRuntimeCandidate> ForPlatform(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        return
        [
            .. Candidates.Where(candidate =>
                string.Equals(candidate.Platform, platform, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>
    /// 挑一个候选：**版本 JSON 声明的组件优先**，其次主版本完全相同的，最后"不低于所需版本里最小的那个"。
    /// </summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="requiredMajor">所需 Java 主版本；<see langword="null"/> 表示没有版本上下文（取最新一代）。</param>
    /// <param name="componentHint">版本 JSON 里声明的组件名（可为 <see langword="null"/>）。</param>
    /// <returns>候选；这个平台上没有可用组件时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 四步各有理由：
    /// <list type="number">
    /// <item><b>声明的组件优先</b>：那是 Mojang 自己写的答案（与 C-9 对 <c>javaVersion.majorVersion</c> 的处理同一条纪律）。
    /// 但**要求它不低于所需主版本** —— 一份过期的声明（例如游戏已升到 Java 21、JSON 还写着 gamma 17）
    /// 若照抄，用户会下载一个仍然跑不起来的运行时，而界面上的"缺少 Java"照旧。</item>
    /// <item><b>主版本完全相同</b>：<c>jre-legacy</c> 是 Java 8，正好满足"≤1.12.2 封在 8"这条硬上限。</item>
    /// <item><b>不低于所需版本里最小的那个</b>：没有完全对应的一代时（例如所需 22，而清单只有 21 与 25），
    /// 取最小的更高版本，而不是一步跳到最新 —— 后者会在 1.12.2 那种"上限封在 8"的版本上装错东西。</item>
    /// <item><b>没有版本上下文时取最新一代</b>：p10 的「获取 Java 25」就是这个语义（按钮上写的就是最新一代）。</item>
    /// </list>
    /// </remarks>
    public JavaRuntimeCandidate? Select(string platform, int? requiredMajor, string? componentHint)
    {
        List<JavaRuntimeCandidate> usable =
        [
            .. ForPlatform(platform).Where(static candidate =>
                !string.Equals(
                    candidate.Component,
                    NativeLauncherComponent,
                    StringComparison.OrdinalIgnoreCase)),
        ];

        if (usable.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(componentHint))
        {
            var hinted = Newest(usable.Where(candidate =>
                string.Equals(candidate.Component, componentHint, StringComparison.OrdinalIgnoreCase)));

            if (hinted is not null && (requiredMajor is not { } wanted || hinted.MajorVersion >= wanted))
            {
                return hinted;
            }
        }

        if (requiredMajor is not { } major)
        {
            return Newest(usable);
        }

        var exact = Newest(usable.Where(candidate => candidate.MajorVersion == major));
        if (exact is not null)
        {
            return exact;
        }

        List<JavaRuntimeCandidate> newer = [.. usable.Where(candidate => candidate.MajorVersion > major)];
        if (newer.Count == 0)
        {
            return null;
        }

        var smallest = newer.Min(static candidate => candidate.MajorVersion);
        return Newest(newer.Where(candidate => candidate.MajorVersion == smallest));
    }

    /// <summary>
    /// 一组候选里"最新"的那个：主版本高的优先，同主版本时正式版优先于快照，然后看官方发布时间，最后比版本名。
    /// </summary>
    /// <remarks>
    /// 快照排在正式版之后是刻意的：<c>java-runtime-gamma</c> 与 <c>java-runtime-gamma-snapshot</c>
    /// 是同一代的两个条目，把快照挑出来当默认运行时没有道理。
    /// </remarks>
    private static JavaRuntimeCandidate? Newest(IEnumerable<JavaRuntimeCandidate> candidates) =>
        candidates
            .OrderByDescending(static candidate => candidate.MajorVersion)
            .ThenBy(static candidate => candidate.IsSnapshot)
            .ThenByDescending(static candidate => candidate.Released ?? DateTimeOffset.MinValue)
            .ThenByDescending(static candidate => candidate.VersionName, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>远端索引里的一条可用版本（原样映射，不做判断）。</summary>
    private sealed record RawRelease
    {
        public RawManifestPointer? Manifest { get; init; }

        public RawVersionInfo? Version { get; init; }
    }

    /// <summary>索引里的清单指针。</summary>
    private sealed record RawManifestPointer
    {
        public string? Sha1 { get; init; }

        public long Size { get; init; }

        public string? Url { get; init; }
    }

    /// <summary>索引里的版本信息。</summary>
    private sealed record RawVersionInfo
    {
        public string? Name { get; init; }

        public DateTimeOffset? Released { get; init; }
    }
}

/// <summary>运行时清单里一条条目的类型。</summary>
public enum JavaRuntimeFileKind
{
    /// <summary>文件（要下载，落盘前校验 SHA-1）。</summary>
    File,

    /// <summary>目录（要建出来；可能是空目录）。</summary>
    Directory,

    /// <summary>符号链接（Windows 上退化成复制一份目标文件）。</summary>
    Link,

    /// <summary>本启动器不认识的类型（计划阶段就拒绝，不静默跳过）。</summary>
    Unknown,
}

/// <summary>运行时清单里的一条条目（已摊平成"一条一个相对路径"）。</summary>
/// <param name="RelativePath">相对运行时根目录的路径（<c>bin/javaw.exe</c>）。</param>
/// <param name="Kind">条目类型。</param>
/// <param name="Target">链接的目标（相对路径；非链接为 <see langword="null"/>）。</param>
/// <param name="Sha1">文件的 SHA-1；非文件或清单没给时为 <see langword="null"/>。</param>
/// <param name="Size">文件字节数；未知为 0。</param>
/// <param name="Url">下载地址；非文件为 <see langword="null"/>。</param>
/// <param name="Executable">清单是否标记为可执行（Windows 上不需要 chmod，保留它只为如实记账）。</param>
public sealed record JavaRuntimeEntry(
    string RelativePath,
    JavaRuntimeFileKind Kind,
    string? Target,
    string? Sha1,
    long Size,
    string? Url,
    bool Executable);

/// <summary>
/// 某个组件在某平台上的**文件清单**（索引里那个 <c>manifest.url</c> 指向的 JSON）。
/// <para>
/// 只做"解析成一条条条目"这一件事：路径安全（远端数据不能带 <c>..</c>）由计划阶段把关，
/// 下载与落盘由安装器做。
/// </para>
/// </summary>
/// <param name="Entries">条目（按清单里的原始顺序）。</param>
public sealed record JavaRuntimeManifest(IReadOnlyList<JavaRuntimeEntry> Entries)
{
    /// <summary>解析清单文本。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>清单。</returns>
    /// <exception cref="JavaRuntimeDataException">文本不是合法 JSON、没有 <c>files</c>、或一条条目都没有。</exception>
    public static JavaRuntimeManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        RawManifest? raw;

        try
        {
            raw = LauncherJson.Deserialize<RawManifest>(json, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new JavaRuntimeDataException("官方 Java 运行时文件清单不是合法 JSON。", ex);
        }

        if (raw?.Files is null || raw.Files.Count == 0)
        {
            throw new JavaRuntimeDataException("官方 Java 运行时文件清单里没有 files。");
        }

        List<JavaRuntimeEntry> entries = [];

        foreach (var (path, file) in raw.Files)
        {
            if (string.IsNullOrWhiteSpace(path) || file is null)
            {
                continue;
            }

            var kind = KindOf(file.Type);
            var download = file.Downloads?.Raw;

            entries.Add(new JavaRuntimeEntry(
                RelativePath: path,
                Kind: kind,
                Target: file.Target,
                Sha1: download?.Sha1,
                Size: download?.Size ?? 0,
                Url: download?.Url,
                Executable: file.Executable));
        }

        if (entries.Count == 0)
        {
            throw new JavaRuntimeDataException("官方 Java 运行时文件清单里没有可用条目。");
        }

        return new JavaRuntimeManifest(entries);
    }

    /// <summary>把清单里的 <c>type</c> 映射成枚举；认不出的类型留给计划阶段拒绝。</summary>
    /// <param name="type">清单里的 <c>type</c> 字段。</param>
    /// <returns>条目类型。</returns>
    private static JavaRuntimeFileKind KindOf(string? type) => type switch
    {
        "file" => JavaRuntimeFileKind.File,
        "directory" => JavaRuntimeFileKind.Directory,
        "link" => JavaRuntimeFileKind.Link,
        _ => JavaRuntimeFileKind.Unknown,
    };

    /// <summary>清单的原始形状（<c>files</c> 是"相对路径 → 条目"的字典）。</summary>
    private sealed record RawManifest
    {
        public Dictionary<string, RawFile>? Files { get; init; }
    }

    /// <summary>清单里的一条条目。</summary>
    private sealed record RawFile
    {
        public string? Type { get; init; }

        public string? Target { get; init; }

        public bool Executable { get; init; }

        public RawDownloads? Downloads { get; init; }
    }

    /// <summary>条目声明的下载形式（<c>raw</c> 是未压缩的那一份，<c>lzma</c> 我们不处理）。</summary>
    private sealed record RawDownloads
    {
        public RawDownload? Raw { get; init; }
    }

    /// <summary>一个可下载对象。</summary>
    private sealed record RawDownload
    {
        public string? Sha1 { get; init; }

        public long Size { get; init; }

        public string? Url { get; init; }
    }
}

/// <summary>把清单里的相对路径按"单段路径名"逐个校验的工具。</summary>
/// <remarks>
/// 清单来自远端，其中的键会**直接拼进磁盘路径**。除了 <c>PathSafety</c> 的越界检查之外，
/// 这里额外拒绝含驱动器号（<c>C:evil</c>）、反斜杠（清单里一律用 <c>/</c>）与 <c>.</c> / <c>..</c> 的段 ——
/// 越界检查对付不了 <c>C:evil</c> 这种"基准目录恰好是当前目录"的写法，而它同样会让文件落到别处。
/// </remarks>
internal static class JavaRuntimePathSafety
{
    /// <summary>相对路径是否安全（非空、全为普通段、不含驱动器号或反斜杠）。</summary>
    /// <param name="relativePath">清单里的相对路径。</param>
    /// <returns>安全返回 <see langword="true"/>。</returns>
    public static bool IsSafe(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (relativePath.Contains(':') || relativePath.Contains('\\') || relativePath.Contains('\0'))
        {
            return false;
        }

        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || !PathSafety.IsSafePathSegment(segment, 96))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>主版本号（或组件名）是否可用作单段目录名。</summary>
    /// <param name="name">目录名（组件名）。</param>
    /// <returns>可用返回 <see langword="true"/>。</returns>
    public static bool IsSafeComponent(string? name) =>
        !string.IsNullOrWhiteSpace(name) && PathSafety.IsSafePathSegment(name, 96);

    /// <summary>把相对路径切成段（用于 <c>PathSafety.CombineWithin</c>）。</summary>
    /// <param name="relativePath">相对路径。</param>
    /// <returns>分段。</returns>
    public static string[] Segments(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
