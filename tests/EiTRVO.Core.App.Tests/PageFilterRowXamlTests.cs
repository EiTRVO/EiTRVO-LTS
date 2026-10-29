using System.IO;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// S9-c：p02 / p07 的筛选行必须真的绑到筛选、排序、刷新与来源过滤上。
/// </summary>
/// <remarks>
/// 死控件门禁只能证明"按钮有 Command"，证明不了「Command 绑的是对的那一个」：
/// 排序按钮绑到刷新、或来源筛选绑到一个永远不改状态的命令，界面上都看不出来。
/// 这两页的筛选行之前正是"画出来的装饰"，所以在这里把接线钉死。
/// </remarks>
[TestClass]
public sealed class PageFilterRowXamlTests
{
    [TestMethod]
    public void InstanceFilterRowIsWiredToFilterSortAndRefresh()
    {
        var text = File.ReadAllText(ViewPath("P02InstancesView.xaml"));
        var row = FilterRow(text);

        StringAssert.Contains(row, "Text=\"{Binding FilterText, UpdateSourceTrigger=PropertyChanged}\"", "筛选框要真的写回 FilterText。");
        StringAssert.Contains(row, "Text=\"{Binding SortHintText}\"", "右侧说明要如实显示排序方向。");
        StringAssert.Contains(row, "Icon=\"&#xE8CB;\"", "排序按钮要用 E8CB(Sort) —— E70B 是字形用错（Q13）。");
        StringAssert.Contains(row, "Command=\"{Binding ToggleSortCommand}\"");
        StringAssert.Contains(row, "Icon=\"&#xE72C;\"");
        StringAssert.Contains(row, "Command=\"{Binding RefreshCommand}\"");
        Assert.IsFalse(row.Contains("E70B", StringComparison.Ordinal), "p02 筛选行不应再有 E70B。");
    }

    [TestMethod]
    public void ModsFilterRowIsWiredToFilterSortRefreshAndSource()
    {
        var text = File.ReadAllText(ViewPath("P07ModsView.xaml"));
        var row = FilterRow(text);

        StringAssert.Contains(row, "Text=\"{Binding FilterText, UpdateSourceTrigger=PropertyChanged}\"");
        StringAssert.Contains(row, "Icon=\"&#xE8CB;\"");
        StringAssert.Contains(row, "Command=\"{Binding ToggleSortCommand}\"");
        StringAssert.Contains(row, "Icon=\"&#xE72C;\"");
        StringAssert.Contains(row, "Command=\"{Binding RefreshCommand}\"");
        StringAssert.Contains(row, "Command=\"{Binding ShowAllSourcesCommand}\"");
        StringAssert.Contains(row, "Command=\"{Binding ShowModrinthSourcesCommand}\"");
        StringAssert.Contains(row, "Command=\"{Binding ShowLocalSourcesCommand}\"");
        StringAssert.Contains(row, "Binding=\"{Binding IsSourceAll}\"");
        StringAssert.Contains(row, "Binding=\"{Binding IsSourceModrinth}\"");
        StringAssert.Contains(row, "Binding=\"{Binding IsSourceLocal}\"");
        Assert.IsFalse(row.Contains("E70B", StringComparison.Ordinal), "p07 筛选行不应再有 E70B。");
        StringAssert.Contains(text, "Text=\"{Binding EmptyStateText}\"", "筛选无结果与\"没有模组\"要用各自的话说清楚。");
    }

    [TestMethod]
    public void TheDashboardAndOverviewPanelEventRowsHaveSortAndRefresh()
    {
        // 2026-10-24：p06 的下载事件流整段被用户要求删除；p01 的筛选器与工具按实例详情补齐。
        AssertEventRow(Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", "P01DashboardView.xaml"));
        AssertEventRow(Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Parts", "InstanceOverviewPanel.xaml"));
    }

    private static void AssertEventRow(string path)
    {
        var text = File.ReadAllText(path);
        var row = FilterRow(text);

        StringAssert.Contains(row, "Icon=\"&#xE8CB;\"", "排序按钮要用 E8CB(Sort)，不是 Q13 指出的 E70B。");
        StringAssert.Contains(row, "Command=\"{Binding ToggleEventSortCommand}\"");
        StringAssert.Contains(row, "Icon=\"&#xE72C;\"");
        StringAssert.Contains(row, "Command=\"{Binding RefreshEventsCommand}\"");
        Assert.IsFalse(row.Contains("E70B", StringComparison.Ordinal), "事件筛选行不应再有 E70B。");
    }
    [TestMethod]
    public void ModpacksBrowseIsWiredToSearchFiltersAndAnHonestInstallState()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", "P08ModpacksView.xaml"));

        StringAssert.Contains(text, "Text=\"{Binding SearchText, UpdateSourceTrigger=PropertyChanged}\"", "搜索框要真的写回 SearchText。");
        StringAssert.Contains(text, "Command=\"{Binding SearchCommand}\"", "放大镜与回车要触发查询。");
        StringAssert.Contains(text, "Key=\"Enter\"");
        StringAssert.Contains(text, "ItemsSource=\"{Binding GameVersionChoices}\"");
        StringAssert.Contains(text, "SelectedValue=\"{Binding SelectedGameVersion, Mode=TwoWay}\"");
        StringAssert.Contains(text, "IsEnabled=\"{Binding HasGameVersionChoices}\"", "没有版本清单时下拉要如实置灰。");
        StringAssert.Contains(text, "ItemsSource=\"{Binding LoaderChoices}\"");
        StringAssert.Contains(text, "SelectedValue=\"{Binding SelectedLoader, Mode=TwoWay}\"");
        StringAssert.Contains(text, "Content=\"{loc:Text 安装}\"", "「安装」按钮还在。");
        StringAssert.Contains(text, "IsEnabled=\"{Binding CanInstall}\"", "旧缓存没有项目 id 的行必须如实置灰。");
        StringAssert.Contains(text, "InstallFromModrinthCommand", "「安装」必须接真实命令。");
        Assert.IsFalse(text.Contains("<ComboBoxItem Content=\"1.20.4\"", StringComparison.Ordinal), "p08 不应再有写死的版本下拉。");
        Assert.IsFalse(text.Contains("<ComboBoxItem Content=\"Forge\"", StringComparison.Ordinal), "p08 不应再有写死的加载器下拉。");
    }
    private static string FilterRow(string text)
    {
        var start = text.IndexOf("Style=\"{StaticResource FilterRow}\"", StringComparison.Ordinal);
        Assert.IsTrue(start > 0, "找不到 FilterRow —— 筛选行搬家了？");

        // 从筛选行开头截到表格之前：足够覆盖筛选框、两颗图标按钮与来源过滤。
        var end = text.IndexOf("<DataGrid", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start, "筛选行与表格之间找不到边界。");

        return text[start..end];
    }

    private static string ViewPath(string name) =>
        Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", name);

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根。");
    }
}
