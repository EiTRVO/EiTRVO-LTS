using System.Text;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M0 的存储地基：数据目录布局、原子写、时间源。
/// <para>
/// 全部在临时目录上做真实文件操作（不 mock 文件系统）—— 原子写的行为恰恰由
/// 文件系统语义决定，mock 掉就测不到真正要防的那件事。
/// </para>
/// </summary>
[TestClass]
public sealed class StorageTests
{
    private string _tempRoot = string.Empty;

    /// <summary>每个用例一个全新的临时根目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>清理临时目录（先把只读属性去掉，否则删不掉）。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (!Directory.Exists(_tempRoot))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_tempRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_tempRoot, recursive: true);
    }

    // ------------------------------------------------------------ 路径布局 ---

    [TestMethod]
    public void DefaultRootIsTheLauncherDirectory()
    {
        var paths = new LauncherPaths();

        Assert.AreEqual(
            Path.GetFullPath(AppContext.BaseDirectory),
            paths.Root);
    }

    [TestMethod]
    public void AllFilesLiveUnderTheRoot()
    {
        var paths = new LauncherPaths(_tempRoot);

        Assert.AreEqual(Path.Combine(_tempRoot, "settings.json"), paths.SettingsFile);
        Assert.AreEqual(Path.Combine(_tempRoot, "accounts.json"), paths.AccountsFile);
        Assert.AreEqual(Path.Combine(_tempRoot, "log"), paths.LogDirectory);
        Assert.AreEqual(Path.Combine(_tempRoot, "instances"), paths.InstancesDirectory);
        Assert.AreEqual(
            Path.Combine(_tempRoot, "instances", "1.20.4-Forge", "instance.json"),
            paths.InstanceMetadataFile("1.20.4-Forge"));
        Assert.AreEqual(
            Path.Combine(_tempRoot, "instances", "1.20.4-Forge", "overrides.json"),
            paths.InstanceOverridesFile("1.20.4-Forge"));
        Assert.AreEqual(Path.Combine(_tempRoot, "recycle"), paths.RecycleDirectory);
        Assert.AreEqual(Path.Combine(_tempRoot, "cache", "downloads"), paths.DownloadCacheDirectory);
        Assert.AreEqual(Path.Combine(_tempRoot, "cache", "versions"), paths.VersionCacheDirectory);
    }

    [TestMethod]
    public void JavaRuntimeDirectoryCanLiveOutsideTheDataRoot()
    {
        var runtimeDirectory = Path.Combine(_tempRoot, "documents", "EiTRVO LTS", "runtime");
        var paths = new LauncherPaths(_tempRoot, runtimeDirectory);

        Assert.AreEqual(Path.GetFullPath(runtimeDirectory), paths.JavaRuntimeDirectory);
        Assert.AreEqual(Path.Combine(_tempRoot, "settings.json"), paths.SettingsFile);
        Assert.AreEqual(
            Path.Combine(_tempRoot, "runtime"),
            new LauncherPaths(_tempRoot).JavaRuntimeDirectory,
            "未显式指定时仍回落到数据根下的 runtime。");
    }

    [TestMethod]
    public void AccountsFileCanLiveOutsideTheDataRoot()
    {
        var accountsDirectory = Path.Combine(_tempRoot, "documents", "EiTRVO LTS");
        var paths = new LauncherPaths(_tempRoot, javaRuntimeDirectory: null, accountsDirectory);

        Assert.AreEqual(Path.Combine(accountsDirectory, "accounts.json"), paths.AccountsFile);
        Assert.AreEqual(Path.Combine(_tempRoot, "settings.json"), paths.SettingsFile);
        Assert.AreEqual(
            Path.Combine(_tempRoot, "accounts.json"),
            new LauncherPaths(_tempRoot).AccountsFile,
            "未显式指定账户目录时仍回落到数据根。");
    }

    [TestMethod]
    public void EnsureCreatedCreatesEveryDirectoryAndIsIdempotent()
    {
        var paths = new LauncherPaths(_tempRoot);

        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.IsTrue(Directory.Exists(paths.Root));
        Assert.IsTrue(Directory.Exists(paths.LogDirectory));
        // 新布局：实例目录在 <游戏目录>\versions\<实例>，启动器数据目录里不再需要 instances\。
        Assert.IsFalse(
            Directory.Exists(paths.InstancesDirectory),
            "EnsureCreated 不该再创建旧布局的实例元数据目录。");
        Assert.IsTrue(Directory.Exists(paths.DownloadCacheDirectory));
        Assert.IsTrue(Directory.Exists(paths.VersionCacheDirectory));
        Assert.IsTrue(Directory.Exists(paths.RecycleDirectory));
    }

    [TestMethod]
    public void ConstructorRejectsAnEmptyRoot()
    {
        Assert.ThrowsException<ArgumentException>(() => new LauncherPaths("   "));
    }

    [TestMethod]
    [DataRow("1.20.4-Forge")]
    [DataRow("我的实例")]
    [DataRow("Fabric 1.20.1")]
    [DataRow("a")]
    public void AcceptsOrdinaryInstanceNames(string name)
    {
        Assert.IsTrue(LauncherPaths.IsValidInstanceName(name));
        Assert.AreEqual(Path.Combine(_tempRoot, "instances", name), new LauncherPaths(_tempRoot).InstanceDirectory(name));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("../escape")]
    [DataRow("..\\escape")]
    [DataRow("sub/dir")]
    [DataRow("sub\\dir")]
    [DataRow("CON")]
    [DataRow("com1")]
    [DataRow("trailing.")]
    [DataRow(" leading")]
    public void RejectsInstanceNamesThatAreNotSafeDirectoryNames(string name)
    {
        Assert.IsFalse(LauncherPaths.IsValidInstanceName(name), $"'{name}' 不应被接受为实例名。");
        Assert.ThrowsException<ArgumentException>(() => new LauncherPaths(_tempRoot).InstanceDirectory(name));
    }

    [TestMethod]
    public void RejectsOverlongInstanceNames()
    {
        Assert.IsFalse(LauncherPaths.IsValidInstanceName(new string('a', 65)));
        Assert.IsTrue(LauncherPaths.IsValidInstanceName(new string('a', 64)));
    }

    // -------------------------------------------------------------- 原子写 ---

    [TestMethod]
    public void WriteAllTextCreatesTheFileAndItsDirectory()
    {
        var target = Path.Combine(_tempRoot, "nested", "settings.json");

        AtomicFile.WriteAllText(target, "{\"a\":1}");

        Assert.AreEqual("{\"a\":1}", File.ReadAllText(target));
        // UTF-8 无 BOM：带 BOM 的 JSON 会让不少解析器（含外部工具链）报错。
        var bytes = File.ReadAllBytes(target);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("{\"a\":1}"), bytes);
    }

    [TestMethod]
    public void OverwritingKeepsThePreviousVersionAsBackup()
    {
        var target = Path.Combine(_tempRoot, "settings.json");
        AtomicFile.WriteAllText(target, "first");

        AtomicFile.WriteAllText(target, "second");

        Assert.AreEqual("second", File.ReadAllText(target));
        Assert.AreEqual("first", File.ReadAllText(target + AtomicFile.BackupSuffix));
    }

    [TestMethod]
    public void FailedReplaceLeavesTheTargetUntouchedAndNoTempFilesBehind()
    {
        var target = Path.Combine(_tempRoot, "settings.json");
        AtomicFile.WriteAllText(target, "original");

        // 只读目标文件会让 File.Replace 失败 —— 这正是"写入失败"最贴近现实的模拟。
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsException<UnauthorizedAccessException>(() => AtomicFile.WriteAllText(target, "should-not-land"));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }

        Assert.AreEqual("original", File.ReadAllText(target), "目标文件必须保持原内容。");
        Assert.AreEqual(
            0,
            Directory.GetFiles(_tempRoot, "*" + ".tmp", SearchOption.AllDirectories).Length,
            "失败后不允许留下临时文件。");
    }

    [TestMethod]
    public async Task AsyncWriteBehavesLikeTheSyncOne()
    {
        var target = Path.Combine(_tempRoot, "instance.json");

        await AtomicFile.WriteAllTextAsync(target, "{\"name\":\"a\"}");
        await AtomicFile.WriteAllTextAsync(target, "{\"name\":\"b\"}");

        Assert.AreEqual("{\"name\":\"b\"}", File.ReadAllText(target));
        Assert.AreEqual("{\"name\":\"a\"}", File.ReadAllText(target + AtomicFile.BackupSuffix));
    }

    [TestMethod]
    public void ReadAllTextOrNullReturnsNullInsteadOfThrowing()
    {
        Assert.IsNull(AtomicFile.ReadAllTextOrNull(Path.Combine(_tempRoot, "missing.json")));

        var target = Path.Combine(_tempRoot, "settings.json");
        AtomicFile.WriteAllText(target, "content");

        Assert.AreEqual("content", AtomicFile.ReadAllTextOrNull(target));
    }

    // --------------------------------------------------------------- 时钟 ---

    [TestMethod]
    public void SystemClockTracksWallClockTime()
    {
        var before = DateTimeOffset.Now;
        var now = new SystemClock().Now;
        var after = DateTimeOffset.Now;

        Assert.IsTrue(now >= before && now <= after, "系统时钟必须落在调用前后的墙钟时间之间。");
    }
}
