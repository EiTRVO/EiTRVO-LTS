using System.IO;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M1 / F-10 崩溃处理：写一条 Critical 事件（按 Q8 的形状带参数），
/// 并把日志路径明确告诉用户。
/// <para>
/// 这里用内存替身而不是真文件：真文件的追加与坏行容忍已经在
/// <c>DiagnosticsLogTests</c> 里用真实文件测过了；本组用例关心的是
/// "崩溃时到底记了什么、有没有把日志位置给用户"。
/// </para>
/// <para>
/// 本类有一条用例会切全进程的语言单例（M6-f 的"崩溃事件读得懂"），所以整个类串行。
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CrashHandlerTests
{
    private string _tempRoot = string.Empty;

    /// <summary>每个用例一个全新的数据根目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-crash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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

    private CrashHandler NewHandler(out RecordingDiagnosticsLog diagnostics)
    {
        diagnostics = new RecordingDiagnosticsLog();
        return new CrashHandler(diagnostics, new LauncherPaths(_tempRoot));
    }

    [TestMethod]
    public void ACrashIsWrittenAsACriticalEventWithTheExceptionDetails()
    {
        var handler = NewHandler(out var diagnostics);

        var report = handler.Capture(new InvalidOperationException("磁盘满了"), "DispatcherUnhandledException");

        var entry = diagnostics.Entries.Single();
        Assert.AreEqual(LauncherEventLevel.Critical, entry.Level);
        Assert.AreEqual(DiagnosticsSources.Launcher, entry.Source);
        Assert.AreEqual("启动器发生未处理的异常（{0} · {1}）：{2} 调用栈：{3}", entry.MessageKey, "键**带占位符** —— 少了它，参数会被静默丢掉。");

        // 参数顺序是对外契约：来源 → 异常类型 → 消息 → 调用栈。
        Assert.AreEqual("DispatcherUnhandledException", entry.Parameters[0]);
        StringAssert.Contains(entry.Parameters[1], nameof(InvalidOperationException));
        Assert.AreEqual("磁盘满了", entry.Parameters[2]);
        Assert.IsNotNull(report);
    }

    [TestMethod]
    public void TheUserIsToldWhereTheLogActuallyIs()
    {
        var handler = NewHandler(out _);

        var report = handler.Capture(new InvalidOperationException("boom"), "Test");

        Assert.AreEqual(
            Path.Combine(_tempRoot, "log", "events.jsonl"),
            report.LogFilePath);
        StringAssert.Contains(report.Detail, "boom");
        StringAssert.Contains(report.Detail, report.LogFilePath);
        Assert.AreEqual("启动器遇到未处理的错误", report.Title);
    }

    [TestMethod]
    public void CapturingRejectsBadArguments()
    {
        var handler = NewHandler(out _);

        Assert.ThrowsException<ArgumentNullException>(() => handler.Capture(null!, "Test"));
        Assert.ThrowsException<ArgumentException>(() => handler.Capture(new InvalidOperationException(), "  "));
    }

    [TestMethod]
    public void TheLogPathFollowsTheDataDirectory()
    {
        var custom = Path.Combine(_tempRoot, "custom-root");
        var handler = new CrashHandler(new RecordingDiagnosticsLog(), new LauncherPaths(custom));

        Assert.AreEqual(Path.Combine(custom, "log", "events.jsonl"), handler.LogFilePath);
    }

    /// <summary>
    /// M6-f：崩溃事件在事件表里是**读得懂的一句话**（键在两份资源里、参数把占位符填满）——
    /// p12「关于」指向的就是这份日志，用户会照着它自查或贴给别人。
    /// </summary>
    [TestMethod]
    public void TheCrashEventIsReadableInBothLanguages()
    {
        var handler = NewHandler(out var diagnostics);
        handler.Capture(new InvalidOperationException("磁盘满了"), "DispatcherUnhandledException");

        var entry = diagnostics.Entries.Single();

        try
        {
            foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
            {
                Loc.Service.SetLanguage(language);

                var sentence = Loc.T(entry.MessageKey, [.. entry.Parameters]);

                StringAssert.Contains(sentence, "磁盘满了", "异常消息要在句子里（它是参数）。");
                StringAssert.Contains(sentence, "DispatcherUnhandledException");
                Assert.IsFalse(
                    sentence.Contains('{', StringComparison.Ordinal),
                    "不能留下没填的占位符（那就是「读不懂」）：" + sentence);
            }
        }
        finally
        {
            Loc.Service.SetLanguage(LocalizationService.Chinese);
        }
    }
}
