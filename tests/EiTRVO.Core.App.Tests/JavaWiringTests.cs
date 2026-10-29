using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M3 在界面层的那一半：p10 / p04 / p17 怎么把**检测结果**投影成用户看到的那几行。
/// <para>
/// 内核已经测过"发现了哪些 Java"（<c>FileJavaRuntimeCatalogTests</c>）与
/// "哪个 Java 能用"（<c>JavaPreflightTests</c>）；这里测的是三者之间最容易出错的一环：
/// 界面有没有如实转述 —— 尤其是**不许在没检测过的时候说"缺少 Java"**，
/// 以及"当前不适用"（设计稿里 Java 8 那行的灰勾）必须跟着当前版本走。
/// </para>
/// </summary>
[TestClass]
public sealed class JavaWiringTests
{
    private static JavaInstallation Install(int major, string vendor = "Eclipse Temurin") => new(
        ExecutablePath: $@"C:\Java\jdk-{major}\bin\javaw.exe",
        HomeDirectory: $@"C:\Java\jdk-{major}",
        VersionText: major == 8 ? "1.8.0_421" : $"{major}.0.9",
        MajorVersion: major,
        Vendor: vendor,
        Bits: 64);

    private static FakeVersionCatalog VersionsOn(string activeId) => new(
    [
        new MinecraftVersion(
            activeId, VersionKind.Release, "2023/12/07", "Java 17",
            VersionLocalState.Installed, TextTemplate.Of("已安装"), IsActive: true),
    ]);

    // ------------------------------------------------------------ p10 运行时表 ---

    [TestMethod]
    public void JavaSettings_ProjectsUsableAndUnusableRowsAgainstTheCurrentVersion()
    {
        // 设计稿那张表的语义：25 / 17 是绿勾（能用），8 是灰勾（装了但当前不适用）。
        // 当前版本是 1.20.4（要求 Java 17），所以 8 必须显示成"不适用"而不是"可用"。
        var page = TestViewModels.JavaSettings(
            versions: VersionsOn("1.20.4"),
            java: new FakeJavaRuntimeCatalog([Install(25), Install(17), Install(8)]));

        Assert.AreEqual(3, page.Runtimes.Count);
        Assert.AreEqual(JavaRuntimeState.Disabled, page.Runtimes.Single(r => r.Version == "1.8.0_421").State);
        Assert.AreEqual(JavaRuntimeState.Ready, page.Runtimes.Single(r => r.Version == "25.0.9").State);
        Assert.AreEqual(JavaRuntimeState.Ready, page.Runtimes.Single(r => r.Version == "17.0.9").State);

        // 走的是同一套选择规则：主版本正好等于要求的那一个优先。
        Assert.AreEqual("17.0.9", page.SelectedRuntime!.Version);
        Assert.AreEqual("1.18–1.20.4", page.SelectedRuntime.SupportedRange);
    }

    [TestMethod]
    public void JavaSettings_AddsTheMissingRowTheDesignDraws()
    {
        // 设计稿第四行：版本号 = 所需主版本，路径 = 未检测到，「1.20.5+ 需要」用红色。
        var page = TestViewModels.JavaSettings(
            versions: VersionsOn("1.21.4"),
            java: new FakeJavaRuntimeCatalog([Install(8), Install(17)]));

        var missing = page.Runtimes.Single(static row => row.State == JavaRuntimeState.Missing);

        Assert.AreEqual("21", missing.Version);
        Assert.AreEqual("—", missing.Bits);
        Assert.AreEqual("—", missing.Vendor);
        Assert.AreEqual("未检测到", missing.Path);
        Assert.AreEqual("1.20.5+ 需要", missing.SupportedRange);
        Assert.AreEqual(3, page.Runtimes.Count, "已有的两条照常列出，缺的那一条补在后面。");
    }

    [TestMethod]
    public void JavaSettings_SaysNothingIsMissingWhileNothingWasDetected()
    {
        // 冷启动（还没有检测结果）：表里给一条"尚未检测"，**不能**说缺少 Java。
        var page = TestViewModels.JavaSettings(
            versions: VersionsOn("1.20.4"),
            java: new FakeJavaRuntimeCatalog([], hasDetectionResult: false));

        Assert.AreEqual(1, page.Runtimes.Count);
        Assert.AreEqual(JavaRuntimeState.Missing, page.Runtimes[0].State);
        Assert.AreEqual("尚未检测", page.Runtimes[0].Path);
        Assert.AreEqual("未检测", page.Runtimes[0].SupportedRange);
    }

    [TestMethod]
    public void JavaSettings_MemoryHintUsesTheMachinesOwnNumbers()
    {
        // 原文案把「推荐 8–16 GB · 安全区上限 16384 MB」写死了，等于对每台机器都说"你有 32 GB"。
        var big = TestViewModels.JavaSettings(systemInfo: new FakeSystemInfoProvider { TotalPhysicalMemoryMb = 32768 });
        var small = TestViewModels.JavaSettings(systemInfo: new FakeSystemInfoProvider { TotalPhysicalMemoryMb = 4096 });

        Assert.AreEqual("物理内存 32.0 GB · 推荐 8–16 GB · 安全区上限 16384 MB", big.MemoryHint);
        Assert.AreEqual("物理内存 4.0 GB · 推荐 1–2 GB · 安全区上限 2048 MB", small.MemoryHint);
    }

    [TestMethod]
    public void JavaSettings_OverrideOptionsCarryTheRuntimePathNotJustItsLabel()
    {
        // 设置里存的必须是**能用来启动的路径**：存一句 "17.0.9（Temurin）" 在启动时毫无用处。
        var page = TestViewModels.JavaSettings(java: new FakeJavaRuntimeCatalog([Install(17)]));

        var options = page.JavaOverrideOptions;

        Assert.AreEqual(2, options.Count);
        Assert.AreEqual(string.Empty, options[0].Value, "第一项 = 使用全局默认（自动匹配）。");
        Assert.AreEqual(@"C:\Java\jdk-17", options[1].Value);
        Assert.AreEqual("17.0.9（Eclipse Temurin）", options[1].Text);
    }

    [TestMethod]
    public void JavaSettings_KeepsAStaleOverrideVisibleInsteadOfSilentlyDroppingIt()
    {
        // 用户指定过的运行时被卸载之后：如实标成「已失效」，而不是当成"他从没设过"——
        // 后者会让打开这一页再保存就悄悄抹掉他的设置。
        var settings = new SampleSettingsCatalog();
        settings.SaveGlobal(settings.GetGlobal() with { JavaOverride = @"C:\Java\gone" });

        var page = TestViewModels.JavaSettings(settings: settings, java: new FakeJavaRuntimeCatalog([Install(17)]));

        Assert.AreEqual(@"C:\Java\gone", page.JavaOverride);
        Assert.IsTrue(page.JavaOverrideOptions.Any(option => option.Value == @"C:\Java\gone"));
        Assert.IsFalse(page.IsDirty, "显示一个已失效的旧值不等于「用户改了设置」。");
    }

    [TestMethod]
    public void JavaSettings_SavesTheGlobalDefaultAsNull()
    {
        var settings = new SampleSettingsCatalog();
        var page = TestViewModels.JavaSettings(settings: settings, java: new FakeJavaRuntimeCatalog([Install(17)]));

        page.JavaOverride = @"C:\Java\jdk-17";
        page.SaveCommand.Execute(null);
        Assert.AreEqual(@"C:\Java\jdk-17", settings.GetGlobal().JavaOverride);

        page.JavaOverride = string.Empty;
        page.SaveCommand.Execute(null);
        Assert.IsNull(settings.GetGlobal().JavaOverride, "「使用全局默认」写回设置时应当是 null（自动匹配）。");
    }

    [TestMethod]
    public void JavaSettings_FetchJavaEnqueuesTheRuntimeAndJumpsToTheDownloadsPage()
    {
        // §12.7：按钮不再是"只跳页"（M3 的残留）。它排一个真实任务并把用户带到 p06 ——
        // 一次运行时下载约 100 MB、几十秒到几分钟，停在本页只会让人以为"点了没反应"。
        var downloads = new FakeDownloadCatalog();
        var navigation = new FakeNavigationService();
        var versions = new FakeVersionCatalog { ActiveVersionId = "1.20.4" };

        var page = TestViewModels.JavaSettings(
            versions: versions,
            downloads: downloads,
            navigation: navigation);

        page.FetchJavaCommand.Execute(null);

        var request = downloads.RuntimeRequests.Single();
        Assert.AreEqual("1.20.4", request.GameVersion, "取哪一代由当前版本决定（它的 javaVersion 是关键依据）。");
        Assert.AreEqual(PageId.Downloads, navigation.Visited.Single());
    }

    [TestMethod]
    public void JavaSettings_FetchJavaWithoutAVersionAsksForTheNewestGeneration()
    {
        // 还没选过版本时：按钮上写的就是最新一代（Java 25），请求里如实留空让内核去挑。
        var downloads = new FakeDownloadCatalog();
        var versions = new FakeVersionCatalog { ActiveVersionId = string.Empty };

        var page = TestViewModels.JavaSettings(versions: versions, downloads: downloads);

        page.FetchJavaCommand.Execute(null);

        Assert.IsNull(downloads.RuntimeRequests.Single().GameVersion);
    }

    [TestMethod]
    public async Task JavaSettings_RefreshesOnActivationAndForcesOnRedetect()
    {
        var java = new FakeJavaRuntimeCatalog([Install(17)]);
        var page = TestViewModels.JavaSettings(java: java);

        page.OnActivated();
        await page.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual(2, java.RefreshCount, "进页面要按新鲜度刷新（OnActivated 一次 + 显式一次）。");
        Assert.AreEqual(0, java.RedetectCount);

        await page.RedetectCommand.ExecuteAsync(null);

        Assert.AreEqual(1, java.RedetectCount, "「重新检测 Java」必须走强制路径，否则会被新鲜度窗口挡掉。");
    }

    // -------------------------------------------------------- p17 所需 Java ---

    [TestMethod]
    public void InstanceSettings_RequiredJavaHintReportsWhatWasActuallyDetected()
    {
        var withJava17 = TestViewModels.InstanceSettings(java: new FakeJavaRuntimeCatalog([Install(17)]));
        var withoutJava = TestViewModels.InstanceSettings(java: new FakeJavaRuntimeCatalog([]));
        var notScanned = TestViewModels.InstanceSettings(java: new FakeJavaRuntimeCatalog([], hasDetectionResult: false));

        // 实例详情里的游戏版本是 1.20.4（要求 Java 17）。
        Assert.AreEqual("该版本需要 Java 17；已检测到 17.0.9", withJava17.RequiredJavaHint);
        Assert.AreEqual("该版本需要 Java 17；未检测到可用的运行时", withoutJava.RequiredJavaHint);
        Assert.AreEqual("该版本需要 Java 17；尚未检测", notScanned.RequiredJavaHint);
    }

    [TestMethod]
    public void InstanceSettings_AnUnusableInstanceOverrideIsSaidOutLoud()
    {
        var settings = new SampleSettingsCatalog();
        settings.SaveInstanceOverrides(new InstanceOverrides(
            InstanceName: "1.20.4-Forge",
            JavaOverride: @"C:\Java\gone",
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true));

        var page = TestViewModels.InstanceSettings(settings: settings, java: new FakeJavaRuntimeCatalog([Install(17)]));

        Assert.AreEqual("该版本需要 Java 17；指定的运行时不可用", page.RequiredJavaHint);
    }

    [TestMethod]
    public void InstanceSettings_AStaleGlobalDefaultIsReportedAsAFallbackNotAsAFailure()
    {
        var settings = new SampleSettingsCatalog();
        settings.SaveGlobal(settings.GetGlobal() with { JavaOverride = @"C:\Java\gone" });
        settings.SaveInstanceOverrides(new InstanceOverrides(
            InstanceName: "1.20.4-Forge",
            JavaOverride: null,
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true));

        var page = TestViewModels.InstanceSettings(settings: settings, java: new FakeJavaRuntimeCatalog([Install(17)]));

        // Q14：全局默认只是默认值 —— 失效时回落到自动匹配，并如实说明（不是报"指定的运行时不可用"）。
        StringAssert.Contains(page.RequiredJavaHint, "全局默认的 Java 不可用");
        StringAssert.Contains(page.RequiredJavaHint, "17.0.9");
    }

    [TestMethod]
    public void InstanceSettings_MemoryHintUsesTheRealTotal()
    {
        // 样本里 1.20.4-Forge 的逐实例覆盖是 6144 MB（设计稿那一行写的就是它），
        // 物理内存总量则必须来自真实探测 —— 原文案把它写死成 32.0 GB。
        var page = TestViewModels.InstanceSettings(systemInfo: new FakeSystemInfoProvider { TotalPhysicalMemoryMb = 16384 });

        Assert.AreEqual("物理内存 16.0 GB · 全局默认 4096 MB · 本实例覆盖为 6144 MB", page.MemoryHint);
    }

    [TestMethod]
    public void InstanceSettings_OverrideOptionsComeFromTheRealDetection()
    {
        var page = TestViewModels.InstanceSettings(java: new FakeJavaRuntimeCatalog([Install(21, "Oracle")]));

        var options = page.JavaOverrideOptions;

        Assert.AreEqual(2, options.Count);
        Assert.AreEqual(string.Empty, options[0].Value);
        Assert.AreEqual(@"C:\Java\jdk-21", options[1].Value);
        Assert.AreEqual("21.0.9（Oracle）", options[1].Text);
    }

    [TestMethod]
    public void ForceSelectedJavaTracksTheSelectedRowAndIsSaved()
    {
        var settings = new SampleSettingsCatalog();
        var page = TestViewModels.JavaSettings(
            settings: settings,
            java: new FakeJavaRuntimeCatalog([Install(17)]));

        Assert.IsFalse(page.ForceSelectedJava);
        page.SelectedRuntime = page.Runtimes[0];
        page.ForceSelectedJava = true;

        Assert.AreEqual(@"C:\Java\jdk-17", page.ForcedJavaHome);
        page.SaveCommand.Execute(null);

        Assert.IsTrue(settings.GetGlobal().ForceSelectedJava);
        Assert.AreEqual(@"C:\Java\jdk-17", settings.GetGlobal().ForcedJavaHome);
    }

    [TestMethod]
    public void JvmArgumentsAreEmptyByDefaultOnBothSettingsPages()
    {
        Assert.AreEqual(string.Empty, TestViewModels.JavaSettings().JvmArguments);
        Assert.AreEqual(string.Empty, TestViewModels.InstanceSettings().JvmArguments);
    }

    [TestMethod]
    public void JavaSettings_BrowseGameDirectoryUsesTheFolderPickerAndUpdatesTheField()
    {
        var dialogs = new FakeDialogService { FolderToPick = @"D:\Games\.minecraft" };
        var page = TestViewModels.JavaSettings(dialogs: dialogs);

        page.BrowseGameDirectoryCommand.Execute(null);

        Assert.AreEqual(@"D:\Games\.minecraft", page.GameDirectory);
        CollectionAssert.Contains(dialogs.FolderPicked, "选择游戏目录");
    }
}
