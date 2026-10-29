using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// §12.7 的**计划**部分：把远端清单变成"下到哪、建什么目录、可执行文件是哪个"。
/// <para>
/// 这里最要紧的不是路径拼得对不对，而是**远端数据不能决定我们写到盘上的位置**：
/// 清单里的键会直接拼进磁盘路径，一个 <c>../..</c> 或一个驱动器号就足以把文件写到数据目录之外。
/// 每一条都是"拒绝整份清单"，而不是跳过那一条 —— 带穿越路径的清单只能是被改过的。
/// </para>
/// </summary>
[TestClass]
public sealed class JavaRuntimePlannerTests
{
    private static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(),
        "eitrvo-jre-plan-" + Guid.NewGuid().ToString("N"));

    private static LauncherPaths Paths() => new(TempRoot);

    private static JavaRuntimeCandidate Candidate(string component = "java-runtime-gamma", string platform = JavaRuntimeFixture.Platform) =>
        new(
            Platform: platform,
            Component: component,
            VersionName: "17.0.15",
            Released: null,
            ManifestSha1: "sha1",
            ManifestSize: 1,
            ManifestUrl: "https://piston-meta.mojang.com/v1/packages/sha1/manifest.json");

    [TestMethod]
    public void LayoutMatchesTheOfficialShape()
    {
        var plan = JavaRuntimePlanner.Plan(
            JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
                JavaRuntimeFixture.File("bin/javaw.exe", "javaw"))),
            Paths(),
            Candidate());

        // 官方启动器（以及参照实现）就是这个形状：runtime/<组件>/<平台>/<组件>。
        // M3 的发现深度 4 正是照它准备的，所以这里必须与它逐字一致。
        Assert.AreEqual(
            Path.Combine(TempRoot, "runtime", "java-runtime-gamma", "windows-x64", "java-runtime-gamma"),
            plan.InstallDirectory);

        StringAssert.Contains(plan.InstallDirectory, Path.Combine("runtime", "java-runtime-gamma"));

        Assert.AreEqual(
            Path.Combine(TempRoot, "cache", "downloads", "java-runtime", "java-runtime-gamma", "windows-x64"),
            plan.StagingDirectory);

        // 暂存目录**不在**扫描根（runtime）之下：半成品不可能被当成"已安装的 Java"。
        Assert.IsFalse(
            plan.StagingDirectory.StartsWith(Path.Combine(TempRoot, "runtime"), StringComparison.OrdinalIgnoreCase),
            "暂存目录必须在运行时扫描根之外。");
    }

    [TestMethod]
    public void FilesLandUnderTheStagingDirectoryAndKeepTheirChecksums()
    {
        var plan = JavaRuntimePlanner.Plan(
            JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
                JavaRuntimeFixture.File("bin/javaw.exe", "javaw"),
                JavaRuntimeFixture.File("lib/modules", "modules"))),
            Paths(),
            Candidate());

        Assert.AreEqual(2, plan.Files.Count);

        var javaw = plan.Files.Single(file => file.DisplayName == "javaw.exe");
        Assert.AreEqual(Path.Combine(plan.StagingDirectory, "bin"), Path.GetDirectoryName(javaw.TargetPath));
        Assert.AreEqual(DownloadKind.JavaRuntime, javaw.Kind);
        Assert.AreEqual(JavaRuntimeFixture.Sha1Hex("javaw"), javaw.Sha1);
        Assert.AreEqual(5, javaw.Size);
        Assert.AreEqual("java-runtime-gamma", javaw.VersionId);
        Assert.AreEqual(5 + 7, plan.TotalBytes);
    }

    [TestMethod]
    public void JavaWinsOverJavaAsTheExecutable()
    {
        var both = JavaRuntimePlanner.Plan(
            JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
                JavaRuntimeFixture.File("bin/java.exe", "java"),
                JavaRuntimeFixture.File("bin/javaw.exe", "javaw"))),
            Paths(),
            Candidate());

        Assert.AreEqual("bin/javaw.exe", both.ExecutableRelativePath);

        var onlyJava = JavaRuntimePlanner.Plan(
            JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
                JavaRuntimeFixture.File("bin/java.exe", "java"))),
            Paths(),
            Candidate());

        Assert.AreEqual("bin/java.exe", onlyJava.ExecutableRelativePath);
    }

    [TestMethod]
    public void AManifestWithoutAnyJavaExecutableHasNoExecutable()
    {
        var plan = JavaRuntimePlanner.Plan(
            JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
                JavaRuntimeFixture.File("release", "JAVA_VERSION=17"))),
            Paths(),
            Candidate());

        Assert.IsNull(plan.ExecutableRelativePath);
        Assert.IsNull(plan.StagingExecutablePath);
        Assert.IsNull(plan.InstalledExecutablePath);
    }

    [TestMethod]
    public void DirectoriesAndLinksArePlanned()
    {
        var plan = JavaRuntimePlanner.Plan(
            JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
                JavaRuntimeFixture.Directory("lib/empty"),
                JavaRuntimeFixture.Link("bin/server", "lib/jvm.dll"))),
            Paths(),
            Candidate());

        Assert.AreEqual(Path.Combine(plan.StagingDirectory, "lib", "empty"), plan.Directories.Single());
        Assert.AreEqual("bin/server", plan.Links.Single().RelativePath);
        Assert.AreEqual("lib/jvm.dll", plan.Links.Single().Target);
    }

    [DataTestMethod]
    [DataRow("../evil.exe")]
    [DataRow("bin/../../evil.exe")]
    [DataRow("/absolute/evil.exe")]
    [DataRow("C:evil.exe")]
    [DataRow("bin\\evil.exe")]
    public void PathsThatEscapeTheRuntimeRootAreRejected(string path)
    {
        var manifest = JavaRuntimeManifest.Parse(
            "{\"files\":{\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\":{\"type\":\"file\",\"downloads\":{\"raw\":{\"sha1\":\"x\",\"size\":1,\"url\":\"https://piston-data.mojang.com/v1/objects/x/x\"}}}}}");

        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimePlanner.Plan(manifest, Paths(), Candidate()));
    }

    [TestMethod]
    public void ALinkTargetThatEscapesTheRuntimeRootIsRejected()
    {
        // 链接目标允许带 ..（官方清单里就是这么写的），但**必须仍落在运行时根目录内**：
        // 越界的目标意味着我们会去读运行时目录之外的文件，那是不能接受的。
        var manifest = JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
            JavaRuntimeFixture.Link("bin/server", "../../outside/jvm.dll")));

        var exception = Assert.ThrowsException<JavaRuntimeDataException>(
            () => JavaRuntimePlanner.Plan(manifest, Paths(), Candidate()));

        StringAssert.Contains(exception.Message, "越出了运行时目录");
    }

    [DataTestMethod]
    [DataRow("..")]
    [DataRow("a/b")]
    [DataRow("C:gamma")]
    public void ComponentNamesThatAreNotASingleSafeFolderAreRejected(string component)
    {
        var manifest = JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
            JavaRuntimeFixture.File("bin/javaw.exe", "javaw")));

        Assert.ThrowsException<JavaRuntimeDataException>(
            () => JavaRuntimePlanner.Plan(manifest, Paths(), Candidate(component)));
    }

    [TestMethod]
    public void AFileEntryWithoutDownloadUrlIsRejected()
    {
        var manifest = JavaRuntimeManifest.Parse(
            "{\"files\":{\"bin/javaw.exe\":{\"type\":\"file\",\"downloads\":{\"raw\":{\"sha1\":\"x\",\"size\":1}}}}}");

        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimePlanner.Plan(manifest, Paths(), Candidate()));
    }

    [TestMethod]
    public void AnUnknownEntryTypeIsRejected()
    {
        var manifest = JavaRuntimeManifest.Parse(
            "{\"files\":{\"bin/javaw.exe\":{\"type\":\"hardlink\",\"target\":\"x\"}}}");

        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimePlanner.Plan(manifest, Paths(), Candidate()));
    }

    [TestMethod]
    public void ALinkWithoutTargetIsRejected()
    {
        var manifest = JavaRuntimeManifest.Parse("{\"files\":{\"bin/server\":{\"type\":\"link\"}}}");

        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimePlanner.Plan(manifest, Paths(), Candidate()));
    }

}
