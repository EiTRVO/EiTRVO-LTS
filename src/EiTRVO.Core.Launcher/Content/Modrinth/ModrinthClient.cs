using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Content.Modrinth;

/// <summary>
/// Modrinth API（<c>api.modrinth.com</c>）的客户端。
/// </summary>
/// <remarks>
/// 三条刻意的设计，与 M2 的版本清单客户端同源：
/// <list type="number">
/// <item><b>只用命名客户端</b>（<c>HttpClientNames.Core</c>）：域名白名单与重试挂在它的管道上，
/// <c>api.modrinth.com</c> 与 <c>cdn.modrinth.com</c> 已在白名单里（M0 就登记了），
/// 拿 <c>new HttpClient()</c> 就绕过了策略。</item>
/// <item><b>失败一律变成 <see cref="ModrinthException"/> 并记一条事件</b>：
/// 状态码、网络异常、JSON 解析失败在调用方看来是同一件事（"这次没取到"），
/// 但事件里要能看出是哪一种 —— 否则 429 会被当成"Modrinth 坏了"。</item>
/// <item><b>批量取版本</b>：搜索接口只给"最新版本的 id"，而表格要显示版本号。
/// 逐个项目再发一次请求是 6 次往返；<c>/v2/versions?ids=[…]</c> 一次就够。</item>
/// </list>
/// </remarks>
public sealed class ModrinthClient
{
    /// <summary>API 根地址。</summary>
    public const string ApiBase = "https://api.modrinth.com/v2";

    /// <summary>搜索地址。</summary>
    public const string SearchUrl = ApiBase + "/search";

    /// <summary>批量版本地址。</summary>
    public const string VersionsUrl = ApiBase + "/versions";

    /// <summary>按哈希精确查版本的地址（<c>POST</c>）。</summary>
    public const string VersionFilesUrl = ApiBase + "/version_files";

    /// <summary>按哈希查"有没有更新版本"的地址（<c>POST</c>，可带游戏版本与加载器）。</summary>
    public const string VersionFilesUpdateUrl = ApiBase + "/version_files/update";

    /// <summary>一次批量查询里最多放多少个 id（我们自己定的上限，别把 URL 撑爆）。</summary>
    public const int MaxBatchIds = 64;

    /// <summary>
    /// 事件里用的端点名字（<c>/v2/…</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Uri.AbsolutePath"/> 逐字一致是刻意的：状态码失败那条路径报的就是
    /// <c>AbsolutePath</c>，而 JSON 解析失败这条路径先前手写了 <c>/search</c> ——
    /// 同一个端点会出现两种写法，排障时按端点聚合就会漏掉一半。
    /// </remarks>
    private const string SearchPath = "/v2/search";

    /// <summary>版本端点的名字（同上）。</summary>
    private const string VersionsPath = "/v2/versions";

    /// <summary>哈希对账端点的名字。</summary>
    private const string VersionFilesPath = "/v2/version_files";

    /// <summary>"有没有更新"端点的名字。</summary>
    private const string VersionFilesUpdatePath = "/v2/version_files/update";

    /// <summary>搜索页大小上限（官方上限是 100）。</summary>
    public const int MaxPageSize = 100;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造客户端。</summary>
    /// <param name="httpClientFactory">命名客户端工厂（**不得**直接传 HttpClient）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public ModrinthClient(IHttpClientFactory httpClientFactory, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _diagnostics = diagnostics;
    }

    /// <summary>搜索整合包（按相关性）。</summary>
    /// <param name="limit">页大小（1…100）。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>搜索结果。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
    public Task<ModrinthSearchPage> SearchModpacksAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default) =>
        SearchAsync(ModpackFacet, limit, offset, cancellationToken);

    /// <summary>按搜索词与游戏版本 / 加载器筛选整合包（p08 的搜索框与两个下拉；S9-e）。</summary>
    /// <param name="limit">页大小（1…100）。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="query">搜索词；空表示不按关键词搜。</param>
    /// <param name="gameVersion">游戏版本；空表示不限。</param>
    /// <param name="loader">加载器短名；空表示不限。</param>
    /// <param name="sort">排序方式。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>搜索结果。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
    public Task<ModrinthSearchPage> SearchModpacksAsync(
        int limit,
        int offset,
        string? query,
        string? gameVersion,
        string? loader,
        ModrinthSort sort = ModrinthSort.Relevance,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            BuildFacets(gameVersion, loader),
            limit,
            offset,
            query,
            SortIndex(sort),
            cancellationToken);

    /// <summary>把排序方式映射成官方 <c>index</c> 参数。</summary>
    /// <param name="sort">排序方式。</param>
    /// <returns>官方参数值。</returns>
    public static string SortIndex(ModrinthSort sort) => sort switch
    {
        ModrinthSort.Downloads => "downloads",
        ModrinthSort.Follows => "follows",
        ModrinthSort.Newest => "newest",
        ModrinthSort.Updated => "updated",
        _ => "relevance",
    };

    /// <summary>
    /// 拼搜索的 facets：项目类型固定是整合包，游戏版本与加载器按需追加。
    /// </summary>
    /// <param name="gameVersion">游戏版本；空表示不限。</param>
    /// <param name="loader">加载器短名；空表示不限。</param>
    /// <returns>facet 表达式（形如 <c>[["project_type:modpack"],["versions:1.20.4"]]</c>）。</returns>
    public static string BuildFacets(string? gameVersion, string? loader) =>
        BuildFacets(ModpackProjectType, gameVersion, loader);

    /// <summary>拼搜索的 facets：项目类型由调用方给（整合包 / 模组 / 资源包）。</summary>
    /// <param name="projectType">Modrinth 的 <c>project_type</c> 值（<c>modpack</c> / <c>mod</c> / <c>resourcepack</c>）。</param>
    /// <param name="gameVersion">游戏版本；空表示不限。</param>
    /// <param name="loader">加载器短名；空表示不限（资源包没有加载器）。</param>
    /// <returns>facet 表达式。</returns>
    public static string BuildFacets(string projectType, string? gameVersion, string? loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectType);

        // 每一项是一个"或"组（这里每组只有一个条件），外层数组是"与"。
        List<string> groups = ["[\"project_type:" + projectType.Trim() + "\"]"];

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            groups.Add("[\"versions:" + gameVersion.Trim() + "\"]");
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            groups.Add("[\"categories:" + loader.Trim() + "\"]");
        }

        return "[" + string.Join(",", groups) + "]";
    }

    /// <summary>搜索特定类型的项目。</summary>
    /// <param name="facet">facet 表达式（如 <c>[["project_type:modpack"]]</c>）。</param>
    /// <param name="limit">页大小（1…100）。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>搜索结果。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
#pragma warning disable CA1822
    public Task<ModrinthSearchPage> SearchAsync(
        string facet,
        int limit,
        int offset,
        CancellationToken cancellationToken = default) =>
        SearchAsync(facet, limit, offset, query: null, index: "relevance", cancellationToken);
#pragma warning restore CA1822

    /// <summary>搜索特定类型的项目（可带关键词）。</summary>
    /// <param name="facet">facet 表达式。</param>
    /// <param name="limit">页大小（1…100）。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="query">搜索词；空表示不按关键词搜。</param>
    /// <param name="index">官方排序参数值（默认 <c>relevance</c>）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>搜索结果。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
    public async Task<ModrinthSearchPage> SearchAsync(
        string facet,
        int limit,
        int offset,
        string? query,
        string index = "relevance",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facet);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var json = await GetStringAsync(SearchUri(facet, limit, offset, query, index), cancellationToken).ConfigureAwait(false);

        ModrinthSearchPage? page;

        try
        {
            page = LauncherJson.Deserialize<ModrinthSearchPage>(json, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw Fail(SearchPath, "响应不是合法 JSON", ex);
        }

        // hits 缺失时按空列表处理：那说明这次真的没有匹配，而不是响应坏了。
        return page is null
            ? throw Fail(SearchPath, "响应是 null", null)
            : new ModrinthSearchPage(page.TotalHits, page.Hits ?? []);
    }

    /// <summary>批量取版本（版本号 / 支持的加载器与游戏版本）。</summary>
    /// <param name="ids">版本 id。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>查到的版本（顺序与官方返回一致；查不到的 id 不会出现在结果里）。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        List<ModrinthVersion> versions = [];

        foreach (var batch in Batch(ids))
        {
            var json = await GetStringAsync(VersionsUri(batch), cancellationToken).ConfigureAwait(false);

            try
            {
                var parsed = LauncherJson.Deserialize<List<ModrinthVersion>>(json, LauncherJson.Indented);
                if (parsed is not null)
                {
                    versions.AddRange(parsed);
                }
            }
            catch (JsonException ex)
            {
                throw Fail(VersionsPath, "响应不是合法 JSON", ex);
            }
        }

        return versions;
    }

    /// <summary>搜索整合包的 facet：只要整合包。</summary>
    /// <remarks>
    /// 现在**不带**游戏版本与加载器：p08 的两个下拉由 <c>BuildFacets(string?, string?)</c> 在 S9-e 接上；
    /// 这个常量仍是"默认浏览列表"用的那个表达式。
    /// </remarks>
    public const string ModpackFacet = "[[\"project_type:modpack\"]]";

    /// <summary>Modrinth 的项目类型值。</summary>
    public const string ModpackProjectType = "modpack";

    /// <summary>模组项目类型。</summary>
    public const string ModProjectType = "mod";

    /// <summary>资源包项目类型。</summary>
    public const string ResourcePackProjectType = "resourcepack";

    /// <summary>按 SHA-1 批量精确查版本（本地 jar ↔ Modrinth 的对账第一步）。</summary>
    /// <param name="sha1s">SHA-1（小写十六进制）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>哈希 → 版本（**只含查到的那些**；查不到的键不会出现）。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
    public Task<IReadOnlyDictionary<string, ModrinthVersion>> LookupHashesAsync(
        IReadOnlyList<string> sha1s,
        CancellationToken cancellationToken = default) =>
        PostHashesAsync(VersionFilesUrl, VersionFilesPath, sha1s, gameVersion: null, loader: null, cancellationToken);

    /// <summary>
    /// 按 SHA-1 批量查"有没有更新的版本"（第二步）：带上**当前实例的游戏版本与加载器**，
    /// 官方只返回"在这个版本 + 这个加载器下还有更新"的那些哈希。
    /// </summary>
    /// <param name="sha1s">SHA-1（小写十六进制）。</param>
    /// <param name="gameVersion">游戏版本（<c>1.20.4</c>）；空表示不加这个条件。</param>
    /// <param name="loader">加载器短名（<c>forge</c> / <c>fabric</c> / <c>neoforge</c> / <c>quilt</c>）；空表示不加。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>哈希 → 最新匹配版本（只含**有更新**的那些）。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
    public Task<IReadOnlyDictionary<string, ModrinthVersion>> FindUpdatesAsync(
        IReadOnlyList<string> sha1s,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken = default) =>
        PostHashesAsync(VersionFilesUpdateUrl, VersionFilesUpdatePath, sha1s, gameVersion, loader, cancellationToken);

    /// <summary>两个哈希端点的公共实现（请求体形状一样，只有 facets 与地址不同）。</summary>
    private async Task<IReadOnlyDictionary<string, ModrinthVersion>> PostHashesAsync(
        string url,
        string path,
        IReadOnlyList<string> sha1s,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sha1s);

        Dictionary<string, ModrinthVersion> matches = new(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in Batch(sha1s))
        {
            var body = LauncherJson.Serialize(
                new HashLookupRequest
                {
                    Hashes = batch,
                    Algorithm = ModHashIndex.AlgorithmName,
                    GameVersions = string.IsNullOrWhiteSpace(gameVersion) ? null : [gameVersion.Trim()],
                    Loaders = string.IsNullOrWhiteSpace(loader) ? null : [loader.Trim()],
                },
                LauncherJson.Compact);

            var json = await PostStringAsync(new Uri(url), body, path, cancellationToken).ConfigureAwait(false);

            try
            {
                var parsed = LauncherJson.Deserialize<Dictionary<string, ModrinthVersion>>(json, LauncherJson.Indented);

                foreach (var (hash, version) in parsed ?? [])
                {
                    matches[hash] = version;
                }
            }
            catch (JsonException ex)
            {
                throw Fail(path, "响应不是合法 JSON", ex);
            }
        }

        return matches;
    }

    /// <summary>哈希查询的请求体（字段名是官方的下划线写法）。</summary>
    private sealed record HashLookupRequest
    {
        [JsonPropertyName("hashes")]
        public required IReadOnlyList<string> Hashes { get; init; }

        [JsonPropertyName("algorithm")]
        public required string Algorithm { get; init; }

        [JsonPropertyName("loaders")]
        public IReadOnlyList<string>? Loaders { get; init; }

        [JsonPropertyName("game_versions")]
        public IReadOnlyList<string>? GameVersions { get; init; }
    }

    /// <summary>搜索请求地址（测试与排障都要看它）。</summary>
    /// <param name="facet">facet 表达式。</param>
    /// <param name="limit">页大小。</param>
    /// <param name="offset">偏移。</param>
    /// <returns>地址。</returns>
    public static Uri SearchUri(string facet, int limit, int offset) =>
        SearchUri(facet, limit, offset, query: null, index: "relevance");

    /// <summary>搜索请求地址（可带关键词）。</summary>
    /// <param name="facet">facet 表达式。</param>
    /// <param name="limit">页大小。</param>
    /// <param name="offset">偏移。</param>
    /// <param name="query">搜索词；空表示不带 <c>query</c> 参数。</param>
    /// <param name="index">官方排序参数值。</param>
    /// <returns>地址。</returns>
    public static Uri SearchUri(string facet, int limit, int offset, string? query, string index = "relevance")
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{SearchUrl}?facets={Uri.EscapeDataString(facet)}&index={index}&limit={limit}&offset={offset}");

        // 空关键词不带这个参数：Modrinth 对空 query 的处理是"按热度"，与"不搜"不完全等价，
        // 而默认视图要的正是"浏览"（不搜）。
        if (!string.IsNullOrWhiteSpace(query))
        {
            url += "&query=" + Uri.EscapeDataString(query.Trim());
        }

        return new Uri(url);
    }

    /// <summary>批量版本请求地址。</summary>
    /// <param name="ids">版本 id（1…<see cref="MaxBatchIds"/> 个）。</param>
    /// <returns>地址。</returns>
    public static Uri VersionsUri(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var json = "[" + string.Join(",", ids.Select(static id => JsonSerializer.Serialize(id))) + "]";

        return new Uri(
            string.Create(CultureInfo.InvariantCulture, $"{VersionsUrl}?ids={Uri.EscapeDataString(json)}"));
    }

    /// <summary>把 id 列表切成每批不超过 <see cref="MaxBatchIds"/> 个（去空、去重、保序）。</summary>
    private static IEnumerable<List<string>> Batch(IReadOnlyList<string> ids)
    {
        List<string> batch = [];

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var trimmed = id.Trim();
            if (batch.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            batch.Add(trimmed);

            if (batch.Count >= MaxBatchIds)
            {
                yield return batch;
                batch = [];
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>取一个项目的全部版本（直接安装整合包要按游戏版本 / 加载器挑一个兼容版本）。</summary>
    /// <param name="projectId">项目 id。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>版本列表（官方顺序；坏响应抛异常）。</returns>
    /// <exception cref="ModrinthException">请求失败或响应不可解析。</exception>
    public async Task<IReadOnlyList<ModrinthVersion>> GetProjectVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var uri = new Uri(ApiBase + "/project/" + Uri.EscapeDataString(projectId) + "/version");
        var json = await GetStringAsync(uri, cancellationToken).ConfigureAwait(false);

        try
        {
            return LauncherJson.Deserialize<List<ModrinthVersion>>(json, LauncherJson.Indented) ?? [];
        }
        catch (JsonException ex)
        {
            throw Fail(uri.AbsolutePath, "响应不是合法 JSON", ex);
        }
    }

    /// <summary>
    /// 检查一个 Modrinth 项目 id / slug 是否真实存在。
    /// </summary>
    /// <remarks>
    /// 依赖分析用它筛掉“模组元数据里写了、但 Modrinth 根本没有这个项目”的依赖 id：
    /// 这类 id 通常是加载器内部的模块名或作者随手写的占位名，不该被报成“缺少前置”。
    /// 404 是**确定的“不存在”**；其它失败抛 <see cref="ModrinthException"/>，调用方必须按“未知”处理。
    /// </remarks>
    /// <param name="projectId">项目 id 或 slug。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>存在返回 <see langword="true"/>，404 返回 <see langword="false"/>。</returns>
    /// <exception cref="ModrinthException">请求失败（404 除外）或网络异常。</exception>
    public async Task<bool> ProjectExistsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var uri = new Uri(ApiBase + "/project/" + Uri.EscapeDataString(projectId.Trim()));
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        try
        {
            using var response = await client
                .GetAsync(uri, cancellationToken)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 404)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Fail(
                    uri.AbsolutePath,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
                    null);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Fail(uri.AbsolutePath, ex.GetType().Name, ex);
        }
    }

    /// <summary>把一个远端文件下载到本地（直接安装整合包：先下 .mrpack，再交给导入器）。</summary>
    /// <param name="url">下载地址（<c>cdn.modrinth.com</c>，已在出站白名单里）。</param>
    /// <param name="destination">目标路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <remarks>
    /// 先写 <c>.part</c> 再改名：下载中途失败时目标路径要么不存在、要么是完整文件，
    /// 不会留下一个"看起来下好了"的半截 zip。校验值由调用方（<c>ModrinthCatalog</c>）负责。
    /// </remarks>
    /// <exception cref="ModrinthException">请求失败。</exception>
    public async Task DownloadFileAsync(
        Uri url,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = destination + ".part";
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw Fail(
                    url.AbsolutePath,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
                    null);
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TryDelete(temp);
            throw Fail(url.AbsolutePath, ex.GetType().Name, ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清不掉临时文件不影响"这次失败"的结论。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        try
        {
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw Fail(
                    uri.AbsolutePath,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
                    null);
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Fail(uri.AbsolutePath, ex.GetType().Name, ex);
        }
    }

    /// <summary>POST 一个 JSON 请求体并读回响应文本（哈希对账两个端点共用）。</summary>
    /// <param name="uri">地址。</param>
    /// <param name="body">请求体（JSON 文本）。</param>
    /// <param name="path">事件里用的端点名（与 <see cref="Uri.AbsolutePath"/> 一致）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>响应文本。</returns>
    /// <exception cref="ModrinthException">请求失败。</exception>
    private async Task<string> PostStringAsync(
        Uri uri,
        string body,
        string path,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw Fail(
                    path,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
                    null);
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Fail(path, ex.GetType().Name, ex);
        }
    }

    /// <summary>记一条事件并返回异常（唯一的失败出口，保证"每次失败都有一条可读记录"）。</summary>
    private ModrinthException Fail(string endpoint, string reason, Exception? inner)
    {
        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.Modrinth,
            "获取 Modrinth 数据失败（{0}）：{1}",
            [endpoint, reason]);

        return new ModrinthException($"获取 Modrinth 数据失败（{endpoint}）：{reason}。", inner);
    }
}
