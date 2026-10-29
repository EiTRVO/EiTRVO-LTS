using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-e-3d：加载器显示名（p02 的「模组加载器」列、p04 的加载器行、p01/p03/p13 的属性面板）
/// 在**英文**界面下必须真的是英文。
/// </summary>
/// <remarks>
/// 为什么专门为"加载器"写一组：它的键与别的字段有个要命的区别 ——
/// **`原版` 的英文是 `Vanilla`，而键（简体中文原文）本身看起来就像个正常值**。
/// 所以"没拼、直接把键显示出来"这件事在中文界面上 100% 正常、只有英文那一份能暴露
/// （AGENTS.md §5.3 的第 ⑩ 条与 e-3b 那个静默 bug 同源）。
/// <para>
/// 本类会切全进程的语言单例，所以必须与其它测试串行（项目默认方法级并行）。
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class LoaderDisplayTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void TheInstanceTableRendersTheLoaderColumnInBothLanguages()
    {
        var record = new SampleInstanceCatalog().GetDetail("1.20.4-Forge");

        Assert.AreEqual("Forge 49.0.30", LocalizedText.Of(record.LoaderDisplay));

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual("Forge 49.0.30", LocalizedText.Of(record.LoaderDisplay));

        // 原版那一行才是关键：中文是 `原版`、英文必须是 `Vanilla`。
        var vanilla = new SampleInstanceCatalog().GetDetail("1.21.4-Vanilla");

        Assert.AreEqual("Vanilla", LocalizedText.Of(vanilla.LoaderDisplay));
    }

    [TestMethod]
    public void TheWizardLoaderRowsAndDetailsFollowTheLanguageSwitch()
    {
        var vanilla = LoaderOptionOf(LoaderKind.Vanilla, TextTemplate.Of("不安装任何加载器，纯净游戏"));

        var chinese = LoaderOptionViewModel.From(vanilla, isActive: true);
        Assert.AreEqual("原版", chinese.Display);
        Assert.AreEqual("不安装任何加载器，纯净游戏", chinese.Detail);
        Assert.IsTrue(chinese.IsSelectable, "原版行永远可点（只是不推荐）。");

        var unavailable = LoaderOptionViewModel.From(vanilla with { Availability = LoaderAvailability.Unavailable }, isActive: false);
        Assert.IsFalse(unavailable.IsSelectable, "不可用的加载器行不该看起来能点。");

        Loc.Service.SetLanguage(LocalizationService.English);

        var english = LoaderOptionViewModel.From(vanilla, isActive: true);
        Assert.AreEqual("Vanilla", english.Display);
        Assert.AreEqual("No mod loader — the clean game", english.Detail);

        // 专有名词两种语言一样；版本下拉的**显示**带「（推荐）」、**值**是纯版本号。
        var forge = LoaderOptionViewModel.From(
            LoaderOptionOf(LoaderKind.Forge, TextTemplate.Of("{0} · 生态兼容性最好", "49.0.30")) with
            {
                Versions = ["49.0.30", "49.0.29"],
                RecommendedVersion = "49.0.30",
            },
            isActive: false);

        Assert.AreEqual("Forge", forge.Display);
        Assert.AreEqual("49.0.30 · best ecosystem compatibility", forge.Detail);
        Assert.AreEqual("49.0.30 (recommended)", forge.VersionOptions[0].Text);
        Assert.AreEqual("49.0.30", forge.VersionOptions[0].Value);
        Assert.AreEqual("49.0.29", forge.VersionOptions[1].Text, "没被推荐的项不加后缀。");
    }

    private static LoaderOption LoaderOptionOf(LoaderKind kind, TextTemplate detail) =>
        new(
            kind,
            InstanceNaming.Display(kind),
            detail,
            LoaderAvailability.Available,
            false,
            []);
}
