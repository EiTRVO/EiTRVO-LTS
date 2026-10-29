namespace EiTRVO.Core.App.Tests;

using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Sample;

/// <summary>
/// p07 / p09 的「从 Modrinth 下载」对话框（2026-10-21）：搜索 → 准备 → 排进下载队列。
/// </summary>
[TestClass]
public sealed class ModrinthDownloadDialogTests
{
    [TestMethod]
    public async Task DownloadingAModQueuesTheContentAndOpensTheDownloadsPage()
    {
        var content = new FakeContentCatalog();
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var dialog = new ModrinthDownloadDialogViewModel(content, downloads, navigation);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;

        dialog.Open(ModrinthContentKind.Mod, "1.20.4-Forge");
        await dialog.SearchCommand.ExecuteAsync(null);

        Assert.AreEqual(2, dialog.Entries.Count);
        Assert.IsNotNull(dialog.SelectedEntry);
        Assert.IsTrue(dialog.DownloadCommand.CanExecute(null));

        await dialog.DownloadCommand.ExecuteAsync(null);

        var search = content.ModrinthContentSearches.Last();
        Assert.AreEqual("1.20.4-Forge", search.InstanceName);
        Assert.AreEqual(ModrinthContentKind.Mod, search.Kind);

        var request = downloads.ContentRequests.Single();
        Assert.AreEqual(ModrinthContentKind.Mod, request.Kind);
        Assert.AreEqual("1.20.4-Forge", request.InstanceName);
        Assert.AreEqual(1, request.Targets.Count);

        CollectionAssert.Contains(navigation.Visited, PageId.Downloads);
        Assert.IsTrue(closed, "排队之后对话框要关掉，让用户看到下载页。");
    }

    [TestMethod]
    public async Task DownloadingAResourcePackUsesTheResourcePackKind()
    {
        var content = new FakeContentCatalog();
        var downloads = new FakeDownloadCatalog();
        var dialog = new ModrinthDownloadDialogViewModel(content, downloads, new FakeNavigationService());

        dialog.Open(ModrinthContentKind.ResourcePack, "1.20.4-Forge");
        await dialog.SearchCommand.ExecuteAsync(null);
        await dialog.DownloadCommand.ExecuteAsync(null);

        Assert.AreEqual(ModrinthContentKind.ResourcePack, downloads.ContentRequests.Single().Kind);
    }

    [TestMethod]
    public void ModsPageDisablesModrinthDownloadOnAVanillaInstance()
    {
        var content = new FakeContentCatalog();
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var instances = new SampleInstanceCatalog { ActiveInstanceName = "1.21.4-Vanilla" };
        var mods = new ModsViewModel(
            content,
            instances,
            new FakeShellService(),
            downloads,
            navigation,
            new ModrinthDownloadDialogViewModel(content, downloads, navigation));

        Assert.IsFalse(mods.CanOpenModrinth, "原版实例没有加载器，不能安装模组。");
        Assert.IsFalse(mods.OpenModrinthCommand.CanExecute(null));
        StringAssert.Contains(mods.OpenModrinthDetail, "原版实例");

        mods.OpenModrinthCommand.Execute(null);

        Assert.IsFalse(mods.HasDialog, "按钮不可执行时不应打开下载对话框。");
    }

    [TestMethod]
    public void TheModsAndResourcesPagesOpenTheDialogWithTheirOwnKind()
    {
        var content = new FakeContentCatalog();
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();

        var mods = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            downloads,
            navigation,
            new ModrinthDownloadDialogViewModel(content, downloads, navigation));

        mods.OpenModrinthCommand.Execute(null);
        Assert.IsTrue(mods.HasDialog);
        Assert.AreEqual(ModrinthContentKind.Mod, content.ModrinthContentSearches.Single().Kind);

        var resources = new ResourcesViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            downloads,
            navigation,
            new ModrinthDownloadDialogViewModel(content, downloads, navigation));

        resources.OpenModrinthCommand.Execute(null);
        Assert.IsTrue(resources.HasDialog);
        Assert.AreEqual(ModrinthContentKind.ResourcePack, content.ModrinthContentSearches.Last().Kind);
    }
}
