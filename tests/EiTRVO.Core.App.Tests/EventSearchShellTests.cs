using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.Tests;

/// <summary>外壳的 Ctrl+F 命令只应在含事件区块的页面打开搜索窗口。</summary>
[TestClass]
public sealed class EventSearchShellTests
{
    [TestMethod]
    public void CtrlFOnlyOpensSearchOnPagesWithEvents()
    {
        var eventPage = new SearchableTestPage(canSearch: true);
        var noEventPage = new SearchableTestPage(canSearch: false);
        var current = eventPage;
        var navigation = new FakeNavigationService(_ => current);
        var search = new FakeEventSearchWindowService();

        var shell = new ShellViewModel(
            navigation,
            new FakeAccountCatalog(),
            new FakeDownloadCatalog(),
            Loc.Service,
            new ImmediateRefreshThrottle(),
            new FakeHelpWindowService(),
            search);

        shell.OpenEventSearchCommand.Execute(null);
        Assert.AreEqual(1, search.ShownEvents.Count);
        Assert.AreEqual(1, search.ShownEvents[0].Count);

        current = noEventPage;
        shell.NavigateCommand.Execute(PageId.Downloads);
        shell.OpenEventSearchCommand.Execute(null);

        Assert.AreEqual(1, search.ShownEvents.Count, "没有事件区块的页面不应打开搜索窗口。");
    }

    private sealed class SearchableTestPage : PageViewModelBase
    {
        public SearchableTestPage(bool canSearch)
            : base(PageId.Dashboard, "仪表板")
        {
            CanSearch = canSearch;
        }

        private bool CanSearch { get; }

        public override bool CanSearchEvents => CanSearch;

        public override IReadOnlyList<EventEntry> SearchableEvents =>
            CanSearch
                ?
                [
                    new EventEntry(
                        "Test",
                        1,
                        EventLevel.Info,
                        EventSource.Launcher,
                        "启动器",
                        "测试事件",
                        "2026/10/26 12:00"),
                ]
                : [];

        public override NavKey NavKey => NavKey.Dashboard;

        public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => [];
    }
}
