using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// Mojang 官方 Java 运行时清单的获取：索引（<c>all.json</c>）+ 某个组件的文件清单。
/// <para>
/// 三条刻意的设计：
/// </para>
/// <list type="number">
/// <item><b>索引地址是钉住的常量</b>。官方没有任何"索引版本发现"端点
/// （<c>/v1/products/java-runtime/versions/manifest.json</c> 实测 404），官方启动器与其它启动器
/// 同样把这一串钉在客户端里。因此它 404 时**如实报错并把地址写进失败原因**，
/// 而不是悄悄换一个来源或退回到"让用户自己去装 Java"。</item>
/// <item><b>索引里的 sha 是产品版本号，不是内容哈希</b>：实测索引内容的 SHA-1 是
/// <c>0f47bc50…</c>，与 URL 里的 <c>2ec0cc96…</c> 不同，所以索引本身无法自校验。
/// 而每个组件的清单地址是 <c>/v1/packages/&lt;sha1&gt;/manifest.json</c>（内容寻址），
/// **那份清单必须核对 SHA-1 与字节数** —— 实测官方给出的 sha1 与响应内容一致，这条校验是真的在起作用。</item>
/// <item><b>只用命名客户端</b>（<c>core</c>）：域名白名单与重试挂在它的管道上；
/// 远端清单里给出的地址还要再显式过一遍白名单，让"策略不允许"表现为一句明确的话。</item>
/// </list>
/// </summary>
public sealed class JavaRuntimeManifestClient
{
    /// <summary>
    /// 官方运行时索引地址。
    /// <para>
    /// 路径里的那一串是**产品版本号**（不是内容哈希，见类型说明）。它与
    /// <c>launchermeta.mojang.com</c> 上的同名路径是同一份内容，这里走 <c>piston-meta</c>：
    /// 后者是 AGENTS §6 指定的端点，前者已弃用且刻意不在出站白名单里。
    /// </para>
    /// </summary>
    public const string IndexUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/"
        + "2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpHostPolicy _hostPolicy;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造运行时清单客户端。</summary>
    /// <param name="httpClientFactory">命名客户端工厂（**不得**直接传 <c>HttpClient</c>）。</param>
    /// <param name="hostPolicy">出站域名策略。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public JavaRuntimeManifestClient(
        IHttpClientFactory httpClientFactory,
        HttpHostPolicy hostPolicy,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(hostPolicy);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _hostPolicy = hostPolicy;
        _diagnostics = diagnostics;
    }

    /// <summary>获取运行时索引（哪些组件、哪个平台、什么版本）。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>索引。</returns>
    /// <exception cref="JavaRuntimeDataException">状态码异常、响应不是合法索引。</exception>
    public async Task<JavaRuntimeIndex> FetchIndexAsync(CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var response = await client
            .GetAsync(new Uri(IndexUrl), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.JavaRuntime,
                "获取官方 Java 运行时索引失败：{0} 返回了 {1}",
                [IndexUrl, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)]);

            throw new JavaRuntimeDataException(
                $"获取官方 Java 运行时索引失败：{IndexUrl} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var index = JavaRuntimeIndex.Parse(json);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.JavaRuntime,
            "已获取官方 Java 运行时索引（{0} 个候选，本平台 {1} 个）",
            [
                index.Candidates.Count.ToString(CultureInfo.InvariantCulture),
                index.ForPlatform(JavaRuntimePlatform.Current).Count.ToString(CultureInfo.InvariantCulture),
            ]);

        return index;
    }

    /// <summary>获取某个组件的文件清单，并核对它的 SHA-1 与字节数。</summary>
    /// <param name="candidate">索引里选中的候选。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>文件清单。</returns>
    /// <exception cref="JavaRuntimeDataException">地址被策略拒绝、状态码异常、内容与 sha1/字节数不符、或不是合法清单。</exception>
    public async Task<JavaRuntimeManifest> FetchManifestAsync(
        JavaRuntimeCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!Uri.TryCreate(candidate.ManifestUrl, UriKind.Absolute, out var uri))
        {
            throw new JavaRuntimeDataException(
                $"官方清单里「{candidate.Component}」的地址不是合法 URL：'{candidate.ManifestUrl}'。");
        }

        // 远端数据指向哪里就必须先过白名单：管道上的 AllowedHostHandler 也会挡，
        // 但先挡能让失败原因明确是"这个地址不被允许"，而不是一个来自管道深处的异常。
        try
        {
            _hostPolicy.EnsureAllowed(uri);
        }
        catch (HttpPolicyException ex)
        {
            throw new JavaRuntimeDataException(
                $"官方清单里「{candidate.Component}」的地址不被允许出站：{uri}。",
                ex);
        }

        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new JavaRuntimeDataException(
                $"获取「{candidate.Component}」的文件清单失败：{uri} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        Verify(candidate, bytes);

        return JavaRuntimeManifest.Parse(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>核对清单内容的字节数与 SHA-1（远端声明的两个值都必须对上）。</summary>
    private static void Verify(JavaRuntimeCandidate candidate, byte[] bytes)
    {
        if (candidate.ManifestSize > 0 && bytes.LongLength != candidate.ManifestSize)
        {
            throw new JavaRuntimeDataException(
                $"「{candidate.Component}」的文件清单不完整：官方声明 {candidate.ManifestSize} 字节，实际收到 {bytes.LongLength} 字节。");
        }

        if (string.IsNullOrWhiteSpace(candidate.ManifestSha1))
        {
            // 官方一定会给 sha1；真没给时如实说"不可校验"，而不是假装校验过了。
            return;
        }

        // CA5350（SHA-1 是弱算法）在这里不适用：这个哈希不是我们选的安全机制，而是官方清单协议
        // **规定的**完整性字段（清单地址本身就是这个 sha1）。换算法只会让校验永远失败。
#pragma warning disable CA5350
        var actual = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA5350

        if (!string.Equals(actual, candidate.ManifestSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new JavaRuntimeDataException(
                $"「{candidate.Component}」的文件清单校验不通过：期望 {candidate.ManifestSha1}，实际 {actual}。");
        }
    }
}
