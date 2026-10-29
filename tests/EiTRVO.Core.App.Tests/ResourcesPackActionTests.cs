using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-g-2b：p09 资源包的「启用 / 停用 / 上移优先级 / 下移优先级 / 删除」按**选中的那一行**改磁盘。
/// </summary>
/// <remarks>
/// 这一层钉的是**接线**：按钮什么时候可按、交回内核的是哪个文件名、动作之后页面怎么显示结果；
/// <c>options.txt</c> 的读写与顺序方向在 <c>ContentCatalogTests</c>（内核，跑真实临时文件）。
/// 本类会切全进程的语言单例，因此与其它测试串行。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ResourcesPackActionTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    private static ResourcesViewModel NewPage(FakeContentCatalog content, FakeInstanceCatalog? instances = null) =>
        new(content, instances ?? new FakeInstanceCatalog(), new FakeShellService(), new FakeDownloadCatalog(), new FakeNavigationService());

    private static ResourcePackRowViewModel Row(ResourcesViewModel page, string name) =>
        page.ResourcePacks.Single(pack => string.Equals(pack.Name, name, StringComparison.Ordinal));

    [TestMethod]
    public void TheHighestPriorityPackIsSelectedOnEntry()
    {
        var page = NewPage(new FakeContentCatalog());

        Assert.IsNotNull(page.SelectedResourcePack);
        Assert.AreEqual("Faithful 32x", page.SelectedResourcePack!.Name, "设计稿把优先级最高的那一行画成 `.opt.sel`。");
        Assert.AreEqual(1, page.SelectedResourcePack!.Priority);
    }

    [TestMethod]
    public void AnEnabledRowOnlyAllowsDisableMoveDownOrDelete()
    {
        var page = NewPage(new FakeContentCatalog());

        page.SelectedResourcePack = Row(page, "Faithful 32x");

        Assert.IsFalse(page.EnableResourcePackCommand.CanExecute(null), "它已经启用了。");
        Assert.IsTrue(page.DisableResourcePackCommand.CanExecute(null));
        Assert.IsFalse(page.MoveResourcePackUpCommand.CanExecute(null), "它已经是优先级 1，不能再上移。");
        Assert.IsTrue(page.MoveResourcePackDownCommand.CanExecute(null), "下面还有别的已启用包。");
        Assert.IsTrue(page.DeleteResourcePackCommand.CanExecute(null));

        page.SelectedResourcePack = Row(page, "Fresh Animations");

        Assert.IsTrue(page.MoveResourcePackUpCommand.CanExecute(null));
        Assert.IsFalse(page.MoveResourcePackDownCommand.CanExecute(null), "它已经是启用列表里最低的那个。");
    }

    [TestMethod]
    public void ADisabledRowOnlyAllowsEnableOrDelete()
    {
        var page = NewPage(new FakeContentCatalog());

        page.SelectedResourcePack = Row(page, "Stay True");

        Assert.IsTrue(page.EnableResourcePackCommand.CanExecute(null));
        Assert.IsFalse(page.DisableResourcePackCommand.CanExecute(null));
        Assert.IsFalse(page.MoveResourcePackUpCommand.CanExecute(null), "未启用的包没有优先级可言。");
        Assert.IsFalse(page.MoveResourcePackDownCommand.CanExecute(null));
        Assert.IsTrue(page.DeleteResourcePackCommand.CanExecute(null));
    }

    [TestMethod]
    public void DisablingTheSelectedPackPassesItsFileNameAndKeepsTheSelection()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);
        page.SelectedResourcePack = Row(page, "Fresh Animations");

        page.DisableResourcePackCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { "Fresh Animations.zip" },
            content.DisabledResourcePacks.ToArray(),
            "交给内核的是**磁盘上的文件名**（它同时决定 options.txt 里的 id）。");
        Assert.IsFalse(Row(page, "Fresh Animations").IsEnabled, "动作之后那一行真的变成了「已禁用」。");
        Assert.IsNotNull(page.SelectedResourcePack);
        Assert.AreEqual("Fresh Animations", page.SelectedResourcePack!.Name, "启用 / 停用不改文件名，高亮要留在原行上。");
        Assert.AreEqual(string.Empty, page.ActionError);
        Assert.AreEqual(string.Empty, page.ActionNote);
    }

    [TestMethod]
    public void EnablingTheSelectedPackPassesItsFileName()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);
        page.SelectedResourcePack = Row(page, "Stay True");

        page.EnableResourcePackCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { "Stay True.zip" }, content.EnabledResourcePacks.ToArray());
        Assert.IsTrue(Row(page, "Stay True").IsEnabled);
    }

    [TestMethod]
    public void MovingUpPassesTheDirectionAndRefreshesTheSelection()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);
        page.SelectedResourcePack = Row(page, "Fresh Animations");

        page.MoveResourcePackUpCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { ("Fresh Animations.zip", -1) },
            content.MovedResourcePacks.ToArray(),
            "方向 -1 = 上移（更高优先级）—— 内核把 -1 解释成「往数组末尾挪」，界面这边只管传方向。");
        Assert.AreEqual("Fresh Animations", page.SelectedResourcePack!.Name, "上移之后高亮要跟着那一行走。");
        Assert.AreEqual(1, page.SelectedResourcePack!.Priority);
        Assert.IsFalse(page.MoveResourcePackUpCommand.CanExecute(null), "到顶了，按钮要立刻变灰。");
        Assert.IsTrue(page.MoveResourcePackDownCommand.CanExecute(null));

        page.MoveResourcePackDownCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { ("Fresh Animations.zip", -1), ("Fresh Animations.zip", 1) },
            content.MovedResourcePacks.ToArray());
    }

    [TestMethod]
    public void DeletingSaysWhereItWentAndClearsTheSelection()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);
        page.SelectedResourcePack = Row(page, "Stay True");

        page.DeleteResourcePackCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { "Stay True.zip" }, content.DeletedResourcePacks.ToArray());
        Assert.AreEqual(
            0,
            page.ResourcePacks.Count(pack => string.Equals(pack.Name, "Stay True", StringComparison.Ordinal)));
        Assert.IsNull(page.SelectedResourcePack, "删掉的那一行没得选。");
        Assert.AreEqual(string.Empty, page.ActionError);
        StringAssert.Contains(page.ActionNote, "backups\\resourcepacks");
    }

    [TestMethod]
    public void AFailedResourcePackActionShowsTheReasonInsteadOfSilentlyDoingNothing()
    {
        var content = new FakeContentCatalog
        {
            ResourcePackActionResult = ContentActionResult.Failure(
                TextTemplate.Of("写入 options.txt 失败（{0}），这次没有改动。", "IOException")),
        };

        var page = NewPage(content);
        page.SelectedResourcePack = Row(page, "Faithful 32x");

        page.DisableResourcePackCommand.Execute(null);

        StringAssert.Contains(page.ActionError, "options.txt");
        Assert.AreEqual(string.Empty, page.ActionNote);
        Assert.IsTrue(
            Row(page, "Faithful 32x").IsEnabled,
            "失败了就什么都没变 —— 界面不能显示成一个成功。");
    }

    [TestMethod]
    public void WithoutAnInstanceTheActionsAreDisabled()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content, new FakeInstanceCatalog { ActiveInstanceName = string.Empty });

        page.SelectedResourcePack = page.ResourcePacks[0];

        Assert.IsFalse(page.EnableResourcePackCommand.CanExecute(null));
        Assert.IsFalse(page.DisableResourcePackCommand.CanExecute(null));
        Assert.IsFalse(page.MoveResourcePackUpCommand.CanExecute(null));
        Assert.IsFalse(page.MoveResourcePackDownCommand.CanExecute(null));
        Assert.IsFalse(page.DeleteResourcePackCommand.CanExecute(null));

        page.DisableResourcePackCommand.Execute(null);
        page.DeleteResourcePackCommand.Execute(null);

        Assert.AreEqual(0, content.DisabledResourcePacks.Count);
        Assert.AreEqual(0, content.DeletedResourcePacks.Count);
    }

    [TestMethod]
    public void ActivatingThePageClearsThePreviousActionMessages()
    {
        var content = new FakeContentCatalog
        {
            ResourcePackActionResult = ContentActionResult.Failure(
                TextTemplate.Of("这个资源包当前没有启用，不能调整优先级。")),
        };

        var page = NewPage(content);
        page.SelectedResourcePack = Row(page, "Faithful 32x");
        page.DisableResourcePackCommand.Execute(null);

        StringAssert.Contains(page.ActionError, "没有启用");

        page.OnActivated();

        Assert.AreEqual(string.Empty, page.ActionError);
        Assert.AreEqual(string.Empty, page.ActionNote);
    }
}
