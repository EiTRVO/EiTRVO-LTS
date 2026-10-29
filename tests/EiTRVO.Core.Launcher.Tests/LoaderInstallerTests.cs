using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-f：Fabric / Quilt 安装（profile → 与原版合并 → 同一条下载落盘流水线）。
/// <para>
/// 要钉住的三件事正是这一步的验收：**请求 URL**、**落盘内容**、**版本 JSON 的形状**
/// （它是"已安装"的唯一依据，也是启动时被解析的那一份）。
/// </para>
/// </summary>
[TestClass]
public sealed class LoaderInstallerTests
{
    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一套全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-loader", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.FromHours(8)));
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

    // ==================== 正常路径 ====================

    [TestMethod]
    public async Task AFabricProfileIsMergedWithTheBaseGameAndInstalledUnderItsOwnId()
    {
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
        var stub = LoaderFixture.Stub();
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64,
            DownloadSourcePolicy.Official,
            concurrency: 4);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(LoaderFixture.FabricId, result.VersionId, "装出来的版本 id 必须是启动时能反查到的那个。");
        Assert.AreEqual(3, result.FilesDownloaded, "客户端 JAR + 原版库 + 加载器库（合并后一起下）。");

        // ① 请求 URL：第一个请求必须是 profile；两家的地址形状都对得上。
        HttpAssert.Url(stub, 0, LoaderFixture.FabricProfileUrl);

        // ② 落盘内容：加载器库与原版库都真的到位了。
        Assert.AreEqual(
            LoaderFixture.FabricLoaderJar,
            Read(@"libraries\net\fabricmc\fabric-loader\0.15.7\fabric-loader-0.15.7.jar"));
        Assert.AreEqual(
            LoaderFixture.ClientJar,
            Read(@"versions\fabric-loader-0.15.7-1.20.4\fabric-loader-0.15.7-1.20.4.jar"));

        // ③ 版本 JSON 的形状：合并后的自足一份（Id = 加载器 id、不再有 inheritsFrom、
        //    主类换成加载器的、库是"加载器库 + 原版库"）。
        var merged = store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId);

        Assert.IsNotNull(merged, "版本 JSON 是「已安装」的唯一依据，必须落盘。");
        Assert.AreEqual(LoaderFixture.FabricId, merged.Id);
        Assert.IsNull(merged.InheritsFrom, "写下去的必须是继承链已展开的一份 —— 父版本目录被删了也要能启动。");
        Assert.AreEqual("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.MainClass);
        var libraryNames = merged.Libraries!.Select(static library => library.Name).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "net.fabricmc:fabric-loader:0.15.7", "com.mojang:authlib:6.0.54" },
            libraryNames,
            "实际库：" + string.Join(" | ", libraryNames));

        Assert.AreEqual("{0} {1} 安装完成（游戏 {2}，版本 {3}）", _diagnostics.Entries[^1].MessageKey);
        Assert.IsFalse(
            File.Exists(Path.Combine(_gameDirectory, "versions", LoaderFixture.FabricId, LoaderFixture.FabricId + ".json" + FileDownloader.PartSuffix)),
            "成功之后不该留下 .part。");
    }

    [TestMethod]
    public async Task TheBaseGameVersionJsonIsFetchedFromTheManifestWhenItIsNotLocal()
    {
        // 盘上既没有原版版本 JSON，也没有缓存清单：加载器安装要自己把它取回来。
        var stub = LoaderFixture.Stub(vanillaJson: LoaderFixture.VanillaJson());
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64,
            DownloadSourcePolicy.Official,
            concurrency: 4);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.IsNotNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId));
        Assert.AreEqual(LoaderFixture.ClientJar, Read(@"versions\fabric-loader-0.15.7-1.20.4\fabric-loader-0.15.7-1.20.4.jar"));

        // 原版那份 JSON 不必装进盘上（合并后的加载器版本是自足的），
        // 但它必须真的被读过：清单 → 版本 JSON 两次请求。
        Assert.IsTrue(
            stub.Requests.Any(static request => request.RequestUri!.ToString().Contains("version_manifest_v2.json", StringComparison.Ordinal)),
            "本地没有版本 JSON 时应当去清单里找。");
    }

    [TestMethod]
    public async Task QuiltUsesItsOwnHostAndPrefixThroughTheSameImplementation()
    {
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
        var stub = LoaderFixture.Stub(kind: LoaderKind.Quilt, id: LoaderFixture.QuiltId);
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Quilt,
            "1.20.4",
            "0.23.1",
            _gameDirectory,
            GamePlatform.WindowsX64,
            DownloadSourcePolicy.Official,
            concurrency: 4);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        HttpAssert.Url(stub, 0, LoaderFixture.QuiltProfileUrl);
        Assert.AreEqual(LoaderFixture.QuiltLoaderJar, Read(@"libraries\org\quiltmc\quilt-loader\0.23.1\quilt-loader-0.23.1.jar"));
        Assert.IsNotNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.QuiltId));
    }

    [TestMethod]
    public async Task AProfileWithUrlFormLibrariesIsInstalledEvenThoughTheyHaveNoArtifactPath()
    {
        // 真实 Fabric / Quilt profile 的库长这样：name + url（+ sha1/size），没有 downloads/artifact。
        // 路径只能由 Maven 坐标推出来 —— 推不出来就会"装完少一半库"。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
        var stub = LoaderFixture.UrlFormStub();
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64,
            DownloadSourcePolicy.Official,
            concurrency: 4);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(LoaderFixture.UrlFormJar, Read(@"libraries\org\ow2\asm\asm\9.6\asm-9.6.jar"));
        Assert.AreEqual(
            LoaderFixture.UnverifiableJar,
            Read(@"libraries\net\fabricmc\intermediary\1.20.4\intermediary-1.20.4.jar"),
            "没有校验值的库也要照下（Quilt 的 profile 全部是这种）。");
        Assert.IsNotNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId));
    }

    [TestMethod]
    public async Task AUrlFormLibraryThatCarriesASha1IsActuallyVerified()
    {
        // 库对象自带的 sha1 必须真的用上：以前这里一律把校验值丢掉，
        // 于是 Fabric 那五个带 sha1 的库（asm 系列）都是"下完就用"，损坏了也不知道。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
        var stub = LoaderFixture.UrlFormStub(tamperVerifiableLibrary: true);
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64,
            DownloadSourcePolicy.Official,
            concurrency: 2);

        Assert.IsFalse(result.Succeeded, "下发内容与 sha1 不符时必须失败。");
        Assert.IsNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId));
    }

    // ==================== 如实失败 ====================

    [TestMethod]
    public async Task AProfileIdThatDoesNotMatchTheLocalNamingRuleIsRefusedBeforeDownloadingAnything()
    {
        // 远端把 id 改成别的形状时，装下去启动时也找不到它 —— 必须当场拒绝，而不是硬装。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
        var stub = LoaderFixture.Stub(id: "fabric-loader-0.15.7-1.20.4-experimental");
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "fabric-loader-0.15.7-1.20.4-experimental");
        StringAssert.Contains(TestText.Of(result.Failure), LoaderFixture.FabricId);
        StringAssert.Contains(TestText.Of(result.Failure), "命名规则");
        HttpAssert.RequestCount(stub, 1);
        Assert.IsNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId));
    }

    [TestMethod]
    public async Task AProfileThatInheritsADifferentGameVersionIsRefused()
    {
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
        var stub = LoaderFixture.Stub(inheritsFrom: "1.21");
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "1.21");
        StringAssert.Contains(TestText.Of(result.Failure), "1.20.4");
        Assert.IsNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId));
    }

    [TestMethod]
    public async Task AFailedDownloadLeavesNoVersionJsonBehind()
    {
        // 版本 JSON 是"已安装"的唯一依据：下载没成功就绝不能留下它（否则界面显示装好了、点启动却崩）。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
        var stub = LoaderFixture.Stub(loaderJarSha1: new string('a', 40));
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64,
            DownloadSourcePolicy.Official,
            concurrency: 2);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.IsNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId), "下载失败不许落版本 JSON。");
    }

    [TestMethod]
    public async Task AMissingBaseGameVersionJsonFailsWithAReadableReason()
    {
        // 盘上没有原版版本 JSON，清单里也没有这个版本 → 解析父版本失败，但理由要人能看懂。
        var stub = new StubHandler()
            .RespondTo("meta.fabricmc.net", System.Net.HttpStatusCode.OK, LoaderFixture.ProfileJson(LoaderKind.Fabric, LoaderFixture.FabricId))
            .RespondTo("/mc/game/version_manifest_v2.json", System.Net.HttpStatusCode.OK, LoaderFixture.Manifest("{}", id: "9.9.9"));
        var store = NewStore(stub);

        var result = await NewInstaller(store, stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            "0.15.7",
            _gameDirectory,
            GamePlatform.WindowsX64);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "1.20.4");
        Assert.IsNull(store.TryReadInstalled(_gameDirectory, LoaderFixture.FabricId));
    }

    [TestMethod]
    public async Task ForgeAndOptiFineAreNotHandledHereAndSayWhichMilestoneOwnsThem()
    {
        var stub = new StubHandler();

        var result = await NewInstaller(NewStore(stub), stub).InstallAsync(
            LoaderKind.Forge,
            "1.20.4",
            "49.0.30",
            _gameDirectory,
            GamePlatform.WindowsX64);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "M4-g");
        StringAssert.Contains(TestText.Of(result.Failure), "M4-i");
        HttpAssert.RequestCount(stub, 0);
    }

    [TestMethod]
    public async Task AMissingLoaderVersionIsRefusedInsteadOfGuessing()
    {
        var stub = new StubHandler();

        var result = await NewInstaller(NewStore(stub), stub).InstallAsync(
            LoaderKind.Fabric,
            "1.20.4",
            loaderVersion: null,
            _gameDirectory,
            GamePlatform.WindowsX64);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "Fabric");
        HttpAssert.RequestCount(stub, 0);
    }

    // ==================== 辅助 ====================

    private string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(_gameDirectory, relativePath));

    private VersionJsonStore NewStore(StubHandler stub) =>
        new(new FakeHttpClientFactory(stub), _paths, HttpHostPolicy.Default, _diagnostics);

    private LoaderInstaller NewInstaller(VersionJsonStore store, StubHandler stub)
    {
        var factory = new FakeHttpClientFactory(stub);
        var vanilla = new VanillaInstaller(
            store,
            new VersionManifestClient(factory, _paths, _clock, _diagnostics),
            new FileDownloader(factory, HttpHostPolicy.Default, _clock, _diagnostics, static (_, _) => Task.CompletedTask),
            new DiskSpaceChecker(_diagnostics),
            new NativeExtractor(_diagnostics),
            _diagnostics);

        return new LoaderInstaller(new LoaderProfileClient(factory, _diagnostics), vanilla, store, _diagnostics);
    }
}
