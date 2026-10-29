using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 版本数据（清单或某个版本的 JSON）获取失败：HTTP 状态异常、响应不是合法 JSON、或数据为空。
/// </summary>
public sealed class VersionDataException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="message">消息。</param>
    /// <param name="innerException">底层异常。</param>
    public VersionDataException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 版本清单（<c>version_manifest_v2.json</c>）的获取与缓存。
/// <para>
/// 三条刻意的设计：
/// </para>
/// <list type="number">
/// <item>**只用 <see cref="IHttpClientFactory"/> 的命名客户端**（<c>core</c>）：域名白名单与重试挂在它的管道上，
/// 拿 <c>new HttpClient()</c> 就绕过了策略（迁移评估把它列为阻断项 3）。</item>
/// <item>**响应原样落盘**：缓存里存的就是远端那份文本，不重新序列化 —— 排障时可以直接打开看，
/// 也避免"我们的 JSON 选项改了 → 缓存读不出来"这种自伤。</item>
/// <item>**空清单不覆盖缓存**：远端返回一份 0 个版本的清单（限流页、错误页）时，
/// 若照写就把用户手上唯一可用的离线清单毁了。这里宁可报错。</item>
/// </list>
/// </summary>
public sealed class VersionManifestClient
{
    /// <summary>版本清单地址（<c>piston-meta</c>；Neo 用的 <c>launchermeta</c> 已弃用）。</summary>
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>缓存文件名。</summary>
    public const string CacheFileName = "version_manifest_v2.json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILauncherPaths _paths;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly FileStampGate _cacheWarnings = new();

    /// <summary>构造清单客户端。</summary>
    /// <param name="httpClientFactory">命名客户端工厂（**不得**直接传 HttpClient）。</param>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="clock">时间源。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public VersionManifestClient(
        IHttpClientFactory httpClientFactory,
        ILauncherPaths paths,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _paths = paths;
        _clock = clock;
        _diagnostics = diagnostics;
    }

    /// <summary>缓存文件路径。</summary>
    public string CachePath => Path.Combine(_paths.VersionCacheDirectory, CacheFileName);

    /// <summary>缓存文件是否已存在。</summary>
    public bool HasCache => File.Exists(CachePath);

    /// <summary>缓存写入时间（用于界面上的「上次更新」，缓存不存在时为 <see langword="null"/>）。</summary>
    public DateTimeOffset? CacheWrittenAt
    {
        get
        {
            var info = new FileInfo(CachePath);
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
    }

    /// <summary>联网获取清单，并写入缓存。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>清单。</returns>
    /// <exception cref="VersionDataException">状态码异常、响应不是合法清单、或清单为空。</exception>
    public async Task<VersionManifest> FetchAsync(CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var response = await client
            .GetAsync(new Uri(ManifestUrl), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Downloader,
                "获取版本清单失败：{0} 返回了 {1}",
                [ManifestUrl, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)]);

            throw new VersionDataException(
                $"获取版本清单失败：{ManifestUrl} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var manifest = Parse(json);

        if (manifest.Versions.Count == 0)
        {
            throw new VersionDataException(
                $"版本清单为空（{ManifestUrl}）：已保留上一份缓存不动。");
        }

        AtomicFile.WriteAllText(CachePath, json);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Downloader,
            "已更新版本清单（{0} 个版本，最新正式版 {1}，{2}）",
            [
                manifest.Versions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                manifest.Latest?.Release ?? string.Empty,
                _clock.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            ]);

        return manifest;
    }

    /// <summary>读取本地缓存（离线时的唯一来源）。</summary>
    /// <returns>清单；没有缓存或缓存坏了时为 <see langword="null"/>。</returns>
    public VersionManifest? TryReadCache()
    {
        var json = AtomicFile.ReadAllTextOrNull(CachePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return Parse(json);
        }
        catch (VersionDataException ex)
        {
            // 坏缓存按"没有缓存"处理，但**不删文件**（用户数据安全 §6）：下一次成功获取会原子覆盖它。
            if (_cacheWarnings.ShouldReport(CachePath))
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Launcher,
                    "版本清单缓存不可用（{0}，{1}），本次按无缓存处理（原文件未改动）",
                    [CacheFileName, (ex.InnerException ?? ex).GetType().Name]);
            }

            return null;
        }
    }

    /// <summary>解析清单文本。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>清单。</returns>
    /// <exception cref="VersionDataException">文本不是合法清单。</exception>
    private static VersionManifest Parse(string json)
    {
        VersionManifest? manifest;

        try
        {
            manifest = LauncherJson.Deserialize<VersionManifest>(json, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new VersionDataException("版本清单不是合法 JSON。", ex);
        }

        if (manifest?.Versions is null)
        {
            throw new VersionDataException("版本清单缺少 versions 字段。");
        }

        return manifest;
    }
}
