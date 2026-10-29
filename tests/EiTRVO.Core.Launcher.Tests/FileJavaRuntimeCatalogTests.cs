using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M3：<see cref="FileJavaRuntimeCatalog"/> —— 真实发现 + 真实进程探测（这里用替身）+ 磁盘缓存。
/// <para>
/// 缓存这件事不是为了快，而是**能不能用**：检测要跑若干次 <c>java -version</c>，
/// 冷启动同步做会让启动器卡住；而缓存里的条目可能已经失效（Java 被卸载、盘符变了），
/// 所以要测的重点是"缓存说了什么、什么时候不算数"。
/// </para>
/// </summary>
[TestClass]
public sealed class FileJavaRuntimeCatalogTests
{
    private const string Temurin17 = """
        openjdk version "17.0.9" 2023-10-17
        OpenJDK Runtime Environment Temurin-17.0.9+9 (build 17.0.9+9)
        OpenJDK 64-Bit Server VM Temurin-17.0.9+9 (build 17.0.9+9, mixed mode, sharing)
        """;

    private const string Oracle8 = """
        java version "1.8.0_421"
        Java(TM) SE Runtime Environment (build 1.8.0_421-b09)
        Java HotSpot(TM) 64-Bit Server VM (build 25.421-b09, mixed mode)
        """;

    private const string Oracle25 = """
        java version "25.0.3" 2026-04-15 LTS
        Java(TM) SE Runtime Environment (build 25.0.3+9-LTS-291)
        Java HotSpot(TM) 64-Bit Server VM (build 25.0.3+9-LTS-291, mixed mode, sharing)
        """;

    private const string Oracle17 = """
        java version "17.0.9" 2023-10-17 LTS
        Java(TM) SE Runtime Environment (build 17.0.9+11-LTS-201)
        Java HotSpot(TM) 64-Bit Server VM (build 17.0.9+11-LTS-201, mixed mode, sharing)
        """;

    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-javacatalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(new DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.FromHours(8)));
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

    /// <summary>建一个假的 JDK 目录并返回它的 bin 目录。</summary>
    private string CreateJdk(string name)
    {
        var bin = Path.Combine(_tempRoot, "jdks", name, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "javaw.exe"), string.Empty);
        return bin;
    }

    private (FileJavaRuntimeCatalog Catalog, FakeJavaProcessProbe Probe) NewCatalog(params (string Name, string Output)[] jdks)
    {
        var environment = new FakeJavaEnvironment();
        var probe = new FakeJavaProcessProbe();

        foreach (var (name, output) in jdks)
        {
            var bin = CreateJdk(name);
            var executable = Path.Combine(bin, "javaw.exe");
            environment.JavaHomes.Add(Path.Combine(_tempRoot, "jdks", name));
            probe.Respond(executable, output);
        }

        var catalog = new FileJavaRuntimeCatalog(
            new JavaDiscovery(environment),
            probe,
            _paths,
            _diagnostics,
            _clock);

        return (catalog, probe);
    }

    [TestMethod]
    public async Task AUserAddedJavaIsMergedAndSurvivesLaterDetection()
    {
        var (catalog, probe) = NewCatalog(("jdk-17", Temurin17));
        var customBin = CreateJdk("custom");
        var customExecutable = Path.Combine(customBin, "javaw.exe");
        probe.Respond(customExecutable, Oracle25);

        var inspection = await catalog.InspectCustomAsync(customExecutable);
        Assert.AreEqual(25, inspection.Installation.MajorVersion);
        Assert.IsTrue(
            inspection.Risk.HasFlag(JavaRuntimeRisk.UserWritableLocation),
            "临时目录里的 Java 要标记为可写位置风险，交给界面确认。");

        catalog.AddCustom(inspection.Installation);
        await catalog.RefreshAsync();

        Assert.IsTrue(catalog.GetInstallations().Any(static runtime => runtime.MajorVersion == 25));

        // 下一次启动（环境里找不到任何 Java）仍应从缓存读回用户添加的那一条。
        var reloaded = new FileJavaRuntimeCatalog(
            new JavaDiscovery(new FakeJavaEnvironment()),
            new FakeJavaProcessProbe(),
            _paths,
            _diagnostics,
            _clock);

        Assert.IsTrue(reloaded.GetInstallations().Any(static runtime => runtime.MajorVersion == 25));
    }

    [TestMethod]
    public async Task InspectCustomJavaMarksAnUnknownVendor()
    {
        var (catalog, probe) = NewCatalog();
        var customBin = CreateJdk("unknown-vendor");
        var customExecutable = Path.Combine(customBin, "javaw.exe");
        probe.Respond(customExecutable, "java version \"17.0.1\"\nCustom 64-Bit Server VM\n");

        var inspection = await catalog.InspectCustomAsync(customExecutable);

        Assert.IsTrue(inspection.Risk.HasFlag(JavaRuntimeRisk.UnknownVendor));
    }

    [TestMethod]
    public async Task InspectCustomJavaRejectsAFileThatIsNotJava()
    {
        var (catalog, _) = NewCatalog();
        var customBin = CreateJdk("not-java");
        var customExecutable = Path.Combine(customBin, "javaw.exe");

        await Assert.ThrowsExceptionAsync<JavaRuntimeInspectionException>(
            () => catalog.InspectCustomAsync(customExecutable));
    }

    [TestMethod]
    public async Task DetectionReadsVersionVendorAndBitsFromEachCandidate()
    {
        var (catalog, _) = NewCatalog(("jdk-17", Temurin17), ("jre-8", Oracle8));

        Assert.IsFalse(catalog.HasDetectionResult, "还没检测过时不许有结果。");
        Assert.AreEqual(0, catalog.GetInstallations().Count);

        await catalog.RefreshAsync();

        Assert.IsTrue(catalog.HasDetectionResult);

        var installations = catalog.GetInstallations();

        // 主版本从高到低（界面第一行是最新的那个）。
        Assert.AreEqual(2, installations.Count);
        Assert.AreEqual(17, installations[0].MajorVersion);
        Assert.AreEqual("17.0.9", installations[0].VersionText);
        Assert.AreEqual("Eclipse Temurin", installations[0].Vendor);
        Assert.AreEqual("64 位", TestText.Of(installations[0].BitsText), "位数是「键 + 参数」：数字在参数里、`位` 在键里。");
        Assert.AreEqual("1.18–1.20.4", installations[0].SupportedGameRange);
        Assert.AreEqual(Path.Combine(_tempRoot, "jdks", "jdk-17"), installations[0].HomeDirectory);

        Assert.AreEqual(8, installations[1].MajorVersion);
        Assert.AreEqual("Oracle", installations[1].Vendor);
    }

    [TestMethod]
    public async Task TheResultIsCachedSoAColdStartDoesNotRunAnyProcess()
    {
        var (catalog, _) = NewCatalog(("jdk-17", Temurin17));
        await catalog.RefreshAsync();

        // 下一次启动：环境里什么都没找到（比如 PATH 变了、盘还没挂上），但缓存里有上次的结果。
        var probe = new FakeJavaProcessProbe();
        var coldStart = new FileJavaRuntimeCatalog(
            new JavaDiscovery(new FakeJavaEnvironment()),
            probe,
            _paths,
            _diagnostics,
            _clock);

        Assert.IsTrue(coldStart.HasDetectionResult, "缓存里有检测结果，冷启动应当直接用它。");
        Assert.AreEqual(1, coldStart.GetInstallations().Count);
        Assert.AreEqual(17, coldStart.GetInstallations()[0].MajorVersion);
        Assert.AreEqual(0, probe.Probed.Count, "读缓存时不该跑任何进程。");
    }

    [TestMethod]
    public async Task ACachedRuntimeWhoseFileIsGoneIsDropped()
    {
        var (catalog, _) = NewCatalog(("jdk-17", Temurin17), ("jre-8", Oracle8));
        await catalog.RefreshAsync();
        Assert.AreEqual(2, catalog.GetInstallations().Count);

        // 用户卸载了 Java 8（真实世界：控制面板卸载之后缓存还留着）。
        Directory.Delete(Path.Combine(_tempRoot, "jdks", "jre-8"), recursive: true);

        var reopened = new FileJavaRuntimeCatalog(
            new JavaDiscovery(new FakeJavaEnvironment()),
            new FakeJavaProcessProbe(),
            _paths,
            _diagnostics,
            _clock);

        Assert.AreEqual(1, reopened.GetInstallations().Count);
        Assert.AreEqual(17, reopened.GetInstallations()[0].MajorVersion);
    }

    [TestMethod]
    public async Task WhenEveryCachedRuntimeIsGoneTheDetectionResultIsDiscardedToo()
    {
        // 只剩"过期的结论"时不能说"缺少 Java"：依据已经不成立了。
        var (catalog, _) = NewCatalog(("jdk-17", Temurin17), ("jre-8", Oracle8));
        await catalog.RefreshAsync();

        Directory.Delete(Path.Combine(_tempRoot, "jdks"), recursive: true);

        var reopened = new FileJavaRuntimeCatalog(
            new JavaDiscovery(new FakeJavaEnvironment()),
            new FakeJavaProcessProbe(),
            _paths,
            _diagnostics,
            _clock);

        Assert.AreEqual(0, reopened.GetInstallations().Count);
        Assert.IsFalse(reopened.HasDetectionResult, "缓存里的运行时全都没了，就不该再声称「检测过」。");
    }

    [TestMethod]
    public async Task RefreshHonoursTheFreshnessWindowButRedetectDoesNot()
    {
        var (catalog, probe) = NewCatalog(("jdk-17", Temurin17));

        await catalog.RefreshAsync();
        Assert.AreEqual(1, probe.Probed.Count);

        // 5 分钟内再进页面：不重复跑进程（"重新检测 Java"是另一个按钮）。
        await catalog.RefreshAsync();
        Assert.AreEqual(1, probe.Probed.Count);

        await catalog.RedetectAsync();
        Assert.AreEqual(2, probe.Probed.Count, "用户按了「重新检测 Java」，必须真的重跑。");

        // 时钟走过新鲜度窗口之后，普通刷新也会真的重跑。
        _clock.Now = _clock.Now.AddMinutes(6);
        await catalog.RefreshAsync();
        Assert.AreEqual(3, probe.Probed.Count);
    }

    [TestMethod]
    public async Task ACandidateThatDoesNotAnswerLikeJavaIsSkipped()
    {
        // 文件叫 javaw.exe 但不是 Java（或者装坏了）：跳过它，别在界面上编一条记录。
        var (catalog, _) = NewCatalog(
            ("broken", "Error: could not open `C:\\Java\\lib\\amd64\\jvm.cfg'"),
            ("jdk-17", Temurin17));

        await catalog.RefreshAsync();

        Assert.AreEqual(1, catalog.GetInstallations().Count);
        Assert.AreEqual(17, catalog.GetInstallations()[0].MajorVersion);
    }

    [TestMethod]
    public async Task TheSameRuntimeReachableThroughSeveralPathsIsListedOnce()
    {
        // 实机场景：一个 JDK 会同时出现在 PATH（Oracle 的 javapath 垫片）、JAVA_HOME、
        // 安装目录与 <c>latest</c> 联接里 —— 不去重的话界面上会出现三行一模一样的 25.0.3，
        // 用户会以为自己装了三个 JDK。保留先发现的那一条（PATH 在最前，也就是 `java` 实际解析到的那个）。
        var first = CreateJdk("javapath");
        var second = CreateJdk("jdk-25.0.3");

        var environment = new FakeJavaEnvironment();
        environment.PathDirectories.Add(first);
        environment.JavaHomes.Add(Path.Combine(_tempRoot, "jdks", "jdk-25.0.3"));

        var probe = new FakeJavaProcessProbe()
            .Respond(Path.Combine(first, "javaw.exe"), Oracle25)
            .Respond(Path.Combine(second, "javaw.exe"), Oracle25);

        var catalog = new FileJavaRuntimeCatalog(
            new JavaDiscovery(environment),
            probe,
            _paths,
            _diagnostics,
            _clock);

        await catalog.RefreshAsync();

        Assert.AreEqual(1, catalog.GetInstallations().Count);
        Assert.AreEqual(Path.GetDirectoryName(first), catalog.GetInstallations()[0].HomeDirectory);
    }

    [TestMethod]
    public async Task TwoDifferentRuntimesWithTheSameVersionAreStillTwoRowsWhenVendorsDiffer()
    {
        // 版本号相同但供应商不同（Temurin 与 Oracle）是两个真的不同的运行时，不能合并。
        var temurin = CreateJdk("temurin-17");
        var oracle = CreateJdk("oracle-17");

        var environment = new FakeJavaEnvironment();
        environment.PathDirectories.Add(temurin);
        environment.PathDirectories.Add(oracle);

        var probe = new FakeJavaProcessProbe()
            .Respond(Path.Combine(temurin, "javaw.exe"), Temurin17)
            .Respond(Path.Combine(oracle, "javaw.exe"), Oracle17);

        var catalog = new FileJavaRuntimeCatalog(
            new JavaDiscovery(environment),
            probe,
            _paths,
            _diagnostics,
            _clock);

        await catalog.RefreshAsync();

        Assert.AreEqual(2, catalog.GetInstallations().Count);
    }

    [TestMethod]
    public async Task InstalledRuntimesAreListedNewestFirst()
    {
        var (catalog, _) = NewCatalog(("jre-8", Oracle8), ("jdk-17", Temurin17), ("jdk-25", Oracle25));

        await catalog.RefreshAsync();

        CollectionAssert.AreEqual(
            new[] { 25, 17, 8 },
            catalog.GetInstallations().Select(static installation => installation.MajorVersion).ToArray());
    }

    [TestMethod]
    public async Task DetectionIsLoggedOnlyWhenTheOutcomeChanges()
    {
        var (catalog, _) = NewCatalog(("jdk-17", Temurin17));

        await catalog.RefreshAsync();
        await catalog.RedetectAsync();
        await catalog.RedetectAsync();

        Assert.AreEqual(
            1,
            _diagnostics.Entries.Count(entry => entry.MessageKey == "Java 运行时检测完成（{0} 个）"),
            "结果没变就不该反复写事件（事件表会被同一条消息刷满）。");
    }

    [TestMethod]
    public async Task FindingNothingIsLoggedAsAWarning()
    {
        var catalog = new FileJavaRuntimeCatalog(
            new JavaDiscovery(new FakeJavaEnvironment()),
            new FakeJavaProcessProbe(),
            _paths,
            _diagnostics,
            _clock);

        await catalog.RefreshAsync();

        Assert.IsTrue(catalog.HasDetectionResult, "「检测过、结果是零个」与「还没检测」是两件事。");
        Assert.AreEqual(0, catalog.GetInstallations().Count);
        Assert.AreEqual("未检测到任何 Java 运行时", _diagnostics.Entries.Last().MessageKey);
    }

    [TestMethod]
    public async Task ACorruptCacheIsIgnoredWithoutDeletingIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.JavaRuntimeCacheFile)!);
        File.WriteAllText(_paths.JavaRuntimeCacheFile, "{ this is not json");

        var (catalog, _) = NewCatalog(("jdk-17", Temurin17));
        await catalog.RefreshAsync();

        // 读坏了就当没有（缓存是可再生成的产物），但**不删文件**；检测照样把结果补上。
        Assert.AreEqual(1, catalog.GetInstallations().Count);
        Assert.IsTrue(File.Exists(_paths.JavaRuntimeCacheFile));
        Assert.IsTrue(_diagnostics.Entries.Any(entry => entry.MessageKey.Contains("检测缓存不可用", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ARuntimeWhoseDirectoryIsNotCalledBinIsStillGivenItsOwnHome()
    {
        // 有的发行版把可执行文件直接放在根目录（没有 bin 这一层）。
        // 路径那一列显示的是"运行时根目录"，认错了会显示一个不存在的目录 ——
        // 而这个路径还会被当成 Java 覆盖设置存起来。
        var root = Path.Combine(_tempRoot, "weird-jdk");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "javaw.exe");
        File.WriteAllText(executable, string.Empty);

        var environment = new FakeJavaEnvironment();
        environment.PathDirectories.Add(root);
        var probe = new FakeJavaProcessProbe().Respond(executable, Temurin17);

        var catalog = new FileJavaRuntimeCatalog(
            new JavaDiscovery(environment),
            probe,
            _paths,
            _diagnostics,
            _clock);

        await catalog.RefreshAsync();

        Assert.AreEqual(1, catalog.GetInstallations().Count);
        Assert.AreEqual(root, catalog.GetInstallations()[0].HomeDirectory);
    }
}
