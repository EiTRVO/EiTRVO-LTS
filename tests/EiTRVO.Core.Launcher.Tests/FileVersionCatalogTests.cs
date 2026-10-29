using System.Net;
using System.Text;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2：真实 <see cref="IVersionCatalog"/> —— 清单计数与行、本地安装状态、当前行、
/// 加载器选项，以及"联网失败仍要能用"的降级路径。
/// </summary>
[TestClass]
public sealed class FileVersionCatalogTests
{
    /// <summary>两个正式版 + 一个快照 + 一个 <c>old_alpha</c>（最后一个不算进计数）。</summary>
    private const string ManifestJson = """
        {
          "latest": { "release": "1.21.4", "snapshot": "24w03a" },
          "versions": [
            { "id": "1.21.4", "type": "release", "url": "https://piston-meta.mojang.com/v1/packages/aaaa/1.21.4.json",
              "time": "2024-12-03T10:00:00+00:00", "releaseTime": "2024-12-03T09:00:00+00:00", "sha1": "aaaa" },
            { "id": "24w03a", "type": "snapshot", "url": "https://piston-meta.mojang.com/v1/packages/dddd/24w03a.json",
              "time": "2024-01-17T15:04:29+00:00", "releaseTime": "2024-01-17T15:00:00+00:00", "sha1": "dddd" },
            { "id": "1.20.4", "type": "release", "url": "https://piston-meta.mojang.com/v1/packages/bbbb/1.20.4.json",
              "time": "2023-12-07T12:00:00+00:00", "releaseTime": "2023-12-07T11:00:00+00:00", "sha1": "bbbb" },
            { "id": "a1.2.6", "type": "old_alpha", "url": "https://piston-meta.mojang.com/v1/packages/cccc/a1.2.6.json",
              "time": "2013-09-20T12:00:00+00:00", "releaseTime": "2010-11-30T12:00:00+00:00", "sha1": "cccc" }
          ]
        }
        """;

    private const string FabricJson = """
        [ { "loader": { "version": "0.15.7", "stable": true } },
          { "loader": { "version": "0.15.6", "stable": false } } ]
        """;

    private const string QuiltJson = """
        [ { "loader": { "version": "0.27.1" } } ]
        """;

    private const string ForgeXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata><versioning><versions>
          <version>1.20.4-49.0.29</version>
          <version>1.20.4-49.0.30</version>
        </versions></versioning></metadata>
        """;

    private const string NeoForgeXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata><versioning><versions>
          <version>20.4.237</version>
        </versions></versioning></metadata>
        """;

    /// <summary>官方 downloads 页里属于 1.20.4 的三条链接（两个正式版 + 一个预览版）。</summary>
    private const string OptiFineHtml = """
        <a href="http://optifine.net/adloadx?f=OptiFine_1.20.4_HD_U_I6.jar">x</a>
        <a href="http://optifine.net/adloadx?f=OptiFine_1.20.4_HD_U_I8.jar">x</a>
        <a href="http://optifine.net/adloadx?f=preview_OptiFine_1.20.4_HD_U_I8_pre4.jar">x</a>
        """;

    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FileSettingsCatalog _settings = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录与游戏目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-versioncatalog", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();
        _settings = new FileSettingsCatalog(_paths, _diagnostics);
        _clock = new FixedClock(new DateTimeOffset(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8)));

        _settings.SaveGlobal(SettingsDefaults.CreateGlobal() with { GameDirectory = _gameDirectory });
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

    private FileVersionCatalog NewCatalog(StubHandler? handler = null, IJavaRuntimeCatalog? java = null, string optiFineHtml = "<html></html>")
    {
        var stub = handler ?? new StubHandler();
        var factory = new FakeHttpClientFactory(stub);

        // OptiFine 列表（M4-i）会在刷新加载器时被问一次。**最后注册**，所以用例自己先注册的
        // "/optifine/" 规则优先（桩按注册顺序取第一条命中的规则）。
        stub.RespondTo("/downloads", HttpStatusCode.OK, optiFineHtml);

        return new FileVersionCatalog(
            new VersionManifestClient(factory, _paths, _clock, _diagnostics),
            new LoaderMetadataClient(factory, _clock, _diagnostics),
            new OptiFineCatalogClient(factory, _diagnostics),
            new VersionJsonStore(factory, _paths, HttpHostPolicy.Default, _diagnostics),
            // 默认替身"还没检测过"：此时内核不许说"缺少 Java"（见下面的用例）。
            java ?? new FakeJavaRuntimeCatalog(),
            _settings,
            _clock,
            _diagnostics);
    }

    private void WriteCachedManifest(string json)
    {
        Directory.CreateDirectory(_paths.VersionCacheDirectory);
        File.WriteAllText(
            Path.Combine(_paths.VersionCacheDirectory, VersionManifestClient.CacheFileName),
            json,
            new UTF8Encoding(false));

        // 把文件时间也拨到**固定时钟**附近：内核的新鲜度判断是「文件写入时间 vs IClock」之差，
        // 两者不同源时用例会随"跑测试那一刻机器上的真实时间"而飘。
        SetCacheWrittenAt(_clock.Now.AddMinutes(-1));
    }

    /// <summary>
    /// 写一份**旧**缓存：内核有 15 分钟的新鲜度窗口，刚写的缓存会被判定为仍然新鲜而不去联网，
    /// 于是"拿缓存兜底"这类用例必须先让缓存过期。
    /// </summary>
    private void WriteStaleCachedManifest(string json)
    {
        WriteCachedManifest(json);
        SetCacheWrittenAt(_clock.Now.AddHours(-1));
    }

    private void SetCacheWrittenAt(DateTimeOffset writtenAt) =>
        File.SetLastWriteTimeUtc(
            Path.Combine(_paths.VersionCacheDirectory, VersionManifestClient.CacheFileName),
            writtenAt.UtcDateTime);

    private void MarkInstalled(string versionId, int? javaMajor = null)
    {
        var directory = Path.Combine(_gameDirectory, "versions", versionId);
        Directory.CreateDirectory(directory);
        var json = javaMajor is null
            ? "{\"id\":\"" + versionId + "\"}"
            : $$"""
              { "id": "{{versionId}}", "javaVersion": { "majorVersion": {{javaMajor}} } }
              """;
        File.WriteAllText(Path.Combine(directory, versionId + ".json"), json);
    }

    // --------------------------------------------------------- 当前版本（Q12 决策 (b)）---

    [TestMethod]
    public void TheUserCanPinWhichRowIsTheCurrentOne()
    {
        // 用户在 p05 选中一行后按「用所选版本新建实例」→ 内核把那一行记为当前版本，
        // 向导本来就是按"当前行"决定默认值的（NewInstanceViewModel.UseCurrentVersion）。
        WriteCachedManifest(ManifestJson);
        var catalog = NewCatalog();

        // 自动判断：没有已安装版本时是最新正式版。
        Assert.AreEqual("1.21.4", catalog.ActiveVersionId);
        Assert.IsTrue(catalog.GetVersions().Single(v => v.Id == "1.21.4").IsActive);

        catalog.ActiveVersionId = "1.20.4";

        Assert.AreEqual("1.20.4", catalog.ActiveVersionId);
        Assert.IsTrue(catalog.GetVersions().Single(v => v.Id == "1.20.4").IsActive);
        Assert.AreEqual(
            1,
            catalog.GetVersions().Count(static v => v.IsActive),
            "只能有一行是当前行（与加载器那行的「已选择」同一条规矩）。");

        // 写空串 = 交还给自动判断。
        catalog.ActiveVersionId = string.Empty;
        Assert.AreEqual("1.21.4", catalog.ActiveVersionId);
    }

    [TestMethod]
    public void PinningAVersionThatIsNotInTheManifestIsRefusedLoudly()
    {
        // 静默接受一个不存在的版本，界面会一行都不高亮，而调用方以为自己选中了东西。
        WriteCachedManifest(ManifestJson);
        var catalog = NewCatalog();

        Assert.ThrowsException<ArgumentException>(() => catalog.ActiveVersionId = "9.9.9");
        Assert.ThrowsException<ArgumentException>(() => catalog.ActiveVersionId = "../evil");
    }

    [TestMethod]
    public void SnapshotVersionsCanBePinnedToo()
    {
        // 快照也在表里，用户当然可以选它（自动判断只会落到正式版，但显式选择不该被挡）。
        WriteCachedManifest(ManifestJson);
        var catalog = NewCatalog();

        catalog.ActiveVersionId = "24w03a";

        Assert.AreEqual("24w03a", catalog.ActiveVersionId);
        Assert.IsTrue(catalog.GetVersions().Single(v => v.Id == "24w03a").IsActive);
    }

    // --------------------------------------------------------- 清单与行 ---

    [TestMethod]
    public void TheCatalogStartsFromTheDiskCacheSoItWorksOffline()
    {
        WriteCachedManifest(ManifestJson);

        var catalog = NewCatalog();

        Assert.AreEqual(3, catalog.TotalCount, "old_alpha 不算进设计稿的 917 这个数。");
        Assert.AreEqual(2, catalog.ReleaseCount);
        Assert.AreEqual(1, catalog.SnapshotCount);
    }

    [TestMethod]
    public void AnEmptyCacheMeansAnEmptyListRatherThanACrash()
    {
        var catalog = NewCatalog();

        Assert.AreEqual(0, catalog.TotalCount);
        Assert.AreEqual(0, catalog.GetVersions().Count);
    }

    [TestMethod]
    public void RowsCarryKindDateAndTheInferredJavaRequirement()
    {
        WriteCachedManifest(ManifestJson);

        var rows = NewCatalog().GetVersions();

        var latest = rows.Single(r => r.Id == "1.21.4");
        Assert.AreEqual(VersionKind.Release, latest.Kind);
        Assert.AreEqual("2024/12/03", latest.ReleaseDate);
        Assert.AreEqual("Java 21", latest.RequiredJava);
        Assert.AreEqual(VersionLocalState.NotInstalled, latest.LocalState);
        Assert.AreEqual("未安装", TestText.Of(latest.LocalStateText));

        Assert.AreEqual("Java 17", rows.Single(r => r.Id == "1.20.4").RequiredJava);
    }

    [TestMethod]
    [DataRow("1.20.4", "Java 17")]
    [DataRow("1.20.5", "Java 21")]
    [DataRow("1.18.2", "Java 17")]
    [DataRow("1.12.2", "Java 8")]
    [DataRow("26.3", "Java 25")]
    public void TheJavaColumnComesFromTheRuleTableNotFromNineHundredRequests(string id, string expected)
    {
        WriteCachedManifest(
            $$"""
            { "latest": { "release": "{{id}}" },
              "versions": [ { "id": "{{id}}", "type": "release", "url": "https://piston-meta.mojang.com/x.json",
                              "releaseTime": "2024-01-01T00:00:00+00:00" } ] }
            """);

        Assert.AreEqual(expected, NewCatalog().GetVersions().Single().RequiredJava);
    }

    [TestMethod]
    public void InstalledVersionsAreMarkedFromTheRealGameDirectory()
    {
        WriteCachedManifest(ManifestJson);
        MarkInstalled("1.20.4");

        var rows = NewCatalog().GetVersions();

        Assert.AreEqual(VersionLocalState.Installed, rows.Single(r => r.Id == "1.20.4").LocalState);
        Assert.AreEqual("已安装", TestText.Of(rows.Single(r => r.Id == "1.20.4").LocalStateText));
        Assert.AreEqual(VersionLocalState.NotInstalled, rows.Single(r => r.Id == "1.21.4").LocalState);
    }

    [TestMethod]
    public void TheActiveRowIsTheNewestInstalledVersionNotTheNewestRelease()
    {
        // 设计稿高亮的是 1.20.4（本机装着的），而清单里最新的是 1.21.4。
        WriteCachedManifest(ManifestJson);
        MarkInstalled("1.20.4");

        var rows = NewCatalog().GetVersions();

        Assert.IsTrue(rows.Single(r => r.Id == "1.20.4").IsActive);
        Assert.IsFalse(rows.Single(r => r.Id == "1.21.4").IsActive);
    }

    // ------------------------------------------------- M3：所需 Java 与「缺少 Java」---

    [TestMethod]
    public void ADeclaredJavaVersionFromTheLocalJsonWinsOverTheRuleTable()
    {
        // M3 的规矩：本地有版本 JSON 时以它声明的 javaVersion 为准（那是 Mojang 自己写的答案），
        // 规则表只兜底。这里故意让解析结果不一致（清单里 1.20.4 的规则值是 17），
        // 声明的 21 必须赢 —— 用规则表覆盖声明值正是"猜"。
        WriteCachedManifest(ManifestJson);
        WriteCachedVersionJson("1.20.4", javaMajor: 21);

        var row = NewCatalog().GetVersions().Single(r => r.Id == "1.20.4");

        Assert.AreEqual("Java 21", row.RequiredJava);
    }

    [TestMethod]
    public void TheInstalledJsonIsPreferredOverTheCachedOne()
    {
        // 玩家用别的启动器装过的版本，其 JSON 可能被改过（加载器会改写它）——
        // 游戏目录里那一份才是权威。
        WriteCachedManifest(ManifestJson);
        WriteCachedVersionJson("1.20.4", javaMajor: 8);
        MarkInstalled("1.20.4", javaMajor: 17);

        Assert.AreEqual("Java 17", NewCatalog().GetVersions().Single(r => r.Id == "1.20.4").RequiredJava);
    }

    [TestMethod]
    public void AnInstalledVersionWithoutAUsableJavaIsMarkedAsSuch()
    {
        WriteCachedManifest(ManifestJson);
        MarkInstalled("1.20.4"); // 需要 Java 17

        // 本机只有 Java 8：装是装上了，但跑不起来。
        var rows = NewCatalog(java: new FakeJavaRuntimeCatalog([Java(8)], hasDetectionResult: true)).GetVersions();

        var row = rows.Single(r => r.Id == "1.20.4");
        Assert.AreEqual(VersionLocalState.MissingJava, row.LocalState);
        Assert.AreEqual("缺少 Java 17", TestText.Of(row.LocalStateText));
        Assert.IsFalse(row.CanCreateInstance, "缺 Java 的版本不该被当成「可以直接建实例」。");
    }

    [TestMethod]
    public void AnInstalledVersionWithAMatchingJavaStaysInstalled()
    {
        WriteCachedManifest(ManifestJson);
        MarkInstalled("1.20.4");

        var rows = NewCatalog(java: new FakeJavaRuntimeCatalog([Java(17)], hasDetectionResult: true)).GetVersions();

        Assert.AreEqual(VersionLocalState.Installed, rows.Single(r => r.Id == "1.20.4").LocalState);
        Assert.AreEqual("已安装", TestText.Of(rows.Single(r => r.Id == "1.20.4").LocalStateText));
    }

    [TestMethod]
    public void WithoutADetectionResultNothingIsCalledMissingJava()
    {
        // 「还没查过」不是「缺少」：M2 刻意没有伪造这一态，M3 也不能在检测结果出来之前就说缺。
        WriteCachedManifest(ManifestJson);
        MarkInstalled("1.20.4");

        var rows = NewCatalog(java: new FakeJavaRuntimeCatalog([], hasDetectionResult: false)).GetVersions();

        Assert.AreEqual(VersionLocalState.Installed, rows.Single(r => r.Id == "1.20.4").LocalState);
        Assert.AreEqual("已安装", TestText.Of(rows.Single(r => r.Id == "1.20.4").LocalStateText));
    }

    [TestMethod]
    public void AnOldVersionIsMissingWhenOnlyANewJavaIsAround()
    {
        // 1.12.2 用 LWJGL 2，Java 17 起不来 —— 只判下限会把它说成"已安装、可玩"。
        WriteCachedManifest(
            """
            { "latest": { "release": "1.12.2" },
              "versions": [ { "id": "1.12.2", "type": "release", "url": "https://piston-meta.mojang.com/x.json",
                              "releaseTime": "2017-09-18T00:00:00+00:00" } ] }
            """);
        MarkInstalled("1.12.2");

        var rows = NewCatalog(java: new FakeJavaRuntimeCatalog([Java(17)], hasDetectionResult: true)).GetVersions();

        Assert.AreEqual(VersionLocalState.MissingJava, rows.Single().LocalState);
        Assert.AreEqual("缺少 Java 8", TestText.Of(rows.Single().LocalStateText));
    }

    [TestMethod]
    public void NotInstalledVersionsAreNeverMissingJava()
    {
        // 「缺少 Java」的前提是"这个版本真的装在盘上"（见 VersionLocalState 的定义）。
        WriteCachedManifest(ManifestJson);

        var rows = NewCatalog(java: new FakeJavaRuntimeCatalog([], hasDetectionResult: true)).GetVersions();

        Assert.AreEqual(VersionLocalState.NotInstalled, rows.Single(r => r.Id == "1.20.4").LocalState);
        Assert.AreEqual("未安装", TestText.Of(rows.Single(r => r.Id == "1.20.4").LocalStateText));
    }

    /// <summary>写一份本启动器缓存里的版本 JSON（<c>cache\versions\&lt;id&gt;.json</c>）。</summary>
    private void WriteCachedVersionJson(string versionId, int javaMajor)
    {
        Directory.CreateDirectory(_paths.VersionCacheDirectory);
        File.WriteAllText(
            Path.Combine(_paths.VersionCacheDirectory, versionId + ".json"),
            $$"""
            { "id": "{{versionId}}", "javaVersion": { "component": "java-runtime-gamma", "majorVersion": {{javaMajor}} } }
            """,
            new UTF8Encoding(false));
    }

    private static JavaInstallation Java(int major) => new(
        ExecutablePath: $@"C:\Java\jdk-{major}\bin\javaw.exe",
        HomeDirectory: $@"C:\Java\jdk-{major}",
        VersionText: major == 8 ? "1.8.0_421" : major + ".0.9",
        MajorVersion: major,
        Vendor: "Eclipse Temurin",
        Bits: 64);

    [TestMethod]
    public void WithoutAnythingInstalledTheLatestReleaseIsTheActiveRow()
    {
        WriteCachedManifest(
            """
            { "latest": { "release": "1.21.4" },
              "versions": [
                { "id": "24w03a", "type": "snapshot", "url": "https://piston-meta.mojang.com/x.json", "releaseTime": "2024-01-17T00:00:00+00:00" },
                { "id": "1.21.4", "type": "release", "url": "https://piston-meta.mojang.com/y.json", "releaseTime": "2024-12-03T00:00:00+00:00" }
              ] }
            """);

        var rows = NewCatalog().GetVersions();

        Assert.IsTrue(rows.Single(r => r.Id == "1.21.4").IsActive, "没有已安装版本时高亮最新正式版，而不是最新的快照。");
    }

    // ------------------------------------------------------------ 刷新 ---

    [TestMethod]
    public async Task RefreshingFetchesAndReplacesTheCachedList()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ManifestJson);
        var catalog = NewCatalog(handler);
        Assert.AreEqual(0, catalog.TotalCount);

        await catalog.RefreshAsync();

        HttpAssert.Url(handler, 0, VersionManifestClient.ManifestUrl);
        Assert.AreEqual(3, catalog.TotalCount);
    }

    [TestMethod]
    public async Task AFailedRefreshKeepsUsingTheCacheAndSaysSo()
    {
        WriteStaleCachedManifest(ManifestJson);
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable, "down");
        var catalog = NewCatalog(handler);

        await catalog.RefreshAsync();

        Assert.AreEqual(3, catalog.TotalCount, "联网失败不该把表清空。");
        var entry = _diagnostics.Entries.Last();
        Assert.AreEqual(LauncherEventLevel.Warning, entry.Level);
        Assert.AreEqual("获取版本清单失败，已沿用本地缓存（{0}）", entry.MessageKey);
    }

    [TestMethod]
    public async Task AFreshCacheIsNotFetchedAgainJustBecauseTheUserReopenedThePage()
    {
        // p05 没有「刷新」按钮，刷新只能发生在导航到时；没有新鲜度窗口就会反复拉那份 300KB 的清单。
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, ManifestJson)
            .Respond(HttpStatusCode.OK, ManifestJson);

        var catalog = NewCatalog(handler);

        await catalog.RefreshAsync();
        HttpAssert.RequestCount(handler, 1);

        // 第一次刷新落了缓存；把它的写入时间对齐到固定时钟，这样"新鲜"是由用例说了算的。
        SetCacheWrittenAt(_clock.Now);

        await catalog.RefreshAsync();
        HttpAssert.RequestCount(handler, 1);

        // 窗口过期后重新问。
        _clock.Now = _clock.Now.AddMinutes(16);
        await catalog.RefreshAsync();
        HttpAssert.RequestCount(handler, 2);
    }

    [TestMethod]
    public async Task AStaleCacheIsRefreshedOnTheNextVisit()
    {
        WriteStaleCachedManifest(ManifestJson);
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ManifestJson);

        await NewCatalog(handler).RefreshAsync();

        HttpAssert.RequestCount(handler, 1);
    }

    [TestMethod]
    public async Task AFailedRefreshWithoutACacheReportsThatThereIsNothingToFallBackTo()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable, "down");
        var catalog = NewCatalog(handler);

        await catalog.RefreshAsync();

        Assert.AreEqual(0, catalog.TotalCount);
        Assert.AreEqual("获取版本清单失败，且本地没有缓存（{0}）", _diagnostics.Entries.Last().MessageKey);
    }

    // ---------------------------------------------------------- 加载器 ---

    [TestMethod]
    public void BeforeAnyRefreshTheLoaderOptionsAreHonestlyUnknown()
    {
        WriteCachedManifest(ManifestJson);

        var options = NewCatalog().GetLoaders("1.20.4");

        Assert.AreEqual(6, options.Count);
        Assert.AreEqual("原版", TestText.Of(options[0].Display));
        Assert.AreEqual(LoaderKind.Vanilla, options[0].Kind);
        Assert.AreEqual("点击后获取版本", TestText.Of(options[1].Detail));
        Assert.AreEqual(LoaderAvailability.Available, options[1].Availability, "占位行要能点，点中之后才单独取这一家。");

        // OptiFine 与别家一样：先给可点击的占位项，选中后再单独取它的构建列表。
        Assert.AreEqual("点击后获取版本", TestText.Of(options[5].Detail));
        Assert.AreEqual(LoaderAvailability.Available, options[5].Availability);
    }

    [TestMethod]
    public async Task RefreshingLoadersFillsEveryOptionFromTheRealEndpoints()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, FabricJson)
            .Respond(HttpStatusCode.OK, QuiltJson)
            .Respond(HttpStatusCode.OK, ForgeXml)
            .Respond(HttpStatusCode.OK, NeoForgeXml)
            // OptiFine（M4-i）：列表来自官方 downloads 页。
            .RespondTo("/downloads", HttpStatusCode.OK, OptiFineHtml);

        var catalog = NewCatalog(handler);
        await catalog.RefreshLoadersAsync("1.20.4");

        HttpAssert.RequestCount(handler, 5);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "https://meta.fabricmc.net/v2/versions/loader/1.20.4",
                "https://meta.quiltmc.org/v3/versions/loader/1.20.4",
                LoaderMetadataClient.ForgeMetadataUrl,
                LoaderMetadataClient.NeoForgeMetadataUrl,
                "https://optifine.net/downloads",
            },
            handler.Requests.Select(static request => request.RequestUri!.ToString()).ToArray(),
            "四家加载器与 OptiFine 是并发请求的，顺序不固定，但五个地址都应当出现。");

        var options = catalog.GetLoaders("1.20.4");

        var fabric = options.Single(o => o.Kind == LoaderKind.Fabric);
        Assert.AreEqual(LoaderAvailability.Available, fabric.Availability, "Fabric 可用，但它不是选中项。");
        Assert.AreEqual("0.15.7 · 轻量 · 更新快", TestText.Of(fabric.Detail));

        // 版本列表只装**纯版本号**，「（推荐）」是显示文本（由 App 层拼）——见 LoaderOption.RecommendedVersion。
        CollectionAssert.AreEqual(new[] { "0.15.7", "0.15.6" }, fabric.Versions.ToArray());
        Assert.AreEqual("0.15.7", fabric.RecommendedVersion);
        Assert.IsFalse(fabric.Versions.Any(static v => v.Contains('（')), "版本段里不许有装饰，它会原样进安装请求。");

        var forge = options.Single(o => o.Kind == LoaderKind.Forge);
        Assert.IsTrue(forge.IsActive, "设计稿把 Forge 画成选中项。");
        Assert.AreEqual(LoaderAvailability.Recommended, forge.Availability);
        Assert.AreEqual("49.0.30", forge.RecommendedVersion);

        // 「已选择」是选中态，全列表只能有一个 —— 否则界面上会出现两行都写着「已选择」。
        Assert.AreEqual(
            1,
            options.Count(static o => o.Availability == LoaderAvailability.Recommended),
            "「已选择」只能有一个。");

        var quilt = options.Single(o => o.Kind == LoaderKind.Quilt);
        Assert.AreEqual(LoaderAvailability.Available, quilt.Availability);

        Assert.AreEqual("20.4.237 · Forge 的新一代", TestText.Of(options.Single(o => o.Kind == LoaderKind.NeoForge).Detail));

        // OptiFine：只列正式版、最新在前；预览版只报数量（它的版本段带后缀，不适合直接塞进下拉框）。
        var optiFine = options.Single(o => o.Kind == LoaderKind.OptiFine);
        Assert.AreEqual(LoaderAvailability.Available, optiFine.Availability);
        CollectionAssert.AreEqual(new[] { "HD_U_I8", "HD_U_I6" }, optiFine.Versions.ToArray());
        Assert.AreEqual("HD_U_I8 · 光影与画质增强（另有 1 个预览版未列出）", TestText.Of(optiFine.Detail));
    }

    [TestMethod]
    public async Task ALoaderWithoutAVersionForThatGameVersionIsMarkedUnavailable()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, FabricJson)
            .Respond(HttpStatusCode.OK, QuiltJson)
            .Respond(HttpStatusCode.OK, """<?xml version="1.0"?><metadata><versioning><versions><version>1.21.4-54.0.16</version></versions></versioning></metadata>""")
            .Respond(HttpStatusCode.OK, NeoForgeXml);

        var catalog = NewCatalog(handler);
        await catalog.RefreshLoadersAsync("1.20.4");

        var options = catalog.GetLoaders("1.20.4");
        var forge = options.Single(o => o.Kind == LoaderKind.Forge);

        Assert.AreEqual(LoaderAvailability.Unavailable, forge.Availability);
        Assert.AreEqual("该游戏版本没有对应版本", TestText.Of(forge.Detail));
        Assert.IsFalse(forge.IsActive, "不可用的加载器不该被选中。");
        Assert.IsTrue(
            options.Count(static o => o.IsActive) == 1,
            "选中位要让给别的可用加载器，向导不能开局没有选中项。");
        Assert.IsTrue(options.Single(o => o.Kind == LoaderKind.Fabric).IsActive, "Forge 不可用时让给第一个可用的加载器。");
    }

    [TestMethod]
    public async Task AFailedLoaderEndpointDoesNotHideTheLoadersThatAnswered()
    {
        // Forge 的 Maven 端点返回 500：其它三家仍然要可选，而且 Forge 要如实写"获取失败"，
        // 不能把"网络失败"伪装成"该游戏版本没有对应版本"。
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, FabricJson)
            .Respond(HttpStatusCode.OK, QuiltJson)
            .Respond(HttpStatusCode.InternalServerError, "boom")
            .Respond(HttpStatusCode.OK, NeoForgeXml);

        var catalog = NewCatalog(handler);
        await catalog.RefreshLoadersAsync("1.20.4");

        var options = catalog.GetLoaders("1.20.4");
        Assert.AreNotEqual(LoaderAvailability.Unavailable, options.Single(o => o.Kind == LoaderKind.Fabric).Availability);
        Assert.AreNotEqual(LoaderAvailability.Unavailable, options.Single(o => o.Kind == LoaderKind.Quilt).Availability);
        Assert.AreNotEqual(LoaderAvailability.Unavailable, options.Single(o => o.Kind == LoaderKind.NeoForge).Availability);

        var forge = options.Single(o => o.Kind == LoaderKind.Forge);
        Assert.AreEqual(LoaderAvailability.Failed, forge.Availability);
        Assert.AreEqual("获取加载器版本失败（网络）", TestText.Of(forge.Detail));
        Assert.IsTrue(options.Single(o => o.Kind == LoaderKind.Fabric).IsActive, "Forge 失败时让给第一个可用的加载器。");
        Assert.AreEqual("获取 {0} 的加载器列表失败（{1}），这一项本次不可用", _diagnostics.Entries.Last().MessageKey);
    }
    [TestMethod]
    public void TheWizardStepsAreTheFourFromTheDesign()
    {
        var steps = NewCatalog().GetWizardSteps();

        CollectionAssert.AreEqual(
            new[] { "游戏版本", "模组加载器", "名称与目录", "确认并安装" },
            steps.Select(static s => TestText.Of(s.Title)).ToArray());
    }
}
