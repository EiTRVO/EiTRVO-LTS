using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// S9-e：p08 的搜索框与游戏版本 / 加载器两个下拉真的把条件交给 Modrinth。
/// </summary>
/// <remarks>
/// 这一层钉的是**接线**：查询对象里有什么、失败时界面怎么处理、空状态那句话属于哪一种。
/// 真正的 facets / query 拼装与 URL 形状在 `ModrinthClient` 的测试里（内核）。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ModpacksSearchTests
{
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public async Task SearchingPassesTheQueryAndBothFiltersToTheContentCatalog()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);

        page.SearchText = "Prominence";
        page.SelectedGameVersion = "1.20.4";
        page.SelectedLoader = "forge";

        await page.SearchCommand.ExecuteAsync(null);

        var query = content.ModrinthSearches.Single();
        Assert.AreEqual("Prominence", query.Query);
        Assert.AreEqual("1.20.4", query.GameVersion);
        Assert.AreEqual("forge", query.Loader);
        Assert.AreEqual(1, page.Catalog.Count, "替身按关键词过滤了演示数据（只剩 Prominence II RPG）。");
        Assert.AreEqual(string.Empty, page.CatalogError);
    }

    [TestMethod]
    public async Task TheSelectedSortIsPassedToTheQueryAndShownNextToTheButton()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);

        Assert.AreEqual("相关性", page.SortText);

        page.CycleSortCommand.Execute(null);
        Assert.AreEqual(ModrinthSort.Downloads, page.SelectedSort);
        Assert.AreEqual("下载量", page.SortText);

        await page.SearchCommand.ExecuteAsync(null);
        Assert.AreEqual(ModrinthSort.Downloads, content.ModrinthSearches[^1].Sort);
    }

    [TestMethod]
    public void CyclingTooltipCyclesAllSortsBackToRelevance()
    {
        var page = NewPage(new FakeContentCatalog());

        for (var i = 0; i < 5; i++)
        {
            page.CycleSortCommand.Execute(null);
        }

        Assert.AreEqual(ModrinthSort.Relevance, page.SelectedSort, "五档转一圈回到相关性。");
    }
    [TestMethod]
    public async Task AFailedSearchKeepsThePreviousResultsAndSaysWhy()
    {
        var content = new FakeContentCatalog { ModrinthSearchFails = true };
        var page = NewPage(content);
        var before = page.Catalog.Count;

        page.SearchText = "Prominence";
        await page.SearchCommand.ExecuteAsync(null);

        Assert.AreEqual(before, page.Catalog.Count, "请求失败时不能把上一次能看的结果清空。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(page.CatalogError), "失败必须说出来，不能说成『没有匹配』。");
    }

    [TestMethod]
    public async Task AFilteredSearchWithNoHitsUsesTheNoMatchMessage()
    {
        var page = NewPage(new FakeContentCatalog());

        page.SearchText = "zzz";
        await page.SearchCommand.ExecuteAsync(null);

        Assert.IsTrue(page.HasActiveFilter);
        Assert.IsTrue(page.IsCatalogEmpty);
        StringAssert.Contains(page.CatalogEmptyText, "没有匹配");
    }

    [TestMethod]
    public void InstallingFromModrinthQueuesTheDownloadAndOpensTheQueuePage()
    {
        var content = new FakeContentCatalog();
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var page = new ModpacksViewModel(content, downloads, new FakeDialogService(), navigation, new SampleVersionCatalog(), new FakeInstanceCatalog());

        page.SelectedGameVersion = "1.20.4";
        page.SelectedLoader = "forge";

        var entry = page.Catalog.Single(static row => row.Name.StartsWith("Better MC", StringComparison.Ordinal));
        page.InstallFromModrinthCommand.Execute(entry);

        // 2026-10-21：下载 .mrpack 也进队列（不再在界面里先下完）。
        var (request, title) = downloads.ModrinthModpackRequests.Single();
        Assert.AreEqual("better-mc", request.ProjectId);
        Assert.AreEqual("1.20.4", request.GameVersion);
        Assert.AreEqual("forge", request.Loader);
        StringAssert.Contains(title, "Better MC");

        Assert.AreEqual(0, downloads.MrpackRequests.Count, "队列任务自己先下载，不再提前生成导入请求。");
        CollectionAssert.Contains(navigation.Visited, PageId.Downloads);
        Assert.AreEqual(string.Empty, page.CatalogError);
    }

    [TestMethod]
    public void ARowWithoutAProjectIdCannotBeInstalled()
    {
        var content = new FakeContentCatalog();
        var downloads = new FakeDownloadCatalog();
        var page = new ModpacksViewModel(content, downloads, new FakeDialogService(), new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());

        page.InstallFromModrinthCommand.Execute(new ModrinthEntry("旧缓存", "没有 id", "1", "1"));

        Assert.AreEqual(0, downloads.ModrinthModpackRequests.Count, "没有项目 id 就不该发请求。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(page.CatalogError));
        Assert.IsFalse(new ModrinthEntry("旧缓存", "没有 id", "1", "1").CanInstall);
    }
    [TestMethod]
    public void TheExportButtonNamesTheCurrentInstance()
    {
        var page = new ModpacksViewModel(
            new FakeContentCatalog(),
            new FakeDownloadCatalog(),
            new FakeDialogService(),
            new FakeNavigationService(),
            new SampleVersionCatalog(),
            new FakeInstanceCatalog());

        Assert.IsTrue(page.CanExport);
        StringAssert.Contains(page.ExportToolTip, "1.20.4-Forge");
    }

    [TestMethod]
    public void WithoutACurrentInstanceTheExportButtonIsDisabledAndExplainsWhy()
    {
        var instances = new FakeInstanceCatalog { ActiveInstanceName = string.Empty };
        var dialogs = new FakeDialogService { SaveFileToPick = @"C:\out\pack.mrpack" };
        var page = new ModpacksViewModel(
            new FakeContentCatalog(),
            new FakeDownloadCatalog(),
            dialogs,
            new FakeNavigationService(),
            new SampleVersionCatalog(),
            instances);

        Assert.IsFalse(page.CanExport);
        Assert.AreEqual("没有当前实例，无法导出。", page.ExportToolTip);
        Assert.AreEqual(0, dialogs.SavePicked.Count);
    }
    [TestMethod]
    public async Task ExportingTheCurrentInstanceUsesTheChosenPathAndSaysWhereItWent()
    {
        var content = new FakeContentCatalog();
        var dialogs = new FakeDialogService { SaveFileToPick = @"C:\out\pack.mrpack" };
        var page = new ModpacksViewModel(content, new FakeDownloadCatalog(), dialogs, new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());

        await page.ExportInstanceCommand.ExecuteAsync(null);

        var exported = content.ExportedInstances.Single();
        Assert.AreEqual("1.20.4-Forge", exported.InstanceName);
        Assert.AreEqual(@"C:\out\pack.mrpack", exported.DestinationPath);
        StringAssert.Contains(page.ExportNote, "已导出到");
        Assert.AreEqual(string.Empty, page.CatalogError);
        StringAssert.Contains(dialogs.SavePicked.Single().DefaultName, ".mrpack");
    }

    [TestMethod]
    public async Task CancellingTheExportDialogDoesNothing()
    {
        var content = new FakeContentCatalog();
        var page = new ModpacksViewModel(content, new FakeDownloadCatalog(), new FakeDialogService(), new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());

        await page.ExportInstanceCommand.ExecuteAsync(null);

        Assert.AreEqual(0, content.ExportedInstances.Count);
        Assert.AreEqual(string.Empty, page.ExportNote);
    }

    [TestMethod]
    public async Task AFailedExportShowsTheReason()
    {
        var content = new FakeContentCatalog
        {
            ExportInstanceResult = ContentActionResult.Failure(TextTemplate.Of("导出失败（{0}）：目标文件被占用。", "pack.mrpack")),
        };

        var page = new ModpacksViewModel(
            content,
            new FakeDownloadCatalog(),
            new FakeDialogService { SaveFileToPick = @"C:\out\pack.mrpack" },
            new FakeNavigationService(),
            new SampleVersionCatalog(),
            new FakeInstanceCatalog());

        await page.ExportInstanceCommand.ExecuteAsync(null);

        StringAssert.Contains(page.CatalogError, "导出失败");
    }
    [TestMethod]
    public void TheGameVersionOptionsComeFromTheReleaseListOnly()
    {
        var versions = new FakeVersionCatalog(
        [
            Row("1.20.4", VersionKind.Release),
            Row("26.4-snapshot-1", VersionKind.Snapshot),
            Row("1.20.1", VersionKind.Release),
        ]);

        var page = NewPage(new FakeContentCatalog(), versions);

        Assert.IsTrue(page.HasGameVersionChoices);
        CollectionAssert.AreEqual(
            new[] { "1.20.4", "1.20.1" },
            page.GameVersionChoices.Skip(1).Select(static choice => choice.Value).ToArray(),
            "下拉只列正式版（快照不放进筛选）。");
        Assert.IsNull(page.GameVersionChoices[0].Value, "第一项是『任意版本』。");
    }

    [TestMethod]
    public void WithoutAVersionListTheGameVersionDropdownIsHonestlyDisabled()
    {
        var page = NewPage(new FakeContentCatalog(), new FakeVersionCatalog());

        Assert.IsFalse(page.HasGameVersionChoices, "没有清单时只剩下『任意版本』，下拉应当置灰而不是假装备选。");
        Assert.AreEqual(1, page.GameVersionChoices.Count);
    }

    [TestMethod]
    public void ReactivatingThePageResetsTheFiltersAndRefreshesTheDefaultList()
    {
        var content = new FakeContentCatalog();
        var page = NewPage(content);

        page.SearchText = "Prominence";
        page.SelectedLoader = "forge";
        page.OnActivated();

        Assert.AreEqual(string.Empty, page.SearchText);
        Assert.IsNull(page.SelectedLoader);
        Assert.AreEqual(1, content.ModrinthRefreshCount, "回到默认视图时刷新一次默认列表。");
    }

    private static ModpacksViewModel NewPage(FakeContentCatalog content, FakeVersionCatalog? versions = null) =>
        new(
            content,
            new FakeDownloadCatalog(),
            new FakeDialogService(),
            new FakeNavigationService(),
            (IVersionCatalog?)versions ?? new SampleVersionCatalog(), new FakeInstanceCatalog());

    private static MinecraftVersion Row(string id, VersionKind kind) =>
        new(id, kind, "2026/10/19", "Java 21", VersionLocalState.NotInstalled, TextTemplate.Of("未安装"), IsActive: false);
}
