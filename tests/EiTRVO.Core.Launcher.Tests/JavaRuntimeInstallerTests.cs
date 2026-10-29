using System.Globalization;
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
/// §12.7 的**执行**部分：官方 Java 运行时的下载 → 布局 → 搬运，全部用真文件系统 + HTTP 桩。
/// <para>
/// 三条最要紧的性质：
/// </para>
/// <list type="number">
/// <item><b>半成品不可能被当成已安装</b>：探测扫的是 <c>&lt;数据目录&gt;\runtime</c>，而下载先落暂存目录，
/// 所以任何一步失败之后，扫描根里都不会出现一个"能跑 <c>-version</c>、却缺文件"的运行时。
/// 这条只有断言"目录根本不存在"才看得见。</item>
/// <item><b>已经装好就不再下 100 MB</b>：判别依据是清单逐条核对（存在 + SHA-1），不是"目录在不在"。</item>
/// <item><b>失败之后还能接着下</b>：已落暂存目录的文件在第二次尝试里被跳过（断网续下就是这个场景）。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class JavaRuntimeInstallerTests
{
    private const string Gamma = "java-runtime-gamma";
    private const string Delta = "java-runtime-delta";
    private const string Epsilon = "java-runtime-epsilon";
    private const string Legacy = "jre-legacy";

    private string _root = string.Empty;
    private string _game = string.Empty;
    private JavaRuntimeWorld _world = null!;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一个临时数据目录（以及它下面的游戏目录）。</summary>
    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "eitrvo-jre-" + Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "minecraft");
        Directory.CreateDirectory(_game);

        _paths = new LauncherPaths(_root);
        _paths.EnsureCreated();

        _diagnostics = new RecordingDiagnosticsLog();
        _world = new JavaRuntimeWorld();
    }

    /// <summary>清掉临时目录（安装出来的运行时也一并回收）。</summary>
    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删不掉临时目录不值得让用例红。
        }
    }

    [TestMethod]
    public async Task InstallsTheRuntimeIntoTheOfficialLayout()
    {
        var gamma = PublishAllGenerations();

        var result = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(Gamma, result.VersionId, "结果里的目标名就是组件名。");
        Assert.AreEqual(DefaultFileCount, result.FilesDownloaded);
        Assert.IsTrue(result.Bytes > 0);

        // 版本 JSON 声明的是 gamma（17），索引里还有 21 / 25 两代：请求必须打到 gamma 的清单上。
        HttpAssert.Url(_world.Handler, 0, JavaRuntimeManifestClient.IndexUrl);
        HttpAssert.Url(_world.Handler, 1, gamma.ManifestUrl);

        var executable = Path.Combine(RuntimeDirectory(Gamma), "bin", "javaw.exe");
        Assert.IsTrue(File.Exists(executable), "可执行文件必须落在 runtime/<组件>/<平台>/<组件>/bin 下。");
        Assert.AreEqual("javaw-bytes", File.ReadAllText(executable, Encoding.UTF8));
        Assert.AreEqual("modules-bytes", File.ReadAllText(Path.Combine(RuntimeDirectory(Gamma), "lib", "modules"), Encoding.UTF8));

        Assert.IsFalse(Directory.Exists(StagingDirectory(Gamma)), "搬运之后暂存目录不该留下（它已经整个搬走了）。");
        Assert.IsTrue(_diagnostics.Entries.Any(entry => entry.MessageKey.Contains("{0} {1} 已就绪：{2}（新下 {3} 个、跳过 {4} 个文件）", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AnAlreadyInstalledRuntimeIsNotDownloadedAgain()
    {
        PublishAllGenerations();
        var installer = NewInstaller();

        var first = await installer.InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);
        var afterFirst = _world.Handler.Requests.Count;

        var second = await installer.InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsTrue(second.Succeeded, TestText.Of(second.Failure));
        Assert.AreEqual(0, second.FilesDownloaded, "已经完整装好时一个文件都不该再下。");
        Assert.AreEqual(DefaultFileCount, second.FilesSkipped);
        Assert.AreEqual(0, second.Bytes);

        // 只多了索引与组件清单各一次请求：一个文件都没再下。
        // （清单必须再取一次：判断"装好没装好"的依据就是那份清单。）
        Assert.AreEqual(afterFirst + 2, _world.Handler.Requests.Count);
        Assert.IsTrue(_diagnostics.Entries.Any(entry => entry.MessageKey.Contains("已是最新", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AnOutdatedRuntimeIsReplacedAndTheOldOneIsMovedToRecycle()
    {
        PublishAllGenerations();
        var installer = NewInstaller();

        var first = await installer.InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);
        Assert.IsTrue(first.Succeeded, TestText.Of(first.Failure));

        // Mojang 换了组件版本：清单多一个文件、且原来的文件内容也变了（升级的典型形状）。
        var world = new JavaRuntimeWorld();
        world.Publish(
            Gamma,
            "17.0.16",
            [
                JavaRuntimeFixture.Directory("bin"),
                JavaRuntimeFixture.File("bin/javaw.exe", "javaw-17.0.16"),
                JavaRuntimeFixture.File("lib/modules", "modules-bytes"),
                JavaRuntimeFixture.File("release", "JAVA_VERSION=17.0.16"),
                JavaRuntimeFixture.File("lib/new.txt", "new"),
            ]);
        world.PublishIndex();

        var second = await NewInstaller(world).InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsTrue(second.Succeeded, TestText.Of(second.Failure));
        Assert.AreEqual("javaw-17.0.16", File.ReadAllText(Path.Combine(RuntimeDirectory(Gamma), "bin", "javaw.exe"), Encoding.UTF8));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(RuntimeDirectory(Gamma), "lib", "new.txt"), Encoding.UTF8));

        // 旧的那一份**搬进回收目录**，而不是被递归删掉（我们造出来的东西也不该被无声抹掉）。
        var recycled = Directory.GetDirectories(Path.Combine(_root, "recycle"));
        Assert.AreEqual(1, recycled.Length, "旧的运行时应当整体搬进回收目录。");
        StringAssert.Contains(Path.GetFileName(recycled[0]), Gamma);
        Assert.AreEqual("javaw-bytes", File.ReadAllText(Path.Combine(recycled[0], "bin", "javaw.exe"), Encoding.UTF8));
    }

    [TestMethod]
    public async Task AFailedFileLeavesNoRuntimeDirectoryButKeepsTheStagingCopy()
    {
        _world.Publish(Gamma, "17.0.15", DefaultEntries(), corruptPaths: ["bin/javaw.exe"]);
        _world.PublishIndex();

        var result = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "校验不通过");

        // 最关键的一条：扫描根里**不能**出现一个半成品 —— 探测只认 bin\javaw.exe，
        // 而一个缺 lib\modules 的运行时可能连 `java -version` 都跑得起来，于是会被当成"可用的 Java"。
        Assert.IsFalse(Directory.Exists(RuntimeDirectory(Gamma)), "失败时不许留下运行时目录。");

        // 已经下好的那几份留在暂存目录里：重试要接着下，不是从零开始。
        Assert.IsTrue(File.Exists(Path.Combine(StagingDirectory(Gamma), "lib", "modules")));
        Assert.IsFalse(File.Exists(Path.Combine(StagingDirectory(Gamma), "bin", "javaw.exe")));
    }

    [TestMethod]
    public async Task TheSecondAttemptSkipsTheFilesThatAreAlreadyGood()
    {
        var entries = DefaultEntries();

        // 第一次：lib\modules 反复失败，其余三个下好并留在暂存目录。
        _world.Publish(Gamma, "17.0.15", entries, failingPaths: ["lib/modules"]);
        _world.PublishIndex();

        var first = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);
        Assert.IsFalse(first.Succeeded);
        Assert.AreEqual(1, ObjectRequestCountFor("javaw-bytes"), "第一次里每个好文件只请求一次。");

        // 第二次：已经下好并校验通过的文件不再请求（断网续下就是这个场景）。
        var second = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);
        Assert.IsFalse(second.Succeeded);

        Assert.AreEqual(1, ObjectRequestCountFor("javaw-bytes"), "重试要接着下，不是从零开始。");
        Assert.IsTrue(
            ObjectRequestCountFor("modules-bytes") >= 2,
            "一直失败的那个文件在重试里仍然会被请求（每次尝试一次）。");
    }

    [TestMethod]
    public async Task TheDeclaredJavaVersionDecidesWhichGenerationIsFetched()
    {
        WriteVersionJson("1.20.4", Gamma, 17);
        WriteVersionJson("1.12.2", component: null, major: null);
        PublishAllGenerations();

        var declared = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);
        Assert.AreEqual(Gamma, declared.VersionId);

        // 1.12.2 的本地 JSON 没有 javaVersion → 规则表兜底（LWJGL 2 封在 Java 8）→ jre-legacy。
        var legacy = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.12.2"), _game);
        Assert.AreEqual(Legacy, legacy.VersionId, TestText.Of(legacy.Failure));
        Assert.IsTrue(File.Exists(Path.Combine(RuntimeDirectory(Legacy), "bin", "javaw.exe")));
    }

    [TestMethod]
    public async Task WithoutAVersionContextTheNewestGenerationIsFetched()
    {
        PublishAllGenerations();

        var result = await NewInstaller().InstallAsync(new JavaRuntimeRequest(null), _game);

        // p10 的「获取 Java 25」在没选过版本时就是这个语义。
        Assert.AreEqual(Epsilon, result.VersionId, TestText.Of(result.Failure));
        Assert.IsTrue(Directory.Exists(RuntimeDirectory(Epsilon)));
    }

    [TestMethod]
    public async Task AManifestWithoutAJavaExecutableFailsInsteadOfInstallingAHalfRuntime()
    {
        _world.Publish(Gamma, "17.0.15", [JavaRuntimeFixture.File("release", "JAVA_VERSION=17.0.15")]);
        _world.PublishIndex();

        var result = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "javaw.exe");
        Assert.IsFalse(Directory.Exists(RuntimeDirectory(Gamma)));
    }

    [TestMethod]
    public async Task InsufficientDiskSpaceStopsBeforeAnyFileIsDownloaded()
    {
        PublishAllGenerations();

        var result = await NewInstaller(freeSpaceProbe: static _ => 0)
            .InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "磁盘空间不足");
        Assert.AreEqual(
            0,
            _world.Handler.Requests.Count(request =>
                request.RequestUri!.AbsolutePath.Contains("/v1/objects/", StringComparison.Ordinal)),
            "空间不够就一个字节都不该下 —— 这正是预检放在下载之前的原因。");
    }

    [TestMethod]
    public async Task PathsThatEscapeTheRuntimeRootRejectTheWholeManifest()
    {
        _world.Publish(Gamma, "17.0.15", [JavaRuntimeFixture.File("../evil.exe", "evil")]);
        _world.PublishIndex();

        var result = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(TestText.Of(result.Failure), "不安全");
        Assert.IsFalse(File.Exists(Path.Combine(_root, "evil.exe")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "runtime")));
    }

    [TestMethod]
    public async Task AManifestLinkIsCopiedBecauseWindowsSymlinksNeedPrivileges()
    {
        // windows-x64 的真实清单里一条 link 都没有（实测 484 条 = 402 文件 + 82 目录），
        // 这条路径是防御性的：真出现时复制一份目标，而不是留下一条指向空气的链接。
        _world.Publish(
            Gamma,
            "17.0.15",
            [
                JavaRuntimeFixture.File("bin/javaw.exe", "javaw-bytes"),
                JavaRuntimeFixture.File("lib/jvm.dll", "jvm-dll"),
                JavaRuntimeFixture.Directory("bin/server"),
                JavaRuntimeFixture.Link("bin/server/jvm.dll", "lib/jvm.dll"),
            ]);
        _world.PublishIndex();

        var result = await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.IsTrue(result.Succeeded, TestText.Of(result.Failure));
        Assert.AreEqual(
            "jvm-dll",
            File.ReadAllText(Path.Combine(RuntimeDirectory(Gamma), "bin", "server", "jvm.dll"), Encoding.UTF8));
    }

    [TestMethod]
    public async Task TheIndexIsReadOnlyOncePerInstall()
    {
        PublishAllGenerations();

        await NewInstaller().InstallAsync(new JavaRuntimeRequest("1.20.4"), _game);

        Assert.AreEqual(
            1,
            _world.Handler.Requests.Count(request =>
                string.Equals(request.RequestUri!.ToString(), JavaRuntimeManifestClient.IndexUrl, StringComparison.Ordinal)),
            "一次安装只读一次索引（索引里有全部组件，不需要按组件重复取）。");
    }

    // ==================== 辅助 ====================

    /// <summary><see cref="DefaultEntries"/> 里的**文件**数（那份清单是 4 条条目：3 个文件 + 1 个目录）。</summary>
    private const int DefaultFileCount = 3;

    private static JavaRuntimeFixture.Entry[] DefaultEntries(string version = "17.0.15") =>
    [
        JavaRuntimeFixture.Directory("bin"),
        JavaRuntimeFixture.File("bin/javaw.exe", "javaw-bytes"),
        JavaRuntimeFixture.File("lib/modules", "modules-bytes"),
        JavaRuntimeFixture.File("release", "JAVA_VERSION=" + version),
    ];

    /// <summary>把真实存在的四代组件都挂到桩上，并挂上索引；返回 gamma 那一条（断言 URL 要用）。</summary>
    private JavaRuntimeFixture.ComponentRelease PublishAllGenerations()
    {
        // 每一代的清单内容不同（release 文件里写着版本号）→ 清单地址也不同，
        // 于是"请求打到哪一代的清单上"才是可断言的，而不是四代共用一份内容。
        _ = _world.Publish(Legacy, "8u51-cacert462b08", DefaultEntries("8u51"));
        var gamma = _world.Publish(Gamma, "17.0.15", DefaultEntries());
        _ = _world.Publish(Delta, "21.0.7", DefaultEntries("21.0.7"));
        _ = _world.Publish(Epsilon, "25.0.1", DefaultEntries("25.0.1"));

        _world.PublishIndex();

        return gamma;
    }

    /// <summary>某个对象（按内容 SHA-1 认）被请求过几次。</summary>
    private int ObjectRequestCountFor(string body) =>
        _world.Handler.Requests.Count(request =>
            request.RequestUri!.AbsolutePath.Contains(JavaRuntimeFixture.Sha1Hex(body), StringComparison.Ordinal));

    /// <summary>写一份已安装的版本 JSON（<c>component</c> 为 <see langword="null"/> 时不写 <c>javaVersion</c>）。</summary>
    private void WriteVersionJson(string versionId, string? component, int? major)
    {
        var directory = Path.Combine(_game, "versions", versionId);
        Directory.CreateDirectory(directory);

        var javaVersion = component is null || major is null
            ? string.Empty
            : ",\"javaVersion\":{\"component\":\"" + component + "\",\"majorVersion\":"
              + major.Value.ToString(CultureInfo.InvariantCulture) + "}";

        File.WriteAllText(
            Path.Combine(directory, versionId + ".json"),
            "{\"id\":\"" + versionId + "\"" + javaVersion + "}",
            new UTF8Encoding(false));
    }

    private string RuntimeDirectory(string component) =>
        Path.Combine(_root, "runtime", component, JavaRuntimeFixture.Platform, component);

    private string StagingDirectory(string component) =>
        Path.Combine(_root, "cache", "downloads", JavaRuntimePlanner.StagingFolderName, component, JavaRuntimeFixture.Platform);

    /// <summary>造一个指向当前世界的安装器（工厂、时钟、诊断都按用例替换）。</summary>
    private JavaRuntimeInstaller NewInstaller(JavaRuntimeWorld? world = null, Func<string, long>? freeSpaceProbe = null)
    {
        var actual = world ?? _world;
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

        return new JavaRuntimeInstaller(
            new JavaRuntimeManifestClient(actual.Factory, HttpHostPolicy.Default, _diagnostics),
            new VersionJsonStore(actual.Factory, _paths, HttpHostPolicy.Default, _diagnostics),
            new FileDownloader(actual.Factory, HttpHostPolicy.Default, clock, _diagnostics, static (_, _) => Task.CompletedTask),
            new DiskSpaceChecker(_diagnostics, freeSpaceProbe),
            _paths,
            _diagnostics,
            clock);
    }
}
