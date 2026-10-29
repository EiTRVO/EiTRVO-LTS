using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-e-3e：向导四步名（p04）与版本表的「本地状态」（p05）在**英文**界面下必须真的是英文。
/// </summary>
/// <remarks>
/// 为什么专门写一组：这两处的键（`游戏版本` / `已安装`）与加载器那一族同理 ——
/// **键就是简体中文原文**，所以"没拼、直接把键显示出来"在中文界面上 100% 正常，
/// 只有英文那一份能证明它真的走过投影（AGENTS.md §5.3 第 ⑨ / ⑩ 条）。
/// 2026-10-09 的英文 p04 归档里，页头那句还是 `Step 2 of 4 · 模组加载器` —— 前缀是英文、步骤名是中文，
/// 正是这一条要钉住的现象。
/// <para>
/// 本类会切全进程的语言单例，所以必须与其它测试串行（项目默认方法级并行）。
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class VersionTextTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void TheWizardStepTitlesAndTheHeaderFollowTheLanguageSwitch()
    {
        var wizard = NewWizard();

        Assert.AreEqual("模组加载器", wizard.CurrentStepTitle);
        CollectionAssert.AreEqual(
            new[] { "游戏版本", "模组加载器", "名称与目录", "确认并安装" },
            wizard.Steps.Select(static step => step.Title).ToArray());
        Assert.AreEqual("第 2 步，共 4 步 · 模组加载器", wizard.SectionSubtitle, "步骤名是嵌进去的（第 ⑤ 条）。");

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual("Mod loader", wizard.CurrentStepTitle);
        CollectionAssert.AreEqual(
            new[] { "Version", "Mod loader", "Name and directory", "Confirm and install" },
            wizard.Steps.Select(static step => step.Title).ToArray());
        StringAssert.Contains(wizard.SectionSubtitle, "Mod loader", "页头那句里的步骤名也要跟着切。");
    }

    [TestMethod]
    public void TheVersionLocalStateFollowsTheLanguageSwitch()
    {
        var rows = new SampleVersionCatalog().GetVersions();

        Assert.AreEqual("已安装", VersionRowViewModel.From(rows.Single(static r => r.Id == "1.20.4")).LocalStateText);
        Assert.AreEqual("未安装", VersionRowViewModel.From(rows.Single(static r => r.Id == "1.7.10")).LocalStateText);
        Assert.AreEqual("缺少 Java 21", VersionRowViewModel.From(rows.Single(static r => r.Id == "1.21.4")).LocalStateText);

        Loc.Service.SetLanguage(LocalizationService.English);

        var installed = VersionRowViewModel.From(rows.Single(static r => r.Id == "1.20.4"));

        Assert.AreEqual("Installed", installed.LocalStateText);
        Assert.AreEqual("Not installed", VersionRowViewModel.From(rows.Single(static r => r.Id == "1.7.10")).LocalStateText);

        // 版本号是**数据**：它留在参数里，不跟着语言变（第 ③ / ⑩ 条）。
        Assert.AreEqual("Requires Java 21", VersionRowViewModel.From(rows.Single(static r => r.Id == "1.21.4")).LocalStateText);

        // 着色按枚举判断，两种语言下都一样。
        Assert.AreEqual(VersionLocalState.Installed, installed.LocalState);
        Assert.AreEqual(VersionKind.Release, installed.Kind);
    }

    /// <summary>p04 的向导，用的是演示版本清单（键与真实实现同一套）。</summary>
    private static NewInstanceViewModel NewWizard() =>
        new(
            new SampleVersionCatalog(),
            new FakeInstanceCatalog(),
            new FakeNavigationService(),
            new FakeDownloadCatalog(),
            new FakeJavaRuntimeCatalog());
}
