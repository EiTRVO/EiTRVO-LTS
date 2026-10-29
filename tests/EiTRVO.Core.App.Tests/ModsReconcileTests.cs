using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-c：p07 的对账接线与"对账之后那几个格子真的变了"。
/// </summary>
/// <remarks>
/// 这里钉住的是**接线**（页面被导航到时对一次账）与**效果**（来源 / 状态 / 备注跟着变）；
/// 内核那边的哈希、缓存与请求形状由 <c>ModrinthModIndexTests</c> 与 <c>ContentCatalogTests</c> 负责。
/// </remarks>
[TestClass]
public sealed class ModsReconcileTests
{
    [TestMethod]
    public void ActivatingTheModsPageRunsTheReconciliationOnce()
    {
        var content = new FakeContentCatalog();
        var page = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        // 构造只读缓存，**不联网**（否则每次解析页面都会触发一次对账）。
        Assert.AreEqual(0, content.ModsRefreshCount);

        page.OnActivated();

        Assert.AreEqual(1, content.ModsRefreshCount, "页面被导航到时对一次账。");
    }

    [TestMethod]
    public void TheRowsFollowTheReconciliation()
    {
        var content = new FakeContentCatalog { ReconcileOnRefresh = true };
        var page = new ModsViewModel(
            content,
            new SampleInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        // 对账之前：没有来源信息的那一条显示的是"别的东西"（这里是冲突备注与"可更新"）。
        Assert.AreEqual("与 Sodium 冲突", page.Mods.Single(static mod => mod.Name == "OptiFine（作为模组）").Note);
        Assert.AreEqual(
            ModState.Updatable,
            page.Mods.Single(static mod => mod.Name == "未收录模组（3 个）").State);

        page.OnActivated();

        // 对账之后：查不到来源的那两条如实写「不在 Modrinth 收录」，而"可更新"只留给真有更新的行。
        Assert.AreEqual("不在 Modrinth 收录", page.Mods.Single(static mod => mod.Name == "OptiFine（作为模组）").Note);

        var unknown = page.Mods.Single(static mod => mod.Name == "未收录模组（3 个）");

        Assert.AreEqual(ModState.Normal, unknown.State);
        Assert.AreEqual(ModEnablement.Unverified, unknown.Enablement);
        Assert.AreEqual("不在 Modrinth 收录", unknown.Note);
    }

    [TestMethod]
    public void WithoutAnInstanceTheReconciliationIsSkipped()
    {
        var content = new FakeContentCatalog();
        var instances = new SampleInstanceCatalog { ActiveInstanceName = string.Empty };
        var page = new ModsViewModel(
            content,
            instances,
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        page.OnActivated();

        // 零实例是首次运行的正常状态：不该对着空实例名去查一次（那会白读一遍目录）。
        Assert.AreEqual(0, content.ModsRefreshCount);
        Assert.IsFalse(page.HasInstance);
        Assert.IsTrue(page.HasNoMods);
    }
}
