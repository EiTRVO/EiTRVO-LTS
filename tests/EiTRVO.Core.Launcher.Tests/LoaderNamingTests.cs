using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-f：加载器版本 id 的命名规则。
/// <para>
/// 安装器写下的目录名与启动时反查的名字必须由**同一条规则**算出来，
/// 所以这条规则单独测：两边各写一遍的后果是"装完了、启动说版本不存在"。
/// </para>
/// </summary>
[TestClass]
public sealed class LoaderNamingTests
{
    [TestMethod]
    public void FabricAndQuiltIdsFollowTheShapeTheirProfilesDeclare()
    {
        Assert.AreEqual("fabric-loader-0.15.7-1.20.4", LoaderNaming.VersionId(LoaderKind.Fabric, "0.15.7", "1.20.4"));
        Assert.AreEqual("quilt-loader-0.23.1-1.20.4", LoaderNaming.VersionId(LoaderKind.Quilt, "0.23.1", "1.20.4"));
    }

    [TestMethod]
    public void ForgeAndNeoForgeIdsFollowTheDirectoryNamesTheirInstallersActuallyWrite()
    {
        // 三个形状都是 2026-10-04 用真安装器核过的目录名：
        // Forge 1.20.4 → 1.20.4-forge-49.0.30、Forge 1.12.2 → 1.12.2-forge-14.23.5.2860、
        // NeoForge 20.4.237 → neoforge-20.4.237、旧体系 NeoForge（1.20.1）→ 1.20.1-forge-47.1.106。
        Assert.AreEqual("1.20.4-forge-49.0.30", LoaderNaming.VersionId(LoaderKind.Forge, "49.0.30", "1.20.4"));
        Assert.AreEqual("1.12.2-forge-14.23.5.2860", LoaderNaming.VersionId(LoaderKind.Forge, "14.23.5.2860", "1.12.2"));
        Assert.AreEqual("neoforge-20.4.237", LoaderNaming.VersionId(LoaderKind.NeoForge, "20.4.237", "1.20.4"));
        Assert.AreEqual("1.20.1-forge-47.1.106", LoaderNaming.VersionId(LoaderKind.NeoForge, "47.1.106", "1.20.1"));
    }

    [TestMethod]
    public void OnlyOneTwentyOneCountsAsTheLegacyNeoForgeEra()
    {
        // 这条规则同时被版本列表（B-8）、安装地址与版本 id 用着，只有一份。
        Assert.IsTrue(LoaderNaming.IsLegacyNeoForge("1.20.1"));
        Assert.IsFalse(LoaderNaming.IsLegacyNeoForge("1.20.2"));
        Assert.IsFalse(LoaderNaming.IsLegacyNeoForge("1.20.4"));
    }

    [TestMethod]
    public void SurroundingWhitespaceIsTrimmedInsteadOfBecomingPartOfTheDirectoryName()
    {
        Assert.AreEqual("fabric-loader-0.15.7-1.20.4", LoaderNaming.VersionId(LoaderKind.Fabric, " 0.15.7 ", " 1.20.4 "));
    }

    [TestMethod]
    public void OnlyFabricAndQuiltAreProfileLoaders()
    {
        Assert.IsTrue(LoaderNaming.IsProfileLoader(LoaderKind.Fabric));
        Assert.IsTrue(LoaderNaming.IsProfileLoader(LoaderKind.Quilt));
        Assert.IsFalse(LoaderNaming.IsProfileLoader(LoaderKind.Vanilla));
        Assert.IsFalse(LoaderNaming.IsProfileLoader(LoaderKind.Forge));
        Assert.IsFalse(LoaderNaming.IsProfileLoader(LoaderKind.NeoForge));
        Assert.IsFalse(LoaderNaming.IsProfileLoader(LoaderKind.OptiFine));
    }

    [TestMethod]
    public void OnlyForgeAndNeoForgeAreInstallerLoaders()
    {
        Assert.IsTrue(LoaderNaming.IsInstallerLoader(LoaderKind.Forge));
        Assert.IsTrue(LoaderNaming.IsInstallerLoader(LoaderKind.NeoForge));
        Assert.IsFalse(LoaderNaming.IsInstallerLoader(LoaderKind.Fabric));
        Assert.IsFalse(LoaderNaming.IsInstallerLoader(LoaderKind.Quilt));
        Assert.IsFalse(LoaderNaming.IsInstallerLoader(LoaderKind.Vanilla));
        Assert.IsFalse(LoaderNaming.IsInstallerLoader(LoaderKind.OptiFine));
    }

    [TestMethod]
    public void OnlyVanillaHasNoLoaderVersionId()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LoaderNaming.VersionId(LoaderKind.Vanilla, "1.2.3", "1.20.4"));
    }

    [TestMethod]
    public void TheOptiFineIdIsTheDirectoryNameTheOfficialInstallerWrites()
    {
        // 真机核过：官方安装器（`optifine.Installer`）写出的就是 <游戏版本>-OptiFine_<版本段>，
        // 而"版本段"取自官方文件名（OptiFine_1.20.1_HD_U_I6.jar → HD_U_I6）。
        // 这一条写错的表现是"装完了、点启动说这个版本不存在"。
        Assert.AreEqual("1.20.1-OptiFine_HD_U_I6", LoaderNaming.VersionId(LoaderKind.OptiFine, "HD_U_I6", "1.20.1"));
        Assert.AreEqual(
            "1.20.4-OptiFine_HD_U_I8_pre4",
            LoaderNaming.VersionId(LoaderKind.OptiFine, "HD_U_I8_pre4", "1.20.4"));

        Assert.IsTrue(LoaderNaming.IsOptiFine(LoaderKind.OptiFine));
        Assert.IsFalse(LoaderNaming.IsOptiFine(LoaderKind.Forge));
        Assert.IsFalse(LoaderNaming.IsInstallerLoader(LoaderKind.OptiFine));
        Assert.IsFalse(LoaderNaming.IsProfileLoader(LoaderKind.OptiFine));
    }

    [TestMethod]
    public void AMissingVersionStringIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(
            () => LoaderNaming.VersionId(LoaderKind.Fabric, "  ", "1.20.4"));

        Assert.ThrowsException<ArgumentException>(
            () => LoaderNaming.VersionId(LoaderKind.Fabric, "0.15.7", string.Empty));
    }
}
