using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.Tests;

/// <summary>Ctrl+F 事件搜索窗口的筛选逻辑。</summary>
[TestClass]
public sealed class EventSearchViewModelTests
{
    private static readonly EventEntry Info = new(
        "Alpha",
        101,
        EventLevel.Info,
        EventSource.Launcher,
        "启动器",
        "版本清单已更新",
        "2026/10/26 10:00");

    private static readonly EventEntry Error = new(
        "Beta",
        102,
        EventLevel.Error,
        EventSource.Downloader,
        "下载器",
        "下载失败",
        "2026/10/26 10:05");

    private static readonly EventEntry Success = new(
        "Alpha",
        103,
        EventLevel.Success,
        EventSource.Launcher,
        "启动器",
        "安装完成",
        "2026/10/26 10:10");

    [TestMethod]
    public void OpeningTheWindowShowsAllEvents()
    {
        var page = new EventSearchViewModel();

        page.Open([Info, Error, Success]);

        Assert.AreEqual("搜索事件", page.Title);
        Assert.AreEqual(3, page.Results.Count);
        StringAssert.Contains(page.ResultCountText, "3");
    }

    [TestMethod]
    public void SearchMatchesMessageSourceInstanceAndTime()
    {
        var page = new EventSearchViewModel();
        page.Open([Info, Error, Success]);

        page.SearchText = "下载";
        CollectionAssert.AreEqual(new[] { 102 }, page.Results.Select(static e => e.Id).ToArray());

        page.SearchText = "alpha";
        CollectionAssert.AreEqual(
            new[] { 101, 103 },
            page.Results.Select(static e => e.Id).ToArray());

        page.SearchText = "10:10";
        CollectionAssert.AreEqual(new[] { 103 }, page.Results.Select(static e => e.Id).ToArray());
    }

    [TestMethod]
    public void ClosingTheWindowRaisesCloseRequested()
    {
        var page = new EventSearchViewModel();
        var closed = false;
        page.CloseRequested += (_, _) => closed = true;

        page.CloseCommand.Execute(null);

        Assert.IsTrue(closed);
    }
}
