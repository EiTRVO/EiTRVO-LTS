using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M3 的 C-7 / C-8：解析 <c>java -version</c> 的输出。
/// <para>
/// 这是一组**参考向量**测试：下面每一段输出都是真实发行版的真实格式（Oracle 8、
/// Eclipse Temurin 17、Oracle 25、Microsoft OpenJDK、32 位 Temurin 8），
/// 逐字段断言版本号 / 主版本 / 供应商 / 位数 —— 不接受"看起来差不多"。
/// </para>
/// <para>
/// 为什么值得为这几行文本写这么多断言：解析错了不会有任何异常，
/// 只会让界面把 Java 8 说成 Java 17、把 Temurin 说成 OpenJDK，
/// 而"缺少 Java"的判断正建立在主版本号上（判错会拦住能玩的用户，或者放行一个起不来的版本）。
/// </para>
/// </summary>
[TestClass]
public sealed class JavaVersionParserTests
{
    /// <summary>Oracle JDK 8u421（64 位）。</summary>
    private const string Oracle8 = """
        java version "1.8.0_421"
        Java(TM) SE Runtime Environment (build 1.8.0_421-b09)
        Java HotSpot(TM) 64-Bit Server VM (build 25.421-b09, mixed mode)
        """;

    /// <summary>Eclipse Temurin 17.0.9（64 位）。</summary>
    private const string Temurin17 = """
        openjdk version "17.0.9" 2023-10-17
        OpenJDK Runtime Environment Temurin-17.0.9+9 (build 17.0.9+9)
        OpenJDK 64-Bit Server VM Temurin-17.0.9+9 (build 17.0.9+9, mixed mode, sharing)
        """;

    /// <summary>Oracle JDK 25.0.3（64 位，LTS）。</summary>
    private const string Oracle25 = """
        java version "25.0.3" 2026-04-15 LTS
        Java(TM) SE Runtime Environment (build 25.0.3+9-LTS-291)
        Java HotSpot(TM) 64-Bit Server VM (build 25.0.3+9-LTS-291, mixed mode, sharing)
        """;

    /// <summary>Microsoft Build of OpenJDK 17.0.13（64 位）。</summary>
    private const string Microsoft17 = """
        openjdk version "17.0.13" 2024-10-15
        OpenJDK Runtime Environment Microsoft-10377968 (build 17.0.13+11)
        OpenJDK 64-Bit Server VM Microsoft-10377968 (build 17.0.13+11, mixed mode, sharing)
        """;

    /// <summary>Eclipse Temurin 8u402（**32 位**：VM 那行写的是 Client VM，没有 64-Bit 字样）。</summary>
    private const string Temurin8x86 = """
        openjdk version "1.8.0_402"
        OpenJDK Runtime Environment (Temurin)(build 1.8.0_402-b06)
        OpenJDK Client VM (Temurin)(build 25.402-b06, mixed mode)
        """;

    /// <summary>Ubuntu 的 OpenJDK 21（无发行版标记）。</summary>
    private const string Ubuntu21 = """
        openjdk version "21.0.5" 2024-10-15
        OpenJDK Runtime Environment (build 21.0.5+11-Ubuntu-1ubuntu124.04)
        OpenJDK 64-Bit Server VM (build 21.0.5+11-Ubuntu-1ubuntu124.04, mixed mode, sharing)
        """;

    [TestMethod]
    public void OracleEightIsParsedFieldByField()
    {
        var info = JavaVersionParser.TryParse(Oracle8);

        Assert.IsNotNull(info);
        Assert.AreEqual("1.8.0_421", info.VersionText);
        Assert.AreEqual(8, info.MajorVersion);
        Assert.AreEqual("Oracle", info.Vendor);
        Assert.AreEqual(64, info.Bits);
    }

    [TestMethod]
    public void TemurinSeventeenIsParsedFieldByField()
    {
        var info = JavaVersionParser.TryParse(Temurin17);

        Assert.IsNotNull(info);
        Assert.AreEqual("17.0.9", info.VersionText);
        Assert.AreEqual(17, info.MajorVersion);
        // 关键点：Temurin 的输出里同时含 "Temurin" 与 "OpenJDK"，
        // 按"具体优先"匹配才不会把它说成 OpenJDK（Neo 缺的正是这个字段）。
        Assert.AreEqual("Eclipse Temurin", info.Vendor);
        Assert.AreEqual(64, info.Bits);
    }

    [TestMethod]
    public void OracleTwentyFiveIsParsedFieldByField()
    {
        var info = JavaVersionParser.TryParse(Oracle25);

        Assert.IsNotNull(info);
        Assert.AreEqual("25.0.3", info.VersionText);
        Assert.AreEqual(25, info.MajorVersion);
        Assert.AreEqual("Oracle", info.Vendor);
        Assert.AreEqual(64, info.Bits);
    }

    [TestMethod]
    public void MicrosoftBuildIsRecognisedAsMicrosoftNotJustOpenJdk()
    {
        var info = JavaVersionParser.TryParse(Microsoft17);

        Assert.IsNotNull(info);
        Assert.AreEqual(17, info.MajorVersion);
        Assert.AreEqual("Microsoft", info.Vendor);
        Assert.AreEqual(64, info.Bits);
    }

    [TestMethod]
    public void AThirtyTwoBitRuntimeIsReportedAsThirtyTwoBit()
    {
        // 32 位 Java 8 的输出里**没有** "64-Bit" 字样，只有 "Client VM" ——
        // 不加这条判断，界面会把 32 位写成「—」，而 32 位 Java 根本跑不了现代版本。
        var info = JavaVersionParser.TryParse(Temurin8x86);

        Assert.IsNotNull(info);
        Assert.AreEqual(8, info.MajorVersion);
        Assert.AreEqual("Eclipse Temurin", info.Vendor);
        Assert.AreEqual(32, info.Bits);
    }

    [TestMethod]
    public void AnUnbrandedOpenJdkIsReportedAsOpenJdk()
    {
        var info = JavaVersionParser.TryParse(Ubuntu21);

        Assert.IsNotNull(info);
        Assert.AreEqual(21, info.MajorVersion);
        Assert.AreEqual("OpenJDK", info.Vendor);
        Assert.AreEqual(64, info.Bits);
    }

    [TestMethod]
    public void TheVersionIsReadFromStandardErrorWhichIsWhereItActuallyIs()
    {
        // java -version 不写 stdout；这里喂进去的就是 stderr 那一份文本。
        // 解析器不关心它从哪来，但"喂 stdout 会得到 null"这件事要钉住，
        // 否则将来有人"顺手"改成读 stdout，界面会显示成"本机没有 Java"。
        Assert.IsNull(JavaVersionParser.TryParse(string.Empty));
        Assert.IsNull(JavaVersionParser.TryParse("            "));
        Assert.IsNull(JavaVersionParser.TryParse("Usage: java [-options] class [args...]"));
        Assert.IsNull(JavaVersionParser.TryParse("Error: could not open `C:\\Java\\lib\\amd64\\jvm.cfg'"));
    }

    [TestMethod]
    public void AVersionWithoutQuotesIsStillUnderstood()
    {
        // 个别精简发行版（以及 -version 的某些包装脚本）不写引号。
        var info = JavaVersionParser.TryParse("openjdk version 21.0.5 2024-10-15");

        Assert.IsNotNull(info);
        Assert.AreEqual("21.0.5", info.VersionText);
        Assert.AreEqual(21, info.MajorVersion);
    }

    [TestMethod]
    public void AnUnknownVendorFallsBackToADashRatherThanGuessing()
    {
        var info = JavaVersionParser.TryParse("""
            java version "17.0.9" 2023-10-17
            Some Runtime Environment (build 17.0.9+1)
            Some VM (build 17.0.9+1, mixed mode)
            """);

        Assert.IsNotNull(info);
        Assert.AreEqual("17.0.9", info.VersionText);
        Assert.AreEqual("—", info.Vendor);
        Assert.AreEqual(0, info.Bits, "认不出的位数写 0（界面显示「—」），不猜。");
    }
}
