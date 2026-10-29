using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M1 实例目录（A-1…A-6）：真实目录扫描、新建、删除进回收目录、元数据容错。
/// <para>
/// 这一组用例全部在临时目录上跑真实文件操作。实例目录是本仓库里最像"用户数据"的东西，
/// 用 mock 文件系统测它就等于什么都没测。
/// </para>
/// </summary>
[TestClass]
public sealed class InstanceCatalogTests
{
    private static readonly DateTimeOffset StartupTime =
        new(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8));

    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private FileSettingsCatalog _settings = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录与固定时钟。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-instances", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(StartupTime);
        _settings = new FileSettingsCatalog(_paths, _diagnostics);
        _settings.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = Path.Combine(_tempRoot, "game"),
        });
        _diagnostics.Entries.Clear();
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

    private FileInstanceCatalog NewCatalog() => new(_paths, _settings, _clock, _diagnostics);

    // ------------------------------------------------------------ 命名 ---

    [TestMethod]
    public void TheSuggestedNameFollowsTheDesignShape()
    {
        Assert.AreEqual("1.20.4-Forge", InstanceNaming.SuggestUniqueName("1.20.4", LoaderKind.Forge));
        Assert.AreEqual("1.21.4-Vanilla", InstanceNaming.SuggestUniqueName("1.21.4", LoaderKind.Vanilla));
    }

    [TestMethod]
    public void ADuplicateNameGetsASuffix()
    {
        Assert.AreEqual(
            "1.20.4-Forge-2",
            InstanceNaming.SuggestUniqueName("1.20.4", LoaderKind.Forge, ["1.20.4-Forge"]));
        Assert.AreEqual(
            "1.20.4-Forge-3",
            InstanceNaming.SuggestUniqueName("1.20.4", LoaderKind.Forge, ["1.20.4-Forge", "1.20.4-Forge-2"]));
    }

    [TestMethod]
    public void NameComparisonIgnoresCaseBecauseWindowsPathsDo()
    {
        Assert.AreEqual(
            "1.20.4-Forge-2",
            InstanceNaming.SuggestUniqueName("1.20.4", LoaderKind.Forge, ["1.20.4-forge"]));
    }

    [TestMethod]
    public void AMissingVersionStillProducesAUsableName()
    {
        Assert.AreEqual("未指定版本-Vanilla", InstanceNaming.SuggestUniqueName("  ", LoaderKind.Vanilla));
    }

    [TestMethod]
    public void LoaderTextHandlesTheVanillaSpecialCase()
    {
        // 显示名是「键 + 参数」：`原版` 是**文案**（英文界面要显示 Vanilla），
        // 其余是专有名词（纯数据键 `{0}`）；版本号进参数。
        Assert.AreEqual("原版", InstanceNaming.Display(LoaderKind.Vanilla).Key);
        Assert.AreEqual("{0}", InstanceNaming.Display(LoaderKind.Forge).Key);
        Assert.AreEqual("原版", TestText.Of(InstanceNaming.Display(LoaderKind.Vanilla)));
        Assert.AreEqual("Forge", TestText.Of(InstanceNaming.Display(LoaderKind.Forge)));
        Assert.AreEqual("Forge 49.0.30", TestText.Of(InstanceNaming.DisplayWithVersion(LoaderKind.Forge, "49.0.30")));
        Assert.AreEqual("原版", TestText.Of(InstanceNaming.DisplayWithVersion(LoaderKind.Vanilla, null)));
        Assert.AreEqual("Fabric 0.15.7", TestText.Of(InstanceNaming.DisplayWithVersion(LoaderKind.Fabric, " 0.15.7 ")));

        // 事件参数 / 日志那一份是**纯数据**：ASCII、与语言无关（`原版` 在这里写 Vanilla）。
        Assert.AreEqual("Vanilla", InstanceNaming.DataDisplay(LoaderKind.Vanilla));
        Assert.AreEqual("Forge 49.0.30", InstanceNaming.DataDisplay(LoaderKind.Forge, " 49.0.30 "));
        Assert.AreEqual("Fabric", InstanceNaming.DataDisplay(LoaderKind.Fabric));
    }

    // -------------------------------------------------------- 数值文本 ---

    [TestMethod]
    [DataRow(0L, "0 B")]
    [DataRow(512L, "512 B")]
    [DataRow(1536L, "1.5 KB")]
    [DataRow(13314398618L, "12.4 GB")]
    public void SizesAreFormattedLikeTheDesign(long bytes, string expected) =>
        Assert.AreEqual(expected, InstanceText.FormatSize(bytes));

    [TestMethod]
    [DataRow(0L, "0 分")]
    [DataRow(1800L, "30 分")]
    [DataRow(455460L, "126 小时 31 分")]
    public void PlayTimeIsFormattedLikeTheDesign(long seconds, string expected) =>
        Assert.AreEqual(expected, TestText.Of(InstanceText.FormatPlayTime(seconds)));

    [TestMethod]
    public void TimestampsUseTodayYesterdayAndAbsoluteDates()
    {
        var now = StartupTime;

        // 时间戳现在是「键 + 参数」：断言键与参数（不是拼好的句子），"从未…"那几个键由调用方给。
        var today = InstanceText.FormatTimestamp(now, now, TextTemplate.Of("从未启动"));

        Assert.AreEqual("今天 {0}", today.Key);
        CollectionAssert.AreEqual(new[] { "16:01" }, today.Parameters.ToArray());

        var yesterday = InstanceText.FormatTimestamp(
            new DateTimeOffset(2026, 9, 29, 21, 30, 0, TimeSpan.FromHours(8)),
            now,
            TextTemplate.Of("从未启动"));

        Assert.AreEqual("昨天 {0}", yesterday.Key);
        CollectionAssert.AreEqual(new[] { "21:30" }, yesterday.Parameters.ToArray());

        // 绝对时间那一支是**纯数据键**（`2026/9/20 19:02` 两种语言逐字相同）。
        var absolute = InstanceText.FormatTimestamp(
            new DateTimeOffset(2026, 9, 20, 19, 2, 0, TimeSpan.FromHours(8)),
            now,
            TextTemplate.Of("从未启动"));

        Assert.AreEqual("{0}", absolute.Key);
        CollectionAssert.AreEqual(new[] { "2026/9/20 19:02" }, absolute.Parameters.ToArray());

        Assert.AreEqual("从未启动", InstanceText.FormatTimestamp(null, now, TextTemplate.Of("从未启动")).Key);
    }

    [TestMethod]
    public void WindowModeTextCoversAllThreeModes()
    {
        // 窗口模式是「键 + 参数」：**模式词进键**（三个键），尺寸进参数 ——
        // 反过来（把「窗口化」当参数）在英文界面里会露出三个汉字。
        Assert.AreEqual("1920 × 1080 窗口化", TestText.Of(InstanceText.FormatWindowMode(1920, 1080, WindowLaunchMode.Windowed)));
        Assert.AreEqual("1920 × 1080 全屏", TestText.Of(InstanceText.FormatWindowMode(1920, 1080, WindowLaunchMode.Fullscreen)));
        Assert.AreEqual("1920 × 1080 无边框全屏", TestText.Of(InstanceText.FormatWindowMode(1920, 1080, WindowLaunchMode.Borderless)));

        var windowed = InstanceText.FormatWindowMode(1920, 1080, WindowLaunchMode.Windowed);
        Assert.AreEqual("{0} × {1} 窗口化", windowed.Key);
        CollectionAssert.AreEqual(new[] { "1920", "1080" }, windowed.Parameters.ToArray());
    }

    // -------------------------------------------------------- 空数据目录 ---

    [TestMethod]
    public void AnEmptyDataDirectoryHasNoInstances()
    {
        var catalog = NewCatalog();

        Assert.AreEqual(0, catalog.GetInstances().Count);
        Assert.AreEqual(0, catalog.GetOverview().Count);
        Assert.AreEqual(string.Empty, catalog.ActiveInstanceName, "没有实例时当前实例是空串，而不是抛异常。");
    }

    [TestMethod]
    public void TheCatalogIsBornWithTheDirectoryLayoutReady()
    {
        _ = NewCatalog();

        // 新布局不再使用启动器目录下的 instances\：一个实例 = <游戏目录>\versions\<实例>。
        Assert.IsFalse(
            Directory.Exists(_paths.InstancesDirectory),
            "新布局不该再创建旧的实例元数据目录。");
        Assert.IsTrue(Directory.Exists(_paths.RecycleDirectory));
    }

    [TestMethod]
    public void LegacyMetadataIsCopiedIntoTheGameVersionsDirectoryAndOldFilesAreKept()
    {
        // 旧布局：启动器目录的 instances\<实例> 里只有元数据；新布局把它**复制**到
        // <游戏目录>\versions\<实例>，旧文件原样保留（不删除用户数据）。
        var legacy = _paths.InstanceDirectory("旧实例");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(
            Path.Combine(legacy, "instance.json"),
            "{\"gameVersion\": \"1.20.4\"}",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(legacy, "overrides.json"), "{}", new UTF8Encoding(false));

        var catalog = NewCatalog();

        var target = InstancePaths.Directory(_settings.GetGlobal(), "旧实例");
        Assert.IsTrue(File.Exists(Path.Combine(target, "instance.json")), "元数据必须复制到新目录。");
        Assert.IsTrue(File.Exists(Path.Combine(target, "overrides.json")), "实例设置也要一起复制。");
        Assert.IsTrue(Directory.Exists(legacy), "旧文件不删除（用户回退版本时还能拿回来）。");
        Assert.AreEqual(1, catalog.GetInstances().Count);
        Assert.AreEqual("1.20.4", TestText.Of(catalog.GetDetail("旧实例").GameVersion));
    }

    // -------------------------------------------------------------- 新建 ---

    [TestMethod]
    public void CreatingAnInstanceWritesItsMetadataAndSkeleton()
    {
        var catalog = NewCatalog();

        var detail = catalog.CreateInstance(new InstanceCreationRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));

        var directory = InstancePaths.Directory(_settings.GetGlobal(), "1.20.4-Forge");
        Assert.IsTrue(File.Exists(InstancePaths.MetadataFile(_settings.GetGlobal(), "1.20.4-Forge")));
        foreach (var skeleton in FileInstanceCatalog.SkeletonDirectories)
        {
            Assert.IsTrue(Directory.Exists(Path.Combine(directory, skeleton)), $"应建立 {skeleton} 目录。");
        }

        Assert.AreEqual("1.20.4-Forge", detail.Name);
        Assert.AreEqual("1.20.4", TestText.Of(detail.GameVersion));
        Assert.AreEqual("Forge 49.0.30", TestText.Of(detail.LoaderDisplay));
        Assert.IsTrue(detail.IsolatedDirectory);
        Assert.AreEqual(directory, detail.InstancePath);
        Assert.AreEqual("从未启动", detail.LastLaunched.Key);

        // 新建的实例还没校验过：这一支就是「从未校验」本身（"已完成 + 时间"是另一支，见下面的用例）。
        Assert.AreEqual("从未校验", detail.ResourceVerification.Key);
        Assert.IsNull(detail.ResourceVerification.Note);
        Assert.AreEqual("未绑定", TestText.Of(detail.Account));
        Assert.AreEqual("0 分", TestText.Of(detail.PlayTime));
        Assert.AreEqual(0, detail.LaunchCount);
        Assert.AreEqual("0 个", TestText.Of(detail.ModSummary));
    }

    [TestMethod]
    public void TheMetadataFileIsReadableAndVersioned()
    {
        NewCatalog().CreateInstance(new InstanceCreationRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));

        var text = File.ReadAllText(
            InstancePaths.MetadataFile(_settings.GetGlobal(), "1.20.4-Forge"),
            Encoding.UTF8);

        StringAssert.Contains(text, "\"schemaVersion\": 1");
        StringAssert.Contains(text, "\"gameVersion\": \"1.20.4\"");
        StringAssert.Contains(text, "\"loader\": \"Forge\"");
        StringAssert.Contains(text, "\"installState\": \"NotInstalled\"");
        StringAssert.Contains(text, "2026-09-30T16:01:02", "创建时间必须来自注入的时钟。");
    }

    [TestMethod]
    public void ANewInstanceInheritsTheGlobalDefaults()
    {
        _settings.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = Path.Combine(_tempRoot, "d-game"),
            MaxMemoryMb = 2048,
            WindowWidth = 1280,
            WindowHeight = 720,
        });
        var catalog = NewCatalog();

        var detail = catalog.CreateInstance(new InstanceCreationRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge));

        Assert.AreEqual(Path.Combine(_tempRoot, "d-game"), detail.GameDirectory);
        Assert.AreEqual(2048, detail.MemoryMb);
        Assert.AreEqual("1280 × 720 窗口化", TestText.Of(detail.WindowMode));
    }

    [TestMethod]
    public void TheFirstCreatedInstanceBecomesTheActiveOne()
    {
        var catalog = NewCatalog();

        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        Assert.AreEqual("a", catalog.ActiveInstanceName);

        catalog.CreateInstance(new InstanceCreationRequest("b", "1.20.4", LoaderKind.Vanilla));
        Assert.AreEqual("a", catalog.ActiveInstanceName, "已有当前实例时不应被后来的创建抢走。");
    }

    [TestMethod]
    public void CreatingADuplicateInstanceFails()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));

        Assert.ThrowsException<InvalidOperationException>(
            () => catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla)));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("a/b")]
    [DataRow("CON")]
    [DataRow("")]
    public void CreatingAnInstanceWithAnUnsafeNameFails(string name)
    {
        var catalog = NewCatalog();

        Assert.ThrowsException<ArgumentException>(
            () => catalog.CreateInstance(new InstanceCreationRequest(name, "1.20.4", LoaderKind.Vanilla)));

        // 游戏目录的 versions\ 下不该多出任何东西（非法名字不该留下目录）。
        var versionsDirectory = InstancePaths.VersionsDirectory(_settings.GetGlobal());
        Assert.IsFalse(
            Directory.Exists(versionsDirectory) && Directory.GetFileSystemEntries(versionsDirectory).Length > 0,
            "非法名字不该在游戏目录里留下任何目录。");
    }

    [TestMethod]
    public void RenamingMovesTheDirectoryAndUpdatesTheActiveInstance()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));

        var detail = catalog.RenameInstance("a", "b");

        Assert.AreEqual("b", detail.Name);
        Assert.IsFalse(Directory.Exists(InstancePaths.Directory(_settings.GetGlobal(), "a")));
        Assert.IsTrue(Directory.Exists(InstancePaths.Directory(_settings.GetGlobal(), "b")));
        Assert.AreEqual("b", catalog.ActiveInstanceName);
        Assert.AreEqual("b", catalog.GetInstances().Single().Name);
    }

    [TestMethod]
    public void RenamingToAnExistingNameFails()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        catalog.CreateInstance(new InstanceCreationRequest("b", "1.20.4", LoaderKind.Vanilla));

        Assert.ThrowsException<InvalidOperationException>(() => catalog.RenameInstance("a", "b"));
    }

    [TestMethod]
    public void TheInstanceSurvivesARestart()
    {
        NewCatalog().CreateInstance(new InstanceCreationRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));

        // 换一个实例（等价于关掉程序再打开）：数据必须来自磁盘。
        var reopened = NewCatalog();

        Assert.AreEqual(1, reopened.GetInstances().Count);
        Assert.AreEqual("1.20.4-Forge", reopened.ActiveInstanceName);
        Assert.AreEqual("1.20.4", TestText.Of(reopened.GetDetail("1.20.4-Forge").GameVersion));
    }

    // -------------------------------------------------------- 列表与详情 ---

    [TestMethod]
    public void InstancesAreSortedByLastLaunchThenName()
    {
        WriteInstance("很久没玩", lastLaunchedAt: "2026-09-01T10:00:00+08:00");
        WriteInstance("刚刚玩过", lastLaunchedAt: "2026-09-30T15:00:00+08:00");
        WriteInstance("从未启动-B", lastLaunchedAt: null);
        WriteInstance("从未启动-A", lastLaunchedAt: null);

        var names = NewCatalog().GetInstances().Select(record => record.Name).ToArray();

        CollectionAssert.AreEqual(
            new[] { "刚刚玩过", "很久没玩", "从未启动-A", "从未启动-B" },
            names);
    }

    [TestMethod]
    public void TheActiveInstanceIsMarkedInTheTable()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        catalog.CreateInstance(new InstanceCreationRequest("b", "1.21.4", LoaderKind.Vanilla));

        catalog.ActiveInstanceName = "b";

        Assert.IsFalse(catalog.GetInstances().Single(row => row.Name == "a").IsActive);
        Assert.IsTrue(catalog.GetInstances().Single(row => row.Name == "b").IsActive);
    }

    [TestMethod]
    public void AHandCopiedInstanceDirectoryWithMetadataIsListed()
    {
        // 「把别人的实例目录拷进来」是正常路径：有 instance.json 就是实例。
        var directory = InstancePaths.Directory(_settings.GetGlobal(), "拷来的实例");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "instance.json"),
            "{\"gameVersion\": \"1.20.4\"}",
            new UTF8Encoding(false));

        var catalog = NewCatalog();
        var record = catalog.GetInstances().Single();

        Assert.AreEqual("拷来的实例", record.Name);
        Assert.AreEqual("1.20.4", TestText.Of(record.GameVersion));
        Assert.AreEqual("原版", TestText.Of(record.LoaderDisplay));
        Assert.AreEqual(0, _diagnostics.Entries.Count, "缺元数据不是错误，不该报警。");
    }

    [TestMethod]
    public void AVersionDirectoryWithoutInstanceMetadataIsNotAnInstance()
    {
        // 只装了一个版本（p05 的「安装所选版本」）、还没建实例：versions\ 下有目录但不是实例。
        var versions = InstancePaths.VersionsDirectory(_settings.GetGlobal());
        Directory.CreateDirectory(Path.Combine(versions, "1.20.4"));

        Assert.AreEqual(0, NewCatalog().GetInstances().Count);
    }

    [TestMethod]
    public void StatusIsReadyOnceTheVersionJsonIsOnDisk()
    {
        // 用临时游戏目录：默认值是用户真实的 .minecraft，测试绝不能往那里写版本 JSON。
        var gameDir = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(gameDir);
        _settings.SaveGlobal(_settings.GetGlobal() with { GameDirectory = gameDir });

        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla));

        Assert.AreEqual("未安装", TestText.Of(catalog.GetInstances().Single().StatusText));

        // 安装器最后写的就是实例目录里的 version.json（先写它会让"已安装"在文件还没下完时变真）。
        File.WriteAllText(
            Path.Combine(InstancePaths.Directory(_settings.GetGlobal(), "1.20.4-Vanilla"), "version.json"),
            "{}");

        Assert.AreEqual("就绪", TestText.Of(catalog.GetInstances().Single().StatusText), "版本 JSON 在 → p02 必须显示就绪。");
    }

    [TestMethod]
    public void APartialInstallDirectoryWithoutTheVersionJsonNeedsRepair()
    {
        var gameDir = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(gameDir);
        _settings.SaveGlobal(_settings.GetGlobal() with { GameDirectory = gameDir });

        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla));

        // 版本目录建了、JSON 还没写：这正是安装器中途失败留下的形状。
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.20.4"));

        Assert.AreEqual("需要修复", TestText.Of(catalog.GetInstances().Single().StatusText));
    }
    [TestMethod]
    public void ALoaderInstanceIsReadyOnlyWhenItsLoaderJsonIsOnDisk()
    {
        var gameDir = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(gameDir);
        _settings.SaveGlobal(_settings.GetGlobal() with { GameDirectory = gameDir });

        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));

        // 只有原版基底、没有实例自己的 version.json：这个实例还启动不了，不能报「就绪」。
        var versions = Path.Combine(gameDir, "versions");
        var vanilla = Path.Combine(versions, "1.20.4");
        Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(vanilla, "1.20.4.json"), "{}");

        Assert.AreEqual("未安装", TestText.Of(catalog.GetInstances().Single().StatusText));

        // 安装器把版本写进实例目录之后（Neo 26.1 的一实例一目录结构）才算装好。
        File.WriteAllText(
            Path.Combine(InstancePaths.Directory(_settings.GetGlobal(), "1.20.4-Forge"), "version.json"),
            "{}");

        Assert.AreEqual("就绪", TestText.Of(catalog.GetInstances().Single().StatusText));
    }
    [TestMethod]
    public void CorruptMetadataFallsBackToDefaultsAndKeepsTheFile()
    {
        var directory = InstancePaths.Directory(_settings.GetGlobal(), "坏的实例");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "instance.json");
        File.WriteAllText(path, "{\"gameVersion\": \"1.20", new UTF8Encoding(false));

        var record = NewCatalog().GetInstances().Single();

        Assert.AreEqual("未指定", TestText.Of(record.GameVersion));
        Assert.AreEqual("{\"gameVersion\": \"1.20", File.ReadAllText(path, Encoding.UTF8), "原文件必须保留。");
        Assert.AreEqual(LauncherEventLevel.Warning, _diagnostics.Entries.Single().Level);
    }

    [TestMethod]
    public void AStrayDirectoryWithAnUnusableNameIsIgnored()
    {
        // versions\ 下可能被别的启动器 / 手工操作留下乱七八糟的目录：没有 instance.json 的一律不当实例。
        Directory.CreateDirectory(Path.Combine(
            InstancePaths.VersionsDirectory(_settings.GetGlobal()),
            "not-an-instance"));

        Assert.AreEqual(0, NewCatalog().GetInstances().Count);
    }

    [TestMethod]
    public void AskingForAnUnknownInstanceFails()
    {
        var catalog = NewCatalog();

        Assert.ThrowsException<KeyNotFoundException>(() => catalog.GetDetail("不存在"));
        Assert.ThrowsException<KeyNotFoundException>(() => catalog.GetActions("不存在"));
    }

    [TestMethod]
    public void SettingTheActiveInstanceToSomethingThatDoesNotExistFails()
    {
        var catalog = NewCatalog();

        Assert.ThrowsException<ArgumentException>(() => catalog.ActiveInstanceName = "不存在");
    }

    [TestMethod]
    public void ClearingTheActiveInstanceFallsBackToTheFirstOne()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        catalog.CreateInstance(new InstanceCreationRequest("b", "1.20.4", LoaderKind.Vanilla));

        catalog.ActiveInstanceName = string.Empty;

        Assert.AreEqual("a", catalog.ActiveInstanceName);
    }

    [TestMethod]
    public void ModsInTheInstanceDirectoryAreCounted()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Forge));

        var mods = Path.Combine(InstancePaths.ContentDirectory(_settings.GetGlobal(), "a"), "mods");
        File.WriteAllText(Path.Combine(mods, "create.jar"), "x");
        File.WriteAllText(Path.Combine(mods, "sodium.jar"), "x");
        File.WriteAllText(Path.Combine(mods, "old.jar.disabled"), "x");

        var detail = catalog.GetDetail("a");
        var modsAction = catalog.GetActions("a").First(action => action.Target == InstanceActionTarget.Mods);

        Assert.AreEqual("3 个 (已启用 2)", TestText.Of(detail.ModSummary));
        Assert.AreEqual("2 个模组", TestText.Of(modsAction.Summary), "动作条上的数量只算启用的。");
    }

    [TestMethod]
    public void ANonIsolatedInstancePointsAtTheGlobalVersionsDirectory()
    {
        _settings.SaveGlobal(SettingsDefaults.CreateGlobal() with { GameDirectory = Path.Combine(_tempRoot, "d-game") });
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla, IsolatedDirectory: false));

        Assert.AreEqual(Path.Combine(_tempRoot, "d-game", "versions", "a"), catalog.GetDetail("a").InstancePath);
    }

    [TestMethod]
    public void DirectorySizeIsCachedForAWhileAndThenRemeasured()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));

        // 初始大小不是 0：instance.json 本身也在目录里。
        var initial = catalog.GetDetail("a").SizeOnDisk;
        Assert.AreNotEqual("0 B", initial);

        File.WriteAllBytes(Path.Combine(InstancePaths.ContentDirectory(_settings.GetGlobal(), "a"), "big.bin"), new byte[2048]);

        Assert.AreEqual(initial, catalog.GetDetail("a").SizeOnDisk, "刚测过的大小会被缓存，免得每次进页面都遍历目录。");

        _clock.Now = _clock.Now.AddMinutes(2);
        var expected = InstanceText.FormatSize(
            new FileInfo(InstancePaths.MetadataFile(_settings.GetGlobal(), "a")).Length + 2048);
        Assert.AreEqual(expected, catalog.GetDetail("a").SizeOnDisk, "缓存过期后要重新统计（新增的 2 KB 必须算进去）。");
    }

    // -------------------------------------------------------------- 删除 ---

    [TestMethod]
    public void DeletingKeepsTheSavesAndConfigInTheRecycleDirectory()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Forge));
        var directory = InstancePaths.Directory(_settings.GetGlobal(), "a");
        var contentDirectory = InstancePaths.ContentDirectory(_settings.GetGlobal(), "a");
        File.WriteAllText(Path.Combine(contentDirectory, "saves", "world.dat"), "存档");
        File.WriteAllText(Path.Combine(contentDirectory, "options.txt"), "fov:70");
        File.WriteAllText(Path.Combine(contentDirectory, "mods", "create.jar"), "x");

        var result = catalog.DeleteInstance("a", new InstanceDeletionOptions(KeepSavesAndConfig: true));

        Assert.IsFalse(Directory.Exists(directory), "实例目录（元数据 + 游戏数据）必须已经不在。");
        Assert.IsFalse(Directory.Exists(contentDirectory), "游戏数据目录也必须一起搬走。");
        Assert.IsTrue(Directory.Exists(result.RecyclePath), "回收条目必须存在。");
        Assert.IsNotNull(result.PreservedDirectory);
        Assert.IsTrue(File.Exists(Path.Combine(result.PreservedDirectory, "saves", "world.dat")));
        Assert.IsTrue(File.Exists(Path.Combine(result.PreservedDirectory, "options.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(result.PreservedDirectory, "mods")), "模组属于可重新下载的内容。");

        // 其余内容仍在回收条目里（所以整次删除都是可回滚的）。
        Assert.IsTrue(File.Exists(Path.Combine(result.RecyclePath, "instance", "mods", "create.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(result.RecyclePath, "instance", "instance.json")));
    }

    [TestMethod]
    public void DeletingWithoutKeepingMovesEverythingIntact()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Forge));
        File.WriteAllText(Path.Combine(InstancePaths.ContentDirectory(_settings.GetGlobal(), "a"), "saves", "world.dat"), "存档");

        var result = catalog.DeleteInstance("a", new InstanceDeletionOptions(KeepSavesAndConfig: false));

        Assert.IsNull(result.PreservedDirectory);
        Assert.IsFalse(Directory.Exists(Path.Combine(result.RecyclePath, "kept")));
        Assert.IsTrue(File.Exists(Path.Combine(result.RecyclePath, "instance", "saves", "world.dat")));
    }

    [TestMethod]
    public void TheRecycleEntryExplainsWhatItIs()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Forge));

        var result = catalog.DeleteInstance("a", new InstanceDeletionOptions(KeepSavesAndConfig: true));

        var marker = File.ReadAllText(Path.Combine(result.RecyclePath, "recycle.json"), Encoding.UTF8);
        StringAssert.Contains(marker, "\"instanceName\": \"a\"");
        StringAssert.Contains(marker, "\"keptSavesAndConfig\": true");
        StringAssert.Contains(marker, "\"deletedAt\": \"2026-09-30T16:01:02");
    }

    [TestMethod]
    public void DeletingReportsTheAccountBindingWhenAsked()
    {
        WriteInstance("a", account: "Steve");
        var catalog = NewCatalog();

        var result = catalog.DeleteInstance("a", new InstanceDeletionOptions(DeleteAccountBinding: true));

        Assert.AreEqual("Steve", result.AccountBinding);

        var marker = File.ReadAllText(Path.Combine(result.RecyclePath, "recycle.json"), Encoding.UTF8);
        StringAssert.Contains(marker, "\"deleteAccountBinding\": true");

        // 没要求解除绑定时不该把账户信息带出来。
        WriteInstance("b", account: "Alex");
        var kept = NewCatalog().DeleteInstance("b", new InstanceDeletionOptions(DeleteAccountBinding: false));
        Assert.IsNull(kept.AccountBinding);
    }

    [TestMethod]
    public void DeletingTheActiveInstanceMovesTheActivePointer()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        catalog.CreateInstance(new InstanceCreationRequest("b", "1.20.4", LoaderKind.Vanilla));
        catalog.ActiveInstanceName = "a";

        catalog.DeleteInstance("a", new InstanceDeletionOptions());

        Assert.AreEqual("b", catalog.ActiveInstanceName);
        Assert.AreEqual(1, catalog.GetInstances().Count);
    }

    [TestMethod]
    public void DeletingAnUnknownInstanceFails()
    {
        var catalog = NewCatalog();

        Assert.ThrowsException<KeyNotFoundException>(
            () => catalog.DeleteInstance("不存在", new InstanceDeletionOptions()));
    }

    [TestMethod]
    public void DeletingIsReportedAsAWarningEventMentioningTheRecyclePath()
    {
        var catalog = NewCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        _diagnostics.Entries.Clear();

        var result = catalog.DeleteInstance("a", new InstanceDeletionOptions());

        var entry = _diagnostics.Entries.Single();
        Assert.AreEqual(LauncherEventLevel.Warning, entry.Level);
        Assert.AreEqual(DiagnosticsSources.InstanceSettings, entry.Source);
        Assert.AreEqual("a", entry.InstanceName);
        CollectionAssert.Contains((System.Collections.ICollection)entry.Parameters, result.RecyclePath);
    }

    [TestMethod]
    public void TwoDeletionsAtTheSameSecondDoNotCollide()
    {
        var catalog = NewCatalog();

        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        var first = catalog.DeleteInstance("a", new InstanceDeletionOptions());
        catalog.CreateInstance(new InstanceCreationRequest("a", "1.20.4", LoaderKind.Vanilla));
        var second = catalog.DeleteInstance("a", new InstanceDeletionOptions());

        Assert.AreNotEqual(first.RecyclePath, second.RecyclePath);
        Assert.IsTrue(Directory.Exists(first.RecyclePath));
        Assert.IsTrue(Directory.Exists(second.RecyclePath));
    }

    /// <summary>手写一份实例元数据（用来构造"有历史记录"的实例）。</summary>
    private void WriteInstance(
        string name,
        string? lastLaunchedAt = null,
        string? account = null,
        string gameVersion = "1.20.4")
    {
        var directory = InstancePaths.Directory(_settings.GetGlobal(), name);
        Directory.CreateDirectory(directory);

        var lastLaunched = lastLaunchedAt is null ? "null" : $"\"{lastLaunchedAt}\"";
        var accountJson = account is null ? "null" : $"\"{account}\"";
        File.WriteAllText(
            Path.Combine(directory, "instance.json"),
            $$"""
            {
              "schemaVersion": 1,
              "gameVersion": "{{gameVersion}}",
              "loader": "Forge",
              "lastLaunchedAt": {{lastLaunched}},
              "account": {{accountJson}}
            }
            """,
            new UTF8Encoding(false));
    }
}
