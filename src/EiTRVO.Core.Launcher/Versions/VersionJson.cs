using System.Text.Json.Serialization;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 版本 JSON（<c>versions/&lt;id&gt;/&lt;id&gt;.json</c>，也来自 <c>piston-meta</c>）的形状。
/// <para>
/// 字段全部可空：这是**远端数据**，而且不同年代的版本 JSON 形状差别很大
/// （1.12 及以前用 <see cref="MinecraftArguments"/> 一个字符串，1.13 起用 <see cref="Arguments"/> 结构化列表；
/// 加载器装出来的 JSON 只写增量、其余靠 <c>inheritsFrom</c> 继承）。
/// 少一个字段就让整份版本读不出来，是这个文件最容易犯的错。
/// </para>
/// </summary>
public sealed record VersionJson
{
    /// <summary>版本 id。</summary>
    public string? Id { get; init; }

    /// <summary>类型：<c>release</c> / <c>snapshot</c> / 加载器自定义。</summary>
    public string? Type { get; init; }

    /// <summary>继承的父版本 id（加载器版本指向原版）。原版为 <see langword="null"/>。</summary>
    public string? InheritsFrom { get; init; }

    /// <summary>主类。</summary>
    public string? MainClass { get; init; }

    /// <summary>资源索引 id（如 <c>5</c>）。</summary>
    public string? Assets { get; init; }

    /// <summary>资源索引（含下载地址与 SHA-1）。</summary>
    public AssetIndexInfo? AssetIndex { get; init; }

    /// <summary>库列表。</summary>
    public IReadOnlyList<Library>? Libraries { get; init; }

    /// <summary>结构化参数（1.13+）。</summary>
    public VersionArguments? Arguments { get; init; }

    /// <summary>旧版参数（1.12 及以前）：一个按空格分隔的整串。</summary>
    public string? MinecraftArguments { get; init; }

    /// <summary>客户端 JAR 等下载信息。</summary>
    public VersionDownloads? Downloads { get; init; }

    /// <summary>日志配置。</summary>
    public VersionLogging? Logging { get; init; }

    /// <summary>
    /// 该版本声明的 Java 主版本（如 <c>17</c>）。
    /// <para>
    /// 这个字段比"用版本号正则去猜"可靠得多 —— 它是 Mojang 自己写的。
    /// Neo 的版本模型里**没有**这个字段（迁移评估 C-9 因此才需要一套推断规则）；
    /// 这里保留它，推断只作为缺失时的兜底。
    /// </para>
    /// </summary>
    public JavaVersionRequirement? JavaVersion { get; init; }

    /// <summary>该版本 JSON 自身的地址（缓存里用来判断"这条记录指向哪里"）。</summary>
    [JsonIgnore]
    public string? SourceUrl { get; init; }
}

/// <summary>版本声明的 Java 要求。</summary>
public sealed record JavaVersionRequirement
{
    /// <summary>组件名（通常为 <c>java-runtime-gamma</c> 之类）。</summary>
    public string? Component { get; init; }

    /// <summary>Java 主版本号。</summary>
    public int? MajorVersion { get; init; }
}

/// <summary>资源索引信息。</summary>
public sealed record AssetIndexInfo
{
    /// <summary>索引 id。</summary>
    public string? Id { get; init; }

    /// <summary>下载地址。</summary>
    public string? Url { get; init; }

    /// <summary>SHA-1。</summary>
    public string? Sha1 { get; init; }

    /// <summary>字节数。</summary>
    public long Size { get; init; }

    /// <summary>已下载文件数（仅清单里有）。</summary>
    public long TotalSize { get; init; }
}

/// <summary>版本级下载信息。</summary>
public sealed record VersionDownloads
{
    /// <summary>客户端 JAR。</summary>
    public DownloadInfo? Client { get; init; }

    /// <summary>服务端 JAR（本产品不用，但保留以免反序列化丢字段时误判）。</summary>
    public DownloadInfo? Server { get; init; }
}

/// <summary>一个可下载文件。</summary>
public sealed record DownloadInfo
{
    /// <summary>标识（日志配置这类文件会带，如 <c>client-1.12.xml</c>）。</summary>
    public string? Id { get; init; }

    /// <summary>SHA-1。</summary>
    public string? Sha1 { get; init; }

    /// <summary>字节数。</summary>
    public long Size { get; init; }

    /// <summary>下载地址。</summary>
    public string? Url { get; init; }

    /// <summary>相对路径（库文件用，如 <c>org/apache/.../x.jar</c>）。</summary>
    public string? Path { get; init; }
}

/// <summary>一个库。</summary>
public sealed record Library
{
    /// <summary>Maven 坐标，如 <c>com.mojang:authlib:1.5.21</c>。</summary>
    public string? Name { get; init; }

    /// <summary>下载信息。</summary>
    public LibraryDownloads? Downloads { get; init; }

    /// <summary>平台 → 分类器（如 <c>windows</c> → <c>natives-windows</c>）。</summary>
    public IReadOnlyDictionary<string, string>? Natives { get; init; }

    /// <summary>适用规则；为空表示无条件下载。</summary>
    public IReadOnlyList<VersionRule>? Rules { get; init; }

    /// <summary>解压规则（natives 用：哪些条目不解压出来）。</summary>
    public LibraryExtract? Extract { get; init; }

    /// <summary>
    /// 旧版 Forge（≤1.12.2）把 Maven 仓库基址写在库对象自身，而不是 <c>downloads/artifact</c> 里。
    /// <see cref="Downloads"/> 为空时用它拼地址。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// 库对象自身的 SHA-1。Fabric 的 profile 把校验值直接写在库上（与 <see cref="Url"/> 并列），
    /// 而不是 <c>downloads/artifact</c> 里 —— 有它就校验，没有（如 Quilt 的 profile）就如实按"不可校验"处理。
    /// </summary>
    public string? Sha1 { get; init; }

    /// <summary>文件大小；未知为 <c>0</c>（磁盘预检会因此低估这一类库）。</summary>
    public long Size { get; init; }
}

/// <summary>库的下载信息。</summary>
public sealed record LibraryDownloads
{
    /// <summary>主构件。</summary>
    public DownloadInfo? Artifact { get; init; }

    /// <summary>分类器（natives 等）。</summary>
    public IReadOnlyDictionary<string, DownloadInfo>? Classifiers { get; init; }
}

/// <summary>库的解压规则。</summary>
public sealed record LibraryExtract
{
    /// <summary>不解压的条目前缀（如 <c>META-INF/</c>）。</summary>
    public IReadOnlyList<string>? Exclude { get; init; }
}

/// <summary>版本日志配置。</summary>
public sealed record VersionLogging
{
    /// <summary>客户端日志配置。</summary>
    public VersionLoggingClient? Client { get; init; }
}

/// <summary>版本日志配置的一项。</summary>
public sealed record VersionLoggingClient
{
    /// <summary>要加进 JVM 参数里的开关模板（含 <c>${path}</c> 占位）。</summary>
    public string? Argument { get; init; }

    /// <summary>配置文件本身。</summary>
    public DownloadInfo? File { get; init; }

    /// <summary>日志类型（如 <c>log4j2-xml</c>）。</summary>
    public string? Type { get; init; }
}
