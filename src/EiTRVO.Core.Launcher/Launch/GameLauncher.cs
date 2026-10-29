using System.Diagnostics;
using System.Globalization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 启动编排（M4-d）：把 M4-a/b/c 的三块拼起来，并把每一步如实反映成阶段与状态。
/// <para>
/// 主流程只依赖接口，进度只经 <see cref="GetSnapshot"/> + <see cref="Changed"/> 暴露 ——
/// 这就是"6 个 UI 回调 → 快照 + 事件"的落点。Neo 的编排里塞了 6 个 <c>Action</c> 回调
/// （以及 <c>MessageBox</c>、<c>Application.Current</c>），于是它只能在真界面上验；
/// 这里"阶段推进、失败拦截、会话记账"全都能在不起真进程的情况下断言。
/// </para>
/// <para>
/// 四步：① 解析实例与版本 → ② 校验本地文件 → ③ 选 Java → ④ 构建命令行并启动。
/// 第 ② 步用 M4-a 的**纯函数**把命令行算一遍，只取它的"缺了哪些文件"——
/// 于是"文件不齐"在启动前就被拦住，而不是等 JVM 抛 NoClassDefFoundError。
/// </para>
/// </summary>
public sealed class GameLauncher : IGameLauncher
{
    /// <summary>保留多少行游戏输出（给实时日志用；p14 现在不显示，M6 接事件表时会用）。</summary>
    public const int RecentOutputLines = 500;

    private readonly IInstanceCatalog _instances;
    private readonly ISettingsCatalog _settings;
    private readonly IDownloadCatalog _downloads;
    private readonly VersionJsonStore _versions;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly ILaunchIdentityProvider _identities;
    private readonly IGameProcessRunner _runner;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly IClock _clock;
    private readonly string _clientId;

    private readonly object _gate = new();
    private readonly Queue<GameOutputLine> _output = new();

    private CancellationTokenSource? _session;
    private List<LaunchStage>? _sessionStages;
    private LaunchSnapshot _snapshot = LaunchSnapshot.Idle;

    /// <summary>构造启动编排。</summary>
    /// <param name="instances">实例目录（含启动要用的原始元数据与会话记账）。</param>
    /// <param name="settings">设置（内存、窗口、附加参数、Java 覆盖）。</param>
    /// <param name="downloads">下载队列（只在「缺少 Java 且设置里允许自动获取」时用它排一个获取运行时的任务）。</param>
    /// <param name="versions">版本 JSON 存储（解析继承链）。</param>
    /// <param name="javaRuntimes">Java 运行时检测结果。</param>
    /// <param name="systemInfo">本机信息（内存分配要按物理内存上限收敛一次，见 M4-e）。</param>
    /// <param name="identities">账户凭据来源（由 App 层实现，内核不引用认证层）。</param>
    /// <param name="runner">进程运行器。</param>
    /// <param name="diagnostics">诊断事件。</param>
    /// <param name="clock">时间源（启动时间要记进实例元数据）。</param>
    /// <param name="options">编排配置（目前只有 Client ID）。</param>
    public GameLauncher(
        IInstanceCatalog instances,
        ISettingsCatalog settings,
        IDownloadCatalog downloads,
        VersionJsonStore versions,
        IJavaRuntimeCatalog javaRuntimes,
        ISystemInfoProvider systemInfo,
        ILaunchIdentityProvider identities,
        IGameProcessRunner runner,
        IDiagnosticsLog diagnostics,
        IClock clock,
        LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(javaRuntimes);
        ArgumentNullException.ThrowIfNull(systemInfo);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _instances = instances;
        _settings = settings;
        _downloads = downloads;
        _versions = versions;
        _javaRuntimes = javaRuntimes;
        _systemInfo = systemInfo;
        _identities = identities;
        _runner = runner;
        _diagnostics = diagnostics;
        _clock = clock;
        _clientId = options.ClientId;

        _runner.OutputReceived += OnOutputReceived;
        _runner.Started += OnProcessStarted;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public LaunchSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GameOutputLine> GetRecentOutput(int maximumCount)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            return [.. _output.TakeLast(maximumCount)];
        }
    }

    /// <inheritdoc />
    public async Task<LaunchOutcome> LaunchAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        if (_runner.IsRunning)
        {
            return Fail(string.Empty, string.Empty, TextTemplate.Of("已经有一个游戏在运行。先结束它，再启动新的。"), report: false);
        }

        List<LaunchStage> stages = NewStages();
        var launchedAt = _clock.Now;
        var stopwatch = Stopwatch.StartNew();

        void Publish(LaunchSessionState state, TextTemplate detail)
        {
            var completed = stages.Count(static stage => stage.State == LaunchStageState.Completed);
            var known = GetSnapshot();

            SetSnapshot(known with
            {
                State = state,
                Detail = detail,
                Stages = [.. stages],
                Percent = completed * 100.0 / LaunchStagePlan.Count,
                Elapsed = stopwatch.Elapsed,
            });
        }

        SetSnapshot(LaunchSnapshot.Idle with
        {
            State = LaunchSessionState.Preparing,
            InstanceName = instanceName,
            Detail = TextTemplate.Of("正在解析实例…"),
            Stages = [.. stages],
        });

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_gate)
        {
            _session = session;

            // 阶段表按会话持有：失败路径（Fail）也要能看到"卡在哪一步"，而它拿不到局部变量。
            _sessionStages = stages;
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.LaunchCore,
            "开始启动游戏：{0}",
            [instanceName],
            instanceName);

        try
        {
            // ---- 前置条件：凭据。它不是"阶段之一"：拿不到就是连门都进不去 ----
            LaunchIdentity identity;

            try
            {
                // 正版账户在这里可能真的去刷一次令牌（异步；界面在那几秒里显示"正在解析实例…"）。
                identity = await _identities.GetIdentityAsync(instanceName, session.Token).ConfigureAwait(false);
            }
            catch (LaunchIdentityException exception)
            {
                return Fail(instanceName, string.Empty, exception.Reason);
            }

            InstanceLaunchInfo info;

            try
            {
                info = _instances.GetLaunchInfo(instanceName);
            }
            catch (KeyNotFoundException)
            {
                // 内核的 KeyNotFoundException 消息是**内部诊断**（不是界面文案）：这里给一句用户看得懂的话。
                return Fail(
                    instanceName,
                    string.Empty,
                    TextTemplate.Of("找不到这个实例（{0}），它可能已经被删除。", instanceName));
            }

            var settings = _settings.GetInstanceOverrides(instanceName);

            // M4-e：用户填的内存量先按本机物理内存的上限收敛一次，再把**实际采用的值**写进 -Xmx。
            // 界面上显示的是实际值 + 为什么，而不是设置里的原值 —— 否则用户会以为设置没生效。
            var memory = MemoryAllocation.Resolve(
                settings.MaxMemoryMb ?? _settings.GetGlobal().MaxMemoryMb,
                _systemInfo.Memory);

            SetSnapshot(GetSnapshot() with { MemoryText = DescribeMemory(memory) });

            if (memory.WasCapped)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Information,
                    DiagnosticsSources.LaunchCore,
                    "内存分配已按本机上限收敛：{0} MB → {1} MB",
                    [
                        memory.RequestedMb.ToString(CultureInfo.InvariantCulture),
                        memory.EffectiveMb.ToString(CultureInfo.InvariantCulture),
                    ],
                    instanceName);
            }

            // ---- 阶段 1：解析实例与版本 ----
            BeginStage(stages, LaunchStagePlan.ResolveVersion, TextTemplate.Of("读取 instance.json 与版本 JSON"));
            Publish(LaunchSessionState.Preparing, TextTemplate.Of("正在解析版本与继承链…"));

            // 新布局：一个实例 = <游戏目录>\versions\<实例名>\，启动读那个目录里的 version.json
            // （Neo 26.1 的版本目录约定）。读到它就说明这是"装进实例目录"的版本，版本 id 就是实例名。
            // 读不到才回退到旧布局（按加载器命名规则算出版本 id，去共享 versions\<id> 找）。
            var instanceVersion = _versions.TryReadInstalled(info.GameDirectory, instanceName);

            string? versionId;
            TextTemplate? versionFailure;

            if (instanceVersion is not null)
            {
                versionId = instanceName;
                versionFailure = null;
            }
            else
            {
                (versionId, versionFailure) = VersionIdFor(info);
            }

            if (versionId is null)
            {
                FailStage(stages, LaunchStagePlan.ResolveVersion, versionFailure!);
                return Fail(instanceName, string.Empty, versionFailure!);
            }

            // 加载器版本的 JSON 是安装器写下来的：没有它就没有"这个版本"，而 M4-d 之前
            // 这里是"非原版一律拒绝"，用户看不到区别。如实说清"缺哪一步"（去 p06 装）。
            if (info.Loader != LoaderKind.Vanilla
                && instanceVersion is null
                && _versions.TryReadInstalled(info.GameDirectory, versionId) is null)
            {
                var reason = TextTemplate.Of(
                    "这个实例的 {0} 还没有安装到本地（实例目录里缺 version.json）。请先在「下载与队列」里安装这个加载器。",
                    // 参数是**排障数据**：DataDisplay 是 ASCII 且两种语言一致（§5.3 第 ③ / ⑥ 条）。
                    InstanceNaming.DataDisplay(info.Loader, info.LoaderVersion));

                FailStage(stages, LaunchStagePlan.ResolveVersion, reason);
                return Fail(instanceName, versionId, reason);
            }

            ResolvedVersion resolved;

            try
            {
                resolved = _versions.Resolve(info.GameDirectory, versionId);
            }
            catch (VersionResolutionException exception)
            {
                // 解析失败的细节（成环 / 层数过深 / 缺父版本）都在异常自己的键里（M6-e-2b 起它也是模板）。
                FailStage(stages, LaunchStagePlan.ResolveVersion, exception.Reason);
                return Fail(instanceName, versionId, exception.Reason);
            }

            var declaredJava = resolved.Merged.JavaVersion?.MajorVersion;
            CompleteStage(
                stages,
                LaunchStagePlan.ResolveVersion,
                resolved.Chain.Count > 1
                    ? TextTemplate.Of(
                        "版本 {0}（继承链 {1} 段）",
                        versionId,
                        resolved.Chain.Count.ToString(CultureInfo.InvariantCulture))
                    : TextTemplate.Of("版本 {0}", versionId));
            Publish(LaunchSessionState.Preparing, TextTemplate.Of("已解析版本 {0}", versionId));
            SetSnapshot(GetSnapshot() with { VersionId = versionId });

            // ---- 阶段 2：校验本地文件 ----
            BeginStage(stages, LaunchStagePlan.VerifyFiles, TextTemplate.Of("检查客户端 JAR、库与 natives"));
            Publish(LaunchSessionState.Preparing, TextTemplate.Of("正在校验本地文件…"));

            // Java 规则表的回退要按**游戏版本**判（versionId 在实例布局下是实例名，不含版本号）。
            var javaRuleVersion = string.IsNullOrWhiteSpace(info.GameVersion) ? versionId : info.GameVersion;
            var requiredJava = declaredJava is > 0
                ? declaredJava.Value
                : JavaRequirement.RequiredMajor(javaRuleVersion);

            var nativesDirectory = ResolveNativesDirectory(info, versionId, resolved);
            var clientJarPath = ResolveClientJar(info, versionId, resolved);

            var request = new LaunchArgumentRequest(
                Version: resolved,
                VersionId: versionId,
                GameDirectory: info.GameDirectory,
                InstanceDirectory: info.InstancePath,
                NativesDirectory: nativesDirectory,
                Identity: identity,
                MemoryMb: memory.EffectiveMb,
                JavaMajorVersion: requiredJava,
                ClientId: _clientId,
                Platform: GamePlatform.Current,
                FileExists: File.Exists)
            {
                ClientJarPath = clientJarPath,
                Width = settings.WindowWidth,
                Height = settings.WindowHeight,
                ExtraJvmArguments = settings.JvmArguments ?? _settings.GetGlobal().JvmArguments,
                ExtraGameArguments = settings.ExtraGameArguments,
            };

            var preview = LaunchArgumentBuilder.Build(request);
            var missing = preview.ProblemsOf(LaunchProblemKind.MissingFile)
                .Where(static problem => problem.BlocksLaunch)
                .ToList();

            if (missing.Count > 0)
            {
                var reason = TextTemplate.Of(
                    "这个实例的文件不完整：缺 {0} 个（例如 {1}）。请先在「下载与队列」里安装这个版本。",
                    missing.Count.ToString(CultureInfo.InvariantCulture),
                    missing[0].Subject);

                // 阶段明细给一句**用户看得懂**的话：LaunchProblem.Reason 是内核的诊断文本
                // （含参数名、路径等），它不进资源表也不该出现在界面上。
                FailStage(
                    stages,
                    LaunchStagePlan.VerifyFiles,
                    TextTemplate.Of("缺少必需的文件（例如 {0}）。", missing[0].Subject));
                return Fail(instanceName, versionId, reason);
            }

            CompleteStage(
                stages,
                LaunchStagePlan.VerifyFiles,
                TextTemplate.Of("{0} 个文件已就位", preview.Classpath.Count.ToString(CultureInfo.InvariantCulture)));
            Publish(LaunchSessionState.Preparing, TextTemplate.Of("本地文件已就位"));
            SetSnapshot(GetSnapshot() with { VerifiedFileCount = preview.Classpath.Count });

            // ---- 阶段 3：选 Java ----
            BeginStage(stages, LaunchStagePlan.SelectJava, TextTemplate.Of("这个版本需要 Java {0}", requiredJava.ToString(CultureInfo.InvariantCulture)));
            Publish(LaunchSessionState.Preparing, TextTemplate.Of("正在选择 Java 运行时…"));

            var globalSettings = _settings.GetGlobal();
            JavaCheck check;

            if (globalSettings.ForceSelectedJava)
            {
                // 用户明确要求「无论版本号与实例设置如何，都用选定的那个 Java」：
                // 这里不查版本区间，也不回退；找不到这个运行时就直接报错。
                var forced = JavaPreflight.FindByPath(
                    _javaRuntimes.GetInstallations(),
                    globalSettings.ForcedJavaHome);

                if (forced is null)
                {
                    var reason = TextTemplate.Of(
                        "强制使用的 Java 运行时不可用：{0}。请到「Java 与设置」页重新选择。",
                        globalSettings.ForcedJavaHome ?? string.Empty);
                    FailStage(stages, LaunchStagePlan.SelectJava, reason);
                    return Fail(instanceName, versionId, reason);
                }

                check = new JavaCheck(
                    JavaCheckStatus.Satisfied,
                    forced.MajorVersion,
                    forced.MajorVersion,
                    forced);
            }
            else
            {
                check = JavaPreflight.Check(
                    versionId,
                    EffectiveJavaOverride(settings, out var overrideScope),
                    _javaRuntimes.GetInstallations(),
                    _javaRuntimes.HasDetectionResult,
                    declaredJava,
                    overrideScope);
            }

            if (check.FellBackFromOverride)
            {
                // Q14：全局默认值只是"默认"，它失效时启动已回落到自动匹配 —— 必须留痕，
                // 否则用户会以为全局默认的那个 Java 正在生效（与 p10/p17 的提示同一条事实）。
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.JavaRuntime,
                    "全局默认 Java 不可用（{0}），已回落到自动匹配（{1}）",
                    [
                        globalSettings.JavaOverride ?? string.Empty,
                        check.Selected?.VersionText ?? string.Empty,
                    ]);
            }

            if (!check.IsSatisfied || check.Selected is null)
            {
                // "顺手排一个自动获取任务"不能让内核把两句话拼起来（拼句子是 App 的事）：
                // 排进去之后改用**另一个键**（同一条原因 + 一句说明）。
                var autoFetch = TryAutoFetchJava(javaRuleVersion, check);
                var reason = DescribeJavaFailure(check, autoFetch);

                FailStage(stages, LaunchStagePlan.SelectJava, reason);
                return Fail(instanceName, versionId, reason);
            }

            var java = check.Selected;
            var javaText = string.IsNullOrWhiteSpace(java.Vendor)
                ? java.VersionText
                : $"{java.Vendor} {java.VersionText}";

            // javaText 是**纯数据**（厂商 + 版本串），没有可翻译的词：用 `{0}` 这个键承载它。
            CompleteStage(stages, LaunchStagePlan.SelectJava, TextTemplate.Of("{0}", javaText));
            Publish(LaunchSessionState.Preparing, TextTemplate.Of("已选择 Java {0}", java.MajorVersion.ToString(CultureInfo.InvariantCulture)));
            SetSnapshot(GetSnapshot() with { JavaRuntime = javaText });

            // ---- 阶段 4：构建参数并启动进程 ----
            BeginStage(stages, LaunchStagePlan.StartProcess, TextTemplate.Of("构建命令行并启动进程"));
            Publish(LaunchSessionState.Preparing, TextTemplate.Of("正在构建命令行…"));

            // 用**选中的** Java 版本重建一次：命令行里的 Java 8 裁剪、`-Xss` 之类都取决于它。
            var commandLine = LaunchArgumentBuilder.Build(request with { JavaMajorVersion = java.MajorVersion });
            var gameRequest = new GameLaunchRequest(java.ExecutablePath, commandLine.Arguments, info.InstancePath)
            {
                InstanceName = instanceName,
                VersionId = versionId,
                Secrets = [identity.AccessToken, identity.Uuid, identity.UuidWithoutDashes],
            };

            Publish(LaunchSessionState.Preparing, TextTemplate.Of("正在启动进程…"));

            var result = await _runner.RunAsync(gameRequest, session.Token).ConfigureAwait(false);

            CompleteStage(
                stages,
                LaunchStagePlan.StartProcess,
                TextTemplate.Of("退出码 {0}", result.ExitCode.ToString(CultureInfo.InvariantCulture)));

            var exitDetail = result.WasKilled
                ? TextTemplate.Of("游戏已被结束")
                : TextTemplate.Of(
                    "游戏已退出（退出码 {0}）",
                    result.ExitCode.ToString(CultureInfo.InvariantCulture));

            SetSnapshot(GetSnapshot() with
            {
                State = LaunchSessionState.Completed,
                Detail = exitDetail,
                Stages = [.. stages],
                Percent = 100,
                Elapsed = stopwatch.Elapsed,
                ProcessId = null,
                Result = result,
                Failure = null,
            });

            RecordSession(info, launchedAt, result);
            RaiseChanged();

            return new LaunchOutcome(Started: true, LaunchSessionState.Completed, Failure: null, result);
        }
        catch (OperationCanceledException)
        {
            // 用户取消启动、或启动器正在退出：**不是失败**。
            var detail = TextTemplate.Of("启动已取消");

            SetSnapshot(GetSnapshot() with
            {
                State = LaunchSessionState.Completed,
                Detail = detail,
                Elapsed = stopwatch.Elapsed,
                ProcessId = null,
            });

            return new LaunchOutcome(Started: false, LaunchSessionState.Completed, Failure: null, Result: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 兜底：任何漏出来的异常都必须让会话进入终态，否则界面会一直显示"正在启动"。
            return Fail(
                instanceName,
                GetSnapshot().VersionId,
                TextTemplate.Of("启动过程中出现未预期的错误（{0}），这次没有启动成功。", exception.GetType().Name));
        }
        finally
        {
            lock (_gate)
            {
                _session = null;
                _sessionStages = null;
            }
        }
    }

    /// <inheritdoc />
    public void RequestStop()
    {
        CancellationTokenSource? session;

        lock (_gate)
        {
            session = _session;
        }

        // 先取消（让等进程的那一段走取消分支、并如实报"被启动器结束"），再直接杀进程树兜底。
        session?.Cancel();
        _runner.Kill();
    }

    private static List<LaunchStage> NewStages() =>
    [
        .. Enumerable.Range(1, LaunchStagePlan.Count).Select(order =>
            new LaunchStage(
                order,
                LaunchStagePlan.Title(order),
                TextTemplate.Of("等待中"),
                LaunchStageState.Pending,
                IsActive: false)),
    ];

    private static void BeginStage(List<LaunchStage> stages, int order, TextTemplate detail) =>
        Replace(stages, order, LaunchStageState.Running, detail);

    private static void CompleteStage(List<LaunchStage> stages, int order, TextTemplate detail) =>
        Replace(stages, order, LaunchStageState.Completed, detail);

    private static void FailStage(List<LaunchStage> stages, int order, TextTemplate detail) =>
        Replace(stages, order, LaunchStageState.Failed, detail);

    private static void Replace(List<LaunchStage> stages, int order, LaunchStageState state, TextTemplate detail)
    {
        var index = stages.FindIndex(stage => stage.Order == order);

        if (index >= 0)
        {
            stages[index] = stages[index] with
            {
                State = state,
                Detail = detail,
                IsActive = state == LaunchStageState.Running,
            };
        }
    }

    /// <summary>
    /// p14「内存分配」那一行的文本：**实际采用**的值，并在它与设置不一致时说明原因。
    /// <para>
    /// 三种写法对应三种事实：原样采用、被本机上限收敛、物理内存读不出来（没依据就不动用户的设置）。
    /// 后两种必须写出来 —— 否则"设置里 16384、这里 5734"看起来像启动器算错了。
    /// </para>
    /// </summary>
    private static TextTemplate DescribeMemory(MemoryAllocation memory)
    {
        var effective = memory.EffectiveMb.ToString(CultureInfo.InvariantCulture);

        if (memory.LimitMb is null)
        {
            return TextTemplate.Of("{0} MB（本机内存未知，未按上限收敛）", effective);
        }

        return memory.WasCapped
            ? TextTemplate.Of(
                "{0} MB（已按本机上限 {1} MB 收敛；设置里填的是 {2} MB）",
                effective,
                memory.LimitMb.Value.ToString(CultureInfo.InvariantCulture),
                memory.RequestedMb.ToString(CultureInfo.InvariantCulture))
            : TextTemplate.Of("{0} MB", effective);
    }

    /// <summary>
    /// 客户端 JAR 的实际位置：实例目录优先（新布局），其次共享版本目录，最后父版本目录
    /// （Forge / NeoForge / OptiFine 的官方安装器不生成自己的客户端 JAR，启动时用原版那个）。
    /// </summary>
    /// <param name="info">实例元数据。</param>
    /// <param name="versionId">本次启动的版本 id（新布局下是实例名）。</param>
    /// <param name="resolved">已解析的版本（<see cref="ResolvedVersion.BaseVersionId"/> 是链根原版）。</param>
    /// <returns>存在的那个 JAR 路径；一个都不存在时返回实例目录里的候选路径（交给启动前校验如实报告）。</returns>
    private static string ResolveClientJar(InstanceLaunchInfo info, string versionId, ResolvedVersion resolved)
    {
        string[] candidates =
        [
            Path.Combine(info.InstancePath, versionId + ".jar"),
            Path.Combine(info.InstancePath, Path.GetFileName(info.InstancePath) + ".jar"),
            Path.Combine(info.GameDirectory, "versions", versionId, versionId + ".jar"),
            Path.Combine(info.GameDirectory, "versions", resolved.BaseVersionId, resolved.BaseVersionId + ".jar"),
            Path.Combine(info.GameDirectory, "versions", info.GameVersion, info.GameVersion + ".jar"),
        ];

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// natives 目录：实例目录里的 <c>natives\</c> 优先（新布局），然后依次回退到
    /// **继承链根**（原版）的目录与实例元数据记下的游戏版本目录。
    /// </summary>
    /// <remarks>
    /// 最后两级回退不是装饰：官方 Forge / NeoForge / OptiFine 安装器只写**自己**的版本 JSON
    /// （<c>inheritsFrom</c> 指回原版），natives 归档里的 dll 是原版安装时解压到
    /// <c>versions\&lt;原版&gt;\natives\</c> 的。以前这里只找实例目录与"加载器 id 目录"，
    /// 于是一个刚装好的 1.12.2-Forge 实例会拿到一个不存在的路径，游戏报
    /// <c>UnsatisfiedLinkError: no lwjgl64 in java.library.path</c>（退出码 1）——
    /// 而同样的 natives 就在隔壁原版目录里躺着。Neo 也是这么做的（加载器实例用
    /// <c>inheritsFrom</c> 那一版的 natives 目录）。
    /// <para>
    /// 合并过的加载器版本（Fabric / Quilt 走我们自己的安装器，<c>version.json</c> 里没有
    /// <c>inheritsFrom</c>）由第二级兜底：用实例元数据记下的 <see cref="InstanceLaunchInfo.GameVersion"/>。
    /// </para>
    /// </remarks>
    /// <param name="info">实例元数据。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="resolved">解析后的版本（含继承链根）。</param>
    /// <returns>目录路径。</returns>
    private static string ResolveNativesDirectory(InstanceLaunchInfo info, string versionId, ResolvedVersion resolved)
    {
        var instanceNatives = Path.Combine(info.InstancePath, "natives");
        if (Directory.Exists(instanceNatives))
        {
            return instanceNatives;
        }

        // 继承链根（原版）的 natives：Forge / NeoForge / OptiFine 的版本 JSON 只写自己那一份。
        if (!string.Equals(resolved.BaseVersionId, versionId, StringComparison.OrdinalIgnoreCase))
        {
            var baseNatives = Path.Combine(info.GameDirectory, "versions", resolved.BaseVersionId, "natives");
            if (Directory.Exists(baseNatives))
            {
                return baseNatives;
            }
        }

        // 合并过的加载器版本没有 inheritsFrom：实例元数据里记的游戏版本是最后一根线索。
        if (!string.IsNullOrWhiteSpace(info.GameVersion)
            && !string.Equals(info.GameVersion, versionId, StringComparison.OrdinalIgnoreCase))
        {
            var gameNatives = Path.Combine(info.GameDirectory, "versions", info.GameVersion, "natives");
            if (Directory.Exists(gameNatives))
            {
                return gameNatives;
            }
        }

        return Path.Combine(info.GameDirectory, "versions", versionId, "natives");
    }

    /// <summary>
    /// 要启动的版本 id / 为什么启动不了。
    /// <para>
    /// <b>五种加载器从 M4-f / M4-g / M4-i 起都可以启动</b>：Fabric / Quilt 的 id 由纯 JSON profile 生成，
    /// Forge / NeoForge 的 id 就是官方安装器**实际写出来的那个目录名**
    /// （<c>1.20.4-forge-49.0.30</c> / <c>neoforge-20.4.237</c>），
    /// OptiFine 的 id 同样是官方安装器写出的目录名（<c>1.20.1-OptiFine_HD_U_I6</c>）——
    /// 三者共用 <see cref="LoaderNaming.VersionId"/> 这一份规则。
    /// </para>
    /// <para>
    /// 这是**旧布局**的回退路径：新布局的实例目录里有 <c>version.json</c>，版本 id 直接用实例名，
    /// 不会走到这里。保留它是因为升级前安装的实例仍然只有共享目录里的 JSON。
    /// </para>
    /// </summary>
    private static (string? VersionId, TextTemplate? Failure) VersionIdFor(InstanceLaunchInfo info)
    {
        if (info.Loader != LoaderKind.Vanilla)
        {
            if (!LoaderNaming.IsProfileLoader(info.Loader)
                && !LoaderNaming.IsInstallerLoader(info.Loader)
                && !LoaderNaming.IsOptiFine(info.Loader))
            {
                return (null, TextTemplate.Of(
                    "这个实例要运行 {0}，但这个加载器还没有接上安装/启动路径。现在可以用原版、Fabric、Quilt、Forge、NeoForge 或 OptiFine 启动 —— 悄悄按原版启动会让模组全都不生效，所以这里明确拒绝。",
                    InstanceNaming.DataDisplay(info.Loader, info.LoaderVersion)));
            }

            if (string.IsNullOrWhiteSpace(info.LoaderVersion))
            {
                return (null, TextTemplate.Of(
                    "这个实例选了 {0}，但元数据里没有记下加载器版本，无法确定要启动哪一个。请到「实例设置」里重新选一次。",
                    InstanceNaming.DataDisplay(info.Loader)));
            }

            if (string.IsNullOrWhiteSpace(info.GameVersion))
            {
                return (null, TextTemplate.Of("这个实例还没有指定游戏版本。请到「实例设置」里选一个版本，或先安装一个版本。"));
            }

            return (LoaderNaming.VersionId(info.Loader, info.LoaderVersion, info.GameVersion), null);
        }

        if (string.IsNullOrWhiteSpace(info.GameVersion))
        {
            return (null, TextTemplate.Of("这个实例还没有指定游戏版本。请到「实例设置」里选一个版本，或先安装一个版本。"));
        }

        return (info.GameVersion.Trim(), null);
    }

    /// <summary>
    /// Java 检查失败的那句话。最后一条按 <paramref name="autoFetchStarted"/> 分成**两个键**：
    /// "顺手排一个获取运行时的任务"不该让内核把两句话拼起来（拼句子是 App 的事）。
    /// </summary>
    /// <param name="check">Java 检查结论。</param>
    /// <param name="autoFetchStarted">是否已经按设置排了"获取官方运行时"的任务。</param>
    /// <returns>失败原因。</returns>
    private static TextTemplate DescribeJavaFailure(JavaCheck check, bool autoFetchStarted)
    {
        var required = check.RequiredMajor.ToString(CultureInfo.InvariantCulture);

        return check.Status switch
        {
            JavaCheckStatus.Unknown =>
                TextTemplate.Of("还没有检测到本机的 Java 运行时。请先到「Java 与设置」页里重新检测一次（还没查过就不能判定缺少 Java）。"),
            JavaCheckStatus.OverrideInvalid =>
                TextTemplate.Of("这个实例指定的 Java 运行时已经不存在了。请到「实例设置」里重新选一个，或改为自动匹配。"),
            _ when check.Selected is not null => TextTemplate.Of(
                "这个实例指定的 Java {0} 不能运行该版本（需要 Java {1}）。",
                check.Selected.MajorVersion.ToString(CultureInfo.InvariantCulture),
                required),
            _ when autoFetchStarted => TextTemplate.Of(
                "本机没有能运行这个版本的 Java（需要 Java {0}）。已按设置开始下载 Mojang 官方运行时，下载完成后可以再启动一次。",
                required),
            _ => TextTemplate.Of(
                "本机没有能运行这个版本的 Java（需要 Java {0}）。可以到「Java 与设置」页里获取官方运行时。",
                required),
        };
    }

    /// <summary>
    /// 若设置里开着「缺失时自动下载 Mojang 官方 JRE」，把获取运行时的任务排进队列。
    /// </summary>
    /// <param name="versionId">要启动的版本 id（它声明的 <c>javaVersion</c> 决定取哪一代）。</param>
    /// <param name="check">Java 检查结论。</param>
    /// <returns>是否真的排进队列（调用方据此挑那句"已按设置开始下载…"的文案）。</returns>
    /// <remarks>
    /// 触发条件刻意很窄，两条都来自已经确立的纪律：
    /// <list type="bullet">
    /// <item>只在 <see cref="JavaCheckStatus.Missing"/> **且没有选中任何运行时**时触发 ——
    /// 「还没检测过」不能当成「缺少 Java」（M3），而"用户指定的那个 Java 版本不对"下载一个新的也没用
    /// （覆盖项优先级更高，装了也不会被选中）；</item>
    /// <item>只有设置里开着才触发：默认值是开，但那是一个用户看得见、也能关掉的开关，
    /// 不该由一次失败的启动**静默**开始一次约 100 MB 的下载而不告诉他。</item>
    /// </list>
    /// 入队是"立即返回"的（进度在 p06 看），因此这一步不会让启动多等哪怕一秒；
    /// 排不进去（演示数据源 / 队列异常）也**不改变**启动失败的原因 —— 顺手做的事不该掩盖真正的问题。
    /// </remarks>
    private bool TryAutoFetchJava(string versionId, JavaCheck check)
    {
        if (check.Status != JavaCheckStatus.Missing || check.Selected is not null)
        {
            return false;
        }

        if (!_settings.GetGlobal().AutoFetchJava)
        {
            return false;
        }

        try
        {
            _downloads.EnqueueJavaRuntime(new JavaRuntimeRequest(versionId));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ObjectDisposedException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.JavaRuntime,
                "想自动获取官方运行时（{0}），但队列用不了（{1}），这次没有排队",
                [versionId, ex.GetType().Name],
                versionId);

            return false;
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.JavaRuntime,
            "{0} 缺少 Java（需要 {1}），已按设置开始下载官方运行时",
            [versionId, check.RequiredMajor.ToString(CultureInfo.InvariantCulture)],
            versionId);

        return true;
    }

    /// <summary>把这次会话写回实例元数据（启动时间、次数、退出码、够长的游玩时长）。</summary>
    private void RecordSession(InstanceLaunchInfo info, DateTimeOffset launchedAt, GameProcessResult result)
    {
        var playSeconds = result.CountsAsPlaySession ? (long)result.Duration.TotalSeconds : 0;

        if (_instances.RecordLaunch(info.Name, launchedAt, result.ExitCode, playSeconds))
        {
            return;
        }

        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.LaunchCore,
            "实例 {0} 的元数据写不进去，这次的启动记录没有保存",
            [info.Name],
            info.Name);
    }

    private LaunchOutcome Fail(string instanceName, string versionId, TextTemplate reason, bool report = true)
    {
        List<LaunchStage>? stages;

        lock (_gate)
        {
            stages = _sessionStages;
        }

        SetSnapshot(GetSnapshot() with
        {
            State = LaunchSessionState.Failed,
            InstanceName = instanceName.Length > 0 ? instanceName : GetSnapshot().InstanceName,
            VersionId = versionId.Length > 0 ? versionId : GetSnapshot().VersionId,
            Detail = reason,

            // 失败时也要把阶段表带上：**卡在哪一步**是用户最需要知道的事，
            // 只改 State 会让界面停在"第 2 步进行中"，看起来像还在跑。
            Stages = stages is null ? GetSnapshot().Stages : [.. stages],
            ProcessId = null,
            Failure = reason,
        });

        if (report)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.LaunchCore,
                "启动失败（{0}）：{1}",
                [instanceName, reason.Key],
                instanceName.Length > 0 ? instanceName : null);
        }

        return new LaunchOutcome(Started: false, LaunchSessionState.Failed, reason, Result: null);
    }

    private void SetSnapshot(LaunchSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 进程起来了：把会话切到"正在运行"，并**在启动这一刻把进度走完**。
    /// </summary>
    /// <remarks>
    /// 进度条描述的是"启动有没有完成"，不是"游戏玩了多久"：等进程退出才走满，
    /// 用户会在整段游戏时间里看到 75%，退出后才看见 100% —— 那已经与"正在启动"无关了。
    /// 因此这里把第 4 阶段标成已完成、 Percent 置 100；退出时只补一条"游戏已退出（退出码）"。
    /// 晚到的 pid（会话已经结束）不改状态。
    /// </remarks>
    private void OnProcessStarted(object? sender, int processId)
    {
        var snapshot = GetSnapshot();

        if (snapshot.State != LaunchSessionState.Preparing)
        {
            return;
        }

        List<LaunchStage>? stages;
        lock (_gate)
        {
            stages = _sessionStages;
        }

        if (stages is null)
        {
            return;
        }

        CompleteStage(stages, LaunchStagePlan.StartProcess, TextTemplate.Of("游戏已启动"));

        SetSnapshot(snapshot with
        {
            State = LaunchSessionState.Running,
            ProcessId = processId,
            Detail = TextTemplate.Of("游戏已启动"),
            Stages = [.. stages],
            Percent = 100,
        });
    }

    private void OnOutputReceived(object? sender, GameOutputBatch batch)
    {
        lock (_gate)
        {
            foreach (var line in batch.Lines)
            {
                _output.Enqueue(line);

                while (_output.Count > RecentOutputLines)
                {
                    _output.Dequeue();
                }
            }
        }

        // 输出批次刻意**不**触发 Changed：p14 现在不显示它，
        // 而每 200ms 一次属性通知风暴会让界面白忙（M6 接实时日志时再决定怎么节流）。
    }

    /// <summary>
    /// 实际生效的 Java 覆盖：实例自己的优先，其次全局默认（Q14）。
    /// </summary>
    /// <param name="settings">该实例的覆盖项。</param>
    /// <param name="scope">输出：这个值来自哪一层。</param>
    /// <returns>覆盖值；两层都没有时为 <see langword="null"/>（自动匹配）。</returns>
    private string? EffectiveJavaOverride(InstanceOverrides settings, out JavaOverrideScope scope)
    {
        if (!string.IsNullOrWhiteSpace(settings.JavaOverride))
        {
            scope = JavaOverrideScope.Instance;
            return settings.JavaOverride;
        }

        var global = _settings.GetGlobal().JavaOverride;

        if (!string.IsNullOrWhiteSpace(global))
        {
            scope = JavaOverrideScope.Global;
            return global;
        }

        scope = JavaOverrideScope.None;
        return null;
    }
}
