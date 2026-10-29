using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// 官方 Java 运行时清单的测试夹具（形状按实测的官方 JSON 来）。
/// <para>
/// 索引是"平台 → 组件 → 可用版本数组"，每个版本里有 <c>manifest</c>（sha1 + size + url）与
/// <c>version</c>（name + released）；组件清单是 <c>files</c> 字典，条目分
/// <c>file</c> / <c>directory</c> / <c>link</c> 三种。夹具不裁剪这些细节 ——
/// 真实形状里少一个字段，解析代码就得跟着改，而"夹具比真实数据简单"正是这类改动最容易漏测的地方。
/// </para>
/// </summary>
internal static class JavaRuntimeFixture
{
    /// <summary>Windows x64 平台标识。</summary>
    public const string Platform = "windows-x64";

    /// <summary>一个组件在某平台上的一条可用版本。</summary>
    /// <param name="Component">组件名。</param>
    /// <param name="VersionName">版本名（主版本号取开头数字）。</param>
    /// <param name="ManifestJson">该组件的文件清单 JSON。</param>
    /// <param name="Released">官方发布时间。</param>
    public sealed record ComponentRelease(
        string Component,
        string VersionName,
        string ManifestJson,
        DateTimeOffset? Released = null)
    {
        /// <summary>清单内容的 SHA-1（索引里声明的那个值）。</summary>
        public string ManifestSha1 => Sha1Hex(ManifestJson);

        /// <summary>清单内容的字节数。</summary>
        public long ManifestSize => Encoding.UTF8.GetByteCount(ManifestJson);

        /// <summary>清单地址（内容寻址，与官方一致）。</summary>
        public string ManifestUrl =>
            "https://piston-meta.mojang.com/v1/packages/" + ManifestSha1 + "/manifest.json";
    }

    /// <summary>组件清单里的一条条目。</summary>
    /// <param name="Path">相对路径。</param>
    /// <param name="Type">条目类型。</param>
    /// <param name="Body">文件内容（<c>file</c> 时必填）。</param>
    /// <param name="Target">链接目标（<c>link</c> 时必填）。</param>
    public sealed record Entry(string Path, string Type, string? Body = null, string? Target = null);

    /// <summary>一个文件条目。</summary>
    /// <param name="path">相对路径。</param>
    /// <param name="body">文件内容。</param>
    /// <returns>条目。</returns>
    public static Entry File(string path, string body) => new(path, "file", body);

    /// <summary>一个目录条目。</summary>
    /// <param name="path">相对路径。</param>
    /// <returns>条目。</returns>
    public static Entry Directory(string path) => new(path, "directory");

    /// <summary>一个链接条目。</summary>
    /// <param name="path">相对路径。</param>
    /// <param name="target">目标相对路径。</param>
    /// <returns>条目。</returns>
    public static Entry Link(string path, string target) => new(path, "link", null, target);

    /// <summary>一条把清单由条目拼出来的可用版本。</summary>
    /// <param name="component">组件名。</param>
    /// <param name="versionName">版本名。</param>
    /// <param name="entries">清单条目。</param>
    /// <returns>可用版本。</returns>
    public static ComponentRelease Release(string component, string versionName, params Entry[] entries) =>
        new(component, versionName, ManifestJson(entries));

    /// <summary>一条用**手写清单**的可用版本（要造"内容与声明的 sha1 不符"时用）。</summary>
    /// <param name="component">组件名。</param>
    /// <param name="versionName">版本名。</param>
    /// <param name="manifestJson">清单 JSON。</param>
    /// <param name="released">发布时间。</param>
    /// <returns>可用版本。</returns>
    public static ComponentRelease ReleaseWithManifest(
        string component,
        string versionName,
        string manifestJson,
        DateTimeOffset? released = null) =>
        new(component, versionName, manifestJson, released);

    /// <summary>造一份组件清单 JSON。</summary>
    /// <param name="entries">条目。</param>
    /// <returns>JSON 文本。</returns>
    public static string ManifestJson(params Entry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var files = new StringBuilder();

        foreach (var entry in entries)
        {
            if (files.Length > 0)
            {
                files.Append(',');
            }

            files.Append('"').Append(entry.Path).Append("\":");

            switch (entry.Type)
            {
                case "file":
                    var body = entry.Body ?? string.Empty;
                    var sha1 = Sha1Hex(body);
                    var size = Encoding.UTF8.GetByteCount(body);

                    files.Append("{\"type\":\"file\",\"executable\":true,\"downloads\":{\"raw\":{\"sha1\":\"")
                        .Append(sha1)
                        .Append("\",\"size\":")
                        .Append(size)
                        .Append(",\"url\":\"")
                        .Append(ObjectUrl(sha1, entry.Path))
                        .Append("\"}}}");
                    break;

                case "directory":
                    files.Append("{\"type\":\"directory\"}");
                    break;

                case "link":
                    files.Append("{\"type\":\"link\",\"target\":\"").Append(entry.Target).Append("\"}");
                    break;

                default:
                    files.Append("{\"type\":\"").Append(entry.Type).Append("\",\"target\":\"x\"}");
                    break;
            }
        }

        return "{\"files\":{" + files + "}}";
    }

    /// <summary>造一份索引 JSON（只有 <paramref name="platform"/> 一个平台）。</summary>
    /// <param name="releases">各组件的一条可用版本。</param>
    /// <param name="platform">平台标识。</param>
    /// <returns>JSON 文本。</returns>
    public static string IndexJson(IEnumerable<ComponentRelease> releases, string platform = Platform)
    {
        ArgumentNullException.ThrowIfNull(releases);

        var components = new StringBuilder();

        foreach (var release in releases)
        {
            if (components.Length > 0)
            {
                components.Append(',');
            }

            components.Append('"').Append(release.Component).Append("\":[{\"availability\":{\"group\":1,\"progress\":100},\"manifest\":{\"sha1\":\"")
                .Append(release.ManifestSha1)
                .Append("\",\"size\":")
                .Append(release.ManifestSize)
                .Append(",\"url\":\"")
                .Append(release.ManifestUrl)
                .Append("\"},\"version\":{\"name\":\"")
                .Append(release.VersionName)
                .Append("\",\"released\":\"")
                .Append((release.Released ?? new DateTimeOffset(2025, 5, 19, 8, 30, 12, TimeSpan.Zero))
                    .ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\"}}]");
        }

        return "{\"" + platform + "\":{" + components + "}}";
    }

    /// <summary>一个运行时对象的下载地址（内容寻址，与官方一致）。</summary>
    /// <param name="sha1">内容 SHA-1。</param>
    /// <param name="path">相对路径（只取文件名）。</param>
    /// <returns>地址。</returns>
    public static string ObjectUrl(string sha1, string path) =>
        "https://piston-data.mojang.com/v1/objects/" + sha1 + "/" + path[(path.LastIndexOf('/') + 1)..];

    /// <summary>算 SHA-1（清单协议规定的完整性字段）。</summary>
    /// <param name="body">文本。</param>
    /// <returns>小写十六进制。</returns>
    public static string Sha1Hex(string body) => Sha1Hex(Encoding.UTF8.GetBytes(body));

    /// <summary>算 SHA-1。</summary>
    /// <param name="bytes">字节。</param>
    /// <returns>小写十六进制。</returns>
    public static string Sha1Hex(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA5350
    }
}

/// <summary>
/// 一套"官方运行时服务"的 HTTP 桩：索引 + 各组件清单 + 各文件对象。
/// <para>
/// 按 URL 片段匹配而不是按请求顺序 —— 下载是并发的，按顺序编排的桩在并发下是随机失败的桩。
/// </para>
/// </summary>
internal sealed class JavaRuntimeWorld
{
    private readonly Dictionary<string, string> _bodiesByPath = new(StringComparer.Ordinal);
    private readonly List<JavaRuntimeFixture.ComponentRelease> _releases = [];

    /// <summary>构造世界。</summary>
    /// <param name="platform">平台标识。</param>
    public JavaRuntimeWorld(string platform = JavaRuntimeFixture.Platform)
    {
        Platform = platform;
        Factory = new FakeHttpClientFactory(Handler);
    }

    /// <summary>平台标识。</summary>
    public string Platform { get; }

    /// <summary>HTTP 桩。</summary>
    public StubHandler Handler { get; } = new();

    /// <summary>客户端工厂。</summary>
    public FakeHttpClientFactory Factory { get; }

    /// <summary>登记一个组件（它的清单与全部文件都挂到桩上）。</summary>
    /// <param name="component">组件名。</param>
    /// <param name="versionName">版本名。</param>
    /// <param name="entries">清单条目。</param>
    /// <param name="corruptPaths">
    /// 这些路径的响应体**故意与清单里的 sha1 不符**（模拟被篡改 / 半截的响应）。
    /// 响应地址仍然按声明的 sha1 拼，所以能精确验证"校验拦住了内容不对的那一份"。
    /// </param>
    /// <param name="failingPaths">这些路径的对象地址返回 500（模拟网络在某个文件上反复失败）。</param>
    /// <returns>登记的这条版本（断言里要用它的 URL）。</returns>
    public JavaRuntimeFixture.ComponentRelease Publish(
        string component,
        string versionName,
        JavaRuntimeFixture.Entry[] entries,
        IReadOnlyCollection<string>? corruptPaths = null,
        IReadOnlyCollection<string>? failingPaths = null)
    {
        var release = JavaRuntimeFixture.Release(component, versionName, entries);

        _releases.Add(release);

        Handler.RespondTo(release.ManifestUrl, HttpStatusCode.OK, release.ManifestJson);

        foreach (var entry in entries)
        {
            if (entry.Type != "file")
            {
                continue;
            }

            var body = entry.Body ?? string.Empty;
            _bodiesByPath[entry.Path] = body;

            var url = JavaRuntimeFixture.ObjectUrl(JavaRuntimeFixture.Sha1Hex(body), entry.Path);

            if (failingPaths is not null && failingPaths.Contains(entry.Path))
            {
                Handler.RespondBytesTo(url, null, HttpStatusCode.InternalServerError);
                continue;
            }

            var payload = corruptPaths is not null && corruptPaths.Contains(entry.Path)
                ? "corrupted-bytes"
                : body;

            Handler.RespondBytesTo(url, Encoding.UTF8.GetBytes(payload));
        }

        return release;
    }

    /// <summary>把索引挂到桩上（必须在 <see cref="Publish"/> 之后调用）。</summary>
    /// <returns>索引 JSON（断言里可能要用）。</returns>
    public string PublishIndex()
    {
        var json = JavaRuntimeFixture.IndexJson(_releases, Platform);
        Handler.RespondTo("/v1/products/java-runtime/", HttpStatusCode.OK, json);
        return json;
    }

    /// <summary>某个文件在某条清单里的内容（用来断言落盘结果）。</summary>
    /// <param name="path">相对路径。</param>
    /// <returns>内容。</returns>
    public string Body(string path) => _bodiesByPath[path];
}
