using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.App.ViewModels.Pages;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-b：p08「从 Modrinth 浏览」的接线与空状态。
/// </summary>
/// <remarks>
/// 这一页有两件事只有在这里才钉得住：
/// ① **刷新发生在页面被导航到时**（设计稿没有搜索框、也没有刷新按钮）；
/// ② 空状态那句话分两种 —— **还没取到过**与**取到了但 0 条**不是一回事。
/// 本类会切全进程的语言单例，所以必须与其它测试串行（项目默认方法级并行）。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ModpacksBrowseTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void ActivatingThePageRefreshesTheBrowseList()
    {
        var content = new FakeContentCatalog();
        var page = new ModpacksViewModel(content, new FakeDownloadCatalog(), new FakeDialogService(), new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());

        // 构造只读缓存，不联网（否则每次解析页面都会打一次 Modrinth）。
        Assert.AreEqual(0, content.ModrinthRefreshCount);

        page.OnActivated();

        Assert.AreEqual(1, content.ModrinthRefreshCount, "页面被导航到时刷新一次。");
        Assert.IsFalse(page.IsCatalogEmpty);
        Assert.IsTrue(page.HasCatalog);
        Assert.AreEqual(2, page.Catalog.Count);
    }

    [TestMethod]
    public void TheEmptyStateSaysWhetherModrinthWasEverFetched()
    {
        var never = new ModpacksViewModel(
            new FakeContentCatalog
            {
                ModrinthEmpty = true,
                HasModrinthCatalog = false,
            },
            new FakeDownloadCatalog(),
            new FakeDialogService(),
            new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());

        var fetched = new ModpacksViewModel(
            new FakeContentCatalog
            {
                ModrinthEmpty = true,
                HasModrinthCatalog = true,
            },
            new FakeDownloadCatalog(),
            new FakeDialogService(),
            new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());

        Assert.IsTrue(never.IsCatalogEmpty);
        Assert.IsFalse(never.HasCatalog);

        // 把"还没联网"说成"Modrinth 没有整合包"就是在编一条用户会信的错误结论。
        Assert.AreEqual("还没有取得 Modrinth 数据：联网后重新进入本页会自动获取。", never.CatalogEmptyText);
        Assert.AreEqual("Modrinth 没有返回任何整合包。", fetched.CatalogEmptyText);

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual(
            "Modrinth data has not been fetched yet: re-open this page while online to fetch it automatically.",
            never.CatalogEmptyText);
        Assert.AreEqual("Modrinth returned no modpacks.", fetched.CatalogEmptyText);
    }
}
