using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// Maven 仓库布局的纯逻辑：版本比较、构件身份、日志配置位置。
/// <para>
/// 这三件事都是"M4 的类路径"与"安装器"共用的判断，也是**同一份数据被两处解释**的地方 ——
/// 解释不一致的表现是"安装时写到一个地方、启动时读另一个地方"，而两处都不会报错。
/// </para>
/// </summary>
[TestClass]
public sealed class MavenLayoutTests
{
    [TestMethod]
    [DataRow("9.10.1", "9.9", 1)]
    [DataRow("9.9", "9.10.1", -1)]
    [DataRow("3.3.2", "3.3.2", 0)]
    [DataRow("1.0.0", "1.0", 0)]
    [DataRow("1.0.0", "1.0.0-beta", 1)]
    [DataRow("4.1.97.Final", "4.1.9.Final", 1)]
    [DataRow("8.5.12", "8.5.9", 1)]
    public void MavenVersionComparisonOrdersNumericSegmentsByValue(string left, string right, int expected)
    {
        // 字符串序会把 9.10.1 判成比 9.9 小 —— Neo 的"保最高版本"因此在 asm / lwjgl 上保住的是低的那个。
        Assert.AreEqual(expected, Math.Sign(MavenVersion.Compare(left, right)));
    }

    [TestMethod]
    public void ALibraryIdentityKeepsTheClassifierOutOfTheVersion()
    {
        var main = MavenArtifactIdentity.FromRelativePath("org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2.jar");

        Assert.AreEqual("lwjgl", main.Artifact);
        Assert.AreEqual("3.3.2", main.Version);
        Assert.IsNull(main.Classifier);
        Assert.AreEqual("lwjgl", main.Key);

        var natives = MavenArtifactIdentity.FromRelativePath("org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-windows.jar");

        Assert.AreEqual("lwjgl", natives.Artifact);
        Assert.AreEqual("3.3.2", natives.Version);
        Assert.AreEqual("natives-windows", natives.Classifier);
        Assert.AreEqual(
            "lwjgl-natives-windows",
            natives.Key,
            "分类器必须留在去重键里，否则 natives 会被当成主 JAR 的旧版本丢掉。");
    }

    [TestMethod]
    public void MavenVersionsWithDashesAreStillOneIdentity()
    {
        // 真实存在：Forge 的版本号里带连字符，目录名就是它。
        var forge = MavenArtifactIdentity.FromRelativePath(
            "net/minecraftforge/forge/1.20.4-49.0.30/forge-1.20.4-49.0.30.jar");

        Assert.AreEqual("forge", forge.Artifact);
        Assert.AreEqual("1.20.4-49.0.30", forge.Version);
        Assert.IsNull(forge.Classifier);
        Assert.AreEqual("forge", forge.Key);
    }

    [TestMethod]
    public void AnUnparsableLayoutIsNotDeduped()
    {
        var weird = MavenArtifactIdentity.FromRelativePath("com/example/thing/1.0/renamed-artifact.jar");

        Assert.AreEqual("thing", weird.Artifact);
        Assert.IsNull(weird.Version, "文件名与坐标对不上时不猜版本。");
        Assert.AreEqual("renamed-artifact", weird.Key, "认不出来就退化成文件名 —— 宁可留下两个库，也不要少一个。");
    }

    [TestMethod]
    [DataRow("lwjgl-3.3.2-natives-windows.jar", "lwjgl")]
    [DataRow("asm-9.10.1.jar", "asm")]
    [DataRow("minecraft-1.20.4-client.jar", "minecraft")]
    [DataRow("lwjgl_util-2.9.4-nightly-20150209.jar", "lwjgl_util")]
    public void TheArtifactNameIsThePartBeforeTheFirstNumericVersion(string fileName, string expected)
    {
        Assert.AreEqual(expected, MavenArtifactIdentity.FromFileName(fileName).Artifact);
    }

    [TestMethod]
    public void TheLoggingConfigGoesWhereTheInstallerWritesIt()
    {
        var version = new VersionJson
        {
            Logging = new VersionLogging
            {
                Client = new VersionLoggingClient
                {
                    Argument = "-Dlog4j.configurationFile=${path}",
                    File = new DownloadInfo
                    {
                        Id = "client-1.12.xml",
                        Url = "https://piston-data.mojang.com/v1/objects/bd65e7d2e3c237be76cfbef4c2405033d7f91521/client-1.12.xml",
                    },
                },
            },
        };

        Assert.AreEqual(
            @"C:\mc\.minecraft\assets\log_configs\client-1.12.xml",
            LoggingConfigLayout.TryGetPath(version, @"C:\mc\.minecraft"));
    }

    [TestMethod]
    public void TheLoggingConfigFileNameFallsBackToTheUrl()
    {
        var version = new VersionJson
        {
            Logging = new VersionLogging
            {
                Client = new VersionLoggingClient
                {
                    File = new DownloadInfo { Url = "https://example.invalid/x/client-1.12.xml" },
                },
            },
        };

        Assert.AreEqual(
            @"C:\mc\.minecraft\assets\log_configs\client-1.12.xml",
            LoggingConfigLayout.TryGetPath(version, @"C:\mc\.minecraft"));
    }

    [TestMethod]
    public void WithoutAUsableLoggingConfigThereIsNoPathAndNoException()
    {
        Assert.IsNull(LoggingConfigLayout.TryGetPath(new VersionJson(), @"C:\mc\.minecraft"));
        Assert.IsNull(LoggingConfigLayout.TryGetPath(null, @"C:\mc\.minecraft"));

        // 名字只能用作单段路径名；带分隔符的 id 直接不给路径，而不是拼出一个越界路径。
        var bad = new VersionJson
        {
            Logging = new VersionLogging
            {
                Client = new VersionLoggingClient
                {
                    File = new DownloadInfo { Id = @"..\..\evil.xml", Url = "https://example.invalid/evil.xml" },
                },
            },
        };

        Assert.IsNull(LoggingConfigLayout.TryGetPath(bad, @"C:\mc\.minecraft"));
    }
}
