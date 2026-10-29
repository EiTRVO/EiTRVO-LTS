using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M3 的 B-12：启动／安装前的 Java 前置校验。
/// <para>
/// 这里是"缺 Java 要拦住"这条规则的**唯一**实现处，M4 的启动编排、p04 与 p17 的提示
/// 都读它的结论。所以三种"看起来都像缺 Java"的情况必须分清楚：
/// 没查过（Unknown，不许报故障）、查过没有（Missing）、用户指定的那个没了（OverrideInvalid）。
/// </para>
/// </summary>
[TestClass]
public sealed class JavaPreflightTests
{
    private static JavaInstallation Install(string home, int major, string version) => new(
        ExecutablePath: Path.Combine(home, "bin", "javaw.exe"),
        HomeDirectory: home,
        VersionText: version,
        MajorVersion: major,
        Vendor: "Eclipse Temurin",
        Bits: 64);

    private static readonly JavaInstallation Jdk8 = Install(@"C:\Java\jre-1.8", 8, "1.8.0_421");
    private static readonly JavaInstallation Jdk17 = Install(@"C:\Java\jdk-17", 17, "17.0.9");
    private static readonly JavaInstallation Jdk21 = Install(@"C:\Java\jdk-21", 21, "21.0.5");

    [TestMethod]
    public void WithoutADetectionResultNothingIsClaimed()
    {
        // 「还没查过」不等于「没有」：说成缺 Java 会让用户去装一个他其实已经有的东西。
        var check = JavaPreflight.Check("1.20.4", null, [], hasDetectionResult: false);

        Assert.AreEqual(JavaCheckStatus.Unknown, check.Status);
        Assert.AreEqual(17, check.RequiredMajor);
        Assert.IsFalse(check.IsSatisfied);
        Assert.IsNull(check.Selected);
    }

    [TestMethod]
    public void ADetectedRuntimeThatSatisfiesTheVersionIsSelected()
    {
        var check = JavaPreflight.Check("1.20.4", null, [Jdk8, Jdk17, Jdk21], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Satisfied, check.Status);
        Assert.AreEqual("17.0.9", check.Selected!.VersionText, "主版本正好等于要求的那一个优先。");
    }

    [TestMethod]
    public void DetectedRuntimesThatAllMissTheVersionMeanMissing()
    {
        var check = JavaPreflight.Check("1.20.4", null, [Jdk8], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Missing, check.Status);
        Assert.AreEqual(17, check.RequiredMajor);
        Assert.IsNull(check.Selected, "没有可用的运行时就不该报出一个选中项。");
    }

    [TestMethod]
    public void AnOldVersionIsNotSatisfiedByANewJava()
    {
        // 1.12.2 用 LWJGL 2，Java 9+ 起不来：只判下限就会把它放行，然后在启动时崩。
        var check = JavaPreflight.Check("1.12.2", null, [Jdk17, Jdk21], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Missing, check.Status);
        Assert.AreEqual(8, check.MaxUsableMajor);
    }

    [TestMethod]
    public void AnOverridePointingAtSomethingNotDetectedIsReportedAsInvalid()
    {
        var check = JavaPreflight.Check(@"C:\Java\gone", null, [Jdk17], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Satisfied, check.Status, "空覆盖 = 自动匹配。");

        var invalid = JavaPreflight.Check("1.20.4", @"C:\Java\gone", [Jdk17], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.OverrideInvalid, invalid.Status);
        Assert.IsNull(invalid.Selected);
    }

    [TestMethod]
    public void AnOverrideIsUsedEvenWhenAnotherRuntimeWouldAlsoWork()
    {
        var check = JavaPreflight.Check("1.20.4", @"C:\Java\jdk-21", [Jdk17, Jdk21], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Satisfied, check.Status);
        Assert.AreEqual("21.0.5", check.Selected!.VersionText, "用户显式指定了就用他指定的。");
    }

    [TestMethod]
    public void AnOverrideThatIsDetectedButUnusableIsMissing()
    {
        // 用户把实例指到 Java 8 上，而版本要求 17：结论是"没有可用的 Java"，
        // 但 Selected 仍然报出来（界面要能说清"你指定的那个不能用"）。
        var check = JavaPreflight.Check("1.20.4", @"C:\Java\jre-1.8", [Jdk8, Jdk17], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Missing, check.Status);
        Assert.AreEqual("1.8.0_421", check.Selected!.VersionText);
    }

    [TestMethod]
    public void PathsAreMatchedLenientlyForCaseAndTrailingSeparators()
    {
        var byHome = JavaPreflight.Check("1.20.4", @"c:\java\jdk-17\", [Jdk17], hasDetectionResult: true);
        var byExecutable = JavaPreflight.Check(
            "1.20.4",
            @"C:\Java\jdk-17\bin\javaw.exe",
            [Jdk17],
            hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Satisfied, byHome.Status);
        Assert.AreEqual(JavaCheckStatus.Satisfied, byExecutable.Status);
    }

    [TestMethod]
    public void WhenNothingMatchesExactlyTheLowestSatisfyingRuntimeWins()
    {
        // 1.20.4 要求 17，本机只有 21 与 25：取 21（这一代明确支持的版本），
        // 而不是"版本号最大的那个"。
        var check = JavaPreflight.Check(
            "1.20.4",
            null,
            [Jdk21, Install(@"C:\Java\jdk-25", 25, "25.0.3")],
            hasDetectionResult: true);

        Assert.AreEqual("21.0.5", check.Selected!.VersionText);
    }

    [TestMethod]
    public void NoInstallationsAtAllIsMissingOnceADetectionHasRun()
    {
        var check = JavaPreflight.Check("1.20.4", null, [], hasDetectionResult: true);

        Assert.AreEqual(JavaCheckStatus.Missing, check.Status);
        Assert.AreEqual(17, check.RequiredMajor);
        Assert.AreEqual(JavaRequirement.NoUpperBound, check.MaxUsableMajor, "1.13 起没有硬性上限。");
    }

    // ------------------------------------------------- Q14：全局默认值只是默认 ---

    [TestMethod]
    public void AStaleGlobalDefaultFallsBackToAutoMatching()
    {
        // 全局默认指向一个装过又卸载的 Java：它只是"所有实例的默认值"，不该把一个
        // 本来能自动匹配到 17 的实例卡成启动失败 —— 但要标出"这是回落"。
        var check = JavaPreflight.Check(
            "1.20.4",
            @"C:\Java\gone",
            [Jdk17],
            hasDetectionResult: true,
            declaredMajor: null,
            overrideScope: JavaOverrideScope.Global);

        Assert.AreEqual(JavaCheckStatus.Satisfied, check.Status);
        Assert.AreEqual("17.0.9", check.Selected!.VersionText);
        Assert.IsTrue(check.FellBackFromOverride, "回落必须能被界面说明（否则用户以为全局默认还在生效）。");
    }

    [TestMethod]
    public void AGlobalDefaultThatDoesNotFitTheVersionFallsBackToo()
    {
        var check = JavaPreflight.Check(
            "1.20.4",
            @"C:\Java\jre-1.8",
            [Jdk8, Jdk17],
            hasDetectionResult: true,
            declaredMajor: null,
            overrideScope: JavaOverrideScope.Global);

        Assert.AreEqual(JavaCheckStatus.Satisfied, check.Status);
        Assert.AreEqual("17.0.9", check.Selected!.VersionText);
        Assert.IsTrue(check.FellBackFromOverride);
    }

    [TestMethod]
    public void AStaleInstanceOverrideStillFailsInsteadOfFallingBack()
    {
        // 实例自己的覆盖是用户对这一台实例的明确选择：它失效时不能悄悄换一个 Java，
        // 否则"我明明选了 21，为什么用 17 启动"就没有答案。
        var check = JavaPreflight.Check(
            "1.20.4",
            @"C:\Java\gone",
            [Jdk17],
            hasDetectionResult: true,
            declaredMajor: null,
            overrideScope: JavaOverrideScope.Instance);

        Assert.AreEqual(JavaCheckStatus.OverrideInvalid, check.Status);
        Assert.IsNull(check.Selected);
        Assert.IsFalse(check.FellBackFromOverride);
    }

    [TestMethod]
    public void AGlobalDefaultWithoutAnyFallbackIsMissingNotOverrideInvalid()
    {
        var check = JavaPreflight.Check(
            "1.20.4",
            @"C:\Java\gone",
            [],
            hasDetectionResult: true,
            declaredMajor: null,
            overrideScope: JavaOverrideScope.Global);

        Assert.AreEqual(JavaCheckStatus.Missing, check.Status, "自动匹配也没有；结论是缺运行时，而不是'你指定的那个失效'。");
        Assert.IsTrue(check.FellBackFromOverride);
    }
}
