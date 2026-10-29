using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-f：p09 的「备份所选 / 备份全部存档 / 恢复所选」真的接上了队列。
/// </summary>
/// <remarks>
/// 这一层要钉住的只有三件事：**传下去的是哪一个存档**（目录名，不是显示名）、
/// **什么时候按钮是灰的**（没选行 / 没备份 / 没实例）、以及**入队后跳 p06**。
/// 备份与恢复真正的行为（压缩、复核、换目录）由内核的 <c>WorldBackupInstallerTests</c> 在真文件上跑。
/// 本类会切全进程的语言单例，因此与其它测试串行。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ResourcesBackupTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    private static ResourcesViewModel NewPage(
        FakeDownloadCatalog downloads,
        FakeNavigationService navigation,
        FakeInstanceCatalog? instances = null) =>
        new(new FakeContentCatalog(), instances ?? new FakeInstanceCatalog(), new FakeShellService(), downloads, navigation);

    [TestMethod]
    public void BackingUpTheSelectedSaveSendsTheFolderNameNotTheDisplayName()
    {
        // 界面显示的是「新世界」，磁盘上的目录是「New World」——备份文件名只认后者。
        // 这一条就是"ViewModel 拿显示名去拼路径"那类 bug 的拦网。
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var page = NewPage(downloads, navigation);

        page.SelectedSave = page.Saves.Single(static save => save.Name == "新世界");
        page.BackupSelectedCommand.Execute(null);

        Assert.AreEqual(1, downloads.WorldBackupRequests.Count);
        Assert.AreEqual("1.20.4-Forge", downloads.WorldBackupRequests[0].InstanceName);
        Assert.AreEqual("New World", downloads.WorldBackupRequests[0].Target.Folder);
        Assert.AreEqual("新世界", downloads.WorldBackupRequests[0].Target.Name);

        CollectionAssert.Contains(navigation.Visited, PageId.Downloads);
    }

    [TestMethod]
    public void BackingUpEverySaveQueuesOneTaskPerSave()
    {
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var page = NewPage(downloads, navigation);

        Assert.IsTrue(page.BackupAllCommand.CanExecute(null), "有存档时「备份全部存档」应当可按。");

        page.BackupAllCommand.Execute(null);

        // 一个存档一个任务：进度、失败原因与以后的取消都是按存档的粒度。
        Assert.AreEqual(page.Saves.Count, downloads.WorldBackupRequests.Count);
        CollectionAssert.AreEquivalent(
            page.Saves.Select(static save => save.Folder).ToArray(),
            downloads.WorldBackupRequests.Select(static request => request.Target.Folder).ToArray());

        CollectionAssert.Contains(navigation.Visited, PageId.Downloads);
    }

    [TestMethod]
    public void RestoringIsOnlyPossibleForASaveThatWasBackedUp()
    {
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var page = NewPage(downloads, navigation);

        // 没选行：两个按钮都不可按（而不是按下去什么都不发生）。
        page.SelectedSave = null;
        Assert.IsFalse(page.BackupSelectedCommand.CanExecute(null));
        Assert.IsFalse(page.RestoreSelectedCommand.CanExecute(null));

        // 选中一个**没有**备份的存档：能备份、不能恢复。
        page.SelectedSave = page.Saves.Single(static save => save.Name == "空岛生存");
        Assert.IsTrue(page.BackupSelectedCommand.CanExecute(null));
        Assert.IsFalse(page.RestoreSelectedCommand.CanExecute(null), "没备份就没什么可恢复的。");

        // 选中一个已备份的存档：恢复亮起来。
        page.SelectedSave = page.Saves.Single(static save => save.Name == "新世界");
        Assert.IsTrue(page.RestoreSelectedCommand.CanExecute(null), "选中行变了，可执行性必须跟着变（属性名通知不管它）。");

        page.RestoreSelectedCommand.Execute(null);

        Assert.AreEqual(1, downloads.WorldRestoreRequests.Count);
        Assert.AreEqual("New World", downloads.WorldRestoreRequests[0].Target.Folder);
        Assert.AreEqual(0, downloads.WorldBackupRequests.Count, "恢复不该顺手带一个备份任务。");
        CollectionAssert.Contains(navigation.Visited, PageId.Downloads);
    }

    [TestMethod]
    public void WithoutAnInstanceNothingIsQueued()
    {
        // 零实例（首次运行）：这一页照常能读出来，但三个命令都不可按。
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var instances = new FakeInstanceCatalog { ActiveInstanceName = string.Empty };
        var page = NewPage(downloads, navigation, instances);

        Assert.IsFalse(page.HasInstance);
        Assert.IsFalse(page.BackupAllCommand.CanExecute(null));

        page.SelectedSave = page.Saves[0];
        Assert.IsFalse(page.BackupSelectedCommand.CanExecute(null));

        // 即使有人绕过 CanExecute 直接执行，也不该排出一个没有实例的任务。
        page.BackupSelectedCommand.Execute(null);
        page.BackupAllCommand.Execute(null);
        page.RestoreSelectedCommand.Execute(null);

        Assert.AreEqual(0, downloads.WorldBackupRequests.Count);
        Assert.AreEqual(0, downloads.WorldRestoreRequests.Count);
        Assert.AreEqual(0, navigation.Visited.Count, "没做事就不该跳页面。");
    }
}
