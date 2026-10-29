using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>加载器元数据响应里的一个加载器版本（只取我们要用的字段）。</summary>
internal sealed record FabricLoaderEntry
{
    /// <summary>加载器信息。</summary>
    public FabricLoaderInfo? Loader { get; init; }
}

/// <summary>加载器信息。</summary>
internal sealed record FabricLoaderInfo
{
    /// <summary>版本号，如 <c>0.15.7</c>。</summary>
    public string? Version { get; init; }

    /// <summary>是否为稳定版（Fabric 会标；Quilt 不标）。</summary>
    public bool Stable { get; init; }
}

/// <summary>
/// 加载器版本元数据（B-10）：Fabric / Quilt / Forge / NeoForge。
/// <para>
/// 四家用的是三种不同形态的接口：
/// </para>
/// <list type="bullet">
/// <item><b>Fabric / Quilt</b>：REST + JSON，地址里带游戏版本（<c>/loader/&lt;mc&gt;</c>）；Fabric 还标了 <c>stable</c>。</item>
/// <item><b>Forge</b>：Maven 的 <c>maven-metadata.xml</c>，版本形如 <c>1.20.4-49.0.30</c>，要按游戏版本前缀筛。</item>
/// <item><b>NeoForge</b>：同为 Maven，但版本形如 <c>20.4.237</c> —— 从 1.20.2 起 NeoForge 换了版本号体系，
/// 版本号里**不含**游戏版本，只能由游戏版本推出系列号（<c>1.20.4</c> → <c>20.4</c>）；
/// 而 <c>1.20.1</c> 是上一套体系，仍然在 <c>net/neoforged/forge</c> 这个 artifact 下、
/// 版本形如 <c>1.20.1-47.1.106</c>（迁移评估把这条记为"1.20.1 映射"必须修的地方）。</item>
/// </list>
/// <para>
/// 所有请求都走组合根装配的命名客户端（白名单与重试在管道上）；结果按 (加载器, 游戏版本) 做**短时内存缓存**，
/// 因为向导里来回切加载器会反复问同一批地址。
/// </para>
/// </summary>
public sealed class LoaderMetadataClient
{
    /// <summary>Fabric 加载器列表地址前缀（后面接游戏版本）。</summary>
    public const string FabricLoaderUrlPrefix = "https://meta.fabricmc.net/v2/versions/loader/";

    /// <summary>Quilt 加载器列表地址前缀（后面接游戏版本）。</summary>
    public const string QuiltLoaderUrlPrefix = "https://meta.quiltmc.org/v3/versions/loader/";

    /// <summary>Forge 的 Maven 元数据地址。</summary>
    public const string ForgeMetadataUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";

    /// <summary>NeoForge 的 Maven 元数据地址（1.20.2 起的版本号体系）。</summary>
    public const string NeoForgeMetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    /// <summary>NeoForge 1.20.1 及以前的 Maven 元数据地址（旧的版本号体系）。</summary>
    public const string NeoForgeLegacyMetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml";

    /// <summary>结果缓存有效期：向导里来回切换时不必反复问同一批地址，但也不至于把过期数据留太久。</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;

    private readonly Dictionary<string, (LoaderVersionList List, DateTimeOffset At)> _cache =
        new(StringComparer.Ordinal);

    /// <summary>构造加载器元数据客户端。</summary>
    /// <param name="httpClientFactory">命名客户端工厂。</param>
    /// <param name="clock">时间源（缓存有效期要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public LoaderMetadataClient(
        IHttpClientFactory httpClientFactory,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _clock = clock;
        _diagnostics = diagnostics;
    }

    /// <summary>取某个游戏版本可用的加载器版本。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="gameVersion">游戏版本，如 <c>1.20.4</c>。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>版本列表；原版恒为空列表。</returns>
    /// <exception cref="VersionDataException">请求失败或响应不可解析。</exception>
    public Task<LoaderVersionList> GetAsync(
        LoaderKind kind,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureCleanVersionId(gameVersion);

        return kind switch
        {
            LoaderKind.Vanilla => Task.FromResult(LoaderVersionList.None(LoaderKind.Vanilla)),
            LoaderKind.Fabric => GetFabricAsync(gameVersion, cancellationToken),
            LoaderKind.Quilt => GetQuiltAsync(gameVersion, cancellationToken),
            LoaderKind.Forge => GetForgeAsync(gameVersion, cancellationToken),
            LoaderKind.NeoForge => GetNeoForgeAsync(gameVersion, cancellationToken),

            // OptiFine 没有"加载器版本列表"这种概念（它只有官网的安装器），
            // 它的处置属于决策项 Q1；这里如实返回空，不做任何猜测。
            _ => Task.FromResult(LoaderVersionList.None(kind)),
        };
    }

    /// <summary>取某游戏版本可用的全部加载器（不含原版与 OptiFine）。</summary>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>加载器 → 版本列表。</returns>
    public async Task<IReadOnlyDictionary<LoaderKind, LoaderVersionList>> GetAllAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        LoaderKind[] kinds = [LoaderKind.Fabric, LoaderKind.Quilt, LoaderKind.Forge, LoaderKind.NeoForge];
        Dictionary<LoaderKind, LoaderVersionList> result = [];

        foreach (var kind in kinds)
        {
            result[kind] = await GetAsync(kind, gameVersion, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Fabric / Quilt 共用的 JSON 列表读取。</summary>
    private async Task<LoaderVersionList> GetJsonListAsync(
        LoaderKind kind,
        string gameVersion,
        string url,
        CancellationToken cancellationToken)
    {
        var cacheKey = kind + "/" + gameVersion;
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        List<FabricLoaderEntry>? entries;
        try
        {
            entries = LauncherJson.Deserialize<List<FabricLoaderEntry>>(json, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new VersionDataException($"{kind} 的加载器列表不是合法 JSON：{url}。", ex);
        }

        List<string> versions = [];
        string? recommended = null;

        foreach (var entry in entries ?? [])
        {
            var version = entry.Loader?.Version;
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            versions.Add(version);

            // 只有 Fabric 会标 stable；Quilt 不标，于是 Recommended 留空、默认取最新。
            if (recommended is null && entry.Loader!.Stable)
            {
                recommended = version;
            }
        }

        // Fabric 的 API 恰好是新 → 旧，Quilt 的返回顺序并不保证（实测 0.20.0-beta.9 后面跟着 beta.7 / beta.8），
        // 所以**不要相信远端顺序**：统一按版本号从新到旧排，下拉框第一项才是真正的最新版。
        var list = new LoaderVersionList(kind, NewestFirst(versions), recommended);
        Store(cacheKey, list);
        return list;
    }

    private Task<LoaderVersionList> GetFabricAsync(string gameVersion, CancellationToken cancellationToken) =>
        GetJsonListAsync(
            LoaderKind.Fabric,
            gameVersion,
            FabricLoaderUrlPrefix + gameVersion,
            cancellationToken);

    private Task<LoaderVersionList> GetQuiltAsync(string gameVersion, CancellationToken cancellationToken) =>
        GetJsonListAsync(
            LoaderKind.Quilt,
            gameVersion,
            QuiltLoaderUrlPrefix + gameVersion,
            cancellationToken);

    private async Task<LoaderVersionList> GetForgeAsync(string gameVersion, CancellationToken cancellationToken)
    {
        var cacheKey = "Forge/" + gameVersion;
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var xml = await GetStringAsync(ForgeMetadataUrl, cancellationToken).ConfigureAwait(false);

        // Forge 的版本号是「游戏版本-加载器版本」，按前缀筛出属于这个游戏版本的那些。
        var prefix = gameVersion + "-";
        List<string> versions = [];

        foreach (var version in ReadMavenVersions(xml, ForgeMetadataUrl))
        {
            if (!version.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var loaderVersion = version[prefix.Length..];

            // 1.7.10 / 1.8.9 的旧 Forge 在 Maven 里常写成
            // `1.7.10-10.13.4.1614-1.7.10`；末尾那次游戏版本是路径的一部分，不是加载器版本。
            // 不剥掉它，Version.TryParse 会失败，1.7.10 的列表就永远是空的。
            var legacySuffix = "-" + gameVersion;
            if (loaderVersion.EndsWith(legacySuffix, StringComparison.Ordinal))
            {
                loaderVersion = loaderVersion[..^legacySuffix.Length];
            }

            // 1.7.10 的旧 FML（10.13.4.1614 之前）在 Java 8 上会因
            // `Collections.sort` 改了 ArrayList 的 modCount 而抛
            // FMLInjectionAndSortingTweaker / launchwrapper 的 ConcurrentModificationException。
            // 这些构建在列表里只会让用户装出一个"启动就崩"的实例；留最新一版即可。
            if (gameVersion == "1.7.10" && IsJava8CompatibleForge1710(loaderVersion))
            {
                versions.Add(loaderVersion);
            }
            else if (gameVersion != "1.7.10")
            {
                versions.Add(loaderVersion);
            }
        }

        var list = new LoaderVersionList(LoaderKind.Forge, NewestFirst(versions.Distinct(StringComparer.Ordinal)), null);
        Store(cacheKey, list);
        return list;
    }

    /// <summary>
    /// 把加载器版本列表按**从新到旧**排序（下拉框第一项即最新版，和默认选中的就是它）。
    /// </summary>
    /// <remarks>
    /// 不能依赖远端返回顺序：Fabric 是新→旧，Quilt 实测是乱序（<c>0.20.0-beta.9</c> 后面跟着
    /// <c>beta.7</c> / <c>beta.8</c>），Maven 元数据是旧→新。统一在这里排一次，界面与默认值
    /// 就都不必猜远端心情。
    /// </remarks>
    /// <param name="versions">远程序列。</param>
    /// <returns>从新到旧的列表。</returns>
    private static IReadOnlyList<string> NewestFirst(IEnumerable<string> versions) =>
        [.. versions.OrderByDescending(
            static version => version,
            Comparer<string>.Create(static (left, right) => MavenVersion.Compare(left, right)))];

    /// <summary>
    /// 1.7.10 的 Forge 构建在 Java 8 上能不能活下来（10.13.4.1614 修了 FML 的 tweak 排序）。
    /// </summary>
    /// <param name="loaderVersion">Forge 版本（如 10.13.4.1614）。</param>
    /// <returns>能启动返回 <see langword="true"/>。</returns>
    private static bool IsJava8CompatibleForge1710(string loaderVersion) =>
        Version.TryParse(loaderVersion, out var parsed) && parsed >= new Version(10, 13, 4, 1614);

    private async Task<LoaderVersionList> GetNeoForgeAsync(string gameVersion, CancellationToken cancellationToken)
    {
        var cacheKey = "NeoForge/" + gameVersion;
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var legacy = IsLegacyNeoForge(gameVersion);
        var url = legacy ? NeoForgeLegacyMetadataUrl : NeoForgeMetadataUrl;

        // 1.20.2 起 NeoForge 的版本号里不含游戏版本，只能由游戏版本推出系列号。
        // 推不出来（快照、或还没有对应系列）就直接返回空 —— 不猜、也不发一次注定无用的请求。
        var series = legacy ? gameVersion + "-" : NeoForgeSeries(gameVersion);
        if (series is null)
        {
            var none = LoaderVersionList.None(LoaderKind.NeoForge);
            Store(cacheKey, none);
            return none;
        }

        var xml = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        List<string> versions = [];

        foreach (var version in ReadMavenVersions(xml, url))
        {
            if (version.StartsWith(series, StringComparison.Ordinal))
            {
                versions.Add(legacy ? version[series.Length..] : version);
            }
        }

        var list = new LoaderVersionList(LoaderKind.NeoForge, NewestFirst(versions), null);
        Store(cacheKey, list);
        return list;
    }

    /// <summary>
    /// 1.20.1 及以前属于 NeoForge 的旧体系（artifact 叫 <c>forge</c>，版本形如 <c>1.20.1-47.1.106</c>）。
    /// 与 <see cref="LoaderNaming.IsLegacyNeoForge"/> 是同一条规则 —— 版本列表与安装/启动必须一致。
    /// </summary>
    private static bool IsLegacyNeoForge(string gameVersion) => LoaderNaming.IsLegacyNeoForge(gameVersion);

    /// <summary>
    /// 由游戏版本推出 NeoForge 的版本系列前缀（<c>1.20.4</c> → <c>20.4.</c>、<c>1.21</c> → <c>21.0.</c>）。
    /// </summary>
    /// <param name="gameVersion">游戏版本。</param>
    /// <returns>系列前缀；无法判断时为 <see langword="null"/>。</returns>
    public static string? NeoForgeSeries(string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        var text = gameVersion.Trim();
        if (!text.StartsWith("1.", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = text[2..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var minor))
        {
            return null;
        }

        // 1.20.2 之前的 NeoForge 不存在（1.20.1 走旧体系），更早的版本自然也是空。
        if (minor < 20)
        {
            return null;
        }

        var patch = parts.Length >= 2 && int.TryParse(parts[1], out var parsed) ? parsed : 0;
        return string.Create(CultureInfo.InvariantCulture, $"{minor}.{patch}.");
    }

    /// <summary>
    /// 游戏版本 id 会被拼进请求地址，因此不允许任何能改变 URL 结构的字符
    /// （分隔符、查询串、片段、空白）。它来自远端清单，属于不可信输入。
    /// </summary>
    /// <param name="gameVersion">游戏版本 id。</param>
    /// <exception cref="ArgumentException">含非法字符。</exception>
    private static void EnsureCleanVersionId(string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        if (gameVersion.IndexOfAny(['/', '\\', '?', '#', '&', '=']) >= 0
            || gameVersion.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"游戏版本 id 不能用于拼请求地址：'{gameVersion}'。",
                nameof(gameVersion));
        }
    }

    /// <summary>读 Maven 元数据里的版本列表（升序）。</summary>
    private static IEnumerable<string> ReadMavenVersions(string xml, string sourceUrl)
    {
        XDocument document;

        try
        {
            // XDocument.Parse 默认禁止 DTD（不解析外部实体），元数据来自白名单主机，够用。
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new VersionDataException($"Maven 元数据不是合法 XML：{sourceUrl}。", ex);
        }

        var versions = document.Root?
            .Element("versioning")?
            .Element("versions")?
            .Elements("version")
            .Select(static element => element.Value.Trim())
            .Where(static version => version.Length > 0);

        return versions ?? [];
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        // 命名客户端刻意用无限超时（下载大文件不能被 5 分钟砍掉），所以加载器元数据
        // 这里必须自己给一个短超时：一个连不上的 Maven 域名不该让整张加载器表等到
        // 操作系统级超时（实测 maven.neoforged.net 会卡住十几秒）。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        HttpResponseMessage response;
        try
        {
            response = await client
                .GetAsync(new Uri(url), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VersionDataException($"获取加载器版本列表超时：{url}。");
        }

        using var _ = response;

        if (!response.IsSuccessStatusCode)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.ModLoader,
                "获取加载器版本列表失败：{0} 返回了 {1}",
                [url, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)]);

            throw new VersionDataException(
                $"获取加载器版本列表失败：{url} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetCached(string key, out LoaderVersionList list)
    {
        if (_cache.TryGetValue(key, out var entry) && _clock.Now - entry.At < CacheLifetime)
        {
            list = entry.List;
            return true;
        }

        list = LoaderVersionList.None(LoaderKind.Vanilla);
        return false;
    }

    private void Store(string key, LoaderVersionList list) => _cache[key] = (list, _clock.Now);
}
