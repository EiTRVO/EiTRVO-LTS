using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-g：Forge / NeoForge 安装（下官方安装器 → 用它自己装 → 校验产出）。
/// <para>
/// 这一层的风险集中在四处：**地址对不对**、**命令行对不对**、**`launcher_profiles.json` 有没有被破坏**、
/// **失败时有没有被记成成功**。四条都有对应用例。
/// </para>
/// </summary>
[TestClass]
public sealed class ForgeInstallerTests
{
    private const string InstallerJar = "forge installer jar bytes";
    private const string ForgeId = "1.20.4-forge-49.0.30";

    private static readonly JavaInstallation Java17 = new(
        ExecutablePath: @"C:\Java\jdk-17\bin\javaw.exe",
        HomeDirectory: @"C:\Java\jdk-17",
        VersionText: "17.0.9",
        MajorVersion: 17,
        Vendor: "Eclipse Temurin",
        Bits: 64);

    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一套全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-forge", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();

        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);
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

    // ==================== 地址（纯函数）====================

    [TestMethod]
    public void InstallerUrlsAreTheOnesTheTwoMavensActuallyPublish()
    {
        // 三个地址都是 2026-10-04 用真安装器下过的（HTTP 200 + .sha1 存在）。
        Assert.AreEqual(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.4-49.0.30/forge-1.20.4-49.0.30-installer.jar",
            ForgeInstaller.InstallerUrl(LoaderKind.Forge, "1.20.4", "49.0.30"));

        Assert.AreEqual(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/20.4.237/neoforge-20.4.237-installer.jar",
            ForgeInstaller.InstallerUrl(LoaderKind.NeoForge, "1.20.4", "20.4.237"));

        // 1.20.1 的 NeoForge 还在旧 artifact 下，版本号里带游戏版本（B-8）。
        Assert.AreEqual(
            "https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
            ForgeInstaller.InstallerUrl(LoaderKind.NeoForge, "1.20.1", "47.1.106"));

        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => ForgeInstaller.InstallerUrl(LoaderKind.Fabric, "1.20.4", "0.15.7"));

        Assert.ThrowsException<ArgumentException>(
            () => ForgeInstaller.InstallerUrl(LoaderKind.Forge, "1.20.4/../x", "49.0.30"));
    }

    // ==================== 正常路径 ====================

    [TestMethod]
    public async Task AForgeInstallDownloadsTheOfficialInstallerAndRunsItAgainstTheGameDirectory()
    {
        var stub = SuccessfulStub();
        var tools = new FakeToolRunner { OnRun = (_, game) => WriteInstalledVersion("1.20.4-forge-49.0.30") };

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge, "1.20.4", "49.0.30", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(ForgeId, result.VersionId);

        // ① 请求 URL：先取发布方的校验值，再下安装器本体。
        HttpAssert.Url(
            stub,
            0,
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.4-49.0.30/forge-1.20.4-49.0.30-installer.jar.sha1");
        HttpAssert.Url(
            stub,
            1,
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.4-49.0.30/forge-1.20.4-49.0.30-installer.jar");

        // ② 命令行：用选中的 Java 跑 `-jar <缓存的安装器> --installClient <游戏目录>`。
        var run = tools.Runs.Single();

        Assert.AreEqual(Java17.ExecutablePath, run.ExecutablePath);
        Assert.AreEqual("-jar", run.Arguments[0]);
        StringAssert.EndsWith(run.Arguments[1], "forge-1.20.4-49.0.30-installer.jar");
        Assert.IsTrue(File.Exists(run.Arguments[1]), "安装器 jar 必须真的落在盘上（缓存在 cache\\downloads）。");
        Assert.AreEqual("--installClient", run.Arguments[2]);
        Assert.AreEqual(Path.GetFullPath(_gameDirectory), run.Arguments[3]);
        Assert.AreEqual(Path.GetFullPath(_gameDirectory), run.WorkingDirectory);

        // ③ 安装器要求存在的 profile 文件被补上了。
        Assert.AreEqual(
            ForgeInstaller.MinimalLauncherProfiles,
            File.ReadAllText(Path.Combine(_gameDirectory, "launcher_profiles.json")));

        // ④ 事件两头都有。
        Assert.IsTrue(_diagnostics.Entries.Any(static entry => entry.MessageKey == "正在运行 {0} 官方安装器（{1} / {2}）"));
        Assert.IsTrue(_diagnostics.Entries.Any(static entry => entry.MessageKey == "{0} {1} 安装完成（游戏 {2}，版本 {3}）"));
    }

    [TestMethod]
    public async Task NeoForgeUsesItsOwnArtifactAndItsOwnVersionId()
    {
        var stub = SuccessfulStub(kind: LoaderKind.NeoForge, loaderVersion: "20.4.237");
        var tools = new FakeToolRunner { OnRun = (_, _) => WriteInstalledVersion("neoforge-20.4.237") };

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.NeoForge, "1.20.4", "20.4.237", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual("neoforge-20.4.237", result.VersionId);
        HttpAssert.Url(
            stub,
            1,
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/20.4.237/neoforge-20.4.237-installer.jar");
    }

    [TestMethod]
    public async Task LegacyNeoForgeOn1201InstallsUnderTheForgeShapedId()
    {
        // 1.20.1 的 NeoForge 还在 net/neoforged/forge 下，装出来的 id 是 1.20.1-forge-47.1.106
        // （真机核过；这正是 B-8 那条映射的落点）。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory, "1.20.1");

        var stub = SuccessfulStub(kind: LoaderKind.NeoForge, gameVersion: "1.20.1", loaderVersion: "47.1.106");
        var tools = new FakeToolRunner { OnRun = (_, _) => WriteInstalledVersion("1.20.1-forge-47.1.106") };

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.NeoForge, "1.20.1", "47.1.106", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual("1.20.1-forge-47.1.106", result.VersionId);
        HttpAssert.Url(
            stub,
            1,
            "https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar");
        StringAssert.Contains(tools.Runs.Single().Arguments[1], "forge-1.20.1-47.1.106-installer.jar");
    }

    [TestMethod]
    public async Task AnExistingLauncherProfilesFileIsLeftByteForByteUntouched()
    {
        // 那是官方启动器与用户自己的文件：安装器自己会改它，我们只在缺失时补一个最小的。
        var path = Path.Combine(_gameDirectory, "launcher_profiles.json");
        var mine = "{\"profiles\":{\"我的档案\":{\"name\":\"我的档案\"}}}";
        File.WriteAllText(path, mine, new UTF8Encoding(false));

        var stub = SuccessfulStub();
        var tools = new FakeToolRunner { OnRun = (_, _) => WriteInstalledVersion(ForgeId) };

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge, "1.20.4", "49.0.30", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(mine, File.ReadAllText(path), "已有的 launcher_profiles.json 一个字都不该被我们改掉。");
    }

    // ==================== 如实失败 ====================

    [TestMethod]
    public async Task AFailingInstallerIsNotReportedAsSuccessEvenIfAVersionJsonAppeared()
    {
        // 实测 NeoForge 20.4.237：退出码 1（"These libraries failed to download"），
        // 但 versions\neoforge-20.4.237 已经写出来了。只看"版本 JSON 在不在"会把它记成成功，
        // 用户拿到的是一个装着半份库、点启动就崩的实例。
        var stub = SuccessfulStub(kind: LoaderKind.NeoForge, loaderVersion: "20.4.237");
        var tools = new FakeToolRunner
        {
            Result = new ToolRunResult(1, "These libraries failed to download. Try again.", TimedOut: false),
            OnRun = (_, _) => WriteInstalledVersion("neoforge-20.4.237"),
        };

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.NeoForge, "1.20.4", "20.4.237", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded, "退出码非 0 就是失败，哪怕它已经写出了一半东西。");
        StringAssert.Contains(TestText.Of(result.Failure), "退出码 1");
        StringAssert.Contains(TestText.Of(result.Failure), "These libraries failed to download");
        Assert.IsTrue(
            _diagnostics.Entries.Any(static entry => entry.MessageKey == "{0} 官方安装器失败（{1}，退出码 {2}）"),
            "失败要留一条事件。");
    }

    [TestMethod]
    public async Task ATimedOutInstallerIsReportedAsATimeoutInsteadOfAnExitCode()
    {
        var stub = SuccessfulStub();
        var tools = new FakeToolRunner
        {
            Result = new ToolRunResult(-1, "still downloading…", TimedOut: true),
        };

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge, "1.20.4", "49.0.30", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "没有结束");
        StringAssert.Contains(TestText.Of(result.Failure), "still downloading");
    }

    [TestMethod]
    public async Task WithoutASha1SidecarTheInstallerIsNeverRun()
    {
        // 远端程序不能"没校验就运行"。
        var stub = new StubHandler()
            .RespondTo(".sha1", HttpStatusCode.NotFound)
            .RespondTo("installer.jar", HttpStatusCode.OK, InstallerJar);

        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge, "1.20.4", "49.0.30", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "校验值");
        Assert.AreEqual(0, tools.Runs.Count, "校验值都拿不到，绝不能把安装器跑起来。");
        Assert.AreEqual(0, stub.Requests.Count(static request => request.RequestUri!.AbsolutePath.EndsWith(".jar", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AMissingJavaRefusesTheInstallBeforeDownloadingAnything()
    {
        var stub = SuccessfulStub();
        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools, java: []).InstallAsync(
            LoaderKind.Forge, "1.20.4", "49.0.30", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "Java");
        Assert.AreEqual(0, tools.Runs.Count);
        Assert.AreEqual(0, stub.Requests.Count(static request => request.RequestUri!.AbsolutePath.EndsWith(".jar", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ASuccessfulInstallerThatWroteNoVersionJsonIsNotTrusted()
    {
        var stub = SuccessfulStub();
        var tools = new FakeToolRunner(); // 退出码 0，但什么都没写

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge, "1.20.4", "49.0.30", _gameDirectory, DownloadSourcePolicy.Official);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "没有 versions");
        Assert.AreEqual(ForgeId, result.VersionId);
    }

    [TestMethod]
    public async Task FabricAndOptiFineAreNotHandledHere()
    {
        var stub = new StubHandler();
        var tools = new FakeToolRunner();

        var fabric = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Fabric, "1.20.4", "0.15.7", _gameDirectory);

        Assert.IsFalse(fabric.Succeeded);
        StringAssert.Contains(TestText.Of(fabric.Failure), "M4-f");

        var optifine = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.OptiFine, "1.20.4", "H1", _gameDirectory);

        Assert.IsFalse(optifine.Succeeded);
        StringAssert.Contains(TestText.Of(optifine.Failure), "M4-i");

        HttpAssert.RequestCount(stub, 0);
        Assert.AreEqual(0, tools.Runs.Count);
    }

    [TestMethod]
    public async Task AMissingLoaderVersionIsRefusedInsteadOfGuessing()
    {
        var stub = new StubHandler();
        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge, "1.20.4", loaderVersion: null, _gameDirectory);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "Forge");
        HttpAssert.RequestCount(stub, 0);
    }

    [TestMethod]
    [DataRow("1.7.10", true)]
    [DataRow("1.8.9", true)]
    [DataRow("1.12.2", true)]
    [DataRow("1.13", false)]
    [DataRow("1.20.4", false)]
    public void OnlyOldForgeInstallersUseTheExtractionPath(string gameVersion, bool expected) =>
        Assert.AreEqual(expected, ForgeInstaller.RequiresLegacyClientInstall(gameVersion));

    [TestMethod]
    public async Task LegacyForge1710IsInstalledFromTheInstallerJarWithoutRunningIt()
    {
        // 用户实机反馈（2026-10-21）：装 1.7.10 时提示「官方安装器以退出码 1 结束」。
        // 实测 1.7.10 的安装器只认 --extract / --installServer：传 --installClient 会
        // joptsimple.UnrecognizedOptionException。客户端安装只能从安装器 jar 里自己做。
        const string gameVersion = "1.7.10";
        const string loaderVersion = "10.13.0.1150";
        const string forgeId = "1.7.10-forge-10.13.0.1150";
        const string universal = "forge universal bytes";
        const string launchWrapper = "launchwrapper bytes";
        const string clientJar = "1.7.10 client jar";
        const string authlib = "1.7.10 authlib";

        // 原版 JSON 已在本地（VanillaInstaller 会直接读它，不必先取清单）。
        WriteVanilla(gameVersion, clientJar, authlib);

        var installer = CreateLegacyInstallerZip(universal);

        var stub = new StubHandler()
            .RespondTo(".sha1", HttpStatusCode.OK, Sha1Hex(installer))
            .RespondBytesTo("forge-1.7.10-10.13.0.1150-installer.jar", installer)
            .RespondTo("client.jar", HttpStatusCode.OK, clientJar)
            .RespondTo("authlib-1.5.13.jar", HttpStatusCode.OK, authlib)
            .RespondTo("launchwrapper-1.9.jar", HttpStatusCode.OK, launchWrapper);

        var tools = new FakeToolRunner();
        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge,
            gameVersion,
            loaderVersion,
            _gameDirectory,
            DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(forgeId, result.VersionId);
        Assert.AreEqual(0, tools.Runs.Count, "旧版安装器没有客户端安装选项：不许再去跑它。");

        // universal jar 被从安装器 zip 里取出，读掉的版本 JSON 已经合并成自足的一份。
        var universalPath = Path.Combine(
            _gameDirectory,
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            "1.7.10-10.13.0.1150",
            "forge-1.7.10-10.13.0.1150.jar");
        Assert.AreEqual(universal, File.ReadAllText(universalPath, Encoding.UTF8));

        var versionJsonPath = Path.Combine(_gameDirectory, "versions", forgeId, forgeId + ".json");
        Assert.IsTrue(File.Exists(versionJsonPath), "版本 JSON 是「已安装」的唯一依据。");

        var versionJson = File.ReadAllText(versionJsonPath, Encoding.UTF8);
        StringAssert.Contains(versionJson, "net.minecraft.launchwrapper.Launch");
        Assert.IsFalse(
            versionJson.Contains("\"inheritsFrom\"", StringComparison.Ordinal),
            "写下去的必须是继承链已展开的一份。");

        Assert.AreEqual(
            launchWrapper,
            File.ReadAllText(
                Path.Combine(_gameDirectory, "libraries", "net", "minecraft", "launchwrapper", "1.9", "launchwrapper-1.9.jar"),
                Encoding.UTF8));

        Assert.IsTrue(
            File.Exists(Path.Combine(_gameDirectory, "versions", gameVersion, gameVersion + ".jar")),
            "原版客户端 JAR 要先装好。");

        Assert.IsFalse(
            stub.Requests.Any(static request =>
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/forge-1.7.10-10.13.0.1150.jar",
                    StringComparison.OrdinalIgnoreCase)),
            "Forge 的非 universal 名字在 Maven 上不存在，不该去请求它。");
    }

    [TestMethod]
    public async Task LegacyForge1122FindsTheUniversalJarUnderMaven()
    {
        // 1.12.2 的安装器仍然是旧版（没有 --installClient），但 install_profile 的形状不同：
        // 版本 JSON 用 "json": "/version.json" 指向，universal jar 在 maven/ 下且**不带 -universal 后缀**。
        const string gameVersion = "1.12.2";
        const string loaderVersion = "14.23.5.2860";
        const string forgeId = "1.12.2-forge-14.23.5.2860";
        const string universal = "forge 1.12.2 jar bytes";
        const string launchWrapper = "launchwrapper 1.12 bytes";
        const string clientJar = "1.12.2 client jar";
        const string authlib = "1.12.2 authlib";

        WriteVanilla(gameVersion, clientJar, authlib);

        var installer = CreateLegacy1122InstallerZip(universal);

        var stub = new StubHandler()
            .RespondTo(".sha1", HttpStatusCode.OK, Sha1Hex(installer))
            .RespondBytesTo("forge-1.12.2-14.23.5.2860-installer.jar", installer)
            .RespondTo("client.jar", HttpStatusCode.OK, clientJar)
            .RespondTo("authlib-1.5.13.jar", HttpStatusCode.OK, authlib)
            .RespondTo("launchwrapper-1.12.jar", HttpStatusCode.OK, launchWrapper);

        var tools = new FakeToolRunner();
        var result = await NewInstaller(stub, tools).InstallAsync(
            LoaderKind.Forge,
            gameVersion,
            loaderVersion,
            _gameDirectory,
            DownloadSourcePolicy.Official);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(forgeId, result.VersionId);
        Assert.AreEqual(0, tools.Runs.Count, "1.12.2 的安装器也没有 --installClient：不许去跑它。");

        Assert.AreEqual(
            universal,
            File.ReadAllText(
                Path.Combine(
                    _gameDirectory,
                    "libraries",
                    "net",
                    "minecraftforge",
                    "forge",
                    "1.12.2-14.23.5.2860",
                    "forge-1.12.2-14.23.5.2860.jar"),
                Encoding.UTF8),
            "universal jar 在 maven/ 下，文件名不带 -universal 后缀，也必须被取出来。");

        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", forgeId, forgeId + ".json")));
    }

    // ==================== 辅助 ====================

    /// <summary>1.12.2 形状的安装器：json 引用 + maven/ 下的 universal jar。</summary>
    private static byte[] CreateLegacy1122InstallerZip(string universal)
    {
        const string profile = """
        {
          "json": "/version.json",
          "path": "net.minecraftforge:forge:1.12.2-14.23.5.2860",
          "version": "1.12.2-forge-14.23.5.2860"
        }
        """;

        const string versionJson = """
        {
          "id": "1.12.2-forge-14.23.5.2860",
          "inheritsFrom": "1.12.2",
          "mainClass": "net.minecraft.launchwrapper.Launch",
          "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory}",
          "libraries": [
            {
              "name": "net.minecraftforge:forge:1.12.2-14.23.5.2860",
              "url": "http://maven.minecraftforge.net/"
            },
            { "name": "net.minecraft:launchwrapper:1.12" }
          ]
        }
        """;

        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipText(zip, "install_profile.json", profile);
            WriteZipText(zip, "version.json", versionJson);

            using var universalStream = zip
                .CreateEntry("maven/net/minecraftforge/forge/1.12.2-14.23.5.2860/forge-1.12.2-14.23.5.2860.jar")
                .Open();
            var bytes = Encoding.UTF8.GetBytes(universal);
            universalStream.Write(bytes, 0, bytes.Length);
        }

        return memory.ToArray();
    }

    private static void WriteZipText(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private void WriteVanilla(string gameVersion, string clientJar, string authlib)
    {
        var json = $$"""
        {
          "id": "{{gameVersion}}",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "downloads": {
            "client": {
              "url": "https://piston-data.mojang.com/v1/objects/dddd/client.jar",
              "sha1": "{{Sha1Hex(clientJar)}}",
              "size": {{Encoding.UTF8.GetByteCount(clientJar)}}
            }
          },
          "libraries": [
            {
              "name": "com.mojang:authlib:1.5.13",
              "downloads": { "artifact": {
                "path": "com/mojang/authlib/1.5.13/authlib-1.5.13.jar",
                "url": "https://libraries.minecraft.net/com/mojang/authlib/1.5.13/authlib-1.5.13.jar",
                "sha1": "{{Sha1Hex(authlib)}}",
                "size": {{Encoding.UTF8.GetByteCount(authlib)}}
              } }
            }
          ],
          "minecraftArguments": "--username ${auth_player_name}"
        }
        """;

        var directory = Path.Combine(_gameDirectory, "versions", gameVersion);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, gameVersion + ".json"),
            json,
            new UTF8Encoding(false));
    }

    /// <summary>造一个旧版 Forge 安装器 zip：install_profile.json + 根目录的 universal jar。</summary>
    private static byte[] CreateLegacyInstallerZip(string universal)
    {
        var profile = """
        {
          "install": {
            "path": "net.minecraftforge:forge:1.7.10-10.13.0.1150",
            "filePath": "forge-1.7.10-10.13.0.1150-universal.jar",
            "minecraft": "1.7.10"
          },
          "versionInfo": {
            "id": "1.7.10-Forge10.13.0.1150",
            "inheritsFrom": "1.7.10",
            "mainClass": "net.minecraft.launchwrapper.Launch",
            "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory}",
            "libraries": [
              {
                "name": "net.minecraftforge:forge:1.7.10-10.13.0.1150",
                "url": "http://maven.minecraftforge.net/"
              },
              { "name": "net.minecraft:launchwrapper:1.9" }
            ]
          }
        }
        """;

        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var profileStream = zip.CreateEntry("install_profile.json").Open())
            using (var writer = new StreamWriter(profileStream, new UTF8Encoding(false)))
            {
                writer.Write(profile);
            }

            using var universalStream = zip.CreateEntry("forge-1.7.10-10.13.0.1150-universal.jar").Open();
            var bytes = Encoding.UTF8.GetBytes(universal);
            universalStream.Write(bytes, 0, bytes.Length);
        }

        return memory.ToArray();
    }

    private static string Sha1Hex(string text) => Sha1Hex(Encoding.UTF8.GetBytes(text));

    private static string Sha1Hex(byte[] bytes)
    {
#pragma warning disable CA5350 // 与产品代码同理：SHA-1 是 Minecraft 清单协议规定的完整性字段。
        var hash = SHA1.HashData(bytes);
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private ForgeInstaller NewInstaller(
        StubHandler stub,
        FakeToolRunner tools,
        IReadOnlyList<JavaInstallation>? java = null)
    {
        var factory = new FakeHttpClientFactory(stub);
        var store = new VersionJsonStore(factory, _paths, HttpHostPolicy.Default, _diagnostics);
        var downloader = new FileDownloader(factory, HttpHostPolicy.Default, new FixedClock(DateTimeOffset.UtcNow), _diagnostics, static (_, _) => Task.CompletedTask);
        var vanilla = new VanillaInstaller(
            store,
            new VersionManifestClient(factory, _paths, new FixedClock(DateTimeOffset.UtcNow), _diagnostics),
            downloader,
            new DiskSpaceChecker(_diagnostics),
            new NativeExtractor(_diagnostics),
            _diagnostics);

        return new ForgeInstaller(
            tools,
            vanilla,
            downloader,
            store,
            new FakeJavaRuntimeCatalog(java ?? [Java17], hasDetectionResult: true),
            _paths,
            factory,
            _diagnostics);
    }

    /// <summary>安装器 jar + 它的 <c>.sha1</c> 两个响应（按真实发布形态）。</summary>
    private static StubHandler SuccessfulStub(
        LoaderKind kind = LoaderKind.Forge,
        string gameVersion = "1.20.4",
        string loaderVersion = "49.0.30")
    {
        var url = ForgeInstaller.InstallerUrl(kind, gameVersion, loaderVersion);

        return new StubHandler()
            .RespondTo(".sha1", HttpStatusCode.OK, LoaderFixture.Sha1Hex(InstallerJar))
            .RespondTo(Path.GetFileName(new Uri(url).AbsolutePath), HttpStatusCode.OK, InstallerJar);
    }

    /// <summary>模拟官方安装器写出它的产物（真安装器在退出前会写这个文件）。</summary>
    private void WriteInstalledVersion(string versionId)
    {
        var directory = Path.Combine(_gameDirectory, "versions", versionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, versionId + ".json"),
            "{\"id\":\"" + versionId + "\",\"inheritsFrom\":\"1.20.4\",\"mainClass\":\"cpw.mods.bootstraplauncher.BootstrapLauncher\"}",
            new UTF8Encoding(false));
    }
}
