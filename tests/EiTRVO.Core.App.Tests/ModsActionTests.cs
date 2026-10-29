using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-g-2：p07 的「启用所选 / 禁用所选 / 删除所选」真的按**选中的那一行**改磁盘。
/// </summary>
/// <remarks>
/// 这一层钉的是**接线**：按钮什么时候可按、交回内核的是哪个文件名、动作之后页面怎么显示结果；
/// 改名 / 备份目录 / 路径校验的细节在 <c>ContentCatalogTests</c>（内核，跑真实临时目录）。
/// </remarks>
[TestClass]
public sealed class ModsActionTests
{
    private static ModsViewModel NewPage(FakeContentCatalog content, SampleInstanceCatalog? instances = null) =>
        new(
            content,
            instances ?? new SampleInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

    private static ModRowViewModel Row(ModsViewModel page, string name) =>
        page.Mods.Single(mod => string.Equals(mod.Name, name, StringComparison.Ordinal));

    [TestMethod]
    public void WithoutASelectionAllThreeActionsAreDisabled()
    {
        var page = NewPage(new FakeContentCatalog());

        Assert.IsNull(page.SelectedMod);
        Assert.IsFalse(page.EnableSelectedCommand.CanExecute(null), "没有选中行时「启用所选」是灰的。");
        Assert.IsFalse(page.DisableSelectedCommand.CanExecute(null), "没有选中行时「禁用所选」是灰的。");
        Assert.IsFalse(page.DeleteSelectedCommand.CanExecute(null), "没有选中行时「删除所选」是灰的。");
    }

    [TestMethod]
    public void AnEnabledRowOnlyAllowsDisableOrDelete()
    {
        var page = NewPage(new FakeContentCatalog());
        page.SelectedMod = Row(page, "Sodium");

        Assert.IsFalse(page.EnableSelectedCommand.CanExecute(null), "它已经启用了 —— 「启用」是一个不存在的动作，不能亮着。");
        Assert.IsTrue(page.DisableSelectedCommand.CanExecute(null));
        Assert.IsTrue(page.DeleteSelectedCommand.CanExecute(null));
    }

    [TestMethod]
    public void ADisabledRowOnlyAllowsEnableOrDelete()
    {
        var page = NewPage(new FakeContentCatalog());
        page.SelectedMod = Row(page, "OptiFine（作为模组）");

        Assert.IsTrue(page.EnableSelectedCommand.CanExecute(null));
        Assert.IsFalse(page.DisableSelectedCommand.CanExecute(null), "它已经禁用了。");
        Assert.IsTrue(page.DeleteSelectedCommand.CanExecute(null));
    }

    [TestMethod]
    public void DisablingTheSelectedRowPassesItsFileNameToTheKernel()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);
        page.SelectedMod = Row(page, "Sodium");

        page.DisableSelectedCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { "sodium-0.5.8.jar" },
            content.DisabledFileNames.ToArray(),
            "交给内核的是**磁盘上的文件名**（跨层传回来当不可信输入，内核还会再校验一次）。");
        Assert.AreEqual(
            ModEnablement.Disabled,
            Row(page, "Sodium").Enablement,
            "动作之后那一行真的变成了「已禁用」（行是现算的，刷新即可看到）。");
        Assert.IsNull(page.SelectedMod, "改过名之后不把同一行挑回来 —— 按显示名挑可能挑到另一个文件。");
        Assert.AreEqual(string.Empty, page.ActionError);
        Assert.AreEqual(string.Empty, page.ActionNote, "启用 / 禁用无需交代，不占高度。");
    }

    [TestMethod]
    public void EnablingTheSelectedRowPassesItsDisabledFileNameToTheKernel()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);
        page.SelectedMod = Row(page, "OptiFine（作为模组）");

        page.EnableSelectedCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { "optifine-1.20.4.jar.disabled" },
            content.EnabledFileNames.ToArray(),
            "禁用的行交给内核的是带 .disabled 后缀的那个名字（启用 = 去掉它）。");
        Assert.AreEqual(ModEnablement.Enabled, Row(page, "OptiFine（作为模组）").Enablement);
    }

    [TestMethod]
    public void DeletingTheSelectedRowRemovesItAndSaysWhereItWent()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);
        page.SelectedMod = Row(page, "Flywheel");

        page.DeleteSelectedCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { "flywheel-0.6.10.jar" }, content.DeletedFileNames.ToArray());
        Assert.AreEqual(
            0,
            page.Mods.Count(mod => string.Equals(mod.Name, "Flywheel", StringComparison.Ordinal)),
            "删除之后表格里没有它了。");
        Assert.IsNull(page.SelectedMod);
        Assert.AreEqual(string.Empty, page.ActionError);

        // 成功也要交代一句：文件没有消失，只是搬走了 —— 用户该知道去哪里找回来。
        StringAssert.Contains(page.ActionNote, "backups\\mods");
        Assert.AreEqual(string.Empty, page.ActionError);
    }

    [TestMethod]
    public void AFailedActionShowsTheReasonInsteadOfSilentlyDoingNothing()
    {
        var content = new FakeContentCatalog
        {
            SetModEnabledResult = ContentActionResult.Failure(
                TextTemplate.Of("改名失败（{0}）：文件可能被游戏占用，请先退出游戏。", "sodium-0.5.8.jar")),
        };

        var page = NewPage(content);
        page.SelectedMod = Row(page, "Sodium");

        page.DisableSelectedCommand.Execute(null);

        StringAssert.Contains(page.ActionError, "被游戏占用");
        Assert.AreEqual(string.Empty, page.ActionNote);
        Assert.AreEqual(
            ModEnablement.Enabled,
            Row(page, "Sodium").Enablement,
            "失败了就什么都没变 —— 界面不能显示成一个成功。");
    }

    [TestMethod]
    public void WithoutAnInstanceTheActionsAreDisabledEvenWithRows()
    {
        // 有行（替身数据），但没有"当前实例"：动作没有落点，按钮必须是灰的。
        var content = new FakeContentCatalog();
        var page = NewPage(content, new SampleInstanceCatalog { ActiveInstanceName = string.Empty });

        page.SelectedMod = page.Mods.Count > 0 ? page.Mods[0] : null;

        Assert.IsNull(page.SelectedMod, "没有实例时 Mods 为空（替身也只认 1.20.4-Forge）。");
        Assert.IsFalse(page.DeleteSelectedCommand.CanExecute(null));
        Assert.IsFalse(page.EnableSelectedCommand.CanExecute(null));
        Assert.IsFalse(page.DisableSelectedCommand.CanExecute(null));
    }

    [TestMethod]
    public void ActivatingThePageClearsThePreviousActionMessages()
    {
        var content = new FakeContentCatalog
        {
            DeleteModResult = ContentActionResult.Failure(TextTemplate.Of("删除失败（{0}）：文件可能被游戏占用，请先退出游戏。", "x.jar")),
        };

        var page = NewPage(content);
        page.SelectedMod = Row(page, "Flywheel");
        page.DeleteSelectedCommand.Execute(null);

        StringAssert.Contains(page.ActionError, "删除失败");

        // 下次再进这一页时，上一页留下的红字不该留在新的内容旁边。
        page.OnActivated();

        Assert.AreEqual(string.Empty, page.ActionError);
        Assert.AreEqual(string.Empty, page.ActionNote);
    }
}
