using System.IO;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 「在实例列表里换一行选中」这条链路。
/// <para>
/// 这条链路之前是断的：表格高亮行由演示数据里的 <c>IsActive</c> 决定，点击不会改任何东西，
/// 于是选中别的实例后，详情 / 设置 / 启动 / 模组 / 资源包仍然显示原来那个实例。
/// 这组测试就是钉住「选中 = 切换当前实例 = 所有带上下文的页面跟着走」。
/// </para>
/// </summary>
[TestClass]
public sealed class InstanceSelectionTests
{
    private static SampleInstanceCatalog NewCatalog() => new();

    [TestMethod]
    public void Instances_StartWithTheCurrentInstanceSelected()
    {
        var catalog = NewCatalog();
        var page = TestViewModels.Instances(instances: catalog);

        // 设计稿开局就把「当前实例」那一行画成高亮行 —— 靠预先选中复现。
        Assert.IsNotNull(page.SelectedInstance);
        Assert.AreEqual("1.20.4-Forge", page.SelectedInstance.Name);
        Assert.AreEqual(page.SelectedInstance, page.Instances.Single(static row => row.IsActive));
    }

    [TestMethod]
    public void Instances_SelectingAnotherRowSwitchesTheActiveInstance()
    {
        var catalog = NewCatalog();
        var page = TestViewModels.Instances(instances: catalog);

        var fabric = page.Instances.Single(static row => row.Name == "1.20.1-Fabric");
        page.SelectedInstance = fabric;

        Assert.AreEqual("1.20.1-Fabric", catalog.ActiveInstanceName);

        // 首列那枚「当前实例」对勾也要跟着挪到新选中的行上。
        Assert.AreEqual("1.20.1-Fabric", page.Instances.Single(static row => row.IsActive).Name);
    }

    [TestMethod]
    public void InstanceDetail_FollowsTheActiveInstanceAfterActivation()
    {
        var catalog = NewCatalog();
        var page = new InstanceDetailViewModel(catalog, new SampleEventLog(), new FakeShellService());

        Assert.AreEqual("1.20.4-Forge", page.Detail.Name);
        Assert.AreEqual("Forge 49.0.30", page.Detail.LoaderDisplay);

        catalog.ActiveInstanceName = "1.20.1-Fabric";
        page.OnActivated();

        Assert.AreEqual("1.20.1-Fabric", page.Detail.Name);
        Assert.AreEqual("Fabric 0.15.7", page.Detail.LoaderDisplay);
        Assert.AreEqual("用于 1.20.1-Fabric", page.SectionSubtitle);
        Assert.AreEqual("1.20.1-Fabric", page.Breadcrumb[^1].Text);
    }

    [TestMethod]
    public void Launching_And_Settings_And_ContentPages_AllFollowTheActiveInstance()
    {
        var catalog = NewCatalog();
        catalog.ActiveInstanceName = "1.21.1-NeoForge";

        var launching = TestViewModels.Launching(instances: catalog);
        var mods = new ModsViewModel(
            new FakeContentCatalog(),
            catalog,
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());
        var resources = new ResourcesViewModel(
            new FakeContentCatalog(),
            catalog,
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());
        var settings = TestViewModels.InstanceSettings(instances: catalog);
        var detail = new InstanceDetailViewModel(catalog, new SampleEventLog(), new FakeShellService());

        Assert.AreEqual("1.21.1-NeoForge", launching.Detail.Name);
        Assert.AreEqual("1.21.1-NeoForge", mods.SectionSubtitle.Split(' ')[1]);
        Assert.AreEqual("用于 1.21.1-NeoForge", resources.SectionSubtitle);
        Assert.AreEqual("1.21.1-NeoForge", settings.Detail.Name);
        Assert.AreEqual("1.21.1-NeoForge", detail.Detail.Name);
    }

    [TestMethod]
    public void Dashboard_QuickSwitchSwitchesTheInstanceAndOpensItsDetail()
    {
        var catalog = NewCatalog();
        var navigation = new FakeNavigationService();
        var page = new DashboardViewModel(catalog, new FakeSystemInfoProvider(), new SampleEventLog(), navigation, new FakeAccountCatalog(), new FakeJavaRuntimeCatalog());

        var fabric = page.Overview.Single(static item => item.Name == "1.20.1-Fabric");
        page.OpenInstanceCommand.Execute(fabric);

        Assert.AreEqual("1.20.1-Fabric", catalog.ActiveInstanceName);
        CollectionAssert.AreEqual(new[] { PageId.InstanceDetail }, navigation.Visited);
        Assert.AreEqual("1.20.1-Fabric", page.Detail.Name);
    }

    [TestMethod]
    public void Dashboard_OverviewMarksOnlyTheActiveInstance()
    {
        var page = new DashboardViewModel(
            NewCatalog(),
            new FakeSystemInfoProvider(),
            new SampleEventLog(),
            new FakeNavigationService(),
            new FakeAccountCatalog(),
            new FakeJavaRuntimeCatalog());

        Assert.AreEqual(1, page.Overview.Count(static item => item.IsActive));
        Assert.AreEqual("1.20.4-Forge", page.Overview.Single(static item => item.IsActive).Name);
    }

    [TestMethod]
    public void DeleteDialog_ShowsTheCurrentlySelectedInstance()
    {
        var catalog = NewCatalog();
        var dialog = new DeleteConfirmDialogViewModel(catalog, new FakeNavigationService());

        Assert.AreEqual("确定要删除实例「1.20.4-Forge」吗？", dialog.Question);

        catalog.ActiveInstanceName = "1.21.4-Vanilla";
        dialog.Refresh();

        Assert.AreEqual("确定要删除实例「1.21.4-Vanilla」吗？", dialog.Question);
        // 切到原版实例之后，模组数量必须跟着变 —— 之前它写死的是 Forge 那份数据的 87。
        Assert.AreEqual("该实例占用 1.2 GB，包含 0 个模组与 9 个存档。", dialog.Summary);
    }

    [TestMethod]
    public void Resources_And_JavaPages_PreselectTheRowTheDesignHighlights()
    {
        var resources = new ResourcesViewModel(
            new FakeContentCatalog(),
            NewCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        // 设计稿把这两张表的第一行画成高亮行。p10 的"第一行"是**当前版本会用的那一个**：
        // 当前版本 1.20.4 要求 Java 17，所以选中的必须是 17，而不是更高的 25
        // （M2 的样本数据里高亮的是 25.0.3，那只是"第一行"，与版本要求无关）。
        var java = TestViewModels.JavaSettings(
            versions: new FakeVersionCatalog(
            [
                new MinecraftVersion(
                    "1.20.4", VersionKind.Release, "2023/12/07", "Java 17",
                    VersionLocalState.Installed, TextTemplate.Of("已安装"), IsActive: true),
            ]),
            java: new FakeJavaRuntimeCatalog(
            [
                Install("25.0.3", 25, @"C:\Java\jdk-25"),
                Install("17.0.9", 17, @"C:\Java\jdk-17"),
                Install("1.8.0_421", 8, @"C:\Java\jre-1.8"),
            ]));

        Assert.IsNotNull(resources.SelectedSave);
        Assert.AreEqual("新世界", resources.SelectedSave.Name);
        Assert.IsNotNull(java.SelectedRuntime);
        Assert.AreEqual("17.0.9", java.SelectedRuntime.Version);

        // 同一批运行时里，Java 8 对 1.20.4 是"当前不适用"，不是"可用"。
        Assert.AreEqual(JavaRuntimeState.Disabled, java.Runtimes.Single(r => r.Version == "1.8.0_421").State);
        Assert.AreEqual(JavaRuntimeState.Ready, java.Runtimes.Single(r => r.Version == "17.0.9").State);
    }

    private static JavaInstallation Install(string version, int major, string home) =>
        new(
            ExecutablePath: Path.Combine(home, "bin", "javaw.exe"),
            HomeDirectory: home,
            VersionText: version,
            MajorVersion: major,
            Vendor: "Eclipse Temurin",
            Bits: 64);

    [TestMethod]
    public void InstanceCatalog_ActiveInstanceIsWritable()
    {
        var catalog = NewCatalog();
        catalog.ActiveInstanceName = "1.12.2-Forge";

        Assert.AreEqual("1.12.2-Forge", catalog.ActiveInstanceName);

        // 游戏版本是「键 + 参数」：真实的版本号是**数据**（纯数据键 `{0}`，值在参数里），
        // 未知时才是「未指定」那个键。
        var detail = catalog.GetDetail(catalog.ActiveInstanceName);

        Assert.AreEqual("{0}", detail.GameVersion.Key);
        CollectionAssert.AreEqual(new[] { "1.12.2" }, detail.GameVersion.Parameters.ToArray());
    }
}
