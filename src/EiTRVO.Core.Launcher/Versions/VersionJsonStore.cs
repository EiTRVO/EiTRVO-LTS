using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Storage;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 单个版本 JSON 的读取与缓存。
/// <para>
/// 三个来源，优先级从高到低：
/// </para>
/// <list type="number">
/// <item><b>游戏目录里已安装的</b>（<c>&lt;游戏目录&gt;\versions\&lt;id&gt;\</c> 下的
/// <c>version.json</c>，兼容旧布局的 <c>&lt;id&gt;.json</c>）—— 这是玩家机器上**权威**的那一份：
/// 加载器安装时改写过的 JSON 只在这里；</item>
/// <item><b>本启动器的缓存</b>（<c>cache\versions\&lt;id&gt;.json</c>，从 <c>piston-meta</c> 取回的原文）；</item>
/// <item>都没有 → 联网按清单给的 URL 取一份并落缓存。</item>
/// </list>
/// <para>
/// <b><c>version.json</c> 优先</b>是 Neo 26.1 的版本目录约定，也是实例目录的写法：
/// 一个实例 = 一个 <c>versions\&lt;实例名&gt;\</c> 目录，里面的 <c>version.json</c> 就是它的版本。
/// 旧布局（<c>&lt;id&gt;\&lt;id&gt;.json</c>）继续支持，升级后老实例照样能启动。
/// </para>
/// <para>
/// 顺序不能颠倒：若先信缓存、再信游戏目录，那么"玩家用别的启动器装过一个改过的版本"就会被我们的缓存覆盖掉，
/// 表现为"用别的启动器能玩、用我们的起不来"。
/// </para>
/// </summary>
public sealed class VersionJsonStore
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILauncherPaths _paths;
    private readonly HttpHostPolicy _hostPolicy;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly FileStampGate _corruptWarnings = new();

    /// <summary>构造版本 JSON 存储。</summary>
    /// <param name="httpClientFactory">命名客户端工厂。</param>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="hostPolicy">出站域名策略（URL 来自远端清单，必须在这里再挡一次）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public VersionJsonStore(
        IHttpClientFactory httpClientFactory,
        ILauncherPaths paths,
        HttpHostPolicy hostPolicy,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(hostPolicy);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _paths = paths;
        _hostPolicy = hostPolicy;
        _diagnostics = diagnostics;
    }

    /// <summary>缓存文件路径。</summary>
    /// <param name="versionId">版本 id。</param>
    /// <returns>缓存文件路径。</returns>
    /// <exception cref="ArgumentException">版本 id 不能用作文件名。</exception>
    public string CachedFile(string versionId)
    {
        EnsureSafeVersionId(versionId);
        return Path.Combine(_paths.VersionCacheDirectory, versionId + ".json");
    }

    /// <summary>读取游戏目录里已安装的版本 JSON。</summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="versionId">版本 id（新布局下实例名也是它：目录名即版本 id）。</param>
    /// <returns>版本 JSON；不存在或坏了时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 先读 <c>version.json</c>（Neo 26.1 / 实例目录的约定），再读旧布局的 <c>&lt;id&gt;.json</c>。
    /// 两处都按 <paramref name="versionId"/> 作为权威 id —— 文件里写的 id 可能是加载器自己的
    /// （如 <c>fabric-loader-…</c>），但目录名才是启动链要的版本名。
    /// </remarks>
    public VersionJson? TryReadInstalled(string gameDirectory, string versionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        EnsureSafeVersionId(versionId);

        var versionJson = PathSafety.CombineWithin(gameDirectory, "versions", versionId, "version.json");
        var legacyJson = PathSafety.CombineWithin(gameDirectory, "versions", versionId, versionId + ".json");

        return ReadFile(versionJson, versionId) ?? ReadFile(legacyJson, versionId);
    }

    /// <summary>读取本启动器缓存里的版本 JSON。</summary>
    /// <param name="versionId">版本 id。</param>
    /// <returns>版本 JSON；没有缓存或坏了时为 <see langword="null"/>。</returns>
    public VersionJson? TryReadCached(string versionId)
    {
        EnsureSafeVersionId(versionId);
        return ReadFile(CachedFile(versionId), versionId);
    }

    /// <summary>按"游戏目录 → 缓存"的顺序取版本 JSON。</summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="versionId">版本 id。</param>
    /// <returns>版本 JSON；两处都没有时为 <see langword="null"/>。</returns>
    public VersionJson? Load(string gameDirectory, string versionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        return TryReadInstalled(gameDirectory, versionId) ?? TryReadCached(versionId);
    }

    /// <summary>按清单给的地址取一份版本 JSON 并落缓存。</summary>
    /// <param name="versionId">版本 id。</param>
    /// <param name="url">清单里的地址。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>版本 JSON。</returns>
    /// <exception cref="HttpPolicyException">地址不在出站白名单内（远端数据不可信，必须挡）。</exception>
    /// <exception cref="VersionDataException">状态码异常或响应不是合法版本 JSON。</exception>
    public async Task<VersionJson> FetchAsync(
        string versionId,
        string url,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeVersionId(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new VersionDataException($"版本 JSON 的地址不是合法 URL：'{url}'。");
        }

        // 双重保险：管道上的 AllowedHostHandler 也会挡，但这里先挡能让失败原因明确是"策略不允许"，
        // 而不是一个来自管道深处的异常 —— 也保证"策略拒绝时一个请求都没有发出去"。
        _hostPolicy.EnsureAllowed(uri);

        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);
        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VersionDataException(
                $"获取版本 JSON 失败：{uri} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = Parse(json, versionId);
        AtomicFile.WriteAllText(CachedFile(versionId), json);

        return parsed with { SourceUrl = url };
    }

    /// <summary>解析某个版本的继承链（离线即可用：父版本必须已在游戏目录或缓存里）。</summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="versionId">版本 id。</param>
    /// <returns>合并后的版本。</returns>
    /// <exception cref="VersionResolutionException">版本缺失、父版本缺失、成环或层级过深。</exception>
    public ResolvedVersion Resolve(string gameDirectory, string versionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        EnsureSafeVersionId(versionId);

        var version = Load(gameDirectory, versionId)
            ?? throw new VersionResolutionException(
                TextTemplate.Of("本地没有版本「{0}」的 JSON（游戏目录与缓存里都没有）。", versionId),
                [versionId]);

        return VersionInheritanceResolver.Resolve(version, parentId => Load(gameDirectory, parentId));
    }

    /// <summary>版本 id 必须是安全的单段路径名（它来自远端清单，会被拼进路径）。</summary>
    private static void EnsureSafeVersionId(string versionId)
    {
        if (!PathSafety.IsSafePathSegment(versionId))
        {
            throw new ArgumentException($"版本 id 不能用作文件名：'{versionId}'。", nameof(versionId));
        }
    }

    private VersionJson? ReadFile(string path, string versionId)
    {
        var json = AtomicFile.ReadAllTextOrNull(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return Parse(json, versionId);
        }
        catch (VersionDataException ex)
        {
            // 坏文件按"没有"处理，但**不删**（用户数据安全 §6）：它可能是玩家自己改了一半的版本 JSON。
            if (_corruptWarnings.ShouldReport(path))
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Launcher,
                    "版本 {0} 的 JSON 不可用（{1}），本次按缺失处理（原文件未改动）",
                    [versionId, (ex.InnerException ?? ex).GetType().Name]);
            }

            return null;
        }
    }

    private static VersionJson Parse(string json, string versionId)
    {
        VersionJson? version;

        try
        {
            version = LauncherJson.Deserialize<VersionJson>(json, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new VersionDataException($"版本 JSON（{versionId}）不是合法 JSON。", ex);
        }

        if (version is null)
        {
            throw new VersionDataException($"版本 JSON（{versionId}）解析结果为空。");
        }

        // 路径是权威：我们是用这个 id 找到文件的，就以它为准。
        // 一个把 id 写成别人名字（或写成自己、再声明 inheritsFrom 指向自己）的 JSON
        // 会因此无法扰乱继承链的成环判断。
        return version with { Id = versionId };
    }
}
