using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-i 的官方清单规则（纯函数）：版本段来自文件名、1.8/1.9 重映射、排序、以及"不猜"的那几条。
/// <para>
/// 2026-10-23 起列表来自 optifine.net 官方 downloads 页，不再走任何镜像。
/// </para>
/// </summary>
[TestClass]
public sealed class OptiFineCatalogTests
{
    [TestMethod]
    public void TheEditionComesFromTheFileNameNotFromPageText()
    {
        Assert.AreEqual("HD_U_I8", OptiFineCatalog.EditionOf("OptiFine_1.20.4_HD_U_I8.jar"));
        Assert.AreEqual("HD_U_I6", OptiFineCatalog.EditionOf("OptiFine_1.20.1_HD_U_I6.jar"));
        Assert.AreEqual("HD_U_I8_pre4", OptiFineCatalog.EditionOf("preview_OptiFine_1.20.4_HD_U_I8_pre4.jar"));
        Assert.AreEqual("HD_U_I7", OptiFineCatalog.EditionOf("OptiFine_1.8.0_HD_U_I7.jar"));

        Assert.IsNull(OptiFineCatalog.EditionOf("OptiFine.jar"));
        Assert.IsNull(OptiFineCatalog.EditionOf(null));
    }

    [TestMethod]
    public void TheLookupVersionRemapsTheTwoLegacyOnesAndPointsAtTheOfficialPage()
    {
        Assert.AreEqual("1.8.0", OptiFineCatalog.LookupVersion("1.8"));
        Assert.AreEqual("1.9.0", OptiFineCatalog.LookupVersion("1.9"));
        Assert.AreEqual("1.20.4", OptiFineCatalog.LookupVersion("1.20.4"));
        Assert.AreEqual("1.21.11", OptiFineCatalog.LookupVersion("1.21.11"));

        Assert.AreEqual(OptiFineCatalog.OfficialDownloadsPage, OptiFineCatalog.BuildsUrl("1.8"));
        Assert.AreEqual(OptiFineCatalog.OfficialDownloadsPage, OptiFineCatalog.BuildsUrl("1.20.4"));
    }

    [TestMethod]
    public void TheAdloadUrlUsesTheOfficialHost()
    {
        Assert.AreEqual(
            OptiFineFixture.DownloadUrl,
            OptiFineCatalog.AdloadUrl("OptiFine_1.20.4_HD_U_I8.jar"));
    }

    [TestMethod]
    public void TheLegacy1710BuildsAreParsedFromTheOfficialMirrorLink()
    {
        const string html = """
            <table><tr><td class='colMirror'>
              <a href="http://optifine.net/adloadx?f=OptiFine_1.7.10_HD_U_E7.jar">(Mirror)</a>
            </td></tr></table>
            """;

        var builds = OptiFineCatalog.Parse(html, "1.7.10");

        Assert.AreEqual(1, builds.Count);
        Assert.AreEqual("HD_U_E7", builds[0].Edition);
        Assert.AreEqual("1.7.10-OptiFine_HD_U_E7", builds[0].VersionId);
    }

    [TestMethod]
    public void StableBuildsComeFirstNewestFirstAndPreviewsAreMarked()
    {
        var builds = OptiFineCatalog.Parse(OptiFineFixture.ListHtmlThreeBuilds, "1.20.4");

        CollectionAssert.AreEqual(
            new[] { "HD_U_I8", "HD_U_I6", "HD_U_I8_pre4" },
            builds.Select(static build => build.Edition).ToArray());

        Assert.IsFalse(builds[0].IsPreview);
        Assert.IsFalse(builds[1].IsPreview);
        Assert.IsTrue(builds[2].IsPreview, "预览版排在正式版之后，且必须标出来。");

        Assert.AreEqual(OptiFineFixture.VersionId, builds[0].VersionId);
        Assert.AreEqual("1.20.4-OptiFine_HD_U_I8_pre4", builds[2].VersionId);

        Assert.AreEqual(OptiFineFixture.DownloadUrl, builds[0].DownloadUrl);
        Assert.AreEqual(
            "https://optifine.net/adloadx?f=preview_OptiFine_1.20.4_HD_U_I8_pre4.jar",
            builds[2].DownloadUrl);
    }

    [TestMethod]
    public void EntriesWhoseFileNameIsNotAnOptiFineNameAreSkippedInsteadOfGuessed()
    {
        var html = OptiFineFixture.Html("weird-name.jar", "OptiFine_1.20.4_HD_U_I8.jar");

        var builds = OptiFineCatalog.Parse(html, "1.20.4");

        Assert.AreEqual(1, builds.Count);
        Assert.AreEqual("HD_U_I8", builds[0].Edition);
    }

    [TestMethod]
    public void BuildsForOtherGameVersionsAreIgnored()
    {
        var html = OptiFineFixture.Html("OptiFine_1.20.1_HD_U_I6.jar", "OptiFine_1.20.4_HD_U_I8.jar");

        var builds = OptiFineCatalog.Parse(html, "1.20.4");

        Assert.AreEqual(1, builds.Count);
        Assert.AreEqual("HD_U_I8", builds[0].Edition);
    }

    [TestMethod]
    public void TheGameVersionFallbackHandlesByteSizedVersionNames()
    {
        var html = OptiFineFixture.Html("OptiFine_1.8.0_HD_U_I7.jar");

        var builds = OptiFineCatalog.Parse(html, "1.8");

        Assert.AreEqual("1.8-OptiFine_HD_U_I7", builds[0].VersionId, "版本 id 用我们这边的游戏版本。");
        Assert.AreEqual(
            "https://optifine.net/adloadx?f=OptiFine_1.8.0_HD_U_I7.jar",
            builds[0].DownloadUrl);
    }

    [TestMethod]
    public void AnEmptyPageIsATypedFailure()
    {
        Assert.ThrowsException<VersionDataException>(() => OptiFineCatalog.Parse(string.Empty, "1.20.4"));
        Assert.ThrowsException<VersionDataException>(() => OptiFineCatalog.Parse("   ", "1.20.4"));
    }

    [TestMethod]
    public void TheDisplayNameIsReadable()
    {
        var builds = OptiFineCatalog.Parse(OptiFineFixture.ListHtml, "1.20.4");

        Assert.AreEqual("OptiFine HD U I8", builds[0].DisplayName);
    }
}
