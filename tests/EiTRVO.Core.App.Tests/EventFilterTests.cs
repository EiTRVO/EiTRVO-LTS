using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-c：事件表的「筛选器」与「全部级别」。
/// </summary>
/// <remarks>
/// 设计稿里 p03/p13 的事件区块有「筛选器 + 全部级别」、p06 只有「筛选器」（p01/p17 没有筛选行）。
/// 这里钉住三件事：筛选真的把行筛掉、筛掉时会**说出来**（否则用户以为日志只有这么多条）、
/// 以及切语言时级别选项跟着变（`Loc.T` 不能烙在字段里）。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EventFilterTests
{
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void TheKeywordFilterLooksAtTheMessageTheSourceAndTheInstanceName()
    {
        var page = NewDownloadsPage();

        Assert.IsTrue(page.FilteredEvents.Count > 1, "示例事件日志本来就有几条。");
        Assert.AreEqual(string.Empty, page.FilterHintText, "没筛选时不该显示『已筛出 …』。");

        // 消息里出现过的词。
        page.EventFilterText = "校验";
        var byMessage = page.FilteredEvents;
        Assert.IsTrue(byMessage.Count > 0 && byMessage.All(entry => entry.Message.Contains("校验", StringComparison.Ordinal)));
        Assert.IsTrue(page.FilterHintText.StartsWith("已筛出", StringComparison.Ordinal), "筛掉了东西就要说出来。");

        // 来源名（用户可能记得的是"哪来的"而不是"哪句话"）。
        page.EventFilterText = "Modrinth";
        Assert.IsTrue(page.FilteredEvents.All(entry => entry.SourceText.Contains("Modrinth", StringComparison.Ordinal)));

        // 大小写不敏感。
        page.EventFilterText = "modrinth";
        Assert.AreEqual(1, page.FilteredEvents.Count);

        // 实例名。
        page.EventFilterText = "1.20.1-Fabric";
        Assert.IsTrue(page.FilteredEvents.Count > 0);
        Assert.IsTrue(page.FilteredEvents.All(entry => entry.InstanceName == "1.20.1-Fabric"));

        // 什么都没命中：0 行，但**不是**假装日志是空的（提示里带着 0/N）。
        page.EventFilterText = "绝不存在的关键字";
        Assert.AreEqual(0, page.FilteredEvents.Count);
        Assert.AreEqual("已筛出 0 / " + page.Events.Count + " 条", page.FilterHintText);
    }

    [TestMethod]
    public void TheLevelFilterKeepsOnlyThatLevel()
    {
        var page = NewDownloadsPage();
        var total = page.Events.Count;

        page.EventLevelChoice = page.EventLevelChoices.Single(option => option.Value == EventLevel.Warning);

        Assert.IsTrue(page.FilteredEvents.Count > 0);
        Assert.IsTrue(page.FilteredEvents.All(entry => entry.Level == EventLevel.Warning));
        Assert.IsTrue(page.FilteredEvents.Count < total);

        // 换回「全部级别」：一行不少。
        page.EventLevelChoice = page.EventLevelChoices[0];
        Assert.AreEqual(total, page.FilteredEvents.Count);
        Assert.AreEqual(string.Empty, page.FilterHintText);
    }

    [TestMethod]
    public void TheInstancePagesFilterTheirOwnRows()
    {
        // p03/p13 的事件行来自"这个实例的"，筛选在界面层再叠一层。
        var instances = new SampleInstanceCatalog();
        var page = new InstanceDetailViewModel(instances, new SampleEventLog(), new FakeShellService());

        page.EventLevelChoice = page.EventLevelChoices.Single(option => option.Value == EventLevel.Error);

        Assert.IsTrue(page.FilteredEvents.All(entry => entry.Level == EventLevel.Error));
        Assert.IsTrue(page.FilteredEvents.Count <= page.Events.Count);
    }

    [TestMethod]
    public void SwitchingLanguageRebuildsTheLevelOptionsAndKeepsTheSelection()
    {
        var page = NewDownloadsPage();

        page.EventLevelChoice = page.EventLevelChoices.Single(option => option.Value == EventLevel.Warning);
        Assert.AreEqual("警告", page.EventLevelChoice.Text);

        Loc.Service.SetLanguage(LocalizationService.English);
        page.OnLanguageChanged();

        Assert.AreEqual("Warn", page.EventLevelChoice.Text, "切语言后选项要重建，否则下拉框留在旧语言。");
        Assert.AreEqual(EventLevel.Warning, page.EventLevelChoice.Value, "选择要按值保留。");
        Assert.AreEqual("Info", page.EventLevelChoices[1].Text);
        Assert.AreEqual("All levels", page.EventLevelChoices[0].Text);
    }

    [TestMethod]
    public void TheSortToggleReversesTheEventOrderAndKeepsTheFilter()
    {
        var page = NewDownloadsPage();
        var newestFirst = page.FilteredEvents.Select(static entry => entry.Id).ToArray();

        page.ToggleEventSortCommand.Execute(null);

        CollectionAssert.AreEqual(newestFirst.Reverse().ToArray(), page.FilteredEvents.Select(static entry => entry.Id).ToArray(), "排序方向应当整列反转。");
        Assert.IsTrue(page.IsEventSortAscending);

        // 过滤与排序是两件独立的事：筛完再反序，剩的仍然是同一批行。
        page.EventFilterText = "Modrinth";
        var filtered = page.FilteredEvents.Select(static entry => entry.Id).ToArray();
        CollectionAssert.AreEqual(filtered.Reverse().ToArray(), page.FilteredEvents.Select(static entry => entry.Id).ToArray());

        page.EventFilterText = string.Empty;
        page.ToggleEventSortCommand.Execute(null);
        CollectionAssert.AreEqual(newestFirst, page.FilteredEvents.Select(static entry => entry.Id).ToArray(), "再切一次要回到内核默认顺序。");
    }

    [TestMethod]
    public void TheRefreshButtonReevaluatesTheWholeEventList()
    {
        var page = NewDownloadsPage();
        var notified = new List<string>();

        page.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);
        page.RefreshEventsCommand.Execute(null);

        CollectionAssert.Contains(notified, string.Empty, "「立即重读事件」要让整页绑定重新求值（事件行是现算的）。");
    }
    /// <summary>
    /// 事件表的「级别」列是设计稿的 64px，里面还有 12px 的图标与左右内边距 ——
    /// 文本实际只有约 36px（12px 的 Segoe UI 约 6 个字符）。曾经是 `Information` / `Success`，
    /// 真机上被截成 `Inforn` / `Succe`，所以这里按"≤ 5 个字符"卡住（`Info`/`OK`/`Warn`/`Error`）。
    /// 真机核对仍以截图为准（这条只是机械下限）。
    /// </summary>
    [TestMethod]
    public void TheEnglishLevelLabelsFitTheSixtyFourPixelColumn()
    {
        Loc.Service.SetLanguage(LocalizationService.English);

        foreach (var key in new[] { "信息", "成功", "警告", "错误" })
        {
            var text = Loc.T(key);

            Assert.IsTrue(
                text.Length <= 5,
                $"「{key}」的英文是「{text}」（{text.Length} 个字符）—— 级别列只有 64px（扣掉图标与内边距约 36px），装不下。");
        }
    }

    private static DownloadsViewModel NewDownloadsPage() =>
        new(new FakeDownloadCatalog(), new SampleEventLog(), new ImmediateRefreshThrottle());
}
