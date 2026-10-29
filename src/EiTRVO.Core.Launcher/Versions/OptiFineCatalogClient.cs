using System.Globalization;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 取 OptiFine 的可安装列表（B-9，M4-i）。
/// <para>
/// 走组合根装配的命名客户端，因此白名单、https、逐跳重定向检查都照常生效 ——
/// 这个地址来自我们写死的常量，但"写死的地址也走同一条管道"这条纪律不能破。
/// </para>
/// </summary>
public sealed class OptiFineCatalogClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造客户端。</summary>
    /// <param name="httpClientFactory">命名客户端工厂。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public OptiFineCatalogClient(
        IHttpClientFactory httpClientFactory,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _diagnostics = diagnostics;
    }

    /// <summary>取某个游戏版本可用的全部 OptiFine 构建（稳定版在前）。</summary>
    /// <param name="gameVersion">游戏版本 id。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>构建列表；该版本没有 OptiFine 时为空列表。</returns>
    /// <exception cref="VersionDataException">请求失败或响应不可解析。</exception>
    /// <remarks>
    /// 2026-10-23：来源改为 **optifine.net 官方 downloads 页**（与 Neo 一样抓页面），
    /// 不再经过任何第三方镜像。
    /// </remarks>
    public async Task<IReadOnlyList<OptiFineBuild>> FetchAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        var url = OptiFineCatalog.BuildsUrl(gameVersion);
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var response = await client
            .GetAsync(new Uri(url), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.ModLoader,
                "获取 OptiFine 列表失败：{0} 返回了 {1}",
                [url, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)]);

            throw new VersionDataException(
                $"获取 OptiFine 列表失败：{url} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return OptiFineCatalog.Parse(html, gameVersion);
    }

    /// <summary>
    /// 把一个官方文件名解析成 <c>downloadx</c> 一次性下载地址。
    /// </summary>
    /// <remarks>
    /// 官方下载是两跳：<c>adloadx?f=...</c> 页面上才有带 token 的 <c>downloadx?f=...&amp;x=...</c>；
    /// 前一跳带上 downloads 页作为 Referer，后一跳直接 GET（与 Neo 的做法一致）。
    /// </remarks>
    /// <param name="fileName">官方文件名（来自列表）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>可直接下载的地址。</returns>
    /// <exception cref="VersionDataException">页面取不到或找不到 <c>downloadx</c> 链接。</exception>
    public async Task<string> ResolveInstallerUrlAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var adloadUrl = OptiFineCatalog.AdloadUrl(fileName);
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(adloadUrl));
        request.Headers.Referrer = new Uri(OptiFineCatalog.OfficialDownloadsPage);

        using var response = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VersionDataException(
                $"获取 OptiFine 下载页失败：{adloadUrl} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        var html = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            .Replace("&amp;", "&", StringComparison.Ordinal);

        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            @"href=['""](?:https?://optifine\.net/)?downloadx\?f=([^'""&]+)&x=([a-f0-9]+)['""]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            throw new VersionDataException($"OptiFine 下载页面里没有找到 downloadx 链接（{fileName}）。");
        }

        return "https://optifine.net/downloadx?f="
               + Uri.EscapeDataString(match.Groups[1].Value)
               + "&x=" + match.Groups[2].Value;
    }
}
