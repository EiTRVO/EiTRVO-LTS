using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content.Modpack;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2 / p06 的数据源：安装任务队列（入队 → 后台执行 → 进度与阶段快照）。
/// <para>
/// 用**最小版本 JSON**（只有客户端 JAR）来跑通整条链：这里要验证的是队列与快照，
/// 下载本身的行为已经在安装器那组用例里钉过了。
/// </para>
/// </summary>
[TestClass]
public sealed class DownloadCatalogTests
{
    private const string ClientJar = "client jar bytes";

    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FileSettingsCatalog _settings = null!;
    private FixedClock _clock = null!;
    private FileInstanceCatalog _instances = null!;

    /// <summary>最近一次 <see cref="NewCatalog"/> 造出来的工具替身（用例要断言"安装器是怎么被跑的"）。</summary>
    private FakeToolRunner _tools = null!;

    /// <summary>每个用例一套全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-queue", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();
        _settings = new FileSettingsCatalog(_paths, _diagnostics);
        _clock = new FixedClock(new DateTimeOffset(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8)));

        _settings.SaveGlobal(SettingsDefaults.CreateGlobal() with { GameDirectory = _gameDirectory });

        // M7-d：内容下载任务要按实例解析游戏目录（`IInstanceCatalog.GetLaunchInfo`）。
        _instances = new FileInstanceCatalog(_paths, _settings, _clock, _diagnostics);
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

    // ==================== 空队列 ====================

    [TestMethod]
    public void WithNothingQueuedThePageIsHonestlyEmpty()
    {
        var catalog = NewCatalog(new StubHandler());

        Assert.AreEqual(0, catalog.PendingTaskCount);
        Assert.AreEqual(0, catalog.GetQueue().Count);
        Assert.AreEqual("—", catalog.GetProgress().CurrentTask.Key);
        Assert.AreEqual(0, catalog.GetProgress().Percent);

    }

    // ==================== 成功路径 ====================

    [TestMethod]
    public async Task AQueuedInstallRunsInTheBackgroundAndFinishes()
    {
        WriteVersionJson();

        var handler = new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar);
        var catalog = NewCatalog(handler);

        var changes = 0;
        catalog.Changed += (_, _) => Interlocked.Increment(ref changes);

        var job = catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));

        // 入队是**立即返回**的：此刻任务还没跑完（甚至可能还没开始）。
        Assert.AreEqual(InstallJobState.Queued, job.State);
        Assert.AreEqual(1, catalog.PendingTaskCount);

        await catalog.WhenIdleAsync();

        Assert.IsTrue(changes > 0, "队列变更要广播出去，否则界面不会刷新。");
        Assert.AreEqual(0, catalog.PendingTaskCount);

        var finished = catalog.GetJobs().Single();
        Assert.AreEqual(InstallJobState.Completed, finished.State);
        Assert.IsTrue(finished.Result!.Succeeded);
        Assert.AreEqual(1, finished.Result.FilesDownloaded);
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, @"versions\1.20.4-Vanilla\1.20.4-Vanilla.jar")),"客户端 JAR 必须在实例目录里。");
        Assert.IsFalse(Directory.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4")),"共享版本目录不该再留下。");

        // 完成之后：**进度区清空**（用户 2026-10-06 的要求：下载完成就清掉这个项目的信息，
        // 以前这里会留着"安装 1.20.4 / 100% / 4,828 个文件 / 数据源"），
        // 而队列里那一行仍然是「已完成」—— 那是历史，不是当前进度。
        var progress = catalog.GetProgress();
        Assert.AreEqual("—", progress.CurrentTask.Key);
        Assert.AreEqual(0, progress.Percent);
        Assert.AreEqual("—", progress.AmountText);
        Assert.AreEqual("—", progress.FilesText);
        Assert.AreEqual("—", progress.RemainingText.Key);
        Assert.AreEqual("—", progress.ConnectionsText);
        Assert.AreEqual("—", progress.DataSource);

        var row = catalog.GetQueue().Single();
        Assert.AreEqual(DownloadQueueState.Completed, row.State);
        Assert.AreEqual("已完成", row.Detail!.Key);
    }

    [TestMethod]
    public async Task TheProgressShowsTheRunningTaskWhileItRunsAndIsClearedAfterwards()
    {
        WriteVersionJson();

        var handler = new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar);
        var catalog = NewCatalog(handler);

        // 任务一进入"正在跑"就会同步广播一次变更，此刻进度区就该写着它 —— 这一份快照取得到、
        // 且不依赖下载线程什么时候把进度回调投递过来（那个投递是后台的，抢快照就是随机失败的用例）。
        var runningTitle = string.Empty;
        catalog.Changed += (_, _) =>
        {
            var snapshot = catalog.GetProgress();
            if (snapshot.CurrentTask.Key != "—")
            {
                // 进度区的"当前任务"是**键 + 参数**：断言键与参数（不是拼好的句子，也不靠 Key 猜）。
                Assert.AreEqual("安装 {0}", snapshot.CurrentTask.Key);
                CollectionAssert.AreEqual(new[] { "1.20.4" }, snapshot.CurrentTask.Parameters.ToArray());
                runningTitle = snapshot.CurrentTask.Key;
            }
        };

        catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await catalog.WhenIdleAsync();

        Assert.AreEqual("安装 {0}", runningTitle, "任务在跑的时候，进度区写的就是它。");

        // 跑完就清空（用户 2026-10-06 的要求）。这一单真实下了多少，看 job 的结算。
        var job = catalog.GetJobs().Single();
        Assert.AreEqual(ClientJar.Length, job.Result!.Bytes);
        Assert.AreEqual(1, job.Result.FilesDownloaded);

        var afterDone = catalog.GetProgress();
        Assert.AreEqual("—", afterDone.CurrentTask.Key, "下载完成后进度区不能再挂着刚跑完那个项目的名字。");
        Assert.AreEqual(0, afterDone.Percent);
        Assert.AreEqual("—", afterDone.AmountText);
        Assert.AreEqual("—", afterDone.FilesText);
        Assert.AreEqual("—", afterDone.ConnectionsText);

        // 队列里那一行是**历史**：仍然是「已完成」，带着这一单的信息。
        var row = catalog.GetQueue().Single();
        Assert.AreEqual(DownloadQueueState.Completed, row.State);
        Assert.AreEqual("已完成", row.Detail!.Key);
    }

    // ==================== 失败路径 ====================

    [TestMethod]
    public async Task AFailedInstallIsShownAsFailedNotAsCompleted()
    {
        WriteVersionJson();

        var handler = new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.NotFound);
        var catalog = NewCatalog(handler);

        catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Failed, job.State);
        Assert.IsFalse(job.Result!.Succeeded);
        Assert.IsNotNull(job.Detail, "失败的任务必须给出原因（「失败：」前缀由界面加）。");

        // 队列行必须**如实显示失败**，而不是伪装成"已完成"。
        var row = catalog.GetQueue().Single();
        Assert.AreEqual(DownloadQueueState.Failed, row.State);
        Assert.AreEqual(0, catalog.PendingTaskCount);
    }

    [TestMethod]
    public void TheDefaultDataSourceFollowsTheLoaderKind()
    {
        const DownloadSourcePolicy Automatic = DownloadSourcePolicy.Automatic;

        Assert.AreEqual(
            "piston-data.mojang.com",
            FileDownloadCatalog.DataSourceFor(Game(LoaderKind.Vanilla), Automatic),
            "原版的默认源是 Mojang 的分发主机。");
        Assert.AreEqual("meta.fabricmc.net", FileDownloadCatalog.DataSourceFor(Game(LoaderKind.Fabric), Automatic));
        Assert.AreEqual("meta.quiltmc.org", FileDownloadCatalog.DataSourceFor(Game(LoaderKind.Quilt), Automatic));
        Assert.AreEqual(
            "maven.minecraftforge.net",
            FileDownloadCatalog.DataSourceFor(Game(LoaderKind.Forge), Automatic),
            "装 Forge 时数据源不能显示成 piston-data —— 它的库来自 Forge 的 Maven。");
        Assert.AreEqual(
            "maven.neoforged.net",
            FileDownloadCatalog.DataSourceFor(Game(LoaderKind.NeoForge), Automatic));
        Assert.AreEqual("optifine.net", FileDownloadCatalog.DataSourceFor(Game(LoaderKind.OptiFine), Automatic));
        Assert.AreEqual(
            "cdn.modrinth.com",
            FileDownloadCatalog.DataSourceFor(new ContentDownloadTask(new ContentDownloadRequest("a", [], [])), Automatic));
        Assert.AreEqual(
            "—",
            FileDownloadCatalog.DataSourceFor(
                new WorldBackupTask(new WorldBackupRequest("a", new WorldBackupTarget("s", "s"))),
                Automatic),
            "本地磁盘任务没有远端数据源。");
    }

    [TestMethod]
    public async Task AForgeInstallReportsItsOwnSourceAndAnIndeterminateProgress()
    {
        // Forge 官方安装器在子进程里自己下载库：拿不到字节数，但至少要让界面知道
        // ① 真实数据源是 Forge 的 Maven；② 进度是"不确定"而不是一根停在 0% 的条。
        WriteVersionJson();
        WriteVanillaClientJar();

        var handler = new StubHandler()
            .RespondTo(".sha1", HttpStatusCode.OK, LoaderFixture.Sha1Hex("forge installer bytes"))
            .RespondTo("forge-1.20.4-49.0.30-installer.jar", HttpStatusCode.OK, "forge installer bytes");

        FileDownloadCatalog? catalog = null;
        DownloadProgress? duringInstaller = null;

        catalog = NewCatalog(
            handler,
            forgeOnRun: (_, gameDirectory) =>
            {
                // 这一刻官方安装器已经在跑（ForgeInstaller 在 RunAsync 之前报了不确定进度）。
                duringInstaller = catalog!.GetProgress();
                WriteForgeProduct("1.20.4-forge-49.0.30", gameDirectory);
            });

        catalog.EnqueueInstall(new InstallRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));
        await catalog.WhenIdleAsync();

        Assert.IsNotNull(duringInstaller, "安装器运行时应当有进度快照。");
        Assert.IsTrue(duringInstaller.IsIndeterminate, "官方安装器的下载发生在其内部，进度必须标成不确定。");
        Assert.AreEqual("maven.minecraftforge.net", duringInstaller.DataSource, "数据源必须是 Forge 的 Maven。");
    }

    private static GameInstallTask Game(LoaderKind loader) => new(new InstallRequest(
        InstanceName: null,
        VersionId: "1.20.4",
        Loader: loader,
        LoaderVersion: loader == LoaderKind.Vanilla ? null : "1.0.0"));

    [TestMethod]
    public async Task AFabricInstallQueuedFromTheWizardReallyInstallsTheLoader()
    {
        // M4-f：向导里选 Fabric → 确认并安装，队列里真的走完了它（不再是"属于 M4"的失败）。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);

        var handler = LoaderFixture.Stub();
        var catalog = NewCatalog(handler);

        var job = catalog.EnqueueInstall(new InstallRequest("1.20.4-Fabric", "1.20.4", LoaderKind.Fabric, "0.15.7"));

        StringAssert.Contains(TestText.Of(job.Title), "Fabric 0.15.7", "队列标题要写清装的是哪个加载器，否则两行长得一样。");

        await catalog.WhenIdleAsync();

        var finished = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Completed, finished.State, TestText.Of(finished.Result?.Failure));
        Assert.AreEqual(LoaderFixture.FabricId, finished.Result!.VersionId);
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-Fabric", "version.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_gameDirectory, "versions", LoaderFixture.FabricId)),"加载器共享目录不该再留下。");

    }

    [TestMethod]
    public async Task AForgeInstallQueuedFromTheWizardRunsTheOfficialInstaller()
    {
        // M4-g：向导里选 Forge → 队列真的把官方安装器跑起来并校验产出。
        WriteVersionJson();
        WriteVanillaClientJar();

        var handler = new StubHandler()
            .RespondTo(".sha1", HttpStatusCode.OK, LoaderFixture.Sha1Hex("forge installer bytes"))
            .RespondTo("forge-1.20.4-49.0.30-installer.jar", HttpStatusCode.OK, "forge installer bytes");

        var catalog = NewCatalog(handler, forgeProductId: "1.20.4-forge-49.0.30");

        var job = catalog.EnqueueInstall(new InstallRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));

        StringAssert.Contains(TestText.Of(job.Title), "Forge 49.0.30");

        await catalog.WhenIdleAsync();

        var finished = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Completed, finished.State, TestText.Of(finished.Result?.Failure));
        Assert.AreEqual("1.20.4-forge-49.0.30", finished.Result!.VersionId);
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-Forge", "version.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-forge-49.0.30")),"加载器共享目录不该再留下。");
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "launcher_profiles.json")), "安装器要求这个文件存在。");
    }

    [TestMethod]
    public async Task AnOptiFineInstallFailsWhenTheVanillaPrerequisiteCannotBeInstalled()
    {
        // 选 OptiFine 时先装原版；取不到原版版本清单时失败原因必须说清是"先装原版"这一步。
        var handler = new StubHandler();
        var catalog = NewCatalog(handler);

        catalog.EnqueueInstall(new InstallRequest("1.20.4-OptiFine", "1.20.4", LoaderKind.OptiFine, "HD_U_I8"));
        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Failed, job.State);
        StringAssert.Contains(TestText.Of(job.Result!.Failure), "先安装原版 1.20.4");
    }

    [TestMethod]
    public async Task AnOptiFineInstallRunsTheHeadlessInstallerInASandboxAndPromotesTheOutput()
    {
        // 端到端（用替身）：解析列表 → 下安装器 → 结构校验 → 沙箱里跑 headless 安装器 → 搬运产物。
        WriteVersionJson();
        WriteVanillaClientJar();

        var versionId = "1.20.4-OptiFine_HD_U_I8";
        var handler = new StubHandler()
            // 更具体的先注册：桩按注册顺序取第一条命中的规则，而列表地址是取件地址的前缀。
            .RespondTo(
                "/downloads",
                HttpStatusCode.OK,
                "<a href=\"http://optifine.net/adloadx?f=OptiFine_1.20.4_HD_U_I8.jar\">x</a>")
            .RespondTo(
                "/adloadx",
                HttpStatusCode.OK,
                "<a href=\"downloadx?f=OptiFine_1.20.4_HD_U_I8.jar&amp;x=abc123\">x</a>")
            .RespondBytesTo("/downloadx", FakeOptiFineInstallerJar());

        var catalog = NewCatalog(handler, optiFineOnRun: (_, sandboxParent) =>
        {
            // 模拟官方安装器：往 %APPDATA%\.minecraft 里写出版本 JSON 与一个库文件。
            var sandbox = Path.Combine(sandboxParent, OptiFineInstaller.SandboxFolderName);
            var directory = Path.Combine(sandbox, "versions", versionId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, versionId + ".json"), """{"id":"x","inheritsFrom":"1.20.4"}""");

            var library = Path.Combine(sandbox, "libraries", "optifine", "OptiFine", "1.20.4_HD_U_I8");
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(library, "OptiFine-1.20.4_HD_U_I8.jar"), "patched");
        });

        catalog.EnqueueInstall(new InstallRequest("1.20.4-OptiFine", "1.20.4", LoaderKind.OptiFine, "HD_U_I8"));
        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();
        Assert.AreEqual(InstallJobState.Completed, job.State, TestText.Of(job.Result!.Failure));
        Assert.AreEqual(versionId, job.Result.VersionId);

        // 产物真的搬进了实例目录：version.json + 那个库文件。
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-OptiFine", "version.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_gameDirectory, "versions", versionId)),"共享版本目录不该再留下。");

        var promoted = Path.Combine(
            _gameDirectory, "libraries", "optifine", "OptiFine", "1.20.4_HD_U_I8", "OptiFine-1.20.4_HD_U_I8.jar");
        Assert.IsTrue(File.Exists(promoted), "安装器写出的库也要搬过去。");

        // 跑法必须与真机核过的形状一致：headless + 主类 optifine.Installer + APPDATA 指向沙箱上一级。
        var run = _tools.Runs.Single();
        CollectionAssert.Contains(run.Arguments.ToArray(), "-Djava.awt.headless=true");
        CollectionAssert.Contains(run.Arguments.ToArray(), "optifine.Installer");
        Assert.IsNotNull(run.Environment);
        Assert.AreEqual(run.WorkingDirectory, run.Environment!["APPDATA"]);

        // 沙箱必须清掉（它只是中间产物）。
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_paths.DownloadCacheDirectory, "optifine", versionId)),
            "沙箱与安装器 jar 不该留在下载缓存里。");
    }

    [TestMethod]
    public async Task AnUnresolvableVersionFailsTheJobWithoutCrashingTheQueue()
    {
        // 既没有本地版本 JSON，也没有缓存清单 → 解析失败，但队列必须继续可用。
        var catalog = NewCatalog(new StubHandler());

        catalog.EnqueueInstall(new InstallRequest("x", "9.9.9", LoaderKind.Vanilla, null));
        await catalog.WhenIdleAsync();

        Assert.AreEqual(InstallJobState.Failed, catalog.GetJobs().Single().State);

        // 队列还能接着用（worker 不会因为一次失败就死掉）。
        WriteVersionJson();
        var handler = new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar);
        var second = NewCatalog(handler);
        second.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await second.WhenIdleAsync();

        Assert.AreEqual(InstallJobState.Completed, second.GetJobs().Single().State);
    }

    // ==================== M4-h：安装失败回滚 ====================

    [TestMethod]
    public async Task AFailedInstallLeavesTheGameDirectoryExactlyAsItWas()
    {
        // M4-h 的验收：安装中途失败 → 游戏目录回到安装前（不留半个版本 JSON、半个库）。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);

        // 客户端 JAR 能下，库的校验值对不上（必然失败）—— 于是失败发生在"已经写了东西之后"。
        var handler = new StubHandler()
            .RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar)
            .RespondTo("authlib-6.0.54.jar", HttpStatusCode.OK, "tampered bytes");

        var catalog = NewCatalog(handler);

        catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Failed, job.State);
        StringAssert.Contains(TestText.Of(job.Result!.Failure), "已回滚", "失败原因里要写清「这次写入已经撤销」（否则用户会以为目录里躺着一堆半成品）。");

        Assert.IsFalse(
            File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4", "1.20.4.jar")),
            "这次下的半成品客户端 JAR 必须被清掉。");
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_gameDirectory, "libraries")),
            "这次新建的库目录（含里面的空目录）也要清掉。");
        Assert.IsTrue(
            File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4", "1.20.4.json")),
            "装前就存在的版本 JSON 一个字节都不许动。");

        Assert.IsTrue(
            _diagnostics.Entries.Any(static entry => entry.MessageKey == "已回滚这次安装的写入（{0}：删除 {1} 个文件、{2} 个目录，恢复 {3} 个文件，失败 {4} 项）"),
            "回滚要留一条事件（用户与我们都得知道目录里发生了什么）。");
    }

    [TestMethod]
    public async Task ASuccessfulInstallKeepsEveryFileTheTransactionSaw()
    {
        // 提交路径：事务绝不能在成功时把刚装好的东西删掉。
        LoaderFixture.WriteVanillaVersionJson(_gameDirectory);

        var handler = new StubHandler()
            .RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar)
            .RespondTo("authlib-6.0.54.jar", HttpStatusCode.OK, LoaderFixture.AuthlibJar);

        var catalog = NewCatalog(handler);

        catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Completed, job.State, TestText.Of(job.Result?.Failure));
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-Vanilla", "1.20.4-Vanilla.jar")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4")),"共享版本目录不该再留下。");
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "libraries", "com", "mojang", "authlib", "6.0.54", "authlib-6.0.54.jar")));
    }

    [TestMethod]
    public async Task AFailedForgeInstallRollsBackWhatTheOfficialInstallerAlreadyWrote()
    {
        // 官方安装器是个黑盒：它退出前可能已经写了版本目录、库，还改过 launcher_profiles.json
        // （M4-g 的真机探针见过 10058 字节的那一份）。事务用"装前 / 装后各扫一遍"覆盖它 ——
        // 这正是 B-11 要覆盖 f/g 写盘点的原因。
        WriteVersionJson();
        WriteVanillaClientJar();

        var handler = new StubHandler()
            .RespondTo(".sha1", HttpStatusCode.OK, LoaderFixture.Sha1Hex("forge installer bytes"))
            .RespondTo("forge-1.20.4-49.0.30-installer.jar", HttpStatusCode.OK, "forge installer bytes");

        var catalog = NewCatalog(
            handler,
            forgeOnRun: (_, game) =>
            {
                WriteForgeProduct("1.20.4-forge-49.0.30", game);

                var library = Path.Combine(
                    game, "libraries", "net", "minecraftforge", "forge", "1.20.4-49.0.30", "forge-1.20.4-49.0.30-client.jar");

                Directory.CreateDirectory(Path.GetDirectoryName(library)!);
                File.WriteAllText(library, "patched", new UTF8Encoding(false));
            },
            forgeExitCode: 1);

        catalog.EnqueueInstall(new InstallRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));
        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Failed, job.State);
        StringAssert.Contains(TestText.Of(job.Result!.Failure), "已回滚");

        Assert.IsFalse(
            Directory.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4-forge-49.0.30")),
            "安装器已经写出的版本目录必须被回滚掉。");
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_gameDirectory, "libraries")),
            "安装器已经下好的库也必须被回滚掉。");
        Assert.IsFalse(
            File.Exists(Path.Combine(_gameDirectory, "launcher_profiles.json")),
            "为了让安装器干活而补的那个文件也不该留下。");
        Assert.IsTrue(
            File.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4", "1.20.4.json")),
            "装前就有的版本 JSON 不许动。");
    }

    // ==================== Java 运行时任务（§12.7） ====================

    [TestMethod]
    public async Task AJavaRuntimeTaskRunsThroughTheSameQueue()
    {
        var world = new JavaRuntimeWorld();
        world.Publish(
            "java-runtime-gamma",
            "17.0.15",
            [
                JavaRuntimeFixture.Directory("bin"),
                JavaRuntimeFixture.File("bin/javaw.exe", "javaw-bytes"),
            ]);
        world.PublishIndex();

        var javaRuntimes = new FakeJavaRuntimeCatalog(hasDetectionResult: true);
        var catalog = NewCatalog(world.Handler, javaRuntimesOverride: javaRuntimes);

        var job = catalog.EnqueueJavaRuntime(new JavaRuntimeRequest("1.20.4"));

        Assert.AreEqual(InstallJobState.Queued, job.State);
        Assert.AreEqual("获取 Java 运行时（适配 {0}）", job.Title.Key);
        CollectionAssert.AreEqual(new[] { "1.20.4" }, job.Title.Parameters.ToArray());
        Assert.AreEqual(1, catalog.PendingTaskCount);

        await catalog.WhenIdleAsync();

        var finished = catalog.GetJobs().Single();
        Assert.AreEqual(InstallJobState.Completed, finished.State, TestText.Of(finished.Result?.Failure));
        Assert.AreEqual("java-runtime-gamma", finished.Result!.VersionId);
        Assert.IsTrue(
            File.Exists(Path.Combine(
                _paths.JavaRuntimeDirectory,
                "java-runtime-gamma",
                JavaRuntimeFixture.Platform,
                "java-runtime-gamma",
                "bin",
                "javaw.exe")),
            "运行时必须落在发现逻辑扫描的那个根目录下（两处用的是同一个属性）。");

        // 装完之后**强制**重检一次：p10 的表与启动前的选 Java 都读检测结果，
        // 而刷新有 5 分钟新鲜度窗口 —— 不强制的话用户刚装好的运行时要等 5 分钟才出现。
        Assert.AreEqual(1, javaRuntimes.RedetectCount);
    }

    [TestMethod]
    public async Task AFailedJavaRuntimeTaskDoesNotTouchTheGameDirectory()
    {
        var world = new JavaRuntimeWorld();
        world.Publish(
            "java-runtime-gamma",
            "17.0.15",
            [JavaRuntimeFixture.File("bin/javaw.exe", "javaw-bytes")],
            corruptPaths: ["bin/javaw.exe"]);
        world.PublishIndex();

        // 装前就有的版本 JSON：运行时任务的失败**不该**触发 M4-h 的游戏目录回滚
        // （那条事务只覆盖游戏版本安装，见 JavaRuntimeInstaller 的说明）。
        WriteVersionJson();
        var before = Directory.GetFileSystemEntries(_gameDirectory, "*", SearchOption.AllDirectories).Length;

        var catalog = NewCatalog(world.Handler);
        catalog.EnqueueJavaRuntime(new JavaRuntimeRequest("1.20.4"));
        await catalog.WhenIdleAsync();

        var finished = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Failed, finished.State);
        StringAssert.Contains(TestText.Of(finished.Detail), "校验不通过");
        Assert.IsFalse(
            TestText.Of(finished.Result!.Failure).Contains("已回滚", StringComparison.Ordinal),
            "运行时任务没有游戏目录事务，不该出现回滚说明。");
        Assert.AreEqual(
            before,
            Directory.GetFileSystemEntries(_gameDirectory, "*", SearchOption.AllDirectories).Length,
            "游戏目录一个文件都不该动。");
        Assert.IsFalse(Directory.Exists(_paths.JavaRuntimeDirectory), "失败时不许留下运行时目录。");
    }

    // ==================== M7-d：内容下载任务 ====================

    /// <summary>
    /// 内容下载走同一条队列：入队 → 后台跑 → 落盘 → 阶段表按**内容**的那三条显示。
    /// </summary>
    /// <remarks>
    /// 与安装那几条用例同一个目的：验证**队列**（入队即返回、终态、阶段表、进度清空），
    /// 而依赖递归与替换的细节在 <c>ContentInstallerTests</c> 里逐条钉过。
    /// </remarks>
    [TestMethod]
    public async Task AQueuedContentDownloadRunsThroughTheQueueAndLandsInMods()
    {
        var bytes = Encoding.UTF8.GetBytes("sodium bytes");
        var instanceDirectory = CreateInstance("1.20.4-Forge");
        var old = Path.Combine(instanceDirectory, "mods", "sodium-0.5.8.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "old", new UTF8Encoding(false));

        var versionJson = "[{\"id\":\"V2\",\"project_id\":\"SODIUM\",\"version_number\":\"0.6.5\",\"name\":\"0.6.5\","
            + "\"date_published\":\"2026-01-01T00:00:00Z\",\"game_versions\":[\"1.20.4\"],\"loaders\":[\"forge\"],"
            + "\"files\":[{\"url\":\"https://cdn.modrinth.com/data/PROJ/versions/V2/sodium-0.6.5.jar\","
            + "\"filename\":\"sodium-0.6.5.jar\",\"size\":" + bytes.Length + ",\"primary\":true,"
            + "\"hashes\":{\"sha1\":\"" + Sha1Hex(Encoding.UTF8.GetString(bytes)) + "\",\"sha512\":\"\"}}],"
            + "\"dependencies\":[]}]";

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, versionJson)
            .RespondBytesTo("versions/V2/sodium-0.6.5.jar", bytes);

        var catalog = NewCatalog(handler);

        var job = catalog.EnqueueContentDownload(new ContentDownloadRequest(
            "1.20.4-Forge",
            [new ContentDownloadTarget("SODIUM", "V2", "Sodium", old)],
            []));

        Assert.AreEqual(InstallJobState.Queued, job.State, "入队是立即返回的。");
        Assert.AreEqual("更新 {0} 个模组 · {1}", job.Title.Key);
        Assert.AreEqual(1, catalog.PendingTaskCount);

        await catalog.WhenIdleAsync();

        var finished = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Completed, finished.State, finished.Detail?.Key);
        Assert.IsTrue(finished.Result!.Succeeded);
        Assert.AreEqual(1, finished.Result.FilesDownloaded);
        Assert.IsTrue(File.Exists(Path.Combine(instanceDirectory, "mods", "sodium-0.6.5.jar")));
        Assert.IsFalse(File.Exists(old), "更新 = 下新的 + 把旧的移走。");


        // 跑完之后"当前任务/进度"清空（用户 2026-10-06 的要求，与安装任务同一条）。
        Assert.AreEqual("—", catalog.GetProgress().CurrentTask.Key);
    }

    // ==================== M7-e-2：导入整合包 ====================

    [TestMethod]
    public async Task ImportingAMrpackCreatesTheInstanceInstallsTheGameAndLaysDownTheContent()
    {
        WriteVersionJson();

        const string ModContent = "mod jar bytes";
        const string OverrideBody = "config=1";

        // 清单里一个文件（走 Modrinth CDN）+ 一个覆盖层文件（只在 zip 里，没有下载地址）。
        var index = "{ \"formatVersion\": 1, \"game\": \"minecraft\", \"name\": \"Test Pack\", "
            + "\"dependencies\": { \"minecraft\": \"1.20.4\" }, \"files\": [ { \"path\": \"mods/mod.jar\", "
            + "\"hashes\": { \"sha1\": \"" + Sha1Hex(ModContent) + "\", \"sha512\": \"" + Sha512Hex(ModContent) + "\" }, "
            + "\"downloads\": [\"https://cdn.modrinth.com/data/aaa/mod.jar\"], \"fileSize\": " + ModContent.Length + " } ] }";

        var pack = WriteMrpack(index, [("overrides/config/a.toml", OverrideBody)]);

        var handler = new StubHandler()
            .RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar)
            .RespondTo("data/aaa/mod.jar", HttpStatusCode.OK, ModContent);

        var catalog = NewCatalog(handler);

        var job = catalog.EnqueueMrpackImport(new MrpackInstallRequest(pack));

        // 队列行的标题只有一个参数（文件名）：包名要等读清单才知道，而标题在入队那一刻就得有。
        Assert.AreEqual("导入整合包 {0}", job.Title.Key);


        await catalog.WhenIdleAsync();

        var finished = catalog.GetJobs().Single();
        Assert.AreEqual(InstallJobState.Completed, finished.State, Why(finished.Result));

        // ① 实例按包名建出来（不是"版本-加载器"：那形状用户认不出是哪个包）。
        Assert.AreEqual("Test Pack", _instances.GetInstances().Single().Name);

        // ② 游戏装进了实例目录；共享版本目录不再保留。
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "Test Pack", "Test Pack.jar")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_gameDirectory, "versions", "1.20.4")),"共享版本目录不该再留下。");

        // ③ 内容铺进了**那个实例自己的目录**（隔离实例的 InstancePath）。
        var instancePath = _instances.GetLaunchInfo("Test Pack").InstancePath;
        Assert.AreEqual(ModContent, File.ReadAllText(Path.Combine(instancePath, "mods", "mod.jar")));
        Assert.AreEqual(OverrideBody, File.ReadAllText(Path.Combine(instancePath, "config", "a.toml")));

        // ④ 报的是包名，不是文件名。
        Assert.AreEqual("Test Pack", finished.Result?.VersionId);
        Assert.IsTrue(finished.Result!.Succeeded);
    }

    [TestMethod]
    public async Task AMrpackThatFailsTheSafetyScanLeavesNoInstanceBehind()
    {
        WriteVersionJson();

        // 危险扩展名是**阻断**项：一个 .exe 进去，整包不导入。
        var pack = WriteMrpack(MinimalIndex("Test Pack", "mods/mod.jar"), [], [("tool.exe", "MZ")]);

        var catalog = NewCatalog(new StubHandler());
        catalog.EnqueueMrpackImport(new MrpackInstallRequest(pack));

        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();
        Assert.AreEqual(InstallJobState.Failed, job.State);
        StringAssert.Contains(Why(job.Result), "安全扫描");

        // 关键：**一个实例都没建**、一个字节都没铺 —— 扫描在建实例之前。
        Assert.AreEqual(0, _instances.GetInstances().Count);
        Assert.IsFalse(Directory.Exists(_paths.InstanceDirectory("Test Pack")));
    }

    [TestMethod]
    public async Task AMrpackWithoutAGameVersionStopsBeforeCreatingAnything()
    {
        // 清单没有声明 minecraft 版本：无从决定装哪一版游戏。
        var index = "{ \"formatVersion\": 1, \"game\": \"minecraft\", \"name\": \"No Version\", \"files\": [] }";
        var pack = WriteMrpack(index, []);

        var catalog = NewCatalog(new StubHandler());
        catalog.EnqueueMrpackImport(new MrpackInstallRequest(pack));

        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();
        Assert.AreEqual(InstallJobState.Failed, job.State);
        StringAssert.Contains(Why(job.Result), "Minecraft 版本");
        Assert.AreEqual(0, _instances.GetInstances().Count);
    }

    // ==================== M7-f：存档备份与恢复 ====================

    [TestMethod]
    public async Task BackingUpASaveFromTheQueueWritesTheZipAndShowsTheThreeBackupStages()
    {
        var instancePath = CreateWorldSave(
            "1.20.4-Forge",
            "New World",
            ("level.dat", "level"),
            ("region/r.0.0.mca", "region bytes"));

        var catalog = NewCatalog(new StubHandler());

        var job = catalog.EnqueueWorldBackup(new WorldBackupRequest(
            "1.20.4-Forge",
            new WorldBackupTarget("New World", "新世界")));

        // 队列行标题是**显示名**（用户给存档起的名字），而路径用的是目录名。
        Assert.AreEqual("备份存档 {0}", job.Title.Key);
        Assert.AreEqual("新世界", job.Title.Parameters[0]);

        await catalog.WhenIdleAsync();

        Assert.AreEqual(InstallJobState.Completed, catalog.GetJobs().Single().State);

        // 归档落在读取端（FileContentCatalog.HasBackup）认的那个位置：
        // <实例的游戏目录>\backups\<存档目录名>.zip
        var archive = Path.Combine(instancePath, "backups", "New World.zip");
        Assert.IsTrue(File.Exists(archive));

        using var zip = ZipFile.OpenRead(archive);
        Assert.AreEqual(2, zip.Entries.Count);
        Assert.AreEqual("level", ReadEntry(zip, "level.dat"));
        Assert.AreEqual("region bytes", ReadEntry(zip, "region/r.0.0.mca"));

    }

    [TestMethod]
    public async Task RestoringASaveFromTheQueuePutsTheBackupBackAndStashesWhatWasThere()
    {
        var instancePath = CreateWorldSave("1.20.4-Forge", "New World", ("level.dat", "第一版"));
        var catalog = NewCatalog(new StubHandler());

        catalog.EnqueueWorldBackup(new WorldBackupRequest("1.20.4-Forge", new WorldBackupTarget("New World", "新世界")));
        await catalog.WhenIdleAsync();

        // 备份之后用户又玩了一会儿（存档被改写）：恢复要把**备份那一刻**的内容放回去，
        // 而把现在这一份整目录移进回收目录 —— 不是删掉。
        var save = Path.Combine(instancePath, "saves", "New World");
        File.WriteAllText(Path.Combine(save, "level.dat"), "第二版");

        var job = catalog.EnqueueWorldRestore(new WorldRestoreRequest(
            "1.20.4-Forge",
            new WorldBackupTarget("New World", "新世界")));

        Assert.AreEqual("恢复存档 {0}", job.Title.Key);

        await catalog.WhenIdleAsync();

        Assert.AreEqual(InstallJobState.Completed, catalog.GetJobs()[^1].State);
        Assert.AreEqual("第一版", File.ReadAllText(Path.Combine(save, "level.dat")));

        // 被换下来的那一份还在（这就是"恢复前先整目录改名"的收益）。
        var stash = Directory.GetFiles(
            Path.Combine(instancePath, "backups", "saves"),
            "level.dat",
            SearchOption.AllDirectories);

        Assert.AreEqual(1, stash.Length, "被换下来的存档必须还在。");
        Assert.AreEqual("第二版", File.ReadAllText(stash[0]));

        // 暂存目录一定要清掉（半成品留在 backups 里会让人以为它是备份）。
        Assert.IsFalse(Directory.Exists(Path.Combine(instancePath, "backups", ".staging")));

    }

    [TestMethod]
    public async Task BackingUpASaveOfAnInstanceThatIsGoneFailsHonestly()
    {
        // 实例被删掉之后又点「备份所选」：如实失败，而不是去动一个猜出来的目录。
        var catalog = NewCatalog(new StubHandler());

        catalog.EnqueueWorldBackup(new WorldBackupRequest("不存在", new WorldBackupTarget("New World", "新世界")));
        await catalog.WhenIdleAsync();

        var job = catalog.GetJobs().Single();
        Assert.AreEqual(InstallJobState.Failed, job.State);
        StringAssert.Contains(Why(job.Result), "找不到实例");
    }

    /// <summary>建一个隔离实例，并在它的存档目录里放一个存档。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="folder">存档目录名。</param>
    /// <param name="files">存档里的文件（相对路径 + 内容）。</param>
    /// <returns>该实例的游戏目录（备份落在它下面的 <c>backups\</c>）。</returns>
    private string CreateWorldSave(
        string instanceName,
        string folder,
        params (string Relative, string Body)[] files)
    {
        _instances.CreateInstance(new InstanceCreationRequest(
            Name: instanceName,
            GameVersion: "1.20.4",
            Loader: LoaderKind.Vanilla,
            LoaderVersion: null,
            IsolatedDirectory: true));

        var instancePath = _instances.GetLaunchInfo(instanceName).InstancePath;
        var save = Path.Combine(instancePath, "saves", folder);

        foreach (var (relative, body) in files)
        {
            var path = Path.Combine(save, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        return instancePath;
    }

    /// <summary>读归档里某个条目的内容（用例要断言"打包进去的到底是哪一份"）。</summary>
    /// <param name="archive">归档。</param>
    /// <param name="name">条目名（zip 用正斜杠）。</param>
    /// <returns>内容。</returns>
    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.Entries.Single(candidate => candidate.FullName == name);

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>只声明游戏版本、只有一个文件的最小清单（哈希是占位值，扫描阶段不看内容）。</summary>
    /// <param name="name">整合包名。</param>
    /// <param name="path">清单里那个文件的相对路径。</param>
    /// <returns>清单 JSON。</returns>
    private static string MinimalIndex(string name, string path) =>
        "{ \"formatVersion\": 1, \"game\": \"minecraft\", \"name\": \"" + name + "\", "
        + "\"dependencies\": { \"minecraft\": \"1.20.4\" }, \"files\": [ { \"path\": \"" + path + "\", "
        + "\"hashes\": { \"sha1\": \"aa\", \"sha512\": \"bb\" }, \"downloads\": [\"https://cdn.modrinth.com/x/foo.jar\"] } ] }";

    /// <summary>把清单与覆盖层写成一个真的 <c>.mrpack</c>（zip）。</summary>
    /// <param name="indexJson">清单 JSON。</param>
    /// <param name="overrides">覆盖层文件（<c>overrides/</c> 前缀）。</param>
    /// <param name="extraEntries">额外条目（反例用：危险扩展名等）。</param>
    /// <returns>包路径。</returns>
    private string WriteMrpack(
        string indexJson,
        (string Name, string Content)[] overrides,
        (string Name, string Content)[]? extraEntries = null)
    {
        var path = Path.Combine(_tempRoot, "pack-" + Guid.NewGuid().ToString("N")[..8] + ".mrpack");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(archive, MrpackReader.IndexEntryName, indexJson);

        foreach (var (name, content) in overrides)
        {
            WriteEntry(archive, name, content);
        }

        foreach (var (name, content) in extraEntries ?? [])
        {
            WriteEntry(archive, name, content);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Sha512Hex(string body) =>
        Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>失败时把**参数**也打出来（只报键的话永远看不出是哪一步坏了）。</summary>
    /// <param name="result">结果。</param>
    /// <returns>可读的原因。</returns>
    private static string Why(InstallResult? result) =>
        result?.Failure is { } failure
            ? failure.Key + " :: " + string.Join(" | ", failure.Parameters)
            : "（没有失败原因）";

    // ==================== 辅助 ====================

    /// <summary>
    /// 造一条队列。
    /// </summary>
    /// <param name="handler">HTTP 桩。</param>
    /// <param name="forgeProductId">非 <see langword="null"/> 时，安装器替身会把"官方安装器写出的那个版本"写到这个 id 下。</param>
    /// <param name="forgeOnRun">安装器替身被调用时要做的事（模拟黑盒写入）。</param>
    /// <param name="forgeExitCode">安装器替身报的退出码。</param>
    /// <param name="javaRuntimesOverride">Java 运行时检测替身（要断言"装完之后强制重检"时必须自己传一个）。</param>
    /// <param name="optiFineOnRun">OptiFine 安装器替身被调用时要做的事（它要在沙箱里"写出版本 JSON"）。</param>
    private FileDownloadCatalog NewCatalog(
        StubHandler handler,
        string? forgeProductId = null,
        Action<IReadOnlyList<string>, string>? forgeOnRun = null,
        int forgeExitCode = 0,
        IJavaRuntimeCatalog? javaRuntimesOverride = null,
        Action<IReadOnlyList<string>, string>? optiFineOnRun = null)
    {
        var factory = new FakeHttpClientFactory(handler);
        var store = new VersionJsonStore(factory, _paths, HttpHostPolicy.Default, _diagnostics);
        var downloader = new FileDownloader(factory, HttpHostPolicy.Default, _clock, _diagnostics, static (_, _) => Task.CompletedTask);
        var installer = new VanillaInstaller(
            store,
            new VersionManifestClient(factory, _paths, _clock, _diagnostics),
            downloader,
            new DiskSpaceChecker(_diagnostics),
            new NativeExtractor(_diagnostics),
            _diagnostics);

        var loaderInstaller = new LoaderInstaller(
            new LoaderProfileClient(factory, _diagnostics),
            installer,
            store,
            _diagnostics);

        var tools = new FakeToolRunner
        {
            Result = new ToolRunResult(
                forgeExitCode,
                forgeExitCode == 0 ? string.Empty : "These libraries failed to download. Try again.",
                TimedOut: false),
        };

        if (forgeOnRun is not null)
        {
            tools.OnRun = forgeOnRun;
        }
        else if (forgeProductId is not null)
        {
            tools.OnRun = (_, game) => WriteForgeProduct(forgeProductId, game);
        }
        else if (optiFineOnRun is not null)
        {
            tools.OnRun = optiFineOnRun;
        }

        _tools = tools;

        var javaRuntimes = javaRuntimesOverride ?? new FakeJavaRuntimeCatalog(
            [
                new JavaInstallation(
                    ExecutablePath: @"C:\Java\jdk-17\bin\javaw.exe",
                    HomeDirectory: @"C:\Java\jdk-17",
                    VersionText: "17.0.9",
                    MajorVersion: 17,
                    Vendor: "Eclipse Temurin",
                    Bits: 64),
            ],
            hasDetectionResult: true);

        var forgeInstaller = new ForgeInstaller(
            tools,
            installer,
            downloader,
            store,
            javaRuntimes,
            _paths,
            factory,
            _diagnostics);

        var runtimeInstaller = new JavaRuntimeInstaller(
            new JavaRuntimeManifestClient(factory, HttpHostPolicy.Default, _diagnostics),
            store,
            downloader,
            new DiskSpaceChecker(_diagnostics),
            _paths,
            _diagnostics,
            _clock);

        // M4-i：OptiFine 安装器（沙箱里跑官方安装器）。默认的替身不产出任何东西 ——
        // 需要"装成功"的用例自己给 OnRun。
        var optiFineInstaller = new OptiFineInstaller(
            tools,
            installer,
            downloader,
            store,
            new OptiFineCatalogClient(factory, _diagnostics),
            javaRuntimes,
            _paths,
            _diagnostics);

        // M7-d：内容下载（Modrinth 的版本 JSON + 官方 CDN）。
        var contentInstaller = new ContentInstaller(
            new ModrinthClient(factory, _diagnostics),
            downloader,
            new DiskSpaceChecker(_diagnostics, static _ => long.MaxValue),
            _instances,
            _clock,
            _diagnostics);

        // M7-e-2：整合包导入（扫描器与下载器共用同一个出站白名单）。
        var mrpackInstaller = new MrpackInstaller(
            new ArchiveSafetyScanner(HttpHostPolicy.Default),
            downloader,
            new DiskSpaceChecker(_diagnostics, static _ => long.MaxValue),
            _diagnostics);

        // M7-f：存档备份 / 恢复（只碰文件，不联网）。
        var worldBackupInstaller = new WorldBackupInstaller(_clock, _diagnostics);

        // 2026-10-21：p08 的「从 Modrinth 安装」把 .mrpack 下载也放进队列。
        var modrinthCatalog = new ModrinthCatalog(
            new ModrinthClient(factory, _diagnostics),
            new ModrinthCache(_paths, _diagnostics),
            _clock,
            _diagnostics);

        // 2026-10-24：Forge→OptiFine / Fabric→Fabric API 的可选组件。
        var optionalComponents = new OptionalComponentInstaller(
            _instances,
            modrinthCatalog,
            contentInstaller,
            new OptiFineCatalogClient(factory, _diagnostics),
            downloader,
            _diagnostics);

        return new FileDownloadCatalog(
            installer,
            loaderInstaller,
            forgeInstaller,
            optiFineInstaller,
            runtimeInstaller,
            contentInstaller,
            optionalComponents,
            mrpackInstaller,
            modrinthCatalog,
            worldBackupInstaller,
            _instances,
            javaRuntimes,
            _settings,
            _clock,
            _diagnostics);
    }

    /// <summary>建一个隔离实例并返回它的游戏目录（M7-d 的内容下载要按实例解析 mods 目录）。</summary>
    /// <param name="name">实例名。</param>
    /// <returns>该实例的游戏目录。</returns>
    private string CreateInstance(string name)
    {
        _instances.CreateInstance(new InstanceCreationRequest(name, "1.20.4", LoaderKind.Forge, "49.0.30"));
        return _instances.GetLaunchInfo(name).InstancePath;
    }

    /// <summary>模拟官方安装器：把它自己要装的那个版本写出来（真安装器退出前会写这个文件）。</summary>
    private static void WriteForgeProduct(string versionId, string gameDirectory)
    {
        var directory = Path.Combine(gameDirectory, "versions", versionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, versionId + ".json"),
            "{\"id\":\"" + versionId + "\",\"inheritsFrom\":\"1.20.4\"}",
            new UTF8Encoding(false));
    }

    // ==================== M7-g：取消 / 重试 / 清空已完成 ====================

    [TestMethod]
    public async Task CancellingARunningJobEndsItAsCancelledAndRollsBackTheInstall()
    {
        WriteVersionJson();

        // 闸门：把第一个请求卡住，让用例**确实**在一个任务正在跑的时候去取消它
        // （"等它一会儿应该就跑起来了吧"是随机失败的来源）。
        var stub = new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        stub.Gate = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var catalog = NewCatalog(stub);
        var job = catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // 确实在跑（不是"排队中"），所以这一条覆盖的是**运行中取消**那条路。
        Assert.AreEqual(DownloadQueueState.Running, catalog.GetQueue().Single().State);

        Assert.IsTrue(catalog.Cancel(job.Id));

        // 放行：被取消的下载器下一次检查取消标记时就会抛（已经没人读这个响应了）。
        release.TrySetResult();

        await catalog.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var finished = catalog.GetJobs().Single();

        Assert.AreEqual(InstallJobState.Cancelled, finished.State, "用户点的取消在终态上就是「已取消」，不是「失败」。");
        Assert.AreEqual(DownloadQueueState.Cancelled, catalog.GetQueue().Single().State);

        // 队列行说的是取消，而且**带上 M4-h 的回滚说明**（取消走的是与失败同一条善后路）。
        Assert.AreEqual("已取消。", finished.Detail?.Key);
        StringAssert.Contains(Why(finished.Result), "已取消");
        Assert.IsTrue(
            _diagnostics.Entries.Any(static entry => entry.MessageKey == "已回滚这次安装的写入（{0}：删除 {1} 个文件、{2} 个目录，恢复 {3} 个文件，失败 {4} 项）"),
            "取消一次安装与失败一样，要如实说清「回到装之前」这件事。");
    }

    [TestMethod]
    public async Task CancellingAQueuedJobEndsItBeforeItEverRuns()
    {
        WriteVersionJson();

        var stub = new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        stub.Gate = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var catalog = NewCatalog(stub);

        // 第一个任务卡在闸门里 → 第二个必然是"排队中"。
        catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var queued = catalog.EnqueueInstall(new InstallRequest("1.20.4-Second", "1.20.4", LoaderKind.Vanilla, null));
        Assert.AreEqual(DownloadQueueState.Queued, catalog.GetQueue()[1].State);

        Assert.IsTrue(catalog.Cancel(queued.Id));
        Assert.AreEqual(DownloadQueueState.Cancelled, catalog.GetQueue()[1].State, "排队中的任务直接落终态。");
        Assert.IsTrue(
            _diagnostics.Entries.Any(static entry => entry.MessageKey == "排队中的任务已取消：{0}"),
            "取消一个还没开始的任务也要留一条事件。");

        // 再取消一次是空操作（它已经结束了），也不该抛。
        Assert.IsFalse(catalog.Cancel(queued.Id));
        Assert.IsFalse(catalog.Cancel("不存在"));

        release.TrySetResult();
        await catalog.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // 第一个跑完了；被取消的那个永远是"已取消"，没有被执行过。
        Assert.AreEqual(1, catalog.GetJobs().Count(static entry => entry.State == InstallJobState.Completed));
        Assert.AreEqual(1, catalog.GetJobs().Count(static entry => entry.State == InstallJobState.Cancelled));
    }

    [TestMethod]
    public async Task RetryingAFailedJobQueuesTheSameTaskAgain()
    {
        // 没有本地版本 JSON（也没写清单）→ 安装失败。
        var catalog = NewCatalog(new StubHandler());

        var failed = catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await catalog.WhenIdleAsync();

        Assert.AreEqual(InstallJobState.Failed, catalog.GetJobs().Single().State);
        Assert.IsNull(catalog.Retry(failed.Id + "zz"), "不认识的 id 没有可重试的东西。");

        var retried = catalog.Retry(failed.Id);

        Assert.IsNotNull(retried);
        Assert.AreNotEqual(failed.Id, retried.Id, "重试是**新**任务（新 id、重新排队计时）。");
        Assert.AreEqual(failed.Title, retried.Title, "同一件事，标题不变。");
        Assert.AreEqual(InstallJobState.Queued, retried.State);

        await catalog.WhenIdleAsync();

        // 它照样会跑一遍并失败（版本 JSON 还是没有）—— 这一条钉的是"原样再排一次"。
        Assert.AreEqual(2, catalog.GetJobs().Count);
        Assert.AreEqual(2, catalog.GetJobs().Count(static entry => entry.State == InstallJobState.Failed));
    }

    [TestMethod]
    public async Task RetryingASuccessfulJobIsRefused()
    {
        WriteVersionJson();
        var catalog = NewCatalog(new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar));

        var job = catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await catalog.WhenIdleAsync();

        Assert.AreEqual(InstallJobState.Completed, catalog.GetJobs().Single().State);
        Assert.IsNull(catalog.Retry(job.Id), "已经成功的任务不给「重试」（那只会把同一件事再做一遍）。");
    }

    [TestMethod]
    public async Task ClearFinishedRemovesOnlyTheHistory()
    {
        WriteVersionJson();

        var stub = new StubHandler().RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        stub.Gate = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var catalog = NewCatalog(stub);

        catalog.EnqueueInstall(new InstallRequest("1.20.4-Vanilla", "1.20.4", LoaderKind.Vanilla, null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var queued = catalog.EnqueueInstall(new InstallRequest("1.20.4-Second", "1.20.4", LoaderKind.Vanilla, null));

        // 此刻：一个在跑、一个排队、没有历史。
        Assert.AreEqual(0, catalog.ClearFinished(), "没有历史行时是空操作。");

        release.TrySetResult();
        await catalog.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // 两个都跑完了 → 两行历史。
        Assert.AreEqual(2, catalog.ClearFinished());
        Assert.AreEqual(0, catalog.GetQueue().Count);
        Assert.AreEqual(0, catalog.PendingTaskCount);
        Assert.AreEqual(0, catalog.ClearFinished());

        // 被清掉的任务不能再重试（它的那一行已经不在了）。
        Assert.IsNull(catalog.Retry(queued.Id));
    }

    /// <summary>最小版本 JSON：只有一个客户端 JAR（这里验证的是队列，不是下载本身）。</summary>
    private void WriteVersionJson()
    {
        var json = "{\"id\":\"1.20.4\",\"mainClass\":\"net.minecraft.client.main.Main\","
            + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/v1/objects/cccc/client.jar\","
            + "\"sha1\":\"" + Sha1Hex(ClientJar) + "\",\"size\":" + ClientJar.Length + "}}}";

        var directory = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "1.20.4.json"), json, new UTF8Encoding(false));
    }

    /// <summary>原版客户端 JAR（OptiFine 安装器要在它上面打补丁，所以必须有这个文件）。</summary>
    private void WriteVanillaClientJar()
    {
        var directory = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "1.20.4.jar"), ClientJar, new UTF8Encoding(false));
    }

    /// <summary>
    /// 一个"长得像 OptiFine 安装器"的 jar：含 <c>optifine/Installer.class</c>，
    /// 且 MANIFEST 声明 <c>Main-Class: optifine.InstallerFrame</c> —— 这正是安装器的结构校验要看的两件事。
    /// </summary>
    private static byte[] FakeOptiFineInstallerJar()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
            using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
            {
                writer.Write("Manifest-Version: 1.0\nMain-Class: " + OptiFineInstaller.InstallerMainClass + "\n\n");
            }

            archive.CreateEntry(OptiFineInstaller.InstallerClassEntry);
        }

        return buffer.ToArray();
    }

    private static string Sha1Hex(string body)
    {
#pragma warning disable CA5350 // 与产品代码同理：SHA-1 是 Minecraft 清单协议规定的完整性字段。
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(body));
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
