using System.Net;
using System.Security.Cryptography;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M7-b：Modrinth 浏览列表（p08）—— 搜索、版本批量查询、磁盘缓存与三个时间窗口。
/// <para>
/// 全部用 HTTP 桩：**请求 URL 与查询参数逐条断言**（审计 §4.4 的教训：Neo 的 HTTP 测试从不断言地址，
/// 于是"请求发错了地方"没有任何回归保护），缓存与新鲜度则用临时目录 + 可变时钟验证。
/// </para>
/// </summary>
[TestClass]
public sealed class ModrinthCatalogTests
{
    private static readonly DateTimeOffset StartupTime = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(8));

    /// <summary>真实的搜索响应形状（字段名与官方一致，值取自 2026-10-14 的真机响应）。</summary>
    private const string SearchJson = """
        {"hits":[
          {"project_id":"1KVo5zza","project_type":"modpack","slug":"fabulously-optimized",
           "title":"Fabulously Optimized","description":"Beautiful graphics, speedy performance.",
           "downloads":18632180,"latest_version":"D35n5vLL"},
          {"project_id":"AABBCCDD","project_type":"modpack","slug":"all-the-mods-9",
           "title":"All the Mods 9","description":"Kitchen sink pack.",
           "downloads":5800000,"latest_version":"V2"}
        ],"offset":0,"limit":6,"total_hits":1234}
        """;

    /// <summary>批量版本的响应形状（数组）。</summary>
    private const string VersionsJson = """
        [
          {"id":"D35n5vLL","project_id":"1KVo5zza","version_number":"15.0.0","name":"15.0.0 for 26.3",
           "date_published":"2026-10-11T12:00:36.367115Z","game_versions":["26.3"],"loaders":["fabric"]},
          {"id":"V2","project_id":"AABBCCDD","version_number":"0.3.24","name":"ATM9 0.3.24",
           "date_published":"2026-09-01T00:00:00Z","game_versions":["1.20.1"],"loaders":["forge"]}
        ]
        """;

    private const string EmptySearchJson = """{"hits":[],"offset":0,"limit":6,"total_hits":0}""";

    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录与固定时钟。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-modrinth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(StartupTime);
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private (ModrinthCatalog Catalog, StubHandler Handler, FakeHttpClientFactory Factory) NewCatalog(StubHandler handler)
    {
        var factory = new FakeHttpClientFactory(handler);
        var client = new ModrinthClient(factory, _diagnostics);
        var cache = new ModrinthCache(_paths, _diagnostics);

        return (new ModrinthCatalog(client, cache, _clock, _diagnostics), handler, factory);
    }

    private static StubHandler HappyPath() => new StubHandler()
        .RespondTo("/search", HttpStatusCode.OK, SearchJson)
        .RespondTo("/versions?", HttpStatusCode.OK, VersionsJson);

    private string CachePath => Path.Combine(_paths.ContentCacheDirectory, ModrinthCache.BrowseFileName);

    // ------------------------------------------------------------ 正常路径 ---

    [TestMethod]
    public async Task TheSearchGoesToTheModrinthSearchEndpointWithTheModpackFacet()
    {
        var (catalog, handler, factory) = NewCatalog(HappyPath());

        Assert.IsTrue(await catalog.RefreshAsync());

        // 命名客户端（白名单 + 重试挂在它的管道上）：拿 new HttpClient() 就绕过了策略。
        CollectionAssert.Contains(factory.RequestedNames.ToArray(), "core");

        var uri = handler.UriAt(0);
        Assert.AreEqual("https://api.modrinth.com/v2/search", uri.GetLeftPart(UriPartial.Path));
        StringAssert.Contains(uri.Query, "facets=%5B%5B%22project_type%3Amodpack%22%5D%5D");
        StringAssert.Contains(uri.Query, "index=relevance");
        StringAssert.Contains(uri.Query, "limit=6");
        StringAssert.Contains(uri.Query, "offset=0");

        // 第二次请求是**批量**版本查询（表格的「版本」列要版本号，搜索接口只给 id）。
        Assert.AreEqual(2, handler.Requests.Count, "一次搜索 + 一次批量版本，不该逐个项目各查一次。");
        Assert.AreEqual("https://api.modrinth.com/v2/versions", handler.UriAt(1).GetLeftPart(UriPartial.Path));
        StringAssert.Contains(handler.UriAt(1).Query, "ids=");
        StringAssert.Contains(Uri.UnescapeDataString(handler.UriAt(1).Query), "D35n5vLL");
    }

    [TestMethod]
    public async Task TheBrowseRowsCarryNameSummaryDownloadsAndVersionNumber()
    {
        var (catalog, _, _) = NewCatalog(HappyPath());

        await catalog.RefreshAsync();

        var entries = catalog.GetBrowse();

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("Fabulously Optimized", entries[0].Name);
        Assert.AreEqual("Beautiful graphics, speedy performance.", entries[0].Summary);
        Assert.AreEqual("18.6M", entries[0].Downloads);
        Assert.AreEqual("15.0.0", entries[0].Version, "版本号来自那次批量查询，不是搜索接口的 id。");
        Assert.AreEqual("All the Mods 9", entries[1].Name);
        Assert.AreEqual("5.8M", entries[1].Downloads);
        Assert.AreEqual("0.3.24", entries[1].Version);

        Assert.IsTrue(catalog.HasFetched);
        Assert.AreEqual(1234, catalog.TotalHits);
        Assert.AreEqual(StartupTime, catalog.FetchedAt);
    }

    [TestMethod]
    public async Task AFailedVersionLookupOnlyCostsTheVersionColumn()
    {
        var handler = new StubHandler()
            .RespondTo("/search", HttpStatusCode.OK, SearchJson)
            .RespondTo("/versions?", HttpStatusCode.ServiceUnavailable, "{}");

        var (catalog, _, _) = NewCatalog(handler);

        // 版本号取不到不该让整张表失败：名字/简介/下载量都还在。
        Assert.IsTrue(await catalog.RefreshAsync());

        var entries = catalog.GetBrowse();

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("—", entries[0].Version, "版本号取不到就写纯数据键「—」，而不是让整行失败。");
        Assert.AreEqual("Fabulously Optimized", entries[0].Name);
    }

    // -------------------------------------------------------------- 缓存 ---

    [TestMethod]
    public async Task AFreshSnapshotIsReusedWithoutTouchingTheNetwork()
    {
        var (first, handler, _) = NewCatalog(HappyPath());
        await first.RefreshAsync();
        var requestsAfterFirst = handler.Requests.Count;

        // 新实例 = 重启启动器：快照应当从磁盘恢复，且新鲜度窗口内不再联网。
        var (second, _, _) = NewCatalog(new StubHandler());

        Assert.IsTrue(second.HasFetched, "构造时先读磁盘缓存 —— 离线打开也要能显示上次的结果。");
        Assert.AreEqual(2, second.GetBrowse().Count);
        Assert.IsTrue(await second.RefreshAsync());
        Assert.AreEqual(requestsAfterFirst, handler.Requests.Count, "新鲜度窗口内不该再打一次 Modrinth。");
    }

    [TestMethod]
    public async Task AStaleSnapshotIsRefreshed()
    {
        var (catalog, handler, _) = NewCatalog(HappyPath());
        await catalog.RefreshAsync();
        var requestsAfterFirst = handler.Requests.Count;

        // 新鲜度窗口是 30 分钟：31 分钟之后再进页面必须重新取。
        _clock.Now = StartupTime.AddMinutes(31);
        await catalog.RefreshAsync();

        Assert.AreEqual(requestsAfterFirst + 2, handler.Requests.Count);
        Assert.AreEqual(_clock.Now, catalog.FetchedAt);
    }

    [TestMethod]
    public async Task TheCacheIsAJsonSnapshotWithItsOwnSchemaVersion()
    {
        var (catalog, _, _) = NewCatalog(HappyPath());
        await catalog.RefreshAsync();

        Assert.IsTrue(File.Exists(CachePath), $"缓存应当写在 {ModrinthCache.BrowseFileName}。");

        var json = File.ReadAllText(CachePath);

        StringAssert.Contains(json, "\"schemaVersion\": 1");
        StringAssert.Contains(json, "\"fetchedAt\"");
        StringAssert.Contains(json, "Fabulously Optimized");
    }

    [TestMethod]
    public void ACorruptCacheIsTreatedAsNoCacheAndIsNotDeleted()
    {
        Directory.CreateDirectory(_paths.ContentCacheDirectory);
        File.WriteAllText(CachePath, "{ 这不是 JSON");

        var (catalog, _, _) = NewCatalog(new StubHandler());

        Assert.AreEqual(0, catalog.GetBrowse().Count);
        Assert.IsFalse(catalog.HasFetched, "坏缓存按「没有缓存」处理。");
        Assert.IsTrue(File.Exists(CachePath), "读不出来**不删文件**（用户数据安全：下一次成功取回会原子覆盖它）。");

        // 同一个坏文件只报一次警（内容不变时不重复刷）。
        var warnings = _diagnostics.Entries.Count(static entry => entry.MessageKey.StartsWith("Modrinth 浏览缓存不可用", StringComparison.Ordinal));
        Assert.AreEqual(1, warnings);
    }

    // ------------------------------------------------------------ 失败路径 ---

    [TestMethod]
    public async Task AFailedRefreshKeepsTheLastResultAndLogsOneEvent()
    {
        // 先成功取一次（写进磁盘缓存），再用一个只会 429 的客户端刷新 —— 与"重启之后网络坏了"同一个形状。
        var (happy, _, _) = NewCatalog(HappyPath());
        await happy.RefreshAsync();

        var failing = new StubHandler().RespondTo("/search", HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}");
        var (catalog, _, _) = NewCatalog(failing);

        _clock.Now = StartupTime.AddMinutes(31);

        Assert.IsFalse(await catalog.RefreshAsync(), "刷新失败要如实返回 false。");
        Assert.AreEqual(2, catalog.GetBrowse().Count, "失败时**保留**上一次的结果，而不是把表清空。");

        var events = _diagnostics.Entries.Where(static entry => entry.MessageKey == "获取 Modrinth 数据失败（{0}）：{1}").ToList();

        Assert.AreEqual(1, events.Count, "失败原因由客户端记一条事件（429 不该被说成网络坏了）。");
        CollectionAssert.AreEqual(new[] { "/v2/search", "HTTP 429 Too Many Requests" }, events[0].Parameters.ToArray());
        Assert.AreEqual(DiagnosticsSources.Modrinth, events[0].Source);
    }

    [TestMethod]
    public async Task AFailureIsNotRetriedInsideTheBackoffWindow()
    {
        var (catalog, handler, _) = NewCatalog(new StubHandler().RespondTo("/search", HttpStatusCode.BadGateway));
        await catalog.RefreshAsync();
        var requests = handler.Requests.Count;

        // 退避窗口（2 分钟）内重复进页面不该反复重试：网络断了的时候那会拖慢导航并刷满事件表。
        _clock.Now = StartupTime.AddMinutes(1);
        Assert.IsFalse(await catalog.RefreshAsync());
        Assert.AreEqual(requests, handler.Requests.Count);

        // 过了退避窗口就允许再试一次。
        _clock.Now = StartupTime.AddMinutes(3);
        await catalog.RefreshAsync();
        Assert.AreEqual(requests + 1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AnEmptyResponseDoesNotWipeTheUsableList()
    {
        var (happy, _, _) = NewCatalog(HappyPath());
        await happy.RefreshAsync();

        var (catalog, _, _) = NewCatalog(new StubHandler().RespondTo("/search", HttpStatusCode.OK, EmptySearchJson));
        _clock.Now = StartupTime.AddMinutes(31);

        Assert.IsFalse(await catalog.RefreshAsync(), "0 条不算一次成功的刷新。");
        Assert.AreEqual(2, catalog.GetBrowse().Count, "已有的列表不能被一次空响应清掉。");

        var warning = _diagnostics.Entries.Single(static entry =>
            entry.MessageKey == "Modrinth 浏览列表返回了 0 条（匹配总数 {0}），已保留上一次的 {1} 条结果");

        CollectionAssert.AreEqual(new[] { "0", "2" }, warning.Parameters.ToArray());
    }

    [TestMethod]
    public async Task WithNothingCachedAnEmptyResponseLeavesTheCatalogUnfetched()
    {
        var (catalog, _, _) = NewCatalog(new StubHandler().RespondTo("/search", HttpStatusCode.OK, EmptySearchJson));

        await catalog.RefreshAsync();

        // 从没取到过 → 界面说"联网后会自动获取"；而不是说"Modrinth 没有整合包"（那是编的）。
        Assert.IsFalse(catalog.HasFetched);
        Assert.AreEqual(0, catalog.GetBrowse().Count);
    }

    [TestMethod]
    public async Task ABadJsonResponseIsReportedAsAFailedRefresh()
    {
        var (catalog, _, _) = NewCatalog(new StubHandler().RespondTo("/search", HttpStatusCode.OK, "<html>not json</html>"));

        Assert.IsFalse(await catalog.RefreshAsync());

        var failure = _diagnostics.Entries.Single(static entry => entry.MessageKey == "获取 Modrinth 数据失败（{0}）：{1}");

        Assert.AreEqual("/v2/search", failure.Parameters[0]);
        Assert.AreEqual(
            "响应不是合法 JSON",
            failure.Parameters[1],
            "实际参数：" + string.Join(" / ", failure.Parameters));
    }

    // ---------------------------------------------------------------- 工具 ---

    [TestMethod]
    public void DownloadCountsAreShortenedTheWayTheDesignShowsThem()
    {
        Assert.AreEqual("18.6M", ModrinthText.Downloads(18_632_180));
        Assert.AreEqual("5.8M", ModrinthText.Downloads(5_800_000));
        Assert.AreEqual("1.2K", ModrinthText.Downloads(1_200));
        Assert.AreEqual("532", ModrinthText.Downloads(532));
        Assert.AreEqual("0", ModrinthText.Downloads(-1));
    }

    [TestMethod]
    public async Task TheVersionLookupIsBatched()
    {
        var ids = Enumerable.Range(1, ModrinthClient.MaxBatchIds + 1)
            .Select(index => "id" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        var handler = new StubHandler().RespondTo("/versions?", HttpStatusCode.OK, "[]");
        var factory = new FakeHttpClientFactory(handler);
        var client = new ModrinthClient(factory, _diagnostics);

        // 65 个 id → 两批（一批最多 64 个），而不是 65 次请求。
        Assert.AreEqual(0, (await client.GetVersionsAsync(ids)).Count);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public void VersionIdsAreUrlEncodedAsAJsonArray()
    {
        // 用 AbsoluteUri 而不是 ToString()：后者是"给人看"的形式，会把 %22 还原成裸引号。
        Assert.AreEqual(
            "https://api.modrinth.com/v2/versions?ids=%5B%22D35n5vLL%22%2C%22V2%22%5D",
            ModrinthClient.VersionsUri(["D35n5vLL", "V2"]).AbsoluteUri);
    }

    // ------------------------------------------------- 直接安装（S11-c） ---

    [TestMethod]
    public async Task PreparingAModpackPicksACompatibleVersionAndDownloadsThePrimaryFile()
    {
        var payload = "mrpack-bytes";
        var sha1 = Sha1Of(payload);
        var versionsJson = """
            [
              {"id":"v-old","project_id":"proj","version_number":"1.0","name":"Old Pack",
               "date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.1"],"loaders":["forge"],
               "files":[{"url":"https://cdn.modrinth.com/data/proj/versions/v-old/files/old.mrpack","filename":"old.mrpack","size":3,"primary":true,"hashes":{"sha1":"0000000000000000000000000000000000000000"}}]},
              {"id":"v-new","project_id":"proj","version_number":"2.0","name":"New Pack",
               "date_published":"2026-10-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"],
               "files":[{"url":"https://cdn.modrinth.com/data/proj/versions/v-new/files/pack.mrpack","filename":"pack.mrpack","size":11,"primary":true,"hashes":{"sha1":"SHA1"}}]}
            ]
            """.Replace("SHA1", sha1);

        var handler = new StubHandler()
            .RespondTo("/project/proj/version", HttpStatusCode.OK, versionsJson)
            .RespondTo("/files/pack.mrpack", HttpStatusCode.OK, payload);

        var (catalog, _, _) = NewCatalog(handler);
        var result = await catalog.PrepareModpackAsync(new ModrinthModpackRequest("proj", "1.20.4", "forge"));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.IsNotNull(result.Archive);
        StringAssert.EndsWith(result.Archive.ArchivePath, ".mrpack");
        Assert.AreEqual(payload, File.ReadAllText(result.Archive.ArchivePath));
        Assert.AreEqual("New Pack", result.Archive.Title);
        Assert.AreEqual(2, handler.Requests.Count, "一次版本查询 + 一次文件下载。");
    }

    [TestMethod]
    public async Task PreparingAModpackReportsWhenNoVersionMatches()
    {
        var versionsJson = """
            [{"id":"v1","project_id":"proj","version_number":"1.0","date_published":"2026-10-01T00:00:00Z",
              "game_versions":["1.20.1"],"loaders":["fabric"],
              "files":[{"url":"https://cdn.modrinth.com/data/proj/versions/v1/files/pack.mrpack","filename":"pack.mrpack","size":1,"primary":true,"hashes":{"sha1":"0000000000000000000000000000000000000000"}}]}]
            """;

        var handler = new StubHandler().RespondTo("/project/proj/version", HttpStatusCode.OK, versionsJson);
        var (catalog, _, _) = NewCatalog(handler);

        var result = await catalog.PrepareModpackAsync(new ModrinthModpackRequest("proj", "1.20.4", "forge"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Archive);
        StringAssert.Contains(result.Failure!.Key, "没有适配");
    }

    [TestMethod]
    public async Task ACorruptDownloadedArchiveIsRejected()
    {
        var versionsJson = """
            [{"id":"v1","project_id":"proj","version_number":"1.0","date_published":"2026-10-01T00:00:00Z",
              "game_versions":["1.20.4"],"loaders":["forge"],
              "files":[{"url":"https://cdn.modrinth.com/data/proj/versions/v1/files/pack.mrpack","filename":"pack.mrpack","size":4,"primary":true,"hashes":{"sha1":"1111111111111111111111111111111111111111"}}]}]
            """;

        var handler = new StubHandler()
            .RespondTo("/project/proj/version", HttpStatusCode.OK, versionsJson)
            .RespondTo("/files/pack.mrpack", HttpStatusCode.OK, "bad!");

        var (catalog, _, _) = NewCatalog(handler);
        var result = await catalog.PrepareModpackAsync(new ModrinthModpackRequest("proj", "1.20.4", "forge"));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure!.Key, "SHA-1");
    }

    private static string Sha1Of(string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    // -------------------------------------------------- S9-e：搜索与筛选 ---

    [TestMethod]
    public void FacetsCombineProjectTypeWithOnlyTheProvidedConditions()
    {
        Assert.AreEqual("[[\"project_type:modpack\"]]", ModrinthClient.BuildFacets(null, null));
        Assert.AreEqual(
            "[[\"project_type:modpack\"],[\"versions:1.20.4\"]]",
            ModrinthClient.BuildFacets("1.20.4", null));
        Assert.AreEqual(
            "[[\"project_type:modpack\"],[\"categories:fabric\"]]",
            ModrinthClient.BuildFacets(null, "fabric"));
        Assert.AreEqual(
            "[[\"project_type:modpack\"],[\"versions:1.20.4\"],[\"categories:fabric\"]]",
            ModrinthClient.BuildFacets("1.20.4", "fabric"));
    }

    [TestMethod]
    public void TheSortOptionChangesTheOfficialIndexParameter()
    {
        var uri = ModrinthClient.SearchUri("[[\"project_type:modpack\"]]", 6, 0, query: null, index: "downloads");
        StringAssert.Contains(uri.Query, "index=downloads");

        Assert.AreEqual(
            "downloads",
            ModrinthClient.SortIndex(ModrinthSort.Downloads));
    }
    [TestMethod]
    public void TheSearchUriOnlyCarriesQueryWhenItIsNotBlank()
    {
        Assert.IsFalse(
            ModrinthClient.SearchUri("[[\"project_type:modpack\"]]", 6, 0).Query.Contains("query=", StringComparison.Ordinal));
        StringAssert.Contains(
            ModrinthClient.SearchUri("[[\"project_type:modpack\"]]", 6, 0, "fabulously optimized").Query,
            "query=fabulously%20optimized");
    }

    [TestMethod]
    public async Task ASearchWithFiltersAsksModrinthWithQueryAndFacets()
    {
        var (catalog, handler, _) = NewCatalog(HappyPath());

        var rows = await catalog.SearchAsync(new ModrinthSearchQuery("fabulously", "1.20.4", "fabric"));

        Assert.IsNotNull(rows);
        Assert.AreEqual(2, rows.Count);

        var query = Uri.UnescapeDataString(handler.UriAt(0).Query);
        StringAssert.Contains(query, "query=fabulously");
        StringAssert.Contains(query, "versions:1.20.4");
        StringAssert.Contains(query, "categories:fabric");

        Assert.IsFalse(catalog.HasFetched, "筛选查询不写浏览快照，也不改变『取到过没有』这一态。");
    }

    [TestMethod]
    public async Task ASearchWithNoHitsReturnsAnEmptyListAndAFailureReturnsNull()
    {
        var empty = new StubHandler()
            .RespondTo("/search", HttpStatusCode.OK, EmptySearchJson)
            .RespondTo("/versions?", HttpStatusCode.OK, "[]");

        var (catalog, _, _) = NewCatalog(empty);
        var rows = await catalog.SearchAsync(new ModrinthSearchQuery("nothing", null, null));

        Assert.IsNotNull(rows, "查到了但没有匹配 → 空列表，不是失败。");
        Assert.AreEqual(0, rows.Count);

        var failing = new StubHandler().RespondTo("/search", HttpStatusCode.ServiceUnavailable, "{}");
        var (failingCatalog, _, _) = NewCatalog(failing);

        Assert.IsNull(
            await failingCatalog.SearchAsync(new ModrinthSearchQuery("anything", null, null)),
            "请求失败 → null，界面才能区分『没有匹配』与『没查到』。");
        Assert.IsTrue(_diagnostics.Entries.Count > 0, "失败要留下事件（客户端已经记；这里兜底再确认一次）。");
    }
}
