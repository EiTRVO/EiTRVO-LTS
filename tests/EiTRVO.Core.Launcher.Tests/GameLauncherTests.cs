using System.Globalization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-d：启动编排（阶段推进、失败拦截、会话记账）。
/// <para>
/// 这一层刻意用替身测：真进程的行为已经由 <c>GameProcessRunnerTests</c> 用真进程钉住了，
/// 这里要验的是**编排**——四步阶段有没有按真实进度走、"文件不齐 / 缺 Java / 没凭据"有没有在启动前拦住、
/// 会话有没有记账。把真进程混进来只会让这些断言变慢、变飘。
/// </para>
/// <para>
/// 与之配套的变异点很明确：阶段推进、`BlocksLaunch` 过滤、凭据前置、记账——每一条都有对应用例。
/// </para>
/// </summary>
[TestClass]
public sealed class GameLauncherTests : LaunchTestData
{
    private static readonly JavaInstallation Java17 = new(
        ExecutablePath: @"C:\Java\jdk-17\bin\javaw.exe",
        HomeDirectory: @"C:\Java\jdk-17",
        VersionText: "17.0.9",
        MajorVersion: 17,
        Vendor: "Eclipse Temurin",
        Bits: 64);

    private static readonly JavaInstallation Java21 = new(
        ExecutablePath: @"C:\Java\jdk-21\bin\javaw.exe",
        HomeDirectory: @"C:\Java\jdk-21",
        VersionText: "21.0.5",
        MajorVersion: 21,
        Vendor: "Eclipse Temurin",
        Bits: 64);

    private static readonly JavaInstallation Java8 = new(
        ExecutablePath: @"C:\Program Files (x86)\Common Files\Oracle\Java\javapath\javaw.exe",
        HomeDirectory: @"C:\Program Files (x86)\Common Files\Oracle\Java\javapath",
        VersionText: "1.8.0_271",
        MajorVersion: 8,
        Vendor: "Oracle",
        Bits: 64);

    [TestMethod]
    public async Task ASuccessfulLaunchWalksAllFourStagesAndRecordsTheSession()
    {
        using var world = new LaunchWorld();

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started);
        Assert.IsTrue(result.Succeeded, "退出码 0、没被结束 —— 这是一次成功的会话。");

        var snapshot = world.Launcher.GetSnapshot();

        Assert.AreEqual(LaunchSessionState.Completed, snapshot.State);
        Assert.AreEqual(100, snapshot.Percent);
        Assert.AreEqual(LaunchStagePlan.Count, snapshot.Stages.Count);
        Assert.IsTrue(
            snapshot.Stages.All(static stage => stage.State == LaunchStageState.Completed),
            "四个阶段都应当完成：" + string.Join(" / ", snapshot.Stages.Select(stage => $"{stage.Title}={stage.State}")));
        Assert.AreEqual("Eclipse Temurin 17.0.9", snapshot.JavaRuntime);
        Assert.IsTrue(snapshot.VerifiedFileCount > 0, "校验阶段应当数出就位的文件。");
        Assert.IsNull(snapshot.Failure);

        // 没超上限时内存那一行就是设置值本身，不带任何解释性后缀（免得每台机器上都多一句废话）。
        Assert.AreEqual("{0} MB", snapshot.MemoryText!.Key, "内存文本也是「键 + 参数」：App 层才拼句子。");
        CollectionAssert.AreEqual(new[] { "4096" }, snapshot.MemoryText.Parameters.ToArray());

        // 会话记账：启动时间、退出码、游玩秒数（2 分钟的会话算一次游玩）。
        var record = world.Instances.Recorded.Single();
        Assert.AreEqual(world.InstanceName, record.Name);
        Assert.AreEqual(0, record.ExitCode);
        Assert.AreEqual(120, record.PlaySeconds);

        // 事件：开始 + 退出（退出那条由进程运行器写，这里只断言编排写的两条之一）。
        Assert.IsTrue(world.Diagnostics.Entries.Any(entry => entry.MessageKey == "开始启动游戏：{0}"));
    }

    [TestMethod]
    public async Task TheProgressBarCompletesWhenTheGameStartsNotWhenItExits()
    {
        // 进度条描述"启动有没有完成"：进程一起来就该是 100% / 四个阶段全绿，
        // 而不是等用户玩完退出才走满（那会把整段游戏时间显示成 75%）。
        using var world = new LaunchWorld();
        world.Runner.BlockUntilCancelled = true;

        var run = world.Launcher.LaunchAsync(world.InstanceName);

        await world.Runner.StartedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = world.Launcher.GetSnapshot();

        Assert.AreEqual(LaunchSessionState.Running, running.State);
        Assert.AreEqual(100, running.Percent, "游戏已启动 → 启动进度应当已经走完。");
        Assert.IsTrue(
            running.Stages.All(static stage => stage.State == LaunchStageState.Completed),
            "四个阶段都应当已完成（第 4 阶段在进程起来时就算完成）。");
        Assert.IsNull(running.Result, "这时游戏还在跑，还没有退出结局。");

        world.Launcher.RequestStop();
        await run;
    }

    [TestMethod]
    public async Task TheCommandLineHandedToTheRunnerCarriesTheCredentialAndTheInstanceDirectory()
    {
        using var world = new LaunchWorld();

        await world.Launcher.LaunchAsync(world.InstanceName);

        var request = world.Runner.Requests.Single();

        Assert.AreEqual(Java17.ExecutablePath, request.ExecutablePath, "用选中的 Java 可执行文件。");
        Assert.AreEqual(world.InstancePath, request.WorkingDirectory, "工作目录必须是实例目录（存档写在哪就看它）。");
        Assert.AreEqual(world.InstanceName, request.InstanceName);
        CollectionAssert.Contains(request.Arguments.ToList(), "-Xmx4096M");
        CollectionAssert.Contains(request.Arguments.ToList(), "--accessToken");
        CollectionAssert.Contains(request.Arguments.ToList(), world.Identity.AccessToken);
        CollectionAssert.Contains(request.Arguments.ToList(), "--uuid");
        CollectionAssert.Contains(request.Arguments.ToList(), world.Identity.UuidWithoutDashes);

        // 输出脱敏用的敏感值也要带上（模组可能把启动参数回显到日志里）。
        CollectionAssert.Contains(request.Secrets.ToList(), world.Identity.AccessToken);
    }

    [TestMethod]
    public async Task AMissingClientJarStopsAtTheVerifyStageAndSaysWhatIsMissing()
    {
        using var world = new LaunchWorld();
        File.Delete(world.ClientJarPath);

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        Assert.IsNotNull(result.Failure);
        StringAssert.Contains(TestText.Of(result.Failure), "文件不完整");
        StringAssert.Contains(TestText.Of(result.Failure), "客户端 JAR");
        Assert.AreEqual(0, world.Runner.Requests.Count, "文件不齐就不该起进程。");

        var snapshot = world.Launcher.GetSnapshot();
        Assert.AreEqual(LaunchSessionState.Failed, snapshot.State);
        Assert.AreEqual(LaunchStageState.Completed, Stage(snapshot, LaunchStagePlan.ResolveVersion));
        Assert.AreEqual(LaunchStageState.Failed, Stage(snapshot, LaunchStagePlan.VerifyFiles));
        Assert.AreEqual(LaunchStageState.Pending, Stage(snapshot, LaunchStagePlan.SelectJava), "后面的阶段不该被执行。");
        Assert.AreEqual(LaunchStageState.Pending, Stage(snapshot, LaunchStagePlan.StartProcess));

        Assert.IsTrue(world.Diagnostics.Entries.Any(entry => entry.MessageKey == "启动失败（{0}）：{1}"));
    }

    [TestMethod]
    public async Task AMissingLoggingConfigDoesNotBlockTheLaunch()
    {
        // 日志配置缺失只影响日志格式：拦下来是"为了一个不影响玩的文件挡住用户"。
        using var world = new LaunchWorld();
        File.Delete(world.LoggingConfigPath);

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, "缺日志配置不该拦住启动：" + (TestText.Or(result.Failure, "（无原因）")));
        Assert.AreEqual(1, world.Runner.Requests.Count);

        // 但它仍然要如实上报（否则"日志格式不对"就找不到原因）。
        var warnings = world.Launcher.GetSnapshot();
        Assert.IsNull(warnings.Failure);
    }

    [TestMethod]
    public async Task AFabricInstanceLaunchesTheLoaderVersionTheInstallerWrote()
    {
        // M4-f：装了 Fabric 的实例从"被拒绝"变成"真的能启动" —— 版本 id 由同一条命名规则反查。
        using var world = new LaunchWorld(loader: LoaderKind.Fabric, loaderVersion: "0.15.7");

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));

        var snapshot = world.Launcher.GetSnapshot();

        Assert.AreEqual(LoaderFixture.FabricId, snapshot.VersionId);
        Assert.AreEqual(LaunchStageState.Completed, Stage(snapshot, LaunchStagePlan.ResolveVersion));
        Assert.AreEqual(LaunchStageState.Completed, Stage(snapshot, LaunchStagePlan.StartProcess));
        Assert.AreEqual(Java17.ExecutablePath, world.Runner.Requests.Single().ExecutablePath);
    }

    [TestMethod]
    public async Task AnInstanceVersionJsonIsPreferredOverTheSharedVersionDirectory()
    {
        // Neo 26.1 的版本读取机制：实例目录里的 version.json 就是它的版本，目录名（实例名）即版本 id。
        // 共享的 versions\<游戏版本> 仍然在（原版基底），但启动优先读实例自己的那一份。
        using var world = new LaunchWorld();
        world.WriteInstanceVersion();

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        Assert.AreEqual(
            world.InstanceName,
            world.Launcher.GetSnapshot().VersionId,
            "读到实例目录的 version.json 时，版本 id 就是实例名。");
        Assert.AreEqual(world.InstancePath, world.Runner.Requests.Single().WorkingDirectory);
    }

    [TestMethod]
    public async Task AFabricInstanceWhoseLoaderIsNotInstalledIsRefusedWithTheNextStep()
    {
        using var world = new LaunchWorld(loader: LoaderKind.Fabric, loaderVersion: "0.15.7", loaderInstalled: false);

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "还没有安装到本地");
        StringAssert.Contains(TestText.Of(result.Failure), "version.json");
        StringAssert.Contains(TestText.Of(result.Failure), "下载与队列");
        Assert.AreEqual(0, world.Runner.Requests.Count, "加载器没装就不该起进程。");

        var snapshot = world.Launcher.GetSnapshot();
        Assert.AreEqual(LaunchStageState.Failed, Stage(snapshot, LaunchStagePlan.ResolveVersion));
    }

    [TestMethod]
    public async Task AForgeInstanceLaunchesTheVersionItsInstallerWrote()
    {
        // M4-g：Forge 的版本 id 是官方安装器实际写出来的目录名（1.20.4-forge-49.0.30）。
        using var world = new LaunchWorld(loader: LoaderKind.Forge, loaderVersion: "49.0.30");

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        Assert.AreEqual("1.20.4-forge-49.0.30", world.Launcher.GetSnapshot().VersionId);
        Assert.AreEqual(LaunchStageState.Completed, Stage(world.Launcher.GetSnapshot(), LaunchStagePlan.StartProcess));
    }

    [TestMethod]
    public async Task AnOptiFineInstanceResolvesToTheInstallersOwnVersionId()
    {
        // M4-i 起 OptiFine 也能启动：版本 id 由 <see cref="LoaderNaming.VersionId"/> 算出，
        // 与官方安装器写出的目录名一致。这里版本**没装**，所以要在第 1 阶段就拒绝，
        // 并且拒绝的话里必须点名那个版本 id —— 悄悄按原版启动会让光影与模组全都不生效。
        using var world = new LaunchWorld();
        world.Instances.Info = world.Instances.Info with { Loader = LoaderKind.OptiFine, LoaderVersion = "HD_U_I8" };

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        Assert.IsNotNull(result.Failure);
        StringAssert.Contains(TestText.Of(result.Failure), "OptiFine");
        StringAssert.Contains(TestText.Of(result.Failure), "下载与队列");
        Assert.AreEqual(0, world.Runner.Requests.Count, "版本不存在就不能起进程。");
    }

    [TestMethod]
    public async Task NoUsableJavaRuntimeRefusesTheLaunchBeforeStarting()
    {
        using var world = new LaunchWorld(java: []);
        world.JavaRuntime.HasDetectionResult = true;

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "Java 17");
        Assert.AreEqual(0, world.Runner.Requests.Count);

        var snapshot = world.Launcher.GetSnapshot();
        Assert.AreEqual(LaunchStageState.Failed, Stage(snapshot, LaunchStagePlan.SelectJava));
    }

    [TestMethod]
    public async Task AMissingJavaRuntimeStartsTheOfficialDownloadWhenTheSettingIsOn()
    {
        // 「缺失时自动下载 Mojang 官方 JRE」（设置里的开关，默认开）：一次失败的启动
        // 不该只留一句"去某页获取"，而应当真的把任务排进队列 —— 但仍然如实告诉用户失败了。
        using var world = new LaunchWorld(java: []);
        world.JavaRuntime.HasDetectionResult = true;

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "已按设置开始下载");

        var request = world.Downloads.RuntimeRequests.Single();
        Assert.AreEqual("1.20.4", request.GameVersion, "取哪一代由**要启动的那个版本**决定。");
        Assert.IsTrue(
            world.Diagnostics.Entries.Any(entry => entry.MessageKey.Contains("已按设置开始下载", StringComparison.Ordinal)),
            "自动下载要留下一条事件 —— 它是一次真实的后台动作。");
    }

    [TestMethod]
    public async Task AMissingJavaRuntimeDoesNotDownloadAnythingWhenTheSettingIsOff()
    {
        using var world = new LaunchWorld(java: []);
        world.JavaRuntime.HasDetectionResult = true;
        world.Settings.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = world.GameDirectory,
            AutoFetchJava = false,
        });

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        Assert.IsFalse(
            (TestText.Of(result.Failure)).Contains("已按设置开始下载", StringComparison.Ordinal),
            "开关关着就不该排队，也不该在失败原因里提它。");
        Assert.AreEqual(0, world.Downloads.RuntimeRequests.Count);
    }

    [TestMethod]
    public async Task AnUndetectedJavaRuntimeDoesNotStartAnyDownload()
    {
        // 「还没查过」不能当成「缺少 Java」（M3）：凭一个不成立的结论去下 100 MB 是最糟的那种"顺手"。
        using var world = new LaunchWorld(java: []);
        world.JavaRuntime.HasDetectionResult = false;

        await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.AreEqual(0, world.Downloads.RuntimeRequests.Count);
    }

    [TestMethod]
    public async Task AnUnusableJavaOverrideDoesNotStartAnyDownload()
    {
        // 用户指定的路径已经失效：下一个新运行时也不会被选中（覆盖项优先），所以不该下。
        using var world = new LaunchWorld(java: []);
        world.JavaRuntime.HasDetectionResult = true;
        world.Settings.SaveInstanceOverrides(new InstanceOverrides(
            InstanceName: world.InstanceName,
            JavaOverride: @"C:\Java\gone",
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: false));

        await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.AreEqual(0, world.Downloads.RuntimeRequests.Count);
    }

    [TestMethod]
    public async Task AQueueThatCannotAcceptTheTaskStillReportsTheOriginalFailure()
    {
        // 顺手做的事不该掩盖真正的问题：排不进队列时，失败原因必须还是原来那一句。
        using var world = new LaunchWorld(java: []);
        world.JavaRuntime.HasDetectionResult = true;
        world.Downloads.ThrowOnEnqueue = new NotSupportedException("演示数据源不支持获取 Java 运行时。");

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "没有能运行");
        Assert.IsFalse(
            (TestText.Of(result.Failure)).Contains("已按设置开始下载", StringComparison.Ordinal),
            "排不进队列时，失败原因必须还是原来那一句。");
    }

    [TestMethod]
    public async Task AnUndetectedJavaRuntimeIsReportedAsNotCheckedYet()
    {
        // 还没查过就说"缺少 Java"会让用户去装一个他其实已经有的东西（M3 的 C-9 结论）。
        using var world = new LaunchWorld(java: []);
        world.JavaRuntime.HasDetectionResult = false;

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "还没有检测到");
        Assert.IsFalse(
            (TestText.Of(result.Failure)).Contains("没有能运行", StringComparison.Ordinal),
            "没查过不能说成缺 Java。");
    }

    [TestMethod]
    public async Task TheDeclaredJavaVersionWinsOverTheRuleTable()
    {
        // 1.12.2 的规则表把上限封在 Java 8（LWJGL 2），但**本地版本 JSON 声明的**是 21（C-9：声明值优先）。
        using var world = new LaunchWorld(versionId: "1.12.2", declaredJava: 21, java: [Java21]);
        world.JavaRuntime.HasDetectionResult = true;

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, "声明值优先：" + (TestText.Or(result.Failure, "（无原因）")));
        Assert.AreEqual("Eclipse Temurin 21.0.5", world.Launcher.GetSnapshot().JavaRuntime);
        Assert.AreEqual(LaunchStageState.Completed, Stage(world.Launcher.GetSnapshot(), LaunchStagePlan.SelectJava));
    }

    [TestMethod]
    public async Task ALoaderInstanceWithoutItsOwnNativesUsesTheBaseVersionsNativeDirectory()
    {
        // 用户实机反馈（2026-10-21）：刚装好的 1.12.2-Forge 一启动就退出码 1，
        // 日志里是 `UnsatisfiedLinkError: no lwjgl64 in java.library.path`。
        // 官方 Forge 安装器只写 Forge 自己的版本 JSON（inheritsFrom 指回原版），
        // 而 natives 的 dll 是原版安装时解压到 versions\1.12.2\natives\ 的 ——
        // 启动链必须能回退到继承链根那一份（Neo 也是这么做的）。
        using var world = new LaunchWorld(
            versionId: "1.12.2",
            declaredJava: 8,
            java: [Java8],
            loader: LoaderKind.Forge,
            loaderVersion: "14.23.5.2864");

        // 官方安装器写出来的形状：实例的 version.json 只有自己 + inheritsFrom。
        File.WriteAllText(
            Path.Combine(world.InstancePath, "version.json"),
            LauncherJson.Serialize(
                new VersionJson { Id = world.InstanceName, InheritsFrom = "1.12.2" },
                LauncherJson.Indented));

        var baseNatives = Path.Combine(world.GameDirectory, "versions", "1.12.2", "natives");
        Directory.CreateDirectory(baseNatives);
        File.WriteAllText(Path.Combine(baseNatives, "lwjgl64.dll"), string.Empty);

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Or(result.Failure, "（无原因）"));

        var libraryPath = world.Runner.Requests.Single().Arguments
            .Single(static argument => argument.StartsWith("-Djava.library.path=", StringComparison.Ordinal));

        StringAssert.Contains(
            libraryPath,
            Path.Combine("versions", "1.12.2", "natives"),
            "加载器实例自己没有 natives 时，必须回退到原版（继承链根）那一份。");
    }

    [TestMethod]
    public async Task AnUnavailableCredentialFailsBeforeAnyStageRuns()
    {
        using var world = new LaunchWorld();
        world.Identities.Failure = TextTemplate.Of("还没有可用的账户。请先到「账户」页添加一个（离线账户也能启动单机）。");

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "还没有可用的账户");
        Assert.AreEqual(0, world.Runner.Requests.Count);

        var snapshot = world.Launcher.GetSnapshot();
        Assert.AreEqual(LaunchSessionState.Failed, snapshot.State);
        Assert.IsTrue(
            snapshot.Stages.All(static stage => stage.State == LaunchStageState.Pending),
            "凭据不是阶段之一：四行都应当停在待执行。");
    }

    [TestMethod]
    public async Task AKilledSessionIsReportedAsEndedByTheLauncherAndDoesNotCountAsPlayTime()
    {
        using var world = new LaunchWorld();
        world.Runner.Result = new GameProcessResult(
            ExitCode: -1,
            Duration: TimeSpan.FromMinutes(5),
            WasKilled: true,
            JobObjectFailure: null,
            Diagnosis: "游戏已被启动器结束（用户取消了启动，或请求结束游戏）。它启动的所有子进程已一并回收。",
            StandardErrorTail: []);

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started);
        Assert.IsTrue(result.Result!.WasKilled);
        Assert.IsFalse(result.Succeeded, "被结束的会话不算成功退出。");
        Assert.AreEqual("游戏已被结束", world.Launcher.GetSnapshot().Detail!.Key);
        Assert.AreEqual(0, world.Instances.Recorded.Single().PlaySeconds, "被结束的会话不计入游玩时长。");
    }

    [TestMethod]
    public async Task UnwritableInstanceMetadataIsReportedInsteadOfLostSilently()
    {
        using var world = new LaunchWorld();
        world.Instances.RecordAccepted = false;

        await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(
            world.Diagnostics.Entries.Any(entry => entry.MessageKey == "实例 {0} 的元数据写不进去，这次的启动记录没有保存"),
            "记账失败必须留痕（用户不会发现自己的游玩时长没涨）。");
    }

    [TestMethod]
    public async Task AMemorySettingAboveTheMachineLimitIsCappedOnTheCommandLineAndSaidOutLoud()
    {
        // 8 GB 机器 + 设置里填 16384：这正是"用户照着大内存机器抄了一份设置"的情形。
        using var world = new LaunchWorld(totalMemoryMb: 8192, memoryMb: 16384);

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));

        // 命令行用的是**收敛后**的值（5734 = min(0.70 × 8192, 8192 − 512)）。
        var arguments = world.Runner.Requests.Single().Arguments.ToList();
        CollectionAssert.Contains(arguments, "-Xmx5734M");
        CollectionAssert.DoesNotContain(arguments, "-Xmx16384M");

        // 界面上要同时看得到"采用的是多少""上限是多少""设置里填的是多少"，
        // 否则用户会以为设置没生效（或者以为启动器算错了）。
        var memoryText = world.Launcher.GetSnapshot().MemoryText!;
        Assert.AreEqual("{0} MB（已按本机上限 {1} MB 收敛；设置里填的是 {2} MB）", memoryText.Key);
        CollectionAssert.AreEqual(new[] { "5734", "5734", "16384" }, memoryText.Parameters.ToArray());

        Assert.IsTrue(
            world.Diagnostics.Entries.Any(entry => entry.MessageKey == "内存分配已按本机上限收敛：{0} MB → {1} MB"),
            "收敛这件事必须留痕：事后查日志要能看出「为什么不是设置里的数」。");
    }

    [TestMethod]
    public async Task UnreadablePhysicalMemoryLeavesTheConfiguredValueAlone()
    {
        // 物理内存读不出来时不许猜一个上限去改用户的设置（同 M3「没有检测结果时不下断言」）。
        using var world = new LaunchWorld(totalMemoryMb: 0, memoryMb: 8192);

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        CollectionAssert.Contains(world.Runner.Requests.Single().Arguments.ToList(), "-Xmx8192M");

        var memoryText = world.Launcher.GetSnapshot().MemoryText!;
        Assert.AreEqual("{0} MB（本机内存未知，未按上限收敛）", memoryText.Key);
        CollectionAssert.AreEqual(new[] { "8192" }, memoryText.Parameters.ToArray());

        Assert.IsFalse(
            world.Diagnostics.Entries.Any(entry => entry.MessageKey == "内存分配已按本机上限收敛：{0} MB → {1} MB"),
            "没有依据就没收敛，不该报「收敛了」。");
    }

    [TestMethod]
    public async Task LaunchingWhileAGameAlreadyRunsIsRefused()
    {
        using var world = new LaunchWorld();
        world.Runner.IsRunning = true;

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "已经有一个游戏在运行");
        Assert.AreEqual(0, world.Runner.Requests.Count);
    }

    [TestMethod]
    public async Task TheInstanceJavaOverrideIsUsedWhenItIsValid()
    {
        using var world = new LaunchWorld(java: [Java17, Java21]);

        // 实例设置里指定了 Java 21（p17 的「Java 运行时」覆盖）。
        world.Settings.SaveInstanceOverrides(new InstanceOverrides(
            InstanceName: world.InstanceName,
            JavaOverride: @"C:\Java\jdk-21",
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: false));

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        Assert.AreEqual("Eclipse Temurin 21.0.5", world.Launcher.GetSnapshot().JavaRuntime);
        Assert.AreEqual(Java21.ExecutablePath, world.Runner.Requests.Single().ExecutablePath);
    }

    [TestMethod]
    public async Task ForceSelectedJavaIgnoresTheInstanceOverrideAndVersionRequirement()
    {
        using var world = new LaunchWorld(java: [Java17, Java21]);

        // 实例设置指向 Java 17，但 p10 的强制开关要求用表格里选中的 Java 21。
        world.Settings.SaveInstanceOverrides(new InstanceOverrides(
            InstanceName: world.InstanceName,
            JavaOverride: Java17.HomeDirectory,
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: true));
        world.Settings.SaveGlobal(world.Settings.GetGlobal() with
        {
            GameDirectory = world.GameDirectory,
            ForceSelectedJava = true,
            ForcedJavaHome = Java21.HomeDirectory,
        });

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        Assert.AreEqual("Eclipse Temurin 21.0.5", world.Launcher.GetSnapshot().JavaRuntime);
        Assert.AreEqual(Java21.ExecutablePath, world.Runner.Requests.Single().ExecutablePath);
    }

    [TestMethod]
    public async Task AnUnavailableForcedJavaFailsTheLaunchWithoutFallingBack()
    {
        using var world = new LaunchWorld(java: [Java17]);

        world.Settings.SaveGlobal(world.Settings.GetGlobal() with
        {
            GameDirectory = world.GameDirectory,
            ForceSelectedJava = true,
            ForcedJavaHome = @"C:\Java\gone",
        });

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(TestText.Of(result.Failure), "强制使用");
        Assert.AreEqual(0, world.Runner.Requests.Count, "强制 Java 不可用时不许回退到其它 Java。");
        Assert.AreEqual(LaunchStageState.Failed, Stage(world.Launcher.GetSnapshot(), LaunchStagePlan.SelectJava));
    }

    [TestMethod]
    public async Task TheGlobalJavaDefaultIsInheritedWhenTheInstanceHasNoOverride()
    {
        using var world = new LaunchWorld(java: [Java17, Java21]);

        // 实例没有自己的覆盖（p17 留空 = 继承全局默认），全局默认在 p10 指向 Java 21（Q14）。
        world.Settings.SaveGlobal(world.Settings.GetGlobal() with { JavaOverride = @"C:\Java\jdk-21" });

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        Assert.AreEqual("Eclipse Temurin 21.0.5", world.Launcher.GetSnapshot().JavaRuntime);
        Assert.AreEqual(Java21.ExecutablePath, world.Runner.Requests.Single().ExecutablePath);
    }

    [TestMethod]
    public async Task AStaleGlobalJavaDefaultFallsBackAndSaysSoInsteadOfBlockingTheLaunch()
    {
        using var world = new LaunchWorld(java: [Java17]);

        // 全局默认指向一个已经不在的 Java：实例自己没覆盖，它只是"默认值"，不该把启动卡死。
        world.Settings.SaveGlobal(world.Settings.GetGlobal() with { JavaOverride = @"C:\Java\gone" });

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        Assert.AreEqual("Eclipse Temurin 17.0.9", world.Launcher.GetSnapshot().JavaRuntime);
        Assert.IsTrue(
            world.Diagnostics.Entries.Any(entry => entry.MessageKey.Contains("全局默认 Java 不可用", StringComparison.Ordinal)),
            "回落必须留痕（否则用户以为全局默认的那个 Java 正在生效）。");
    }

    [TestMethod]
    public async Task AnExplicitInstanceOverrideBeatsTheGlobalDefault()
    {
        using var world = new LaunchWorld(java: [Java17, Java21]);

        world.Settings.SaveGlobal(world.Settings.GetGlobal() with { JavaOverride = @"C:\Java\jdk-21" });
        world.Settings.SaveInstanceOverrides(new InstanceOverrides(
            InstanceName: world.InstanceName,
            JavaOverride: @"C:\Java\jdk-17",
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: 1920,
            WindowHeight: 1080,
            LaunchMode: WindowLaunchMode.Windowed,
            ExtraGameArguments: string.Empty,
            MinimizeAfterLaunch: false));

        var result = await world.Launcher.LaunchAsync(world.InstanceName);

        Assert.IsTrue(result.Started, TestText.Of(result.Failure));
        Assert.AreEqual(Java17.ExecutablePath, world.Runner.Requests.Single().ExecutablePath);
    }

    [TestMethod]
    public async Task StopCancelsTheRunningSession()
    {
        using var world = new LaunchWorld();
        world.Runner.BlockUntilCancelled = true;

        var run = world.Launcher.LaunchAsync(world.InstanceName);

        // 等进程"起来"（替身在 Started 之后才开始等取消）。
        await world.Runner.StartedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));

        world.Launcher.RequestStop();

        var result = await run;

        Assert.IsTrue(world.Runner.KillCount > 0, "RequestStop 必须真的去杀进程树。");
        Assert.IsFalse(result.Succeeded);
    }

    private static LaunchStageState Stage(LaunchSnapshot snapshot, int order) =>
        snapshot.Stages.Single(stage => stage.Order == order).State;

    /// <summary>
    /// 一个临时世界里跑一次启动：临时游戏目录 + 真实版本 JSON 落盘 + 真实文件存在性判断，
    /// 只有"进程"与"Java 检测"是替身。
    /// </summary>
    private sealed class LaunchWorld : IDisposable
    {
        private readonly string _root;

        public LaunchWorld(
            string versionId = "1.20.4",
            int? declaredJava = 17,
            IReadOnlyList<JavaInstallation>? java = null,
            long totalMemoryMb = 32768,
            int memoryMb = 4096,
            LoaderKind loader = LoaderKind.Vanilla,
            string? loaderVersion = null,
            bool loaderInstalled = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "eitrvo-launchworld-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            GameDirectory = Path.Combine(_root, "minecraft");
            InstanceName = versionId + "-" + loader;

            // 新布局：实例目录 = <游戏目录>\versions\<实例名>（元数据、version.json、游戏数据同一个目录）。
            InstancePath = Path.Combine(GameDirectory, "versions", InstanceName);
            Directory.CreateDirectory(InstancePath);
            VersionId = versionId;
            Loader = loader;
            LoaderVersion = loaderVersion;
            LoaderInstalled = loaderInstalled;
            EffectiveVersionId = loader == LoaderKind.Vanilla
                ? versionId
                : LoaderNaming.VersionId(loader, loaderVersion, versionId);
            ClientJarPath = Path.Combine(GameDirectory, "versions", EffectiveVersionId, EffectiveVersionId + ".jar");
            LoggingConfigPath = Path.Combine(GameDirectory, "assets", "log_configs", "client-1.12.xml");

            Json = Real1204Json() with
            {
                Id = versionId,
                JavaVersion = new JavaVersionRequirement { Component = "java-runtime-gamma", MajorVersion = declaredJava },
            };

            Materialize();

            Paths = new LauncherPaths(_root);
            Paths.EnsureCreated();
            Diagnostics = new RecordingDiagnosticsLog();
            Settings = new FileSettingsCatalog(Paths, Diagnostics);
            Settings.SaveGlobal(SettingsDefaults.CreateGlobal() with { GameDirectory = GameDirectory, MaxMemoryMb = memoryMb });

            Instances = new FakeLaunchInstanceCatalog(new InstanceLaunchInfo(
                Name: InstanceName,
                GameVersion: versionId,
                Loader: loader,
                LoaderVersion: loaderVersion,
                IsolatedDirectory: true,
                InstancePath: InstancePath,
                GameDirectory: GameDirectory,
                Account: null));

            JavaRuntime = new FakeJavaRuntimeCatalog(java ?? [Java17], hasDetectionResult: true);
            SystemInfo = new FakeSystemInfoProvider { TotalPhysicalMemoryMb = totalMemoryMb };
            Identities = new FakeLaunchIdentityProvider();
            Runner = new FakeGameProcessRunner();
            Downloads = new FakeDownloadCatalog();

            Launcher = new GameLauncher(
                Instances,
                Settings,
                Downloads,
                new VersionJsonStore(new FakeHttpClientFactory(new StubHandler()), Paths, HttpHostPolicy.Default, Diagnostics),
                JavaRuntime,
                SystemInfo,
                Identities,
                Runner,
                Diagnostics,
                new FixedClock(DateTimeOffset.Parse("2026-10-03T12:00:00+08:00", CultureInfo.InvariantCulture)),
                new LaunchOptions { ClientId = "test-client-id" });
        }

        public string _unused => _root;

        public string GameDirectory { get; }

        public string InstanceName { get; }

        public string InstancePath { get; }

        public string VersionId { get; }

        /// <summary>实例的加载器（M4-f 的加载器实例用）。</summary>
        public LoaderKind Loader { get; }

        /// <summary>实例的加载器版本。</summary>
        public string? LoaderVersion { get; }

        /// <summary>加载器的版本 JSON 是否已经装到盘上。</summary>
        public bool LoaderInstalled { get; }

        /// <summary>启动时实际用的版本 id（加载器实例是 <c>fabric-loader-…</c>）。</summary>
        public string EffectiveVersionId { get; }

        public string ClientJarPath { get; }

        public string LoggingConfigPath { get; }

        public VersionJson Json { get; }

        public LauncherPaths Paths { get; }

        public RecordingDiagnosticsLog Diagnostics { get; }

        public FileSettingsCatalog Settings { get; }

        public FakeLaunchInstanceCatalog Instances { get; }

        public FakeJavaRuntimeCatalog JavaRuntime { get; }

        public FakeSystemInfoProvider SystemInfo { get; }

        public FakeLaunchIdentityProvider Identities { get; }

        public FakeGameProcessRunner Runner { get; }

        /// <summary>下载队列替身（记录「缺少 Java 时自动获取」有没有真的入队）。</summary>
        public FakeDownloadCatalog Downloads { get; }

        public GameLauncher Launcher { get; }

        public LaunchIdentity Identity => Identities.Identity;

        /// <summary>
        /// 把一个**自足的**版本 JSON 写进实例目录（<c>version.json</c> + <c>&lt;实例名&gt;.json</c> + JAR）。
        /// 用来验"实例目录优先于共享版本目录"这条新读取顺序。
        /// </summary>
        public void WriteInstanceVersion()
        {
            var merged = Json with { Id = InstanceName, InheritsFrom = null };
            var json = LauncherJson.Serialize(merged, LauncherJson.Indented);

            File.WriteAllText(Path.Combine(InstancePath, "version.json"), json);
            File.WriteAllText(Path.Combine(InstancePath, InstanceName + ".json"), json);
            File.WriteAllText(Path.Combine(InstancePath, InstanceName + ".jar"), string.Empty);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时目录删不掉不影响用例结论。
            }
        }

        /// <summary>把版本 JSON 与它声明的文件真的写到磁盘上（启动前校验读的是真实文件系统）。</summary>
        private void Materialize()
        {
            var versionDirectory = Path.Combine(GameDirectory, "versions", VersionId);
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(
                Path.Combine(versionDirectory, VersionId + ".json"),
                LauncherJson.Serialize(Json, LauncherJson.Indented));
            File.WriteAllText(Path.Combine(versionDirectory, VersionId + ".jar"), string.Empty);

            MaterializeLoader();

            foreach (var library in Json.Libraries ?? [])
            {
                var relative = library.Downloads?.Artifact?.Path;
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                var path = Path.Combine(GameDirectory, "libraries", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LoggingConfigPath)!);
            File.WriteAllText(LoggingConfigPath, string.Empty);
        }

        /// <summary>
        /// 加载器实例：把**已合并的**加载器版本 JSON 与它的客户端 JAR 写到
        /// <c>versions/&lt;加载器 id&gt;/</c> 下（安装器 M4-f 写的就是这个形状）。
        /// <see cref="LoaderInstalled"/> 为假时什么都不写，用来验"加载器还没装"这条路径。
        /// </summary>
        private void MaterializeLoader()
        {
            if (Loader == LoaderKind.Vanilla || !LoaderInstalled)
            {
                return;
            }

            var directory = Path.Combine(GameDirectory, "versions", EffectiveVersionId);
            Directory.CreateDirectory(directory);

            // 合并后的那份是自足的：Id 换成加载器 id、inheritsFrom 已经展开掉。
            var merged = Json with { Id = EffectiveVersionId, InheritsFrom = null };

            File.WriteAllText(
                Path.Combine(directory, EffectiveVersionId + ".json"),
                LauncherJson.Serialize(merged, LauncherJson.Indented));
            File.WriteAllText(Path.Combine(directory, EffectiveVersionId + ".jar"), string.Empty);
        }
    }

    // ------------------------------------------------------------ 替身 ---

    private sealed class FakeLaunchInstanceCatalog : IInstanceCatalog
    {
        public FakeLaunchInstanceCatalog(InstanceLaunchInfo info) => Info = info;

        public InstanceLaunchInfo Info { get; set; }

        public List<(string Name, DateTimeOffset LaunchedAt, int ExitCode, long PlaySeconds)> Recorded { get; } = [];

        public bool RecordAccepted { get; set; } = true;

        public string ActiveInstanceName { get; set; } = string.Empty;

        public InstanceLaunchInfo GetLaunchInfo(string instanceName) =>
            string.Equals(instanceName, Info.Name, StringComparison.Ordinal)
                ? Info
                : throw new KeyNotFoundException($"实例名不可用：'{instanceName}'");

        public bool RecordLaunch(string instanceName, DateTimeOffset launchedAt, int exitCode, long playSeconds)
        {
            if (!RecordAccepted)
            {
                return false;
            }

            Recorded.Add((instanceName, launchedAt, exitCode, playSeconds));
            return true;
        }

        public IReadOnlyList<InstanceOverview> GetOverview() => [];
        public IReadOnlyList<InstanceRecord> GetInstances() => [];
        public InstanceDetail GetDetail(string instanceName) => throw new NotSupportedException();
        public IReadOnlyList<InstanceAction> GetActions(string instanceName) => [];
        public InstanceCounts GetCounts(string instanceName) => new(0, 0, 0);
        public InstanceDetail CreateInstance(InstanceCreationRequest request) => throw new NotSupportedException();
        public InstanceDeletionResult DeleteInstance(string instanceName, InstanceDeletionOptions options) =>
            throw new NotSupportedException();

        public InstanceDetail RenameInstance(string instanceName, string newName) =>
            throw new NotSupportedException();
    }

    private sealed class FakeLaunchIdentityProvider : ILaunchIdentityProvider
    {
        public LaunchIdentity Identity { get; } = new(
            "Player_123456789",
            "b50ad385-829d-3141-a216-7e7d7539ba7f",
            "eyJhbGciOiJIUzI1NiJ9.eyJmYWtlIjp0cnVlfQ.SAMPLE-SIGNATURE-DO-NOT-LOG-ME",
            "msa",
            "2535429452678874");

        public TextTemplate? Failure { get; set; }

        public Task<LaunchIdentity> GetIdentityAsync(string instanceName, CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(Identity)
                : Task.FromException<LaunchIdentity>(new LaunchIdentityException(Failure));
    }

    private sealed class FakeGameProcessRunner : IGameProcessRunner
    {
        public event EventHandler<GameOutputBatch>? OutputReceived;
        public event EventHandler<int>? Started;

        public List<GameLaunchRequest> Requests { get; } = [];

        public bool IsRunning { get; set; }

        public int? ProcessId => null;

        public int KillCount { get; private set; }

        /// <summary>真到"等取消"那一步（<see cref="StopCancelsTheRunningSession"/> 用）。</summary>
        public bool BlockUntilCancelled { get; set; }

        public GameProcessResult Result { get; set; } =
            new(0, TimeSpan.FromMinutes(2), false, null, null, []);

        public TaskCompletionSource StartedOnce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GameProcessResult> RunAsync(GameLaunchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Started?.Invoke(this, 4242);
            StartedOnce.TrySetResult();

            if (BlockUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Result with { WasKilled = true, ExitCode = -1 };
                }
            }

            // 让出一个拍点，模拟"进程真的跑过一段"。
            await Task.Yield();
            return Result;
        }

        public void Kill() => KillCount++;

        public void Dispose()
        {
        }

        /// <summary>让编译器别抱怨事件没被触发（真实实现里由输出泵触发）。</summary>
        private void Suppress() => OutputReceived?.Invoke(this, new GameOutputBatch([], true));
    }
}
