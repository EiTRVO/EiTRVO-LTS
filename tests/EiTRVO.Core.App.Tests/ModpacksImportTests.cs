using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-e-2：p08 的「从文件导入 .mrpack」真的接上了队列。
/// </summary>
/// <remarks>
/// 这一页的导入只有一个必须钉住的形状：**选了文件就入队并跳 p06，取消就什么都不发生**。
/// 界面这一层刻意不读清单、也不建实例（那些在队列里按顺序做），
/// 所以"传下去的到底是什么"就是这里唯一要断言的东西 —— 传错了路径，
/// 用户会看到一个"点了没反应"的按钮，而所有内核测试照样全绿。
/// 本类会切全进程的语言单例，因此与其它测试串行。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ModpacksImportTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void ChoosingAFileEnqueuesTheImportAndGoesToTheQueue()
    {
        var downloads = new FakeDownloadCatalog();
        var dialogs = new FakeDialogService { FileToPick = @"C:\packs\Fabulously Optimized.mrpack" };
        var navigation = new FakeNavigationService();

        var page = new ModpacksViewModel(new FakeContentCatalog(), downloads, dialogs, navigation, new SampleVersionCatalog(), new FakeInstanceCatalog());

        page.ImportMrpackCommand.Execute(null);

        // 传下去的必须是**用户选的那个路径**：队列要从它读清单。
        Assert.AreEqual(1, downloads.MrpackRequests.Count);
        Assert.AreEqual(@"C:\packs\Fabulously Optimized.mrpack", downloads.MrpackRequests[0].ArchivePath);

        // 过滤器必须真的限定在 .mrpack 上（否则用户会选到一个 zip 然后看到"这不是一个整合包"）。
        Assert.AreEqual(1, dialogs.Picked.Count);
        StringAssert.Contains(dialogs.Picked[0].Filter, ".mrpack");
        Assert.IsFalse(string.IsNullOrWhiteSpace(dialogs.Picked[0].Title));

        // 入队后跳 p06：导入是几百 MB 的下载，停在 p08 上用户只会觉得"点了没反应"。
        CollectionAssert.Contains(navigation.Visited, PageId.Downloads);
    }

    [TestMethod]
    public void CancellingTheFilePickerDoesNothingAtAll()
    {
        var downloads = new FakeDownloadCatalog();
        var dialogs = new FakeDialogService { FileToPick = null };
        var navigation = new FakeNavigationService();

        var page = new ModpacksViewModel(new FakeContentCatalog(), downloads, dialogs, navigation, new SampleVersionCatalog(), new FakeInstanceCatalog());

        page.ImportMrpackCommand.Execute(null);

        Assert.AreEqual(0, downloads.MrpackRequests.Count, "取消不该建任务。");
        Assert.AreEqual(0, navigation.Visited.Count, "取消不该跳页面。");
    }

    [TestMethod]
    public void ABlankPathIsTreatedAsACancellation()
    {
        // 文件选择框理论上不会给空串，但"空 = 取消"是这一层的判据；写成 null 才算取消的话，
        // 一个空串就会变成一个指向空路径的导入任务（然后队列报"读不到这个整合包"）。
        var downloads = new FakeDownloadCatalog();
        var dialogs = new FakeDialogService { FileToPick = "   " };

        var page = new ModpacksViewModel(new FakeContentCatalog(), downloads, dialogs, new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());

        page.ImportMrpackCommand.Execute(null);

        Assert.AreEqual(0, downloads.MrpackRequests.Count);
    }
}
