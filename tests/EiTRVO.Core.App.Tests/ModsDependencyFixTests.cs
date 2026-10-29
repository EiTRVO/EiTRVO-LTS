namespace EiTRVO.Core.App.Tests;

using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Text;

/// <summary>
/// p07 的「修复依赖」（2026-10-21）：必须真的排进下载队列并跳到 p06，而不是只做一次导航。
/// </summary>
[TestClass]
public sealed class ModsDependencyFixTests
{
    [TestMethod]
    public async Task FixingDependenciesQueuesTheDownloadAndOpensTheQueuePage()
    {
        var content = new FakeContentCatalog
        {
            DependencyFixResult = new ModDependencyFixResult(
                Succeeded: true,
                Request: new ContentDownloadRequest(
                    "1.20.4-Forge",
                    [new ContentDownloadTarget("FLYWHEEL", "V1", "Flywheel", null)],
                    InstalledProjectIds: []),
                Failure: null),
        };

        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var page = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            downloads,
            navigation);

        await page.FixDependenciesCommand.ExecuteAsync(null);

        var request = downloads.ContentRequests.Single();
        Assert.AreEqual(ModrinthContentKind.Mod, request.Kind);
        Assert.AreEqual("FLYWHEEL", request.Targets.Single().ProjectId);
        CollectionAssert.Contains(navigation.Visited, PageId.Downloads);
        Assert.AreEqual(string.Empty, page.ActionError);
    }

    [TestMethod]
    public async Task AFailedDependencyFixShowsTheReasonAndQueuesNothing()
    {
        var content = new FakeContentCatalog
        {
            DependencyFixResult = new ModDependencyFixResult(
                Succeeded: false,
                Request: null,
                Failure: TextTemplate.Of("Modrinth 上找不到依赖 {0}，这次没有下载任何文件。", "flywheel")),
        };

        var downloads = new FakeDownloadCatalog();
        var page = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            downloads,
            new FakeNavigationService());

        await page.FixDependenciesCommand.ExecuteAsync(null);

        Assert.AreEqual(0, downloads.ContentRequests.Count);
        StringAssert.Contains(page.ActionError, "flywheel");
    }
}
