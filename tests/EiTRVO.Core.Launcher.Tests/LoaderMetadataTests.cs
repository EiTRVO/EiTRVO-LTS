using System.Net;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2 / B-10：加载器版本元数据（Fabric / Quilt / Forge / NeoForge）。
/// <para>
/// 四家三种形态，所以每个加载器都断言**完整 URL**（审计 §4.4：Neo 的 HTTP 测试从不断言地址，
/// 于是"请求发错了端点"这类缺陷没有回归保护）与**筛选逻辑**。
/// </para>
/// </summary>
[TestClass]
public sealed class LoaderMetadataTests
{
    private const string FabricJson = """
        [
          { "loader": { "separator": ":", "build": 15, "maven": "net.fabricmc:fabric-loader:0.15.7", "version": "0.15.7", "stable": true },
            "intermediary": { "maven": "net.fabricmc:intermediary:1.20.4", "version": "1.20.4", "stable": true },
            "launcherMeta": { "version": 2 } },
          { "loader": { "separator": ":", "build": 14, "maven": "net.fabricmc:fabric-loader:0.15.6", "version": "0.15.6", "stable": false },
            "intermediary": { "maven": "net.fabricmc:intermediary:1.20.4", "version": "1.20.4", "stable": true },
            "launcherMeta": { "version": 2 } }
        ]
        """;

    private const string QuiltJson = """
        [
          { "loader": { "separator": ":", "build": 42, "maven": "org.quiltmc:quilt-loader:0.27.1", "version": "0.27.1" } },
          { "loader": { "separator": ":", "build": 41, "maven": "org.quiltmc:quilt-loader:0.27.0", "version": "0.27.0" } }
        ]
        """;

    private const string ForgeXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata>
          <groupId>net.minecraftforge</groupId>
          <artifactId>forge</artifactId>
          <versioning>
            <latest>1.21.4-54.0.16</latest>
            <release>1.21.4-54.0.16</release>
            <versions>
              <version>1.7.10-10.13.0.1150</version>
              <version>1.7.10-10.13.4.1614</version>
              <version>1.7.10-10.13.4.1614-1.7.10</version>
              <version>1.8.9-11.15.1.2318-1.8.9</version>
              <version>1.20.2-48.1.0</version>
              <version>1.20.4-49.0.29</version>
              <version>1.20.4-49.0.30</version>
              <version>1.21.4-54.0.16</version>
            </versions>
            <lastUpdated>20250101000000</lastUpdated>
          </versioning>
        </metadata>
        """;

    private const string NeoForgeXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata>
          <groupId>net.neoforged</groupId>
          <artifactId>neoforge</artifactId>
          <versioning>
            <versions>
              <version>20.4.190</version>
              <version>20.4.237</version>
              <version>20.6.119</version>
              <version>21.1.72</version>
            </versions>
          </versioning>
        </metadata>
        """;

    private const string NeoForgeLegacyXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata>
          <groupId>net.neoforged</groupId>
          <artifactId>forge</artifactId>
          <versioning>
            <versions>
              <version>1.19.4-45.1.0</version>
              <version>1.20.1-47.1.79</version>
              <version>1.20.1-47.1.106</version>
            </versions>
          </versioning>
        </metadata>
        """;

    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录与固定时钟。</summary>
    [TestInitialize]
    public void CreatePaths()
    {
        _paths = new LauncherPaths(Path.Combine(Path.GetTempPath(), "eitrvo-core-loaders", Guid.NewGuid().ToString("N")));
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(new DateTimeOffset(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8)));
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeletePaths()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    private LoaderMetadataClient NewClient(StubHandler handler) =>
        new(new FakeHttpClientFactory(handler), _clock, _diagnostics);

    // ------------------------------------------------------------ Fabric ---

    [TestMethod]
    public async Task FabricIsAskedAtTheGameSpecificEndpoint()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, FabricJson);
        var client = NewClient(handler);

        var list = await client.GetAsync(LoaderKind.Fabric, "1.20.4");

        HttpAssert.RequestCount(handler, 1);
        HttpAssert.Url(handler, 0, "https://meta.fabricmc.net/v2/versions/loader/1.20.4");
        Assert.AreEqual(LoaderKind.Fabric, list.Kind);
        CollectionAssert.AreEqual(new[] { "0.15.7", "0.15.6" }, list.Versions.ToArray());
    }

    [TestMethod]
    public async Task FabricsStableFlagBecomesTheRecommendedVersion()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, FabricJson);

        var list = await NewClient(handler).GetAsync(LoaderKind.Fabric, "1.20.4");

        Assert.AreEqual("0.15.7", list.Recommended);
        Assert.AreEqual("0.15.7", list.Default);
    }

    [TestMethod]
    public async Task WithoutAStableFlagTheNewestVersionIsTheDefault()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, QuiltJson);

        var list = await NewClient(handler).GetAsync(LoaderKind.Quilt, "1.20.4");

        Assert.IsNull(list.Recommended);
        Assert.AreEqual("0.27.1", list.Default, "Quilt 不标稳定版，默认取最新。");
    }

    [TestMethod]
    public async Task QuiltListIsSortedNewestFirstEvenWhenTheApiIsNot()
    {
        // 实测 Quilt 的 API 顺序并不可靠（0.20.0-beta.9 后面跟着 beta.7 / beta.8）。
        // 下拉框第一项必须是真正的最新版，否则“默认选最新”就只是一句口号。
        const string unsorted = """
        [
          { "loader": { "separator": ":", "build": 9, "maven": "org.quiltmc:quilt-loader:0.20.0-beta.9", "version": "0.20.0-beta.9" } },
          { "loader": { "separator": ":", "build": 7, "maven": "org.quiltmc:quilt-loader:0.20.0-beta.7", "version": "0.20.0-beta.7" } },
          { "loader": { "separator": ":", "build": 8, "maven": "org.quiltmc:quilt-loader:0.20.0-beta.8", "version": "0.20.0-beta.8" } },
          { "loader": { "separator": ":", "build": 1, "maven": "org.quiltmc:quilt-loader:0.19.0", "version": "0.19.0" } }
        ]
        """;

        var handler = new StubHandler().Respond(HttpStatusCode.OK, unsorted);

        var list = await NewClient(handler).GetAsync(LoaderKind.Quilt, "1.20.4");

        CollectionAssert.AreEqual(
            new[] { "0.20.0-beta.9", "0.20.0-beta.8", "0.20.0-beta.7", "0.19.0" },
            list.Versions.ToArray(),
            "加载器版本下拉必须从新到旧排，第一项就是默认选中的最新版。");
        Assert.AreEqual("0.20.0-beta.9", list.Default);
    }

    [TestMethod]
    public async Task QuiltIsAskedAtItsOwnEndpoint()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, QuiltJson);

        _ = await NewClient(handler).GetAsync(LoaderKind.Quilt, "1.20.4");

        HttpAssert.Url(handler, 0, "https://meta.quiltmc.org/v3/versions/loader/1.20.4");
    }

    // ------------------------------------------------------------- Forge ---

    [TestMethod]
    public async Task ForgeIsFilteredByGameVersionAndListedNewestFirst()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ForgeXml);
        var client = NewClient(handler);

        var list = await client.GetAsync(LoaderKind.Forge, "1.20.4");

        HttpAssert.RequestCount(handler, 1);
        HttpAssert.Url(handler, 0, "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");

        // 只留 1.20.4 的，剥掉「游戏版本-」前缀，且最新在前（Maven 元数据是升序）。
        CollectionAssert.AreEqual(new[] { "49.0.30", "49.0.29" }, list.Versions.ToArray());
        Assert.AreEqual("49.0.30", list.Default);
    }

    [TestMethod]
    public async Task LegacyForgeMavenNamesAreStrippedToTheLoaderVersion()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ForgeXml);

        var list = await NewClient(handler).GetAsync(LoaderKind.Forge, "1.8.9");

        CollectionAssert.AreEqual(new[] { "11.15.1.2318" }, list.Versions.ToArray());
    }

    [TestMethod]
    public async Task Forge1710HidesTheBuildsThatCrashOnJava8()
    {
        // 10.13.0.1150 的 FML 在 Java 8 上会抛 ConcurrentModificationException
        // （Collections.sort 改了 Tweaks 列表的 modCount）；10.13.4.1614 修好了。
        // 把坏构建留在列表里只会让用户装出一个"启动就崩"的实例。
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ForgeXml);

        var list = await NewClient(handler).GetAsync(LoaderKind.Forge, "1.7.10");

        CollectionAssert.AreEqual(new[] { "10.13.4.1614" }, list.Versions.ToArray());
        Assert.AreEqual("10.13.4.1614", list.Default);
    }

    [TestMethod]
    public async Task AForgeGameVersionWithNoBuildsIsAnEmptyListNotAnError()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ForgeXml);

        var list = await NewClient(handler).GetAsync(LoaderKind.Forge, "1.2.5");

        Assert.IsFalse(list.IsAvailable);
        Assert.IsNull(list.Default);
    }

    // ---------------------------------------------------------- NeoForge ---

    [TestMethod]
    [DataRow("1.20.4", "20.4.")]
    [DataRow("1.20.2", "20.2.")]
    [DataRow("1.20.6", "20.6.")]
    [DataRow("1.21", "21.0.")]
    [DataRow("1.21.1", "21.1.")]
    [DataRow("1.7.10", null)]
    [DataRow("24w03a", null)]
    [DataRow("1.19.4", null)]
    public void TheNeoForgeSeriesIsDerivedFromTheGameVersion(string gameVersion, string? expected) =>
        Assert.AreEqual(expected, LoaderMetadataClient.NeoForgeSeries(gameVersion));

    [TestMethod]
    public async Task NeoForgeIsFilteredByTheDerivedSeriesAndKeepsFullVersionNumbers()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, NeoForgeXml);
        var client = NewClient(handler);

        var list = await client.GetAsync(LoaderKind.NeoForge, "1.20.4");

        HttpAssert.RequestCount(handler, 1);
        HttpAssert.Url(handler, 0, "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");

        // NeoForge 的版本号里不含游戏版本，所以原样保留；系列号由游戏版本推出。
        CollectionAssert.AreEqual(new[] { "20.4.237", "20.4.190" }, list.Versions.ToArray());
    }

    [TestMethod]
    public async Task NeoForgeOn1201UsesTheLegacyArtifactAndVersionScheme()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, NeoForgeLegacyXml);
        var client = NewClient(handler);

        var list = await client.GetAsync(LoaderKind.NeoForge, "1.20.1");

        HttpAssert.Url(handler, 0, "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml");
        CollectionAssert.AreEqual(new[] { "47.1.106", "47.1.79" }, list.Versions.ToArray());
    }

    [TestMethod]
    public async Task AVanillaOnlyGameVersionIsNotEvenAskedForNeoForge()
    {
        // 快照 / 1.19.4 之类还没有 NeoForge 系列：不发一次注定 404 的请求。
        var handler = new StubHandler();
        var list = await NewClient(handler).GetAsync(LoaderKind.NeoForge, "24w03a");

        HttpAssert.RequestCount(handler, 0);
        Assert.IsFalse(list.IsAvailable);
    }

    // ------------------------------------------------- 原版 / OptiFine ---

    [TestMethod]
    public async Task VanillaAndOptiFineHaveNoLoaderVersionListAndSendNoRequest()
    {
        var handler = new StubHandler();
        var client = NewClient(handler);

        Assert.IsFalse((await client.GetAsync(LoaderKind.Vanilla, "1.20.4")).IsAvailable);

        // OptiFine 的处置属于决策项 Q1：这里如实返回空，不猜、也不假装有版本列表。
        Assert.IsFalse((await client.GetAsync(LoaderKind.OptiFine, "1.20.4")).IsAvailable);

        HttpAssert.RequestCount(handler, 0);
    }

    // ------------------------------------------------------ 失败与缓存 ---

    [TestMethod]
    public async Task AServerErrorIsReportedAndNotSilentlyTreatedAsAnEmptyList()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.InternalServerError, "boom");
        var client = NewClient(handler);

        await Assert.ThrowsExceptionAsync<VersionDataException>(() =>
            client.GetAsync(LoaderKind.Fabric, "1.20.4"));

        var entry = _diagnostics.Entries.Single();
        Assert.AreEqual(LauncherEventLevel.Warning, entry.Level);
        Assert.AreEqual(DiagnosticsSources.ModLoader, entry.Source);
    }

    [TestMethod]
    public async Task AResponseThatIsNotJsonIsReported()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "<html>nope</html>");

        await Assert.ThrowsExceptionAsync<VersionDataException>(() =>
            NewClient(handler).GetAsync(LoaderKind.Fabric, "1.20.4"));
    }

    [TestMethod]
    public async Task AMavenMetadataThatIsNotXmlIsReported()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "not xml at all");

        await Assert.ThrowsExceptionAsync<VersionDataException>(() =>
            NewClient(handler).GetAsync(LoaderKind.Forge, "1.20.4"));
    }

    [TestMethod]
    public async Task ResultsAreCachedForAWhileSoTheWizardDoesNotHammerTheEndpoints()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, FabricJson)
            .Respond(HttpStatusCode.OK, FabricJson);

        var client = NewClient(handler);

        _ = await client.GetAsync(LoaderKind.Fabric, "1.20.4");
        _ = await client.GetAsync(LoaderKind.Fabric, "1.20.4");
        HttpAssert.RequestCount(handler, 1);

        // 过期之后重新问。
        _clock.Now = _clock.Now.AddMinutes(11);
        _ = await client.GetAsync(LoaderKind.Fabric, "1.20.4");
        HttpAssert.RequestCount(handler, 2);
    }

    [TestMethod]
    public async Task AnUnsafeGameVersionIsRejectedBeforeAnyRequest()
    {
        var handler = new StubHandler();
        var client = NewClient(handler);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            client.GetAsync(LoaderKind.Fabric, "1.20.4/../../evil"));

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            client.GetAsync(LoaderKind.Fabric, "1.20.4?x=1"));

        HttpAssert.RequestCount(handler, 0);
    }

    [TestMethod]
    public async Task GetAllAsksEveryLoaderOnceAndKeysThemByKind()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, FabricJson)
            .Respond(HttpStatusCode.OK, QuiltJson)
            .Respond(HttpStatusCode.OK, ForgeXml)
            .Respond(HttpStatusCode.OK, NeoForgeXml);

        var all = await NewClient(handler).GetAllAsync("1.20.4");

        HttpAssert.RequestCount(handler, 4);
        Assert.AreEqual(4, all.Count);
        Assert.IsTrue(all[LoaderKind.Fabric].IsAvailable);
        Assert.IsTrue(all[LoaderKind.Quilt].IsAvailable);
        Assert.IsTrue(all[LoaderKind.Forge].IsAvailable);
        Assert.IsTrue(all[LoaderKind.NeoForge].IsAvailable);
    }
}
