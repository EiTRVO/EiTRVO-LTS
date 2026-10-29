using System.Net;
using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2：版本 JSON 的读取顺序、坏文件容错，以及"按远端给的地址取 JSON"必须守住出站策略。
/// </summary>
[TestClass]
public sealed class VersionJsonStoreTests
{
    private const string LoaderJson = """
        {
          "id": "1.20.4-forge-49.0.30",
          "inheritsFrom": "1.20.4",
          "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "libraries": [ { "name": "net.minecraftforge:forge:1.20.4-49.0.30" } ],
          "arguments": { "game": [ "--launchTarget", "forge_client" ] }
        }
        """;

    private const string VanillaJson = """
        {
          "id": "1.20.4",
          "mainClass": "net.minecraft.client.main.Main",
          "assets": "12",
          "assetIndex": { "id": "12", "url": "https://piston-meta.mojang.com/v1/packages/aaaa/12.json" },
          "downloads": { "client": { "url": "https://piston-data.mojang.com/v1/objects/bbbb/client.jar", "sha1": "bbbb", "size": 100 } },
          "javaVersion": { "component": "java-runtime-gamma", "majorVersion": 17 },
          "libraries": [ { "name": "com.mojang:authlib:6.0.54" } ],
          "arguments": { "game": [ "--username", "${auth_player_name}" ] }
        }
        """;

    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一个全新的数据目录与游戏目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-versionjson", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();
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

    private VersionJsonStore NewStore(StubHandler? handler = null) =>
        new(new FakeHttpClientFactory(handler ?? new StubHandler()),
            _paths,
            HttpHostPolicy.Default,
            _diagnostics);

    private void WriteInstalled(string versionId, string json)
    {
        var directory = Path.Combine(_gameDirectory, "versions", versionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, versionId + ".json"), json, new UTF8Encoding(false));
    }

    private void WriteCached(string versionId, string json)
    {
        Directory.CreateDirectory(_paths.VersionCacheDirectory);
        File.WriteAllText(Path.Combine(_paths.VersionCacheDirectory, versionId + ".json"), json, new UTF8Encoding(false));
    }

    // ------------------------------------------------------- 读取顺序 ---

    [TestMethod]
    public void AnInstalledVersionIsReadFromTheGameDirectory()
    {
        WriteInstalled("1.20.4", VanillaJson);

        var version = NewStore().TryReadInstalled(_gameDirectory, "1.20.4");

        Assert.AreEqual("1.20.4", version?.Id);
        Assert.AreEqual("net.minecraft.client.main.Main", version?.MainClass);
        Assert.AreEqual(17, version?.JavaVersion?.MajorVersion);
    }

    [TestMethod]
    public void TheInstalledCopyWinsOverOurCache()
    {
        // 玩家用别的启动器装过、或自己改过的那一份才算数。
        WriteInstalled("1.20.4", VanillaJson);
        WriteCached("1.20.4", VanillaJson.Replace("net.minecraft.client.main.Main", "some.other.Main", StringComparison.Ordinal));

        var version = NewStore().Load(_gameDirectory, "1.20.4");

        Assert.AreEqual("net.minecraft.client.main.Main", version?.MainClass);
    }

    [TestMethod]
    public void WithoutAnInstalledCopyTheCacheIsUsed()
    {
        WriteCached("1.20.4", VanillaJson);

        var version = NewStore().Load(_gameDirectory, "1.20.4");

        Assert.AreEqual("1.20.4", version?.Id);
    }

    [TestMethod]
    public void WithoutEitherCopyTheResultIsNull()
    {
        var store = NewStore();

        Assert.IsNull(store.TryReadInstalled(_gameDirectory, "1.20.4"));
        Assert.IsNull(store.TryReadCached("1.20.4"));
        Assert.IsNull(store.Load(_gameDirectory, "1.20.4"));
    }

    [TestMethod]
    public void TheFileIdWinsOverWhateverTheJsonClaimsToBe()
    {
        // 一份自称是 1.20.4 的文件放在 loader 目录里：它不该能借别人的名字扰乱继承链。
        WriteInstalled("my-loader", VanillaJson);

        var version = NewStore().Load(_gameDirectory, "my-loader");

        Assert.AreEqual("my-loader", version?.Id);
    }

    [TestMethod]
    public void TheNeoStyleVersionJsonIsReadBeforeTheLegacyIdJson()
    {
        // 新布局（Neo 26.1）：实例目录里的 version.json 是权威的那一份。
        // 同目录的 <实例名>.json 只是兼容旧读法，两处都有时必须优先 version.json。
        var directory = Path.Combine(_gameDirectory, "versions", "my-instance");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "version.json"),
            VanillaJson.Replace("net.minecraft.client.main.Main", "neo.style.Main", StringComparison.Ordinal),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(directory, "my-instance.json"),
            VanillaJson.Replace("net.minecraft.client.main.Main", "legacy.style.Main", StringComparison.Ordinal),
            new UTF8Encoding(false));

        var version = NewStore().TryReadInstalled(_gameDirectory, "my-instance");

        Assert.AreEqual("neo.style.Main", version?.MainClass);
        Assert.AreEqual("my-instance", version?.Id, "目录名才是版本 id（文件里写的 id 不算数）。");
    }

    [TestMethod]
    public void AnInstanceVersionJsonWithInheritsFromStillResolvesItsParent()
    {
        // 加载器安装器写的是"增量 JSON + inheritsFrom"：实例目录里的 version.json 也走同一条继承链。
        WriteInstalled("1.20.4", VanillaJson);
        var directory = Path.Combine(_gameDirectory, "versions", "forge-instance");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "version.json"), LoaderJson, new UTF8Encoding(false));

        var resolved = NewStore().Resolve(_gameDirectory, "forge-instance");

        Assert.AreEqual("1.20.4", resolved.BaseVersionId);
        Assert.AreEqual("cpw.mods.bootstraplauncher.BootstrapLauncher", resolved.Merged.MainClass);
        CollectionAssert.AreEqual(new[] { "forge-instance", "1.20.4" }, resolved.Chain.ToArray());
    }

    // --------------------------------------------------------- 坏文件 ---

    [TestMethod]
    public void ACorruptVersionJsonIsTreatedAsMissingAndKeptOnDisk()
    {
        WriteInstalled("1.20.4", "{\"mainClass\": ");
        var store = NewStore();

        Assert.IsNull(store.Load(_gameDirectory, "1.20.4"));
        Assert.AreEqual(
            "{\"mainClass\": ",
            File.ReadAllText(Path.Combine(_gameDirectory, "versions", "1.20.4", "1.20.4.json"), Encoding.UTF8));
        Assert.AreEqual(LauncherEventLevel.Warning, _diagnostics.Entries.Single().Level);

        // 同一个坏文件只报一次。
        store.Load(_gameDirectory, "1.20.4");
        Assert.AreEqual(1, _diagnostics.Entries.Count);
    }

    // ------------------------------------------------- 远端数据的边界 ---

    [TestMethod]
    [DataRow("../evil")]
    [DataRow("sub/dir")]
    [DataRow("CON")]
    [DataRow("")]
    public void AnUnsafeVersionIdIsRejectedBeforeTouchingTheDisk(string versionId)
    {
        var store = NewStore();

        Assert.ThrowsException<ArgumentException>(() => store.CachedFile(versionId));
        Assert.ThrowsException<ArgumentException>(() => store.TryReadCached(versionId));
        Assert.ThrowsException<ArgumentException>(() => store.TryReadInstalled(_gameDirectory, versionId));
        Assert.ThrowsException<ArgumentException>(() => store.Resolve(_gameDirectory, versionId));
    }

    [TestMethod]
    public async Task FetchingChecksTheHostPolicyAndSendsNothingWhenItFails()
    {
        var handler = new StubHandler();
        var store = NewStore(handler);

        // 清单是远端数据：它给的地址如果不在白名单里，一个请求都不该发出去。
        await Assert.ThrowsExceptionAsync<HttpPolicyException>(() =>
            store.FetchAsync("1.20.4", "https://evil.example/1.20.4.json"));

        HttpAssert.RequestCount(handler, 0);
    }

    [TestMethod]
    public async Task FetchingAnUnparseableUrlIsRejected()
    {
        var handler = new StubHandler();

        await Assert.ThrowsExceptionAsync<VersionDataException>(() =>
            NewStore(handler).FetchAsync("1.20.4", "not a url"));

        HttpAssert.RequestCount(handler, 0);
    }

    [TestMethod]
    public async Task FetchingCachesTheResponseVerbatimAndRemembersItsUrl()
    {
        const string Url = "https://piston-meta.mojang.com/v1/packages/bbbb/1.20.4.json";
        var handler = new StubHandler().Respond(HttpStatusCode.OK, VanillaJson);
        var store = NewStore(handler);

        var version = await store.FetchAsync("1.20.4", Url);

        HttpAssert.RequestCount(handler, 1);
        HttpAssert.Url(handler, 0, Url);
        Assert.AreEqual(Url, version.SourceUrl);
        Assert.AreEqual(VanillaJson, File.ReadAllText(store.CachedFile("1.20.4"), Encoding.UTF8));
    }

    [TestMethod]
    public async Task AFailedFetchDoesNotOverwriteTheCache()
    {
        WriteCached("1.20.4", VanillaJson);
        var store = NewStore(new StubHandler().Respond(HttpStatusCode.NotFound));

        await Assert.ThrowsExceptionAsync<VersionDataException>(() =>
            store.FetchAsync("1.20.4", "https://piston-meta.mojang.com/v1/packages/bbbb/1.20.4.json"));

        Assert.AreEqual(VanillaJson, File.ReadAllText(store.CachedFile("1.20.4"), Encoding.UTF8));
    }

    // ---------------------------------------------------- 继承链解析 ---

    [TestMethod]
    public void TheChainIsResolvedFromLocalFilesOnly()
    {
        WriteInstalled("1.20.4-forge-49.0.30", LoaderJson);
        WriteInstalled("1.20.4", VanillaJson);
        var store = NewStore();

        var resolved = store.Resolve(_gameDirectory, "1.20.4-forge-49.0.30");

        Assert.AreEqual("1.20.4", resolved.BaseVersionId);
        CollectionAssert.AreEqual(
            new[] { "1.20.4-forge-49.0.30", "1.20.4" },
            resolved.Chain.ToArray());

        var merged = resolved.Merged;
        Assert.AreEqual("cpw.mods.bootstraplauncher.BootstrapLauncher", merged.MainClass);
        Assert.AreEqual("12", merged.Assets, "资源索引来自原版。");
        Assert.AreEqual("bbbb", merged.Downloads?.Client?.Sha1, "客户端 JAR 来自原版。");
        Assert.AreEqual(17, merged.JavaVersion?.MajorVersion, "Java 要求来自原版。");
        CollectionAssert.AreEqual(
            new[] { "net.minecraftforge:forge:1.20.4-49.0.30", "com.mojang:authlib:6.0.54" },
            merged.Libraries!.Select(l => l.Name!).ToArray());
        CollectionAssert.AreEqual(
            new[] { "--username", "${auth_player_name}", "--launchTarget", "forge_client" },
            merged.Arguments!.Game!.SelectMany(a => a.Values).ToArray());
        Assert.IsNull(merged.InheritsFrom);
    }

    [TestMethod]
    public void ResolvingAVersionThatIsNotHereFails()
    {
        var exception = Assert.ThrowsException<VersionResolutionException>(() =>
            NewStore().Resolve(_gameDirectory, "1.20.4"));

        Assert.AreEqual("本地没有版本「{0}」的 JSON（游戏目录与缓存里都没有）。", exception.Reason.Key);
        CollectionAssert.AreEqual(new[] { "1.20.4" }, exception.Reason.Parameters.ToArray());
        CollectionAssert.AreEqual(new[] { "1.20.4" }, exception.Chain.ToArray());
    }

    [TestMethod]
    public void AMissingParentIsReportedAsAMissingParentNotAMissingChild()
    {
        // 只装了加载器版本、没有原版：报错必须指向缺失的父版本（1.20.4），
        // 否则用户看到"缺少 1.20.4-forge-49.0.30"会一头雾水 —— 那个明明装着。
        WriteInstalled("1.20.4-forge-49.0.30", LoaderJson);

        var exception = Assert.ThrowsException<VersionResolutionException>(() =>
            NewStore().Resolve(_gameDirectory, "1.20.4-forge-49.0.30"));

        Assert.AreEqual("1.20.4", exception.MissingVersionId);
        CollectionAssert.AreEqual(new[] { "1.20.4-forge-49.0.30" }, exception.Chain.ToArray());
    }
}
