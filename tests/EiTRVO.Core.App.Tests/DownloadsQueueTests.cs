using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M7-g：p06 队列的行内动作（取消 / 重试）与动作条那两颗按钮（取消全部 / 清空已完成）。
/// </summary>
/// <remarks>
/// 这一层要钉住的只有两件事：**行上显示的是不是它该显示的动作**，
/// 以及**点下去交给内核的是哪一个 id**（标题会重复，id 不会 —— 传错一个字符就是取消了别人的任务）。
/// 真正的取消语义（走到哪一步停下、回滚还是保留）由内核的 <c>DownloadCatalogTests</c> 在真队列上跑。
/// </remarks>
[TestClass]
public sealed class DownloadsQueueTests
{
    private static DownloadsViewModel NewPage(FakeDownloadCatalog downloads) =>
        new(downloads, new SampleEventLog(), new ImmediateRefreshThrottle());

    private static DownloadQueueItem Row(
        string id,
        DownloadQueueState state,
        string detail = "等待中",
        string title = "安装 1.20.4") =>
        new(id, TextTemplate.Of("{0}", title), TextTemplate.Of("{0}", detail), state, IsActive: state == DownloadQueueState.Running);

    private static FakeDownloadCatalog WithRows(params DownloadQueueItem[] rows)
    {
        var downloads = new FakeDownloadCatalog { Queue = rows };
        return downloads;
    }

    [TestMethod]
    public void EachRowOffersTheActionThatMakesSenseForItsState()
    {
        var page = NewPage(WithRows(
            Row("running1", DownloadQueueState.Running),
            Row("queued22", DownloadQueueState.Queued),
            Row("failed33", DownloadQueueState.Failed, detail: "校验没通过"),
            Row("cancel44", DownloadQueueState.Cancelled, detail: "已取消。"),
            Row("done5555", DownloadQueueState.Completed, detail: "已完成")));

        // 还没结束的两行：取消（不是"暂停" —— 我们没有断点续传，叫暂停会骗人）。
        Assert.AreEqual("取消", page.Queue[0].ActionText);
        Assert.AreEqual("取消", page.Queue[1].ActionText);
        Assert.IsTrue(page.Queue[0].CanAct);
        Assert.IsFalse(page.Queue[0].IsRetry);

        // 失败与已取消：重试。
        Assert.AreEqual("重试", page.Queue[2].ActionText);
        Assert.AreEqual("重试", page.Queue[3].ActionText);
        Assert.IsTrue(page.Queue[3].IsRetry);

        // 已完成：没有动作（那一格显示的是状态词，不是按钮）。
        Assert.IsFalse(page.Queue[4].CanAct);
        Assert.AreEqual(string.Empty, page.Queue[4].ActionText);
    }

    [TestMethod]
    public void ClickingCancelAndRetrySendsTheRowIdToTheKernel()
    {
        var downloads = WithRows(
            Row("running1", DownloadQueueState.Running),
            Row("failed33", DownloadQueueState.Failed));

        var page = NewPage(downloads);

        page.QueueActionCommand.Execute(page.Queue[0]);
        page.QueueActionCommand.Execute(page.Queue[1]);

        CollectionAssert.AreEqual(new[] { "running1" }, downloads.Cancelled);
        CollectionAssert.AreEqual(new[] { "failed33" }, downloads.Retried);

        // 传 null（比如绑定还没解析出来）不该崩，也不该误伤某一行。
        page.QueueActionCommand.Execute(null);
        Assert.AreEqual(1, downloads.Cancelled.Count);
        Assert.AreEqual(1, downloads.Retried.Count);
    }

    [TestMethod]
    public void CancelAllCancelsEveryUnfinishedRowAndNothingElse()
    {
        var downloads = WithRows(
            Row("running1", DownloadQueueState.Running),
            Row("queued22", DownloadQueueState.Queued),
            Row("failed33", DownloadQueueState.Failed),
            Row("done5555", DownloadQueueState.Completed));

        var page = NewPage(downloads);

        Assert.IsTrue(page.CancelAllCommand.CanExecute(null), "还有没结束的任务，按钮该亮着。");

        page.CancelAllCommand.Execute(null);

        // 失败的那一行是**历史**：它要的是"重试"，不该被"取消全部"顺手再取消一次。
        CollectionAssert.AreEquivalent(new[] { "running1", "queued22" }, downloads.Cancelled);
        Assert.AreEqual(0, downloads.Retried.Count);
    }

    [TestMethod]
    public void CancelAllIsGreyedOutWhenNothingIsLeftToCancel()
    {
        var downloads = WithRows(Row("done5555", DownloadQueueState.Completed));
        var page = NewPage(downloads);

        Assert.IsFalse(page.CancelAllCommand.CanExecute(null), "只剩历史行时按钮是灰的。");
        Assert.IsTrue(page.ClearFinishedCommand.CanExecute(null), "有历史行，「清空已完成」该亮着。");

        page.CancelAllCommand.Execute(null);
        Assert.AreEqual(0, downloads.Cancelled.Count, "灰按钮被执行也不该做事。");
    }

    [TestMethod]
    public void ClearFinishedAsksTheKernelToDropTheHistory()
    {
        var downloads = WithRows(Row("done5555", DownloadQueueState.Completed));
        var page = NewPage(downloads);

        page.ClearFinishedCommand.Execute(null);

        Assert.AreEqual(1, downloads.ClearFinishedCount);
    }

    [TestMethod]
    public void AnEmptyQueueHasNoActionsAtAll()
    {
        var downloads = new FakeDownloadCatalog();
        var page = NewPage(downloads);

        Assert.AreEqual(0, page.Queue.Count);
        Assert.IsFalse(page.CancelAllCommand.CanExecute(null));
        Assert.IsFalse(page.ClearFinishedCommand.CanExecute(null));
    }

    /// <summary>取消过的行在明细里**不加「失败：」前缀**（用户自己点的那一下不是故障）。</summary>
    [TestMethod]
    public void ACancelledRowIsNotShownAsAFailure()
    {
        var page = NewPage(WithRows(Row("cancel44", DownloadQueueState.Cancelled, detail: "已取消。")));

        Assert.AreEqual("已取消。", page.Queue[0].Detail);
        Assert.AreEqual(DownloadQueueState.Cancelled, page.Queue[0].State);
    }
}
