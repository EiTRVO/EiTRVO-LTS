using System.IO;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// S9-f：p09 的「删除所选」（存档）真的把整目录移进 <c>backups\saves\</c>，而不是跳到别的页。
/// </summary>
/// <remarks>
/// 这一层钉三件事：**传下去的是目录名不是显示名**、**失败要说原因且不清掉选中行**、
/// **按钮在没有选中存档时是灰的**。真文件上的移动由内核
/// <c>ContentCatalogTests.DeletingASaveMovesTheWholeDirectoryIntoBackupsSaves</c> 验证。
/// 本类会切全进程的语言单例，因此与其它测试串行。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ResourcesSaveDeleteTests
{
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void DeletingTheSelectedSaveSendsTheFolderNameAndClearsTheSelection()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);

        page.SelectedSave = page.Saves.Single(static save => save.Name == "新世界");
        Assert.IsTrue(page.DeleteSelectedSaveCommand.CanExecute(null));

        page.DeleteSelectedSaveCommand.Execute(null);

        // 显示名是「新世界」、磁盘目录是「New World」—— 内核只认后者。
        var deleted = content.DeletedWorldSaves.Single();
        Assert.AreEqual("1.20.4-Forge", deleted.InstanceName);
        Assert.AreEqual("New World", deleted.Folder);
        Assert.IsNull(page.SelectedSave, "删掉的那一行不该还高亮着。");
        Assert.AreEqual(string.Empty, page.ActionError);
        StringAssert.Contains(page.ActionNote, "可以从那里拿回来");
    }

    [TestMethod]
    public void AFailedDeleteSaysWhyAndKeepsTheRowSelected()
    {
        var content = new FakeContentCatalog
        {
            WorldSaveDeleteResult = ContentActionResult.Failure(
                TextTemplate.Of("删除失败（{0}）：存档可能被游戏占用，请先退出游戏。", "New World")),
        };

        var page = NewPage(content);
        var selected = page.Saves.Single(static save => save.Name == "新世界");
        page.SelectedSave = selected;

        page.DeleteSelectedSaveCommand.Execute(null);

        Assert.IsFalse(string.IsNullOrWhiteSpace(page.ActionError), "失败必须说出原因（而不是看起来删掉了）。");
        Assert.AreEqual("新世界", page.SelectedSave?.Name, "失败时存档还在，选中行不该被清掉。");
    }

    [TestMethod]
    public void DeletingIsDisabledWithoutASelectedSave()
    {
        var page = NewPage(new FakeContentCatalog());

        page.SelectedSave = null;
        Assert.IsFalse(page.DeleteSelectedSaveCommand.CanExecute(null));

        page.SelectedSave = page.Saves.Count > 0 ? page.Saves[0] : null;
        Assert.AreEqual(page.Saves.Count > 0, page.DeleteSelectedSaveCommand.CanExecute(null));
    }

    [TestMethod]
    public void TheDeleteButtonIsWiredToTheCommandAndNoLongerNavigatesToTheDeletePage()
    {
        var text = File.ReadAllText(Path.Combine(
            PageFilterRowXamlTests.RepositoryRoot(),
            "src",
            "EiTRVO.Core.App",
            "Views",
            "Pages",
            "P09ResourcesView.xaml"));

        StringAssert.Contains(text, "Command=\"{Binding DeleteSelectedSaveCommand}\"");
        Assert.IsFalse(text.Contains("PageId.DeleteInstance", StringComparison.Ordinal), "存档删除只走本页动作，不许跳到删除实例页。");
    }

    private static ResourcesViewModel NewPage(FakeContentCatalog content) =>
        new(content, new FakeInstanceCatalog(), new FakeShellService(), new FakeDownloadCatalog(), new FakeNavigationService());
}
