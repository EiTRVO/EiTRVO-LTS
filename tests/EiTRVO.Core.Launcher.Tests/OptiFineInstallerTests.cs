using System.Net;
using System.Text;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-i：OptiFine 安装器在"**跑之前**"的那几道判断。
/// <para>
/// 这里刻意不测"装成功了"的整链（那个在 <c>DownloadCatalogTests</c> 里走队列跑过一次），
/// 而是把注意力放在**防御**上：下载到的东西不是 OptiFine 安装器时**绝不运行它**、
/// 原版不在时**一次网络都不发**、列表里没有那个版本时如实说清楚。
/// </para>
/// </summary>
[TestClass]
public sealed class OptiFineInstallerTests
{
    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-optifine", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();
    }

    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task AJarWithoutTheInstallerClassIsNeverExecuted()
    {
        WriteVanilla();

        var stub = new StubHandler()
            .RespondTo("/downloads", HttpStatusCode.OK, OptiFineFixture.ListHtml)
            .RespondTo("/adloadx", HttpStatusCode.OK, AdloadPage())
            .RespondBytesTo("/downloadx", OptiFineFixture.InstallerJar(withInstallerClass: false));

        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools).InstallAsync("1.20.4", "HD_U_I8", _gameDirectory);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "不是 OptiFine 安装器");
        Assert.AreEqual(0, tools.Runs.Count, "结构校验不过的远端程序**绝不能**被运行。");
    }

    [TestMethod]
    public async Task AJarWhoseManifestIsNotAnOptiFineInstallerIsNeverExecuted()
    {
        WriteVanilla();

        var stub = new StubHandler()
            .RespondTo("/downloads", HttpStatusCode.OK, OptiFineFixture.ListHtml)
            .RespondTo("/adloadx", HttpStatusCode.OK, AdloadPage())
            .RespondBytesTo("/downloadx", OptiFineFixture.InstallerJar(withExpectedMainClass: false));

        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools).InstallAsync("1.20.4", "HD_U_I8", _gameDirectory);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "MANIFEST 里没有");
        Assert.AreEqual(0, tools.Runs.Count);
    }

    [TestMethod]
    public async Task AMissingVanillaVersionIsReportedBeforeAnyNetworkCall()
    {
        // 官方安装器要在真客户端 JAR 上打补丁：原版不在时连列表都不必取。
        var stub = new StubHandler();
        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools).InstallAsync("1.20.4", "HD_U_I8", _gameDirectory);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "先把原版 1.20.4 装好");
        HttpAssert.RequestCount(stub, 0);
        Assert.AreEqual(0, tools.Runs.Count);
    }

    [TestMethod]
    public async Task AnEditionThatIsNotInTheListIsReportedInsteadOfGuessed()
    {
        WriteVanilla();

        var stub = new StubHandler().RespondTo("/downloads", HttpStatusCode.OK, OptiFineFixture.ListHtml);
        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools).InstallAsync("1.20.4", "HD_U_I99", _gameDirectory);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "列表里没有");
        Assert.AreEqual(0, tools.Runs.Count);
    }

    [TestMethod]
    public async Task AListOutageIsReportedInsteadOfBeingTreatedAsNoBuilds()
    {
        WriteVanilla();

        var stub = new StubHandler().RespondTo("/downloads", HttpStatusCode.BadGateway);
        var tools = new FakeToolRunner();

        var result = await NewInstaller(stub, tools).InstallAsync("1.20.4", "HD_U_I8", _gameDirectory);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "取 OptiFine 列表失败");
        StringAssert.Contains(TestText.Of(result.Failure), "502");
        Assert.AreEqual(0, tools.Runs.Count);
    }

    // ==================== 辅助 ====================

    private static string AdloadPage() =>
        "<html><a href=\"downloadx?f=OptiFine_1.20.4_HD_U_I8.jar&amp;x=abc123\">download</a></html>";

    private OptiFineInstaller NewInstaller(StubHandler stub, FakeToolRunner tools)
    {
        var factory = new FakeHttpClientFactory(stub);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var store = new VersionJsonStore(factory, _paths, HttpHostPolicy.Default, _diagnostics);
        var downloader = new FileDownloader(
            factory, HttpHostPolicy.Default, clock, _diagnostics, static (_, _) => Task.CompletedTask);

        var vanilla = new VanillaInstaller(
            store,
            new VersionManifestClient(factory, _paths, clock, _diagnostics),
            downloader,
            new DiskSpaceChecker(_diagnostics),
            new NativeExtractor(_diagnostics),
            _diagnostics);

        return new OptiFineInstaller(
            tools,
            vanilla,
            downloader,
            store,
            new OptiFineCatalogClient(factory, _diagnostics),
            new FakeJavaRuntimeCatalog(
                [
                    new JavaInstallation(
                        ExecutablePath: @"C:\Java\jdk-17\bin\javaw.exe",
                        HomeDirectory: @"C:\Java\jdk-17",
                        VersionText: "17.0.9",
                        MajorVersion: 17,
                        Vendor: "Eclipse Temurin",
                        Bits: 64),
                ],
                hasDetectionResult: true),
            _paths,
            _diagnostics);
    }

    /// <summary>把原版铺好：版本 JSON + 客户端 JAR（安装器两者都要）。</summary>
    private void WriteVanilla()
    {
        var directory = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "1.20.4.json"),
            """{"id":"1.20.4","mainClass":"net.minecraft.client.main.Main","javaVersion":{"majorVersion":17}}""",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(directory, "1.20.4.jar"), "client jar bytes", new UTF8Encoding(false));
    }
}
