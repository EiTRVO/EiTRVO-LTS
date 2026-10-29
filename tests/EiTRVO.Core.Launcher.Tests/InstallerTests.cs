using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2 / B-4 执行部分：安装器（磁盘预检 → 下载 → natives 解压 → 资源布局 → 落版本 JSON）。
/// <para>
/// 桩用**按地址匹配**而不是按顺序（一次安装要下客户端 JAR、库、natives、资源索引与资源对象，
/// 并发下"谁先跑"本来就不确定）。
/// </para>
/// <para>
/// 所有 fixture 里声明的 SHA-1 都由**真正下发的字节**算出来 —— 写死一个假值只会让安装器
/// 正确地拒绝整批文件（这正是第一版 fixture 的错）。
/// </para>
/// </summary>
[TestClass]
public sealed class InstallerTests
{
    private const string ClientJar = "client jar bytes";
    private const string AuthlibJar = "authlib jar bytes";
    private const string LwjglJar = "lwjgl jar bytes";
    private const string AssetBody = "zh_cn language file";

    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>natives 归档（含一个排除清单里的目录）。</summary>
    private static byte[] NativeZip => Zip(("lwjgl.dll", "native"), ("META-INF/MANIFEST.MF", "manifest"));

    /// <summary>每个用例一套全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-installer", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(new DateTimeOffset(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8)));
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
    public async Task AFullInstallLaysOutEveryFileAndMarksTheVersionInstalled()
    {
        WriteInstalledVersionJson();

        var result = await Installer(VanillaStub()).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official, concurrency: 4);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(
            6,
            result.FilesDownloaded,
            "客户端 JAR + 两个库 + natives 归档 + 资源索引 + 资源对象。");
        Assert.IsTrue(result.NativeFilesExtracted >= 1);

        Assert.AreEqual(ClientJar, Read(@"versions\1.20.4\1.20.4.jar"));
        Assert.AreEqual(LwjglJar, Read(@"libraries\org\lwjgl\lwjgl\3.3.1\lwjgl-3.3.1.jar"));
        Assert.AreEqual(AuthlibJar, Read(@"libraries\com\mojang\authlib\6.0.54\authlib-6.0.54.jar"));
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, @"assets\indexes\12.json")));
        Assert.IsFalse(
            File.Exists(Path.Combine(_gameDirectory, @"versions\1.20.4\1.20.4.jar" + FileDownloader.PartSuffix)),
            "成功之后不该留下 .part。");
    }

    [TestMethod]
    public async Task AFullInstallEventuallyMarksTheVersionAsInstalled()
    {
        WriteInstalledVersionJson();

        var store = NewStore(new StubHandler());
        await NewInstaller(store, VanillaStub()).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        // 版本 JSON 写的是**合并后**的完整一份：重读一次要能把库与参数都拿回来。
        var roundTrip = store.TryReadInstalled(_gameDirectory, "1.20.4");

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual("1.20.4", roundTrip.Id);
        Assert.AreEqual("net.minecraft.client.main.Main", roundTrip.MainClass);
        Assert.AreEqual(2, roundTrip.Libraries!.Count, "natives 归档是 lwjgl 的分类器，不是单独的库条目。");
        CollectionAssert.AreEqual(
            new[] { "--username", "${auth_player_name}" },
            roundTrip.Arguments!.Game!.SelectMany(static a => a.Values).ToArray());
    }

    [TestMethod]
    public async Task TheAssetIndexDrivesASecondDownloadPhase()
    {
        WriteInstalledVersionJson();

        var result = await Installer(VanillaStub()).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));

        var hash = Sha1Hex(AssetBody);
        var assetPath = Path.Combine(_gameDirectory, "assets", "objects", hash[..2], hash);

        Assert.IsTrue(File.Exists(assetPath), "资源对象没有按索引下下来。");
        Assert.AreEqual(AssetBody, File.ReadAllText(assetPath));
    }

    [TestMethod]
    public async Task NativesAreExtractedWithTheExclusionListHonoured()
    {
        WriteInstalledVersionJson();

        var result = await Installer(VanillaStub()).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));

        var natives = Path.Combine(_gameDirectory, @"versions\1.20.4\natives");

        Assert.IsTrue(File.Exists(Path.Combine(natives, "lwjgl.dll")), "natives 没解出来。");
        Assert.IsFalse(Directory.Exists(Path.Combine(natives, "META-INF")), "排除清单里的目录不该被解出来。");
        Assert.AreEqual(1, result.NativeFilesExtracted);
    }

    [TestMethod]
    public async Task ATraversalEntryInANativesArchiveIsSkippedAndReported()
    {
        // 归档里带一个越界条目：它必须被拒绝，但不能让整次安装失败。
        var evilZip = Zip(("lwjgl.dll", "ok"), ("../evil.dll", "escaped"));
        WriteInstalledVersionJson(nativesBytes: evilZip);

        var stub = VanillaStub(nativesBytes: evilZip);
        var result = await Installer(stub).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, @"versions\1.20.4\natives\lwjgl.dll")));
        Assert.IsFalse(
            File.Exists(Path.Combine(_gameDirectory, @"versions\evil.dll")),
            "Zip Slip：越界条目绝不能被写到目标目录之外。");
        Assert.IsTrue(
            _diagnostics.Entries.Any(e => e.MessageKey == "{0} 里有越界条目，已跳过：{1}"),
            "越界条目必须留下一条事件（否则可疑内容就被藏起来了）。");
    }

    [TestMethod]
    public async Task OldVersionsGetTheirVirtualAndResourcesLayouts()
    {
        // 1.6 及以前：游戏按名字读资源，所以要按 virtual / resources 再铺一份。
        WriteInstalledVersionJson(assets: "legacy", legacyIndex: true);

        var result = await Installer(VanillaStub(indexId: "legacy", legacyIndex: true)).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));

        var hash = Sha1Hex(AssetBody);

        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, @"assets\virtual\legacy\minecraft\lang\zh_cn.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, @"resources\minecraft\lang\zh_cn.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "assets", "objects", hash[..2], hash)));
    }

    // ==================== 幂等与跳过 ====================

    [TestMethod]
    public async Task AnInstalledVersionIsNotDownloadedAgain()
    {
        WriteInstalledVersionJson();

        var first = await Installer(VanillaStub()).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsTrue(first.Succeeded, TestText.Of(first.Failure));

        // 第二次：本机文件全都校验通过，一次请求都不该发。
        var second = new StubHandler();
        var result = await Installer(second).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        HttpAssert.RequestCount(second, 0);
        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(0, result.FilesDownloaded);
        Assert.IsTrue(result.FilesSkipped > 0, "已存在的文件要算进跳过数。");
    }

    [TestMethod]
    public async Task AFileThatIsAlreadyCorrectIsNotRequestedAtAll()
    {
        WriteInstalledVersionJson();
        Directory.CreateDirectory(Path.Combine(_gameDirectory, @"versions\1.20.4"));
        File.WriteAllText(Path.Combine(_gameDirectory, @"versions\1.20.4\1.20.4.jar"), ClientJar);

        var stub = VanillaStub();
        var result = await Installer(stub).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.IsFalse(
            stub.Requests.Any(r => r.RequestUri!.ToString().Contains("client.jar", StringComparison.Ordinal)),
            "内容正确的客户端 JAR 不该再下一次。");
        Assert.IsTrue(result.FilesSkipped >= 1);
    }

    // ==================== 失败路径 ====================

    [TestMethod]
    public async Task TheVersionJsonIsFetchedFromTheManifestWhenItIsNotInstalledYet()
    {
        // 首次安装：本机既没有版本 JSON 也没有缓存清单 —— 安装器要自己按 id 去清单里取。
        var json = BuildVersionJson();
        var stub = VanillaStub(versionJson: json);

        var store = NewStore(stub);
        var result = await NewInstaller(store, stub).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.IsTrue(
            stub.Requests.Any(r => r.RequestUri!.ToString().Contains("version_manifest_v2.json", StringComparison.Ordinal)),
            "应当去取过清单。");
        Assert.IsNotNull(store.TryReadInstalled(_gameDirectory, "1.20.4"));
    }

    [TestMethod]
    public async Task ADownloadFailureMeansTheVersionIsNotMarkedAsInstalled()
    {
        // 客户端 JAR 下不下来。用**专门的桩**：桩的规则是"先注册的先匹配"，
        // 在成功的桩上再补 404 规则是永远轮不到的（第一版就是这么写的）。
        var json = BuildVersionJson();
        var stub = new StubHandler()
            .RespondTo("/mc/game/version_manifest_v2.json", System.Net.HttpStatusCode.OK, Manifest(json))
            .RespondTo("/packages/bbbb/1.20.4.json", System.Net.HttpStatusCode.OK, json)
            .RespondTo("objects/cccc/client.jar", System.Net.HttpStatusCode.NotFound)
            .RespondTo("authlib-6.0.54.jar", System.Net.HttpStatusCode.OK, AuthlibJar)
            .RespondTo("lwjgl-3.3.1.jar", System.Net.HttpStatusCode.OK, LwjglJar);

        var store = NewStore(stub);
        var result = await NewInstaller(store, stub).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        StringAssert.Contains(TestText.Of(result.Failure), "失败");

        // 版本 JSON 是"已安装"的**唯一依据**：下载没完成就绝不能写它 ——
        // 否则界面显示"已安装"、点启动却崩，比直接报错糟得多。
        Assert.IsNull(store.TryReadInstalled(_gameDirectory, "1.20.4"));
    }

    [TestMethod]
    public async Task DiskSpaceIsCheckedBeforeAnythingIsDownloaded()
    {
        // 客户端 JAR 声明成一个天文数字 → 预检必然不过。
        WriteInstalledVersionJson(clientJarSize: long.MaxValue / 4);

        var stub = VanillaStub();
        var result = await Installer(stub).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded);
        // 这里断言的是**失败原因**（磁盘空间检查器自己拼的句子），不是事件键 —— 别把它一起改名。
        StringAssert.Contains(TestText.Of(result.Failure), "磁盘空间不足");
        HttpAssert.RequestCount(stub, 0);
    }

    [TestMethod]
    public async Task AMissingVersionIsReportedInsteadOfCrashing()
    {
        // 清单里没有这个版本（本机也没有它的 JSON）。
        var stub = new StubHandler().RespondTo(
            "/mc/game/version_manifest_v2.json",
            System.Net.HttpStatusCode.OK,
            "{\"latest\":{\"release\":\"1.21.4\"},\"versions\":[{\"id\":\"1.21.4\",\"type\":\"release\","
            + "\"url\":\"https://piston-meta.mojang.com/v1/packages/aaaa/1.21.4.json\",\"releaseTime\":\"2024-12-03T09:00:00+00:00\"}]}");

        var store = NewStore(stub);
        var result = await NewInstaller(store, stub).InstallAsync(
            "1.20.4", _gameDirectory, GamePlatform.WindowsX64, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "1.20.4");
        Assert.IsNull(store.TryReadInstalled(_gameDirectory, "1.20.4"));
    }

    // ==================== 进度 ====================

    [TestMethod]
    public async Task ProgressIsAggregatedAcrossBothPhases()
    {
        WriteInstalledVersionJson();

        var recorder = new RecordingProgress();
        var result = await Installer(VanillaStub()).InstallAsync(
            "1.20.4",
            _gameDirectory,
            GamePlatform.WindowsX64,
            DownloadSourcePolicy.Official,
            concurrency: 4,
            progress: recorder);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.IsTrue(recorder.Reports.Count > 0);

        var last = recorder.Reports[^1];

        Assert.AreEqual(last.TotalFiles, last.CompletedFiles, "终态必须显示全部完成。");
        Assert.AreEqual(last.TotalBytes, last.CompletedBytes);
        Assert.AreEqual(6, last.TotalFiles, "两段的总量要合在一起，而不是只报最后一段。");
    }

    // ==================== 辅助 ====================

    private string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(_gameDirectory, relativePath));

    private VersionJsonStore NewStore(StubHandler stub) =>
        new(new FakeHttpClientFactory(stub), _paths, HttpHostPolicy.Default, _diagnostics);

    private VanillaInstaller NewInstaller(VersionJsonStore store, StubHandler stub) =>
        new(
            store,
            new VersionManifestClient(new FakeHttpClientFactory(stub), _paths, _clock, _diagnostics),
            new FileDownloader(new FakeHttpClientFactory(stub), HttpHostPolicy.Default, _clock, _diagnostics, static (_, _) => Task.CompletedTask),
            new DiskSpaceChecker(_diagnostics),
            new NativeExtractor(_diagnostics),
            _diagnostics);

    private VanillaInstaller Installer(StubHandler stub) => NewInstaller(NewStore(stub), stub);

    /// <summary>按下发的字节造一套响应（客户端 JAR、两个库、natives 归档、资源索引、资源对象）。</summary>
    /// <param name="indexId">资源索引 id。</param>
    /// <param name="legacyIndex">索引是否带 virtual / map_to_resources。</param>
    /// <param name="nativesBytes">natives 归档的真实字节。</param>
    /// <param name="versionJson">要按清单地址提供的版本 JSON；为 <see langword="null"/> 表示不发它。</param>
    private static StubHandler VanillaStub(
        string indexId = "12",
        bool legacyIndex = false,
        byte[]? nativesBytes = null,
        string? versionJson = null)
    {
        var hash = Sha1Hex(AssetBody);

        var stub = new StubHandler()
            .RespondTo("objects/cccc/client.jar", System.Net.HttpStatusCode.OK, ClientJar)
            .RespondTo("authlib-6.0.54.jar", System.Net.HttpStatusCode.OK, AuthlibJar)
            .RespondBytesTo("lwjgl-3.3.1-natives-windows.jar", nativesBytes ?? NativeZip)
            .RespondTo("lwjgl-3.3.1.jar", System.Net.HttpStatusCode.OK, LwjglJar)
            .RespondTo(
                "/packages/dddd/" + indexId + ".json",
                System.Net.HttpStatusCode.OK,
                AssetIndexJson(indexId, hash, legacyIndex))
            .RespondTo("resources.download.minecraft.net/" + hash[..2] + "/" + hash, System.Net.HttpStatusCode.OK, AssetBody);

        if (versionJson is not null)
        {
            stub.RespondTo("/mc/game/version_manifest_v2.json", System.Net.HttpStatusCode.OK, Manifest(versionJson));
            stub.RespondTo("/packages/bbbb/1.20.4.json", System.Net.HttpStatusCode.OK, versionJson);
        }

        return stub;
    }

    /// <summary>一份只列了 1.20.4 的清单。</summary>
    private static string Manifest(string versionJson) =>
        "{\"latest\":{\"release\":\"1.20.4\",\"snapshot\":\"1.20.4\"},\"versions\":[{"
        + "\"id\":\"1.20.4\",\"type\":\"release\","
        + "\"url\":\"https://piston-meta.mojang.com/v1/packages/bbbb/1.20.4.json\","
        + "\"sha1\":\"" + Sha1Hex(versionJson) + "\","
        + "\"releaseTime\":\"2023-12-07T11:00:00+00:00\",\"time\":\"2023-12-07T12:00:00+00:00\"}]}";

    private static string AssetIndexJson(string indexId, string hash, bool legacy)
    {
        var legacyFields = legacy ? "\"virtual\": true, \"map_to_resources\": true," : string.Empty;

        return "{\"objects\":{\"minecraft/lang/zh_cn.json\":{\"hash\":\"" + hash + "\",\"size\":" + AssetBody.Length + "}},"
            + legacyFields + "\"id\":\"" + indexId + "\"}";
    }

    /// <summary>把版本 JSON 直接写进游戏目录（这样安装器不必先去取清单）。</summary>
    /// <param name="clientJarSize">客户端 JAR 声明的字节数（磁盘预检用例会把它调大）。</param>
    /// <param name="assets">资源索引 id。</param>
    /// <param name="legacyIndex">索引是否带 virtual / map_to_resources。</param>
    /// <param name="nativesBytes">natives 归档的真实字节（校验值由它算出）。</param>
    private void WriteInstalledVersionJson(
        long clientJarSize = 25_000_000,
        string assets = "12",
        bool legacyIndex = false,
        byte[]? nativesBytes = null)
    {
        var directory = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "1.20.4.json"),
            BuildVersionJson(clientJarSize, assets, legacyIndex, nativesBytes),
            new UTF8Encoding(false));
    }

    /// <summary>造一份贴近真实的 1.20.4 版本 JSON（所有校验值都由真正下发的字节算出）。</summary>
    private static string BuildVersionJson(
        long clientJarSize = 25_000_000,
        string assets = "12",
        bool legacyIndex = false,
        byte[]? nativesBytes = null)
    {
        var assetHash = Sha1Hex(AssetBody);
        var indexJson = AssetIndexJson(assets, assetHash, legacyIndex);
        var natives = nativesBytes ?? NativeZip;

        return $$"""
        {
          "id": "1.20.4",
          "mainClass": "net.minecraft.client.main.Main",
          "assets": "{{assets}}",
          "assetIndex": {
            "id": "{{assets}}",
            "url": "https://piston-meta.mojang.com/v1/packages/dddd/{{assets}}.json",
            "sha1": "{{Sha1Hex(indexJson)}}",
            "size": {{indexJson.Length}},
            "totalSize": 100
          },
          "downloads": {
            "client": {
              "url": "https://piston-data.mojang.com/v1/objects/cccc/client.jar",
              "sha1": "{{Sha1Hex(ClientJar)}}",
              "size": {{clientJarSize}}
            }
          },
          "libraries": [
            {
              "name": "com.mojang:authlib:6.0.54",
              "downloads": { "artifact": {
                "path": "com/mojang/authlib/6.0.54/authlib-6.0.54.jar",
                "url": "https://libraries.minecraft.net/com/mojang/authlib/6.0.54/authlib-6.0.54.jar",
                "sha1": "{{Sha1Hex(AuthlibJar)}}",
                "size": {{AuthlibJar.Length}}
              } }
            },
            {
              "name": "org.lwjgl:lwjgl:3.3.1",
              "natives": { "windows": "natives-windows" },
              "extract": { "exclude": ["META-INF/"] },
              "downloads": {
                "artifact": {
                  "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar",
                  "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar",
                  "sha1": "{{Sha1Hex(LwjglJar)}}",
                  "size": {{LwjglJar.Length}}
                },
                "classifiers": {
                  "natives-windows": {
                    "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
                    "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
                    "sha1": "{{Sha1Hex(natives)}}",
                    "size": {{natives.Length}}
                  }
                }
              }
            }
          ],
          "arguments": { "game": ["--username", "${auth_player_name}"] }
        }
        """;
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string Sha1Hex(string body) => Sha1Hex(Encoding.UTF8.GetBytes(body));

    private static string Sha1Hex(byte[] bytes)
    {
#pragma warning disable CA5350 // 与产品代码同理：SHA-1 是 Minecraft 清单协议规定的完整性字段。
        var hash = SHA1.HashData(bytes);
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>记录每次进度报告。</summary>
    private sealed class RecordingProgress : IProgress<FileTransferProgress>
    {
        /// <summary>收到的报告。</summary>
        public List<FileTransferProgress> Reports { get; } = [];

        /// <inheritdoc />
        public void Report(FileTransferProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }
}
