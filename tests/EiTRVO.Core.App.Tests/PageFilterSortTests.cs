using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// S9-c：p02 / p07 筛选行从"画出来的假控件"变成真的筛选、排序、刷新与来源过滤。
/// </summary>
/// <remarks>
/// 三条都是**界面层**的行为：它们不改内核的顺序与磁盘内容，只决定表格里看得见什么。
/// 因此断言落在「Visible 列表的形状」与「副标题如实报数」上，而不是内核状态。
/// </remarks>
[TestClass]
public sealed class PageFilterSortTests
{
    private static InstancesViewModel Instances() => TestViewModels.Instances(instances: new SampleInstanceCatalog());

    private static ModsViewModel Mods(FakeContentCatalog? content = null) =>
        new(
            content ?? new FakeContentCatalog(),
            new SampleInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

    [TestMethod]
    public void Instances_FilterMatchesInstanceNameAndRestoresWhenCleared()
    {
        var page = Instances();
        var total = page.Instances.Count;

        Assert.IsTrue(total >= 3, "演示目录至少要有三个实例，否则筛选断言没有意义。");

        page.FilterText = "fabric";

        Assert.AreEqual(1, page.Instances.Count);
        Assert.IsTrue(page.Instances[0].Name.Contains("Fabric", StringComparison.OrdinalIgnoreCase), "筛选词应当不区分大小写。");
        Assert.IsTrue(page.HasFilter);
        StringAssert.Contains(page.SectionSubtitle, "筛选出 1 个");

        page.FilterText = string.Empty;

        Assert.AreEqual(total, page.Instances.Count, "清空筛选词必须回到全量列表。");
        Assert.IsFalse(page.HasFilter);
    }

    [TestMethod]
    public void Instances_SortToggleReversesTheKernelOrder()
    {
        var page = Instances();
        var before = page.Instances.Select(static row => row.Name).ToArray();

        page.ToggleSortCommand.Execute(null);

        CollectionAssert.AreEqual(before.Reverse().ToArray(), page.Instances.Select(static row => row.Name).ToArray(), "排序方向应当整列反转。");
        StringAssert.Contains(page.SortHintText, "旧 → 新", "排序说明要如实写出方向。");

        page.ToggleSortCommand.Execute(null);

        CollectionAssert.AreEqual(before, page.Instances.Select(static row => row.Name).ToArray());
        StringAssert.Contains(page.SortHintText, "新 → 旧");
    }

    [TestMethod]
    public void Instances_SelectingARowDoesNotRescanTheCatalog()
    {
        var catalog = new FakeInstanceCatalog();
        catalog.CreateInstance(new InstanceCreationRequest("实例 A", "1.20.4", LoaderKind.Forge));
        catalog.CreateInstance(new InstanceCreationRequest("实例 B", "1.20.4", LoaderKind.Fabric));

        var page = TestViewModels.Instances(instances: catalog);
        var callsAfterConstruction = catalog.GetInstancesCalls;

        Assert.IsTrue(callsAfterConstruction > 0);
        Assert.AreEqual(2, page.Instances.Count);

        page.SelectedInstance = page.Instances[0];

        Assert.AreEqual(
            callsAfterConstruction,
            catalog.GetInstancesCalls,
            "点选实例行只能改 IsActive，不能同步重扫所有实例目录。");
        Assert.IsTrue(
            page.Instances.Any(row => string.Equals(row.Name, "实例 A", StringComparison.Ordinal) && row.IsActive),
            "首列的对勾要跟着选中行移动。");
    }

    [TestMethod]
    public void Instances_RefreshReevaluatesTheList()
    {
        var page = Instances();
        var notified = new List<string>();

        page.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);
        page.RefreshCommand.Execute(null);

        CollectionAssert.Contains(notified, string.Empty, "刷新要让整页绑定重新求值（空属性名通知）。");
    }

    [TestMethod]
    public void Mods_FilterMatchesNameAndExplainsAnEmptyResult()
    {
        var page = Mods();
        var total = page.Mods.Count;

        page.FilterText = "sodium";

        Assert.AreEqual(1, page.Mods.Count);
        Assert.AreEqual("Sodium", page.Mods[0].Name);
        Assert.IsTrue(page.HasActiveFilter);
        StringAssert.Contains(page.SectionSubtitle, "筛选出 1 个");

        page.FilterText = "zzz";

        Assert.AreEqual(0, page.Mods.Count);
        Assert.IsTrue(page.HasNoMods);
        Assert.IsFalse(page.HasMods);
        StringAssert.Contains(page.EmptyStateText, "没有匹配", "筛选没有结果与「该实例没有模组」是两件事，不能共用一句话。");

        page.FilterText = string.Empty;
        Assert.AreEqual(total, page.Mods.Count);
    }

    [TestMethod]
    public void Mods_SourceFilterHasThreeStatesAndModpackIsTreatedAsLocal()
    {
        var page = Mods();

        Assert.IsTrue(page.IsSourceAll);

        page.ShowModrinthSourcesCommand.Execute(null);

        Assert.IsTrue(page.IsSourceModrinth);
        Assert.IsTrue(page.Mods.All(static row => row.Source == ContentSource.Modrinth));
        Assert.AreEqual(4, page.Mods.Count);

        page.ShowLocalSourcesCommand.Execute(null);

        Assert.IsTrue(page.IsSourceLocal);
        Assert.IsTrue(page.Mods.All(static row => row.Source != ContentSource.Modrinth), "整合包导入与本地文件都属于「本地文件」这一个画得出来的分组。");
        Assert.AreEqual(2, page.Mods.Count);

        page.ShowAllSourcesCommand.Execute(null);

        Assert.IsTrue(page.IsSourceAll);
        Assert.AreEqual(6, page.Mods.Count);
    }

    [TestMethod]
    public void Mods_SortToggleOrdersByModName()
    {
        var page = Mods();
        var ascending = page.Mods.Select(static row => row.Name).ToArray();

        page.ToggleSortCommand.Execute(null);

        CollectionAssert.AreEqual(ascending.Reverse().ToArray(), page.Mods.Select(static row => row.Name).ToArray());
    }
}
