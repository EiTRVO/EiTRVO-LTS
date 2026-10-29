using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M1 设置持久化（P1-11 / P1-12）：全局 <c>settings.json</c> + 逐实例 <c>overrides.json</c>。
/// <para>
/// 重点是**坏输入下的行为**：截断、缺字段、非法数值、手改格式。用户数据只丢一次，
/// 就足以让一个人再也不用这个启动器，所以这些反例比"能存能读"更值得测。
/// </para>
/// </summary>
[TestClass]
public sealed class SettingsStoreTests
{
    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一个全新的数据目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-settings", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
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

    private FileSettingsCatalog NewCatalog() => new(_paths, _diagnostics);

    private void WriteSettingsFile(string content) =>
        File.WriteAllText(_paths.SettingsFile, content, new UTF8Encoding(false));

    // ------------------------------------------------------------ 默认值 ---

    [TestMethod]
    public void AFreshInstallUsesTheDefaults()
    {
        var settings = NewCatalog().GetGlobal();

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents");
        }

        Assert.AreEqual(
            Path.Combine(documents, "EiTRVO LTS", ".minecraft"),
            settings.GameDirectory);
        Assert.AreEqual(DownloadSourcePolicy.Official, settings.DownloadSource);
        Assert.AreEqual(64, settings.ConcurrentDownloads);
        Assert.AreEqual(4096, settings.MaxMemoryMb);
        Assert.AreEqual(1280, settings.WindowWidth);
        Assert.AreEqual(720, settings.WindowHeight);
        Assert.AreEqual(string.Empty, settings.Language, "未选择语言时必须留空，而不是写死中文。");
        Assert.IsNull(settings.JavaOverride);
    }

    [TestMethod]
    public void ReadingDoesNotCreateTheSettingsFile()
    {
        _ = NewCatalog().GetGlobal();

        Assert.IsFalse(File.Exists(_paths.SettingsFile), "只读不写：首次打开设置页不该产生文件。");
    }

    // -------------------------------------------------------------- 写读 ---

    [TestMethod]
    public void SavedSettingsSurviveARoundTrip()
    {
        var catalog = NewCatalog();
        var original = SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = @"D:\Games\.minecraft",
            DownloadSource = DownloadSourcePolicy.Mirror,
            ConcurrentDownloads = 16,
            MinimizeAfterLaunch = false,
            Language = "en",
            KeepLogsOnCrash = false,
            MaxMemoryMb = 8192,
            JvmArguments = "-XX:+UseZGC",
            WindowWidth = 2560,
            WindowHeight = 1440,
            Fullscreen = true,
            ExtraGameArguments = "--demo",
            AutoFetchJava = false,
            JavaOverride = "25.0.3 (Oracle)",
        };

        catalog.SaveGlobal(original);

        // 换一个实例读，确认读的是磁盘而不是内存里的缓存。
        Assert.AreEqual(original, NewCatalog().GetGlobal());
    }

    [TestMethod]
    public void TheWrittenFileIsReadableByAHuman()
    {
        var catalog = NewCatalog();
        catalog.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = @"D:\游戏",
            JvmArguments = "-XX:+UseG1GC",
        });

        var text = File.ReadAllText(_paths.SettingsFile, Encoding.UTF8);

        StringAssert.Contains(text, "\"schemaVersion\": 1");
        StringAssert.Contains(text, "\"gameDirectory\": \"D:\\\\游戏\"", "中文必须原样写入，不能变成 \\uXXXX。");
        StringAssert.Contains(text, "\"downloadSource\": \"Official\"", "枚举要写成字符串。");
        // JVM 参数里的 + 若是被转义成 \u002B，设置文件就没法手工编辑了。
        StringAssert.Contains(text, "-XX:+UseG1GC", "JVM 参数必须原样写入。");
        Assert.IsFalse(text.Contains("\\u", StringComparison.Ordinal), "文件里不应出现转义过的字符。");
    }

    [TestMethod]
    public void SavingIsRecordedAsAnEvent()
    {
        NewCatalog().SaveGlobal(SettingsDefaults.CreateGlobal());

        var entry = _diagnostics.Entries.Single();
        Assert.AreEqual(LauncherEventLevel.Information, entry.Level);
        Assert.AreEqual(DiagnosticsSources.Launcher, entry.Source);
        Assert.AreEqual("已保存全局设置", entry.MessageKey);
    }

    [TestMethod]
    public void SavingTwiceKeepsThePreviousVersionAsBackup()
    {
        var catalog = NewCatalog();
        catalog.SaveGlobal(SettingsDefaults.CreateGlobal() with { MaxMemoryMb = 2048 });
        catalog.SaveGlobal(SettingsDefaults.CreateGlobal() with { MaxMemoryMb = 4096 });

        var backup = File.ReadAllText(_paths.SettingsFile + AtomicFile.BackupSuffix, Encoding.UTF8);

        StringAssert.Contains(backup, "2048", "覆盖前的版本要留成 .bak。");
        Assert.AreEqual(4096, catalog.GetGlobal().MaxMemoryMb);
    }

    // ---------------------------------------------------------- 坏输入 ---

    [TestMethod]
    public void ATruncatedSettingsFileFallsBackToDefaultsAndKeepsTheFile()
    {
        // 半截 JSON：模拟断电或磁盘满时被打断的写入。
        const string Truncated = "{\"schemaVersion\": 1, \"global\": {\"maxMemoryMb\": 819";
        WriteSettingsFile(Truncated);

        var settings = NewCatalog().GetGlobal();

        Assert.AreEqual(4096, settings.MaxMemoryMb, "整份回落到默认值。");
        Assert.AreEqual(Truncated, File.ReadAllText(_paths.SettingsFile, Encoding.UTF8), "原文件必须原样保留。");

        var warning = _diagnostics.Entries.Single();
        Assert.AreEqual(LauncherEventLevel.Warning, warning.Level);
        StringAssert.Contains(warning.MessageKey, "不是合法 JSON");
        CollectionAssert.Contains(
            (System.Collections.ICollection)warning.Parameters,
            "settings.json");
    }

    [TestMethod]
    public void ACorruptFileIsOnlyReportedOncePerContentVersion()
    {
        WriteSettingsFile("{ not json");
        var catalog = NewCatalog();

        catalog.GetGlobal();
        catalog.GetGlobal();
        catalog.GetGlobal();

        Assert.AreEqual(1, _diagnostics.Entries.Count, "同一个坏文件不该每次读都刷一条事件。");
    }

    [TestMethod]
    public void MissingFieldsFallBackOneByOneInsteadOfDiscardingTheWholeFile()
    {
        WriteSettingsFile("{\"schemaVersion\":1,\"global\":{\"maxMemoryMb\":8192}}");

        var settings = NewCatalog().GetGlobal();

        Assert.AreEqual(8192, settings.MaxMemoryMb, "写了的字段要用上。");
        Assert.AreEqual(64, settings.ConcurrentDownloads, "没写的字段回落到默认值。");
        Assert.AreEqual(1280, settings.WindowWidth);
    }

    [TestMethod]
    public void AnEmptyLanguageStaysEmptyBecauseItMeansNotChosenYet()
    {
        WriteSettingsFile("{\"global\":{\"language\":\"\"}}");

        Assert.AreEqual(string.Empty, NewCatalog().GetGlobal().Language);
    }

    [TestMethod]
    [DataRow("{\"global\":{\"maxMemoryMb\":99}}", 512)]
    [DataRow("{\"global\":{\"maxMemoryMb\":99999999}}", 262144)]
    public void AnAbsurdMemoryValueIsClamped(string json, int expected)
    {
        WriteSettingsFile(json);

        Assert.AreEqual(expected, NewCatalog().GetGlobal().MaxMemoryMb);
    }

    [TestMethod]
    public void OtherAbsurdNumbersAreClampedToo()
    {
        WriteSettingsFile(
            "{\"global\":{\"concurrentDownloads\":100000,\"windowWidth\":10,\"windowHeight\":999999}}");

        var settings = NewCatalog().GetGlobal();

        Assert.AreEqual(256, settings.ConcurrentDownloads);
        Assert.AreEqual(320, settings.WindowWidth);
        Assert.AreEqual(16384, settings.WindowHeight);
    }

    [TestMethod]
    public void UnknownFieldsAndCommentsAreTolerated()
    {
        // 旧版本写下的字段、或者用户加的注释与尾逗号，都不该让设置失效。
        WriteSettingsFile(
            """
            {
              // 手写的注释
              "schemaVersion": 1,
              "futureField": { "nested": true },
              "global": { "concurrentDownloads": 32, },
            }
            """);

        Assert.AreEqual(32, NewCatalog().GetGlobal().ConcurrentDownloads);
    }

    [TestMethod]
    public void AnEmptyFileIsTreatedAsMissingRatherThanCorrupt()
    {
        WriteSettingsFile(string.Empty);

        var settings = NewCatalog().GetGlobal();

        Assert.AreEqual(4096, settings.MaxMemoryMb);
        Assert.AreEqual(0, _diagnostics.Entries.Count, "空文件是合法的「还没有设置」，不该报警。");
    }

    // ------------------------------------------------------ 实例覆盖项 ---

    [TestMethod]
    public void AnInstanceWithoutAnOverridesFileInheritsTheGlobalValues()
    {
        var catalog = NewCatalog();
        catalog.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            WindowWidth = 1280,
            WindowHeight = 720,
            Fullscreen = true,
            MinimizeAfterLaunch = false,
            ExtraGameArguments = "--server local",
        });
        CreateInstanceDirectory("1.20.4-Forge");

        var overrides = catalog.GetInstanceOverrides("1.20.4-Forge");

        Assert.AreEqual("1.20.4-Forge", overrides.InstanceName);
        Assert.IsNull(overrides.JavaOverride);
        Assert.IsNull(overrides.MaxMemoryMb, "没有覆盖内存时必须是 null（继承），而不是被填成某个值。");
        Assert.AreEqual(1280, overrides.WindowWidth);
        Assert.AreEqual(720, overrides.WindowHeight);
        Assert.AreEqual(WindowLaunchMode.Fullscreen, overrides.LaunchMode, "全局全屏时实例默认也全屏。");
        Assert.AreEqual("--server local", overrides.ExtraGameArguments);
        Assert.IsFalse(overrides.MinimizeAfterLaunch);
    }

    [TestMethod]
    public void InstanceOverridesSurviveARoundTrip()
    {
        var catalog = NewCatalog();
        CreateInstanceDirectory("1.20.4-Forge");
        var overrides = new InstanceOverrides(
            InstanceName: "1.20.4-Forge",
            JavaOverride: "17.0.9（Temurin）",
            MaxMemoryMb: 6144,
            JvmArguments: "-XX:+UseConcMarkSweepGC",
            WindowWidth: 1600,
            WindowHeight: 900,
            LaunchMode: WindowLaunchMode.Borderless,
            ExtraGameArguments: "--width 1600",
            MinimizeAfterLaunch: false);

        catalog.SaveInstanceOverrides(overrides);

        Assert.AreEqual(overrides, NewCatalog().GetInstanceOverrides("1.20.4-Forge"));
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-Forge", "overrides.json")));
        Assert.IsFalse(
            File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-Forge", "instance.json")),
            "覆盖项与元数据是两个文件：保存设置不该顺手造出 instance.json。");
    }

    [TestMethod]
    public void SavingInstanceOverridesIsRecordedWithTheInstanceName()
    {
        var catalog = NewCatalog();
        CreateInstanceDirectory("1.20.1-Fabric");

        catalog.SaveInstanceOverrides(SettingsDefaults.CreateInstanceOverrides(
            "1.20.1-Fabric",
            SettingsDefaults.CreateGlobal()));

        var entry = _diagnostics.Entries.Single();
        Assert.AreEqual(DiagnosticsSources.InstanceSettings, entry.Source);
        Assert.AreEqual("1.20.1-Fabric", entry.InstanceName);
    }

    [TestMethod]
    public void SavingOverridesForAMissingInstanceFailsInsteadOfCreatingPhantomDirectories()
    {
        UseTempGameDirectory();

        var overrides = SettingsDefaults.CreateInstanceOverrides(
            "不存在的实例",
            SettingsDefaults.CreateGlobal());

        Assert.ThrowsException<InvalidOperationException>(() => NewCatalog().SaveInstanceOverrides(overrides));
        Assert.IsFalse(Directory.Exists(Path.Combine(_gameDirectory, "versions", "不存在的实例")));
    }

    [TestMethod]
    public void SavingOverridesWithAnUnsafeInstanceNameIsRejected()
    {
        var overrides = SettingsDefaults.CreateInstanceOverrides(
            "../escape",
            SettingsDefaults.CreateGlobal());

        Assert.ThrowsException<ArgumentException>(() => NewCatalog().SaveInstanceOverrides(overrides));
    }

    [TestMethod]
    public void ACorruptOverridesFileFallsBackToInheritanceAndKeepsTheFile()
    {
        CreateInstanceDirectory("1.20.4-Forge");
        var path = Path.Combine(_gameDirectory, "versions", "1.20.4-Forge", "overrides.json");
        File.WriteAllText(path, "{\"maxMemoryMb\": 61", new UTF8Encoding(false));

        var overrides = NewCatalog().GetInstanceOverrides("1.20.4-Forge");

        Assert.IsNull(overrides.MaxMemoryMb);
        Assert.AreEqual("{\"maxMemoryMb\": 61", File.ReadAllText(path, Encoding.UTF8));
        Assert.AreEqual(LauncherEventLevel.Warning, _diagnostics.Entries.Single().Level);
    }

    [TestMethod]
    public void AskingForOverridesWithoutAnInstanceContextInheritsInsteadOfThrowing()
    {
        // 一个实例都还没建的时候，设置页会被要求"当前实例"的覆盖项。
        var overrides = NewCatalog().GetInstanceOverrides(string.Empty);

        Assert.AreEqual(string.Empty, overrides.InstanceName);
        Assert.IsNull(overrides.MaxMemoryMb);
    }

    [TestMethod]
    public void InstanceOverridesAreClampedAgainstHandEdits()
    {
        CreateInstanceDirectory("1.20.4-Forge");
        File.WriteAllText(
            Path.Combine(_gameDirectory, "versions", "1.20.4-Forge", "overrides.json"),
            "{\"maxMemoryMb\": 1, \"windowWidth\": 1}",
            new UTF8Encoding(false));

        var overrides = NewCatalog().GetInstanceOverrides("1.20.4-Forge");

        Assert.AreEqual(512, overrides.MaxMemoryMb);
        Assert.AreEqual(320, overrides.WindowWidth);
    }

    private void CreateInstanceDirectory(string instanceName)
    {
        UseTempGameDirectory();

        // 设置游戏目录本身会写一条"已保存全局设置"事件；这些用例断言的是后续那一条。
        _diagnostics.Entries.Clear();

        var directory = InstancePaths.Directory(NewCatalog().GetGlobal(), instanceName);
        Directory.CreateDirectory(directory);
    }

    /// <summary>把全局设置里的游戏目录指到临时目录（逐实例文件现在与实例目录同一个根）。</summary>
    private void UseTempGameDirectory()
    {
        var catalog = NewCatalog();
        catalog.SaveGlobal(catalog.GetGlobal() with { GameDirectory = _gameDirectory });
    }
}
