using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-d：p07 的「更新所选」真的排队下载。
/// </summary>
/// <remarks>
/// 这一层钉的是**接线**（请求从哪里来、排给谁、排完跳哪里、什么时候按钮是灰的）；
/// 依赖递归、校验与替换的细节在 <c>ContentInstallerTests</c>（内核），
/// 请求内容由 <c>ContentCatalogTests</c>（真目录）负责。
/// </remarks>
[TestClass]
public sealed class ModsUpdateTests
{
    private static ContentDownloadRequest UpdateRequest() => new(
        "1.20.4-Forge",
        [new ContentDownloadTarget("SODIUM", "V2", "Sodium", @"E:\Minecraft\.minecraft\mods\sodium-0.5.8.jar")],
        ["SODIUM", "JEI"]);

    [TestMethod]
    public void UpdatingEnqueuesTheRealTargetsAndOpensTheQueuePage()
    {
        var content = new FakeContentCatalog { UpdateRequest = UpdateRequest() };
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var page = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            downloads,
            navigation);

        Assert.IsTrue(page.UpdateSelectedCommand.CanExecute(null), "有可更新的模组时按钮可以按。");

        page.UpdateSelectedCommand.Execute(null);

        Assert.AreEqual(1, downloads.ContentRequests.Count);
        var queued = downloads.ContentRequests[0];

        Assert.AreEqual("1.20.4-Forge", queued.InstanceName);
        Assert.AreEqual("V2", queued.Targets.Single().VersionId);
        Assert.AreEqual(
            @"E:\Minecraft\.minecraft\mods\sodium-0.5.8.jar",
            queued.Targets.Single().ReplacePath,
            "替换路径来自对账（本地那个 jar），界面不自己拼。");
        CollectionAssert.AreEqual(
            new[] { PageId.Downloads },
            navigation.Visited.ToArray(),
            "排完队要跳到「下载与队列」：进度、失败原因与旧的移去哪了都在那一页。");
    }

    [TestMethod]
    public void WithNothingUpdatableTheButtonIsDisabledAndNothingIsQueued()
    {
        // UpdateRequest 默认是 null = 对账之后一个可更新的都没有。
        var content = new FakeContentCatalog();
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var page = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            downloads,
            navigation);

        Assert.IsFalse(
            page.UpdateSelectedCommand.CanExecute(null),
            "没有可更新的模组时按钮是灰的，而不是按下去什么都不发生。");
        Assert.IsNull(content.CreateModUpdateRequest("1.20.4-Forge"));
        Assert.AreEqual(0, downloads.ContentRequests.Count);
        Assert.AreEqual(0, navigation.Visited.Count);
    }

    [TestMethod]
    public void WithoutAnInstanceTheButtonIsDisabled()
    {
        var content = new FakeContentCatalog { UpdateRequest = UpdateRequest() };
        var instances = new SampleInstanceCatalog { ActiveInstanceName = string.Empty };
        var page = new ModsViewModel(
            content,
            instances,
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        Assert.IsFalse(page.UpdateSelectedCommand.CanExecute(null));
    }

    [TestMethod]
    public void TheTooltipCountsTheRealUpdatableMods()
    {
        var page = new ModsViewModel(
            new FakeContentCatalog(),
            new SampleInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        // 设计稿按钮上的「（5）」是画稿写死的；真实的 5 个来自对账结果（演示数据里就是 5 个），
        // 所以提示里那个数字必须是真的（这里是 5），而按钮文案只说做什么。
        StringAssert.Contains(page.UpdateSelectedDetail, "5");
    }

    [TestMethod]
    public async Task RefreshingReevaluatesTheButtonSoANewLookupCanEnableIt()
    {
        var content = new FakeContentCatalog();
        var page = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        Assert.IsFalse(page.UpdateSelectedCommand.CanExecute(null));

        var notified = 0;
        page.UpdateSelectedCommand.CanExecuteChanged += (_, _) => notified++;

        // 模拟"对账之后才知道有 5 个可更新"。
        content.UpdateRequest = UpdateRequest();

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.IsTrue(
            notified > 0,
            "刷新之后必须通知一次可执行性变化 —— 命令的可执行性不会跟着「空属性名」通知走，"
            + "漏了这一句就是「刚查出 5 个可更新，按钮还是灰的」。");
        Assert.IsTrue(page.UpdateSelectedCommand.CanExecute(null));
    }
}
