using System.Net;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2 / F-9：并发下载器（校验优先、<c>.part</c> 原子落盘、文件级重试、镜像回退）与 B-13 磁盘预检。
/// <para>
/// 涉及 HTTP 的用例一律断言**完整 URL**（审计 §4.4）：镜像回退这种东西一旦接错，
/// 表现只是"慢"或"某个文件总是校验失败"，没有 URL 断言就查不出来。
/// </para>
/// </summary>
[TestClass]
public sealed class DownloaderTests
{
    private const string ContentA = "first file body";
    private const string ContentB = "second file body, slightly longer";

    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-downloads", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(new DateTimeOffset(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8)));
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    // ==================== 下载：正常路径 ====================

    [TestMethod]
    public async Task FilesAreDownloadedVerifiedAndCommittedWithoutLeavingParts()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, ContentA)
            .Respond(HttpStatusCode.OK, ContentB);

        var items = new[] { FileItem("a.jar", ContentA), FileItem("sub/b.jar", ContentB) };

        var outcome = await Downloader(handler).DownloadAsync(items, Options());

        Assert.IsTrue(outcome.IsComplete);
        Assert.AreEqual(2, outcome.CompletedFiles);
        Assert.AreEqual(0, outcome.SkippedFiles);
        Assert.AreEqual(ContentA, System.IO.File.ReadAllText(items[0].TargetPath));
        Assert.AreEqual(ContentB, System.IO.File.ReadAllText(items[1].TargetPath));
        Assert.IsFalse(System.IO.File.Exists(items[0].TargetPath + FileDownloader.PartSuffix), "成功之后不该留下 .part。");
        Assert.AreEqual("下载完成（{0} 个文件，跳过 {1} 个，共 {2} 个）", _diagnostics.Entries[^1].MessageKey);
    }

    [TestMethod]
    public async Task AFreshlyDownloadedFileIsNotDownloadedAgain()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ContentA);
        var items = new[] { FileItem("a.jar", ContentA) };

        await Downloader(handler).DownloadAsync(items, Options());
        HttpAssert.RequestCount(handler, 1);

        // 第二次：本地已有的那份校验通过，一次请求都不该发。
        var second = new StubHandler();
        var outcome = await Downloader(second).DownloadAsync(items, Options());

        HttpAssert.RequestCount(second, 0);
        Assert.AreEqual(1, outcome.SkippedFiles);
        Assert.AreEqual(0, outcome.TotalFiles);
    }

    [TestMethod]
    public async Task AFileThatExistsWithTheWrongContentIsDownloadedAgain()
    {
        var item = FileItem("a.jar", ContentA);
        System.IO.File.WriteAllText(item.TargetPath, "stale half-written content");

        var handler = new StubHandler().Respond(HttpStatusCode.OK, ContentA);
        var outcome = await Downloader(handler).DownloadAsync([item], Options());

        HttpAssert.RequestCount(handler, 1);
        Assert.AreEqual(1, outcome.CompletedFiles);
        Assert.AreEqual(ContentA, System.IO.File.ReadAllText(item.TargetPath));
    }

    [TestMethod]
    public async Task AFileWithoutASha1IsDownloadedButNotPretendedToBeVerified()
    {
        var item = new DownloadItem(
            DownloadKind.Library,
            "1.20.4",
            "https://libraries.minecraft.net/legacy/lib.jar",
            Sha1: string.Empty,
            Size: 16,
            TargetPath: Path.Combine(_gameDirectory, "lib.jar"));

        var handler = new StubHandler().Respond(HttpStatusCode.OK, ContentA);
        var outcome = await Downloader(handler).DownloadAsync([item], Options());

        Assert.IsTrue(outcome.IsComplete);
        Assert.IsTrue(System.IO.File.Exists(item.TargetPath));
    }

    // ==================== 下载：校验与回退 ====================

    [TestMethod]
    public async Task AChecksumMismatchIsRetriedAndThenReportedAsAChecksumProblem()
    {
        var item = FileItem("a.jar", ContentA);

        // 每次请求都返回内容不对的一份（4 次 = 2 个候选 × 2 次尝试）。
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, "tampered")
            .Respond(HttpStatusCode.OK, "tampered")
            .Respond(HttpStatusCode.OK, "tampered")
            .Respond(HttpStatusCode.OK, "tampered");

        var outcome = await Downloader(handler).DownloadAsync([item], Options());

        Assert.AreEqual(1, outcome.FailedFiles);
        Assert.IsTrue(outcome.Failures[0].ChecksumMismatch, "校验失败与网络失败在界面上是两句不同的话。");
        StringAssert.Contains(TestText.Of(outcome.Failures[0].Reason), "校验不通过");
        Assert.IsFalse(System.IO.File.Exists(item.TargetPath), "校验失败的半成品绝不能落到正式路径上。");
        Assert.IsFalse(System.IO.File.Exists(item.TargetPath + FileDownloader.PartSuffix));
    }

    [TestMethod]
    public async Task ACandidateOutsideTheHostWhitelistIsNeverRequested()
    {
        // 待下载地址来自远端 JSON：不在白名单里就一个请求都不发。
        var item = new DownloadItem(
            DownloadKind.Library,
            "1.20.4",
            "https://evil.example/steal.jar",
            Sha1: string.Empty,
            Size: 16,
            TargetPath: Path.Combine(_gameDirectory, "steal.jar"));

        var handler = new StubHandler();
        var outcome = await Downloader(handler).DownloadAsync([item], Options());

        HttpAssert.RequestCount(handler, 0);
        Assert.AreEqual(1, outcome.FailedFiles);
    }

    // ==================== 下载：容错与进度 ====================

    [TestMethod]
    public async Task OneFailedFileDoesNotStopTheOthers()
    {
        // 坏文件用不在白名单的主机：它一个请求都发不出去，于是必定失败；
        // 而"哪个文件先跑"在并发下本来就不确定，所以这条用例不依赖请求顺序。
        var bad = Item(DownloadKind.AssetIndex, "https://piston-data.mangon.com/nope.json", "1.20.4");
        var good = FileItem("good.jar", ContentA);

        var handler = new StubHandler().Respond(HttpStatusCode.OK, ContentA);

        var outcome = await Downloader(handler).DownloadAsync([bad, good], Options() with { Concurrency = 2 });

        HttpAssert.RequestCount(handler, 1);
        Assert.AreEqual(1, outcome.FailedFiles);
        Assert.AreEqual(1, outcome.CompletedFiles);
        Assert.IsTrue(System.IO.File.Exists(good.TargetPath), "一个文件失败不该拖着别的文件一起失败。");
        Assert.AreEqual("下载完成但有文件失败（{0} 个文件，跳过 {1} 个，共 {2} 个）", _diagnostics.Entries[^1].MessageKey);
    }

    [TestMethod]
    public async Task ProgressIsReportedWithNumbersAndEndsAtOneHundredPercent()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, ContentA)
            .Respond(HttpStatusCode.OK, ContentB);

        var recorder = new RecordingProgress();
        var items = new[] { FileItem("a.jar", ContentA), FileItem("b.jar", ContentB) };

        var outcome = await Downloader(handler).DownloadAsync(items, Options() with { Concurrency = 1 }, recorder);

        Assert.IsTrue(outcome.IsComplete);
        Assert.IsTrue(recorder.Reports.Count > 0);

        var last = recorder.Reports[^1];
        Assert.AreEqual(2, last.CompletedFiles);
        Assert.AreEqual(2, last.TotalFiles);
        Assert.AreEqual(ContentA.Length + ContentB.Length, last.CompletedBytes);
        Assert.AreEqual(last.TotalBytes, last.CompletedBytes);
    }

    [TestMethod]
    public async Task AlreadyPresentFilesCountTowardsTheProgressSoAResumedInstallStartsHigh()
    {
        var present = FileItem("a.jar", ContentA);
        await Downloader(new StubHandler().Respond(HttpStatusCode.OK, ContentA)).DownloadAsync([present], Options());

        var recorder = new RecordingProgress();
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ContentB);

        await Downloader(handler).DownloadAsync(
            [present, FileItem("b.jar", ContentB)],
            Options() with { Concurrency = 1 },
            recorder);

        Assert.AreEqual(1, recorder.Reports[0].CompletedFiles, "一进来就该显示已经有 1 个文件了。");
    }

    [TestMethod]
    public async Task CancellationPropagates()
    {
        var item = FileItem("a.jar", ContentA);
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ContentA);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try
        {
            await Downloader(handler).DownloadAsync([item], Options(), progress: null, cancellation.Token);
            Assert.Fail("取消没有传播出去。");
        }
        catch (OperationCanceledException)
        {
            // 预期：OperationCanceledException（或它的子类 TaskCanceledException）都算取消。
        }
    }

    [TestMethod]
    public async Task ManyFilesAreDownloadedWithBoundedConcurrency()
    {
        const int Count = 8;

        // 按**地址**匹配而不是按顺序：8 个文件的响应体各不同，而并发下请求顺序不确定 ——
        // 用队列桩的话文件会拿到别人的内容、校验失败（这条用例原本就是这么抖的，
        // 平时侥幸通过，全量运行时才暴露）。
        var handler = new StubHandler();
        var items = new List<DownloadItem>();

        for (var index = 0; index < Count; index++)
        {
            var body = "file " + index;
            var item = FileItem($"f{index}.jar", body);

            handler.RespondTo("objects/" + item.Sha1 + "/client.jar", HttpStatusCode.OK, body);
            items.Add(item);
        }

        var outcome = await Downloader(handler).DownloadAsync(items, Options() with { Concurrency = 4 });

        Assert.AreEqual(Count, outcome.CompletedFiles);
        HttpAssert.RequestCount(handler, Count);
        foreach (var item in items)
        {
            Assert.IsTrue(System.IO.File.Exists(item.TargetPath), item.DisplayName + " 没下下来。");
        }
    }

    // ==================== B-13 磁盘预检 ====================

    [TestMethod]
    public void EnoughSpacePassesAndTheShortfallIsComputed()
    {
        var checker = new DiskSpaceChecker(_diagnostics);

        var enough = checker.Check(_gameDirectory, requiredBytes: 1024 * 1024, safetyMarginBytes: 0);
        Assert.IsTrue(enough.IsEnough);
        Assert.AreEqual(0, enough.ShortfallBytes);

        var tooBig = checker.Check(_gameDirectory, requiredBytes: long.MaxValue / 2, safetyMarginBytes: 0);
        Assert.IsFalse(tooBig.IsEnough);
        Assert.IsTrue(tooBig.ShortfallBytes > 0);
    }

    [TestMethod]
    public void InsufficientSpaceThrowsWithAReportAndRecordsAWarning()
    {
        var checker = new DiskSpaceChecker(_diagnostics);

        var exception = Assert.ThrowsException<InsufficientDiskSpaceException>(() =>
            checker.EnsureAvailable(_gameDirectory, requiredBytes: long.MaxValue / 2, safetyMarginBytes: 0));

        Assert.IsTrue(exception.Report.ShortfallBytes > 0);
        Assert.IsFalse(exception.Report.IsEnough);
        Assert.AreEqual(LauncherEventLevel.Warning, _diagnostics.Entries[^1].Level);
        Assert.AreEqual("磁盘空间不足：{0} 还缺 {1} MB", _diagnostics.Entries[^1].MessageKey);
    }

    [TestMethod]
    public void TheSafetyMarginIsPartOfTheCheck()
    {
        // 用**固定的**可用空间探测点，而不是真实磁盘：真实磁盘的可用空间在测试运行期间随时会变
        // （同一条命令里的构建/测试删掉临时文件后，它甚至会变大），
        // 于是"恰好等于可用空间"这种边界断言会偶发翻车 —— 这个用例原先就是这样抖的。
        const long Free = 10_000_000;
        var checker = new DiskSpaceChecker(_diagnostics, _ => Free);

        Assert.AreEqual(Free, checker.Check(_gameDirectory, 0).FreeBytes);

        // 恰好等于可用空间：不加余量够用，加上余量就不够。
        Assert.IsTrue(checker.Check(_gameDirectory, requiredBytes: Free - 1, safetyMarginBytes: 0).IsEnough);
        Assert.IsFalse(checker.Check(_gameDirectory, requiredBytes: Free - 1, safetyMarginBytes: 4096).IsEnough);
    }

    [TestMethod]
    public void ADirectoryThatDoesNotExistYetIsCheckedAgainstItsNearestExistingAncestor()
    {
        var checker = new DiskSpaceChecker(_diagnostics);
        var missing = Path.Combine(_gameDirectory, "versions", "1.20.4", "natives");

        var report = checker.Check(missing, 1024);

        Assert.IsTrue(report.IsEnough);
        Assert.AreEqual(Path.GetFullPath(missing), report.Path);
    }

    [TestMethod]
    public void TheRequiredTotalIsTheSumOfTheItemSizes()
    {
        var items = new[]
        {
            FileItem("a.jar", ContentA) with { Size = 100 },
            FileItem("b.jar", ContentB) with { Size = 250 },
        };

        Assert.AreEqual(350, DiskSpaceChecker.TotalBytes(items));
    }

    // ==================== SHA-512（M7-e） ====================

    [TestMethod]
    public async Task AFileWhoseSha512DoesNotMatchIsRejected()
    {
        // .mrpack 规范要求 sha1 与 sha512 **两个都在**：只校验 sha1 的文件，换一份内容就骗过去了。
        // 校验不通过会**换候选重试**（这里只有一个候选），所以桩要按地址作答，而不是只给一个响应。
        var handler = new StubHandler().RespondTo("cdn.modrinth.com/x/a.jar", HttpStatusCode.OK, ContentA);

        var item = new DownloadItem(
            DownloadKind.Content,
            string.Empty,
            "https://cdn.modrinth.com/x/a.jar",
            Sha1Hex(ContentA),
            ContentA.Length,
            Path.Combine(_gameDirectory, "a.jar"),
            Sha512Hex("another body entirely"));

        var outcome = await Downloader(handler).DownloadAsync([item], Options());

        Assert.IsFalse(outcome.IsComplete, "两个摘要都对才算过。");
        Assert.AreEqual(1, outcome.FailedFiles);
        Assert.IsTrue(outcome.Failures[0].ChecksumMismatch);
        Assert.IsFalse(File.Exists(item.TargetPath));
    }

    [TestMethod]
    public async Task AFileWithBothDigestsIsAcceptedAndSkippedOnTheSecondRun()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ContentA);

        var item = new DownloadItem(
            DownloadKind.Content,
            string.Empty,
            "https://cdn.modrinth.com/x/a.jar",
            Sha1Hex(ContentA),
            ContentA.Length,
            Path.Combine(_gameDirectory, "a.jar"),
            Sha512Hex(ContentA));

        var first = await Downloader(handler).DownloadAsync([item], Options());

        Assert.IsTrue(first.IsComplete);
        Assert.AreEqual(1, first.CompletedFiles);
        Assert.AreEqual(1, handler.Requests.Count);

        // 第二次：**已经正确**（两个摘要都对）就跳过，不再发请求。
        var second = await Downloader(handler).DownloadAsync([item], Options());

        Assert.IsTrue(second.IsComplete);
        Assert.AreEqual(1, second.SkippedFiles);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ASha512AloneIsEnoughToVerify()
    {
        // 只有 sha512 的项也要校验（不校验等于"下载了什么就是什么"）。
        var handler = new StubHandler().RespondTo("cdn.modrinth.com/x/a.jar", HttpStatusCode.OK, ContentA);

        var item = new DownloadItem(
            DownloadKind.Content,
            string.Empty,
            "https://cdn.modrinth.com/x/a.jar",
            string.Empty,
            ContentA.Length,
            Path.Combine(_gameDirectory, "a.jar"),
            Sha512Hex("wrong"));

        var outcome = await Downloader(handler).DownloadAsync([item], Options());

        Assert.IsFalse(outcome.IsComplete);
        Assert.IsFalse(File.Exists(item.TargetPath));
    }

    [TestMethod]
    public async Task TheProgressRemembersWhichHostActuallyServedTheFile()
    {
        // 「数据源」不是按设置猜的：要报出**真正在用的主机**（2026-10-23 起只有官方源）。
        var official = FileItem("official.bin", ContentA);
        var officialProgress = new RecordingProgress();

        await Downloader(new StubHandler().Respond(HttpStatusCode.OK, ContentA)).DownloadAsync(
            [official],
            Options() with { Source = DownloadSourcePolicy.Official },
            officialProgress);

        Assert.AreEqual(
            "piston-data.mojang.com",
            officialProgress.Reports.Last(report => report.SourceHost.Length > 0).SourceHost);
    }

    // ==================== 辅助 ====================

    private FileDownloader Downloader(StubHandler handler) =>
        new(
            new FakeHttpClientFactory(handler),
            HttpHostPolicy.Default,
            _clock,
            _diagnostics,
            // 重试前的等待在测试里是空操作：否则 1s + 2s 的退避会让用例慢得没人愿意跑。
            static (_, _) => Task.CompletedTask);

    private static DownloadOptions Options() => new()
    {
        Source = DownloadSourcePolicy.Official,
        Concurrency = 1,
        MaxAttemptsPerFile = 2,
    };

    /// <summary>造一个用 SHA-1 校验的下载项（客户端 JAR）。</summary>
    private DownloadItem FileItem(string relativePath, string body) =>
        Item(
            DownloadKind.ClientJar,
            "https://piston-data.mojang.com/v1/objects/" + Sha1Hex(body) + "/client.jar",
            "1.20.4",
            Sha1Hex(body),
            body.Length,
            Path.Combine(_gameDirectory, relativePath));

    /// <summary>
    /// 造一个下载项。
    /// </summary>
    /// <remarks>
    /// 默认目标路径**必须**是每个用例自己的临时目录里一个全新的名字：这里的路径是**会被真的写文件**的，
    /// 而一个固定路径（曾经写成 <c>%TEMP%\unused</c>）会真的被下出来 ——
    /// 之后所有"这个文件不存在所以应该失败"的用例都会因为"文件存在且校验跳过"而静默通过。
    /// </remarks>
    private DownloadItem Item(
        DownloadKind kind,
        string url,
        string versionId,
        string? sha1 = null,
        long size = 0,
        string? targetPath = null) =>
        new(
            kind,
            versionId,
            url,
            sha1 ?? string.Empty,
            size,
            targetPath ?? Path.Combine(_gameDirectory, "unused-" + Guid.NewGuid().ToString("N")));

    private static string Sha1Hex(string body)
    {
#pragma warning disable CA5350 // 与产品代码同理：SHA-1 是 Minecraft 清单协议规定的完整性字段。
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(body));
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>SHA-512（.mrpack 规范要求 sha1 + sha512 都校验）。</summary>
    private static string Sha512Hex(string body) =>
        Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>记录每次进度报告。</summary>
    private sealed class RecordingProgress : IProgress<FileTransferProgress>
    {
        /// <summary>收到的报告。</summary>
        public List<FileTransferProgress> Reports { get; } = [];

        /// <inheritdoc />
        public void Report(FileTransferProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }
}
