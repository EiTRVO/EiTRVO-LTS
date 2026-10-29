using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M3 的 C-6：Java 可执行文件的发现（PATH / JAVA_HOME / 注册表 / 常见目录）。
/// <para>
/// 真实的 Program Files 与 HKLM 造不出来，所以"环境从哪来"由替身给定；
/// 而**扫描本身的逻辑**（递归深度、目录去重、<c>javaw.exe</c> 优先）用真实临时目录测 ——
/// 这几条各有一个"静默少找一个 JDK"的失败模式，界面表现出来都是"本机没有 Java"。
/// </para>
/// </summary>
[TestClass]
public sealed class JavaDiscoveryTests
{
    private string _tempRoot = string.Empty;

    /// <summary>每个用例一个临时根目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-javadiscovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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

    [TestMethod]
    public void JavaHomeIsScannedAndJavawWinsOverJavaInTheSameDirectory()
    {
        // 同一个 bin 里两个都有时只保留 javaw.exe：否则界面上会出现两条同一个 JDK 的记录，
        // 而且用 java.exe 起游戏会多一个控制台窗口、任务栏图标也不是游戏的。
        var bin = Path.Combine(_tempRoot, "jdk-17", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "java.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "javaw.exe"), string.Empty);

        var environment = new FakeJavaEnvironment();
        environment.JavaHomes.Add(Path.Combine(_tempRoot, "jdk-17"));

        var found = new JavaDiscovery(environment).FindExecutables();

        Assert.AreEqual(1, found.Count);
        Assert.AreEqual(Path.Combine(bin, "javaw.exe"), found[0]);
    }

    [TestMethod]
    public void PathDirectoriesAreSearchedAndMissingFilesAreIgnored()
    {
        var bin = Path.Combine(_tempRoot, "path-jdk", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "java.exe"), string.Empty);

        var environment = new FakeJavaEnvironment();
        environment.PathDirectories.Add(bin);
        environment.PathDirectories.Add(Path.Combine(_tempRoot, "does-not-exist"));

        var found = new JavaDiscovery(environment).FindExecutables();

        Assert.AreEqual(1, found.Count);
        Assert.AreEqual(Path.Combine(bin, "java.exe"), found[0]);
    }

    [TestMethod]
    public void TheSameRuntimeFoundTwiceIsListedOnce()
    {
        // PATH 与 JAVA_HOME 指向同一个 JDK 是常态（安装器两者都写），
        // 不去重的结果是界面上出现两条一模一样的行。
        var bin = Path.Combine(_tempRoot, "jdk-21", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "javaw.exe"), string.Empty);

        var environment = new FakeJavaEnvironment();
        environment.PathDirectories.Add(bin);
        environment.JavaHomes.Add(Path.Combine(_tempRoot, "jdk-21"));

        Assert.AreEqual(1, new JavaDiscovery(environment).FindExecutables().Count);
    }

    [TestMethod]
    public void SearchRootsAreRecursedToTheirDeclaredDepth()
    {
        // 启动器自己的官方 JRE 是 runtime\<组件>\<平台>\<组件>\bin\javaw.exe（比厂商目录深两级）。
        // 深度不够的表现是"明明装了却检不到"，所以深度是每个根目录各自声明的。
        var deep = Path.Combine(_tempRoot, "runtime", "java-runtime-gamma", "windows-x64", "java-runtime-gamma", "bin");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "javaw.exe"), string.Empty);

        var shallowEnvironment = new FakeJavaEnvironment();
        shallowEnvironment.SearchRoots.Add(new JavaSearchRoot(Path.Combine(_tempRoot, "runtime"), MaxDepth: 1));

        var deepEnvironment = new FakeJavaEnvironment();
        deepEnvironment.SearchRoots.Add(new JavaSearchRoot(Path.Combine(_tempRoot, "runtime"), MaxDepth: 4));

        Assert.AreEqual(0, new JavaDiscovery(shallowEnvironment).FindExecutables().Count);
        Assert.AreEqual(1, new JavaDiscovery(deepEnvironment).FindExecutables().Count);
    }

    [TestMethod]
    public void TheVendorDirectoryLayoutIsFoundAtDepthTwo()
    {
        // <Program Files>\Eclipse Adoptium\jdk-17.0.9.9-hotspot\bin\javaw.exe（真实布局）。
        var bin = Path.Combine(_tempRoot, "Eclipse Adoptium", "jdk-17.0.9.9-hotspot", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "javaw.exe"), string.Empty);

        var environment = new FakeJavaEnvironment();
        environment.SearchRoots.Add(new JavaSearchRoot(Path.Combine(_tempRoot, "Eclipse Adoptium"), MaxDepth: 2));

        var found = new JavaDiscovery(environment).FindExecutables();

        Assert.AreEqual(1, found.Count);
        Assert.AreEqual(Path.Combine(bin, "javaw.exe"), found[0]);
    }

    [TestMethod]
    public void QuotedOrPaddedEnvironmentPathsAreCleaned()
    {
        // Windows 的 PATH 里带引号是常见写法（"C:\Program Files\Java\..."），
        // 直接拼路径会得到一个永远不存在的文件。
        var bin = Path.Combine(_tempRoot, "quoted jdk", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "javaw.exe"), string.Empty);

        var environment = new FakeJavaEnvironment();
        environment.PathDirectories.Add("\"" + bin + "\"");

        Assert.AreEqual(1, new JavaDiscovery(environment).FindExecutables().Count);
    }

    [TestMethod]
    public void NoExecutableNamesMeansNoCandidates()
    {
        var environment = new FakeJavaEnvironment();
        environment.ExecutableNames.Clear();
        environment.PathDirectories.Add(_tempRoot);

        Assert.AreEqual(0, new JavaDiscovery(environment).FindExecutables().Count);
    }

    [TestMethod]
    public void WindowsLooksForJavawFirstAndUnixForJava()
    {
        // javaw.exe 是 GUI 子系统的可执行文件（不会有控制台窗口）；
        // 非 Windows 上根本没有这个名字，落回 java。
        var windows = new FakeJavaEnvironment();
        CollectionAssert.AreEqual(new[] { "javaw.exe", "java.exe" }, windows.GetExecutableNames().ToArray());

        var unix = new WindowsJavaEnvironment(new Storage.LauncherPaths(_tempRoot));
        if (!OperatingSystem.IsWindows())
        {
            CollectionAssert.AreEqual(new[] { "java", "javaw" }, unix.GetExecutableNames().ToArray());
        }
        else
        {
            CollectionAssert.AreEqual(new[] { "javaw.exe", "java.exe" }, unix.GetExecutableNames().ToArray());
        }
    }

    [TestMethod]
    public void TheRealEnvironmentSourceAlwaysOffersTheLauncherRuntimeDirectory()
    {
        // 启动器将来会把 Mojang 官方 JRE 下载到 <数据目录>\runtime 下；
        // 发现逻辑现在就得扫那里，否则"下载完还是说没检测到"。
        var paths = new Storage.LauncherPaths(_tempRoot);
        var roots = new WindowsJavaEnvironment(paths).GetSearchRoots();

        Assert.IsTrue(
            roots.Any(root => string.Equals(root.Path, Path.Combine(_tempRoot, "runtime"), StringComparison.Ordinal)),
            "没有把启动器自己的 runtime 目录列入扫描根目录。");
    }
}
