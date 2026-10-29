using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M1 诊断事件写入器：JSONL 追加、坏行容忍、轮转、时间戳可断言。
/// <para>
/// 这里同时验证 **Q8 的形状**（级别 + 来源 + 参数，没有拼好的句子）——
/// 这是事件的对外契约，越早钉住越便宜。
/// </para>
/// </summary>
[TestClass]
public sealed class DiagnosticsLogTests
{
    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录与固定时钟。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-logtests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
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

    [TestMethod]
    public void WriteStoresLevelSourceKeyAndParameters()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);

        log.Write(
            LauncherEventLevel.Warning,
            "settings",
            "设置文件损坏，已回退到默认设置",
            ["settings.json", "JsonException"],
            instanceName: null);

        var entries = log.Read(10).Recent;
        Assert.AreEqual(1, entries.Count);

        var entry = entries[0];
        Assert.AreEqual(LauncherEventLevel.Warning, entry.Level);
        Assert.AreEqual("settings", entry.Source);
        Assert.AreEqual("设置文件损坏，已回退到默认设置", entry.MessageKey);
        CollectionAssert.AreEqual(
            new[] { "settings.json", "JsonException" },
            (System.Collections.ICollection)entry.Parameters);
        Assert.IsNull(entry.InstanceName, "全局事件不应带实例名。");
    }

    [TestMethod]
    public void TheTimestampComesFromTheInjectedClock()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);

        log.Write(LauncherEventLevel.Information, "instances", "已创建实例：{0}（{1}）", ["1.20.4-Forge"]);

        Assert.AreEqual(_clock.Now, log.Read(1).Recent[0].Timestamp);
    }

    [TestMethod]
    public void MissingParametersBecomeAnEmptyListInsteadOfNull()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);

        log.Write(LauncherEventLevel.Information, "instances", "实例目录已扫描");

        Assert.AreEqual(0, log.Read(1).Recent[0].Parameters.Count);
    }

    [TestMethod]
    public void EventsAreAppendedOneJsonObjectPerLineWithoutBom()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);
        log.Write(LauncherEventLevel.Information, "settings", "第一件事");
        log.Write(LauncherEventLevel.Success, "instances", "第二件事", ["a"]);

        var bytes = File.ReadAllBytes(log.FilePath);
        Assert.AreEqual((byte)'{', bytes[0], "文件必须以 { 开头（不能有 BOM）。");

        var lines = File.ReadAllLines(log.FilePath, Encoding.UTF8);
        Assert.AreEqual(2, lines.Length);
        StringAssert.StartsWith(lines[0], "{\"timestamp\":");
        StringAssert.Contains(lines[1], "\"level\":\"Success\"", "枚举要写成字符串，便于人工读日志。");
    }

    [TestMethod]
    public void ATornLastLineIsSkippedInsteadOfFailingTheWholeRead()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);
        log.Write(LauncherEventLevel.Information, "settings", "完整的一行");

        // 模拟进程在写第二行时被杀：只有半截 JSON。
        File.AppendAllText(log.FilePath, "{\"timestamp\":\"2026-09-30T16:01:02+08:00\",\"level\":\"Err", Encoding.UTF8);

        var entries = log.Read(10).Recent;

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("完整的一行", entries[0].MessageKey);
    }

    [TestMethod]
    public void ReadingReturnsTheMostRecentEntriesInOrder()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);
        for (var i = 1; i <= 5; i++)
        {
            log.Write(LauncherEventLevel.Information, "queue", "第 {0} 条", [i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var entries = log.Read(2).Recent;

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("4", entries[0].Parameters[0], "5 条里最近的 2 条应当是第 4、5 条。");
        Assert.AreEqual("5", entries[1].Parameters[0]);
    }

    [TestMethod]
    public void ReadingANonPositiveCountReturnsNothing()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);
        log.Write(LauncherEventLevel.Information, "queue", "一条");

        Assert.AreEqual(0, log.Read(0).Recent.Count);
        Assert.AreEqual(0, log.Read(-1).Recent.Count);
    }

    [TestMethod]
    public void TheTotalCountIsIndependentOfHowManyRowsWereAskedFor()
    {
        // 表头要「共 N 个」、表格只要最近若干行：一次扫描同时给出两件事
        // （分成两个调用就会每刷一次界面扫两遍文件）。
        var log = new FileDiagnosticsLog(_paths, _clock);
        for (var i = 1; i <= 7; i++)
        {
            log.Write(LauncherEventLevel.Information, "queue", "第 {0} 条", [i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var snapshot = log.Read(2);

        Assert.AreEqual(7, snapshot.TotalCount);
        Assert.AreEqual(2, snapshot.Recent.Count);
        Assert.AreEqual("6", snapshot.Recent[0].Parameters[0]);

        // 只要总数时（≤ 0）也照样数得出来。
        Assert.AreEqual(7, log.Read(0).TotalCount);
        Assert.AreEqual(0, log.Read(0).Recent.Count);
    }

    [TestMethod]
    public void ATornLineIsNotCountedInTheTotal()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);
        log.Write(LauncherEventLevel.Information, "settings", "完整的一行");
        File.AppendAllText(log.FilePath, "{ 半截", Encoding.UTF8);

        Assert.AreEqual(1, log.Read(10).TotalCount, "坏行不算一条事件。");
        Assert.AreEqual(1, log.Read(0).TotalCount);
    }

    [TestMethod]
    public void ReadingWithoutAFileReturnsNothing()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);

        Assert.AreEqual(0, log.Read(10).Recent.Count);
    }

    [TestMethod]
    public void TheLogRotatesOneGenerationWhenItGrowsPastTheLimit()
    {
        // 阈值调到小于一行事件的字节数，让第二次写入前就触发轮转。
        var log = new FileDiagnosticsLog(_paths, _clock, maxBytes: 120);
        log.Write(LauncherEventLevel.Information, "settings", "旧的一条");
        log.Write(LauncherEventLevel.Information, "settings", "新的一条");

        var rotated = log.FilePath + FileDiagnosticsLog.RotatedSuffix;

        Assert.IsTrue(File.Exists(rotated), "上一代应当被保留成 .1。");
        StringAssert.Contains(File.ReadAllText(rotated, Encoding.UTF8), "旧的一条");
        Assert.AreEqual("新的一条", log.Read(1).Recent[0].MessageKey, "当前文件只留轮转之后写入的内容。");
    }

    [TestMethod]
    public void WriteRejectsAnEmptySourceOrKey()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);

        Assert.ThrowsException<ArgumentException>(
            () => log.Write(LauncherEventLevel.Information, "  ", "有键没来源"));
        Assert.ThrowsException<ArgumentException>(
            () => log.Write(LauncherEventLevel.Information, "settings", "  "));
    }

    [TestMethod]
    public void TheConstructorRejectsBadArguments()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new FileDiagnosticsLog(null!, _clock));
        Assert.ThrowsException<ArgumentNullException>(() => new FileDiagnosticsLog(_paths, null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FileDiagnosticsLog(_paths, _clock, 0));
    }

    /// <summary>
    /// M6-f：**每条写入通知一次**，而且是在**落盘之后** —— 订阅者收到通知立刻去读就能读到这一条。
    /// </summary>
    [TestMethod]
    public void EveryWriteNotifiesOnceAfterTheLineIsOnDisk()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);

        var notifications = 0;
        var entriesVisibleWhenNotified = -1;
        log.Changed += (_, _) =>
        {
            notifications++;
            entriesVisibleWhenNotified = log.Read(10).TotalCount;
        };

        log.Write(LauncherEventLevel.Information, "settings", "第一条");
        log.Write(LauncherEventLevel.Information, "settings", "第二条");

        Assert.AreEqual(2, notifications, "写几条就通知几次。");
        Assert.AreEqual(2, entriesVisibleWhenNotified, "通知必须发生在落盘之后（否则订阅者读到的是旧内容）。");
    }

    /// <summary>
    /// M6-f：**订阅者坏了不能连日志都写不进去** —— 日志是诊断的最后一道防线。
    /// </summary>
    [TestMethod]
    public void ABrokenSubscriberDoesNotBreakTheWrite()
    {
        var log = new FileDiagnosticsLog(_paths, _clock);

        var goodCalls = 0;
        log.Changed += (_, _) => throw new InvalidOperationException("订阅者自己崩了");
        log.Changed += (_, _) => goodCalls++;

        log.Write(LauncherEventLevel.Information, "settings", "第一条");
        log.Write(LauncherEventLevel.Information, "settings", "第二条");

        Assert.AreEqual(2, goodCalls, "坏的订阅者不该影响其它订阅者。");
        Assert.AreEqual(2, log.Read(10).TotalCount, "更不该影响日志落盘。");
    }
}
