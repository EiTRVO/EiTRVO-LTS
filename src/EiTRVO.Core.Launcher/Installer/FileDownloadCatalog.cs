using System.Globalization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Content.Modpack;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// <see cref="IDownloadCatalog"/> 的真实实现：一条**安装任务队列** + 它的进度快照。
/// <para>
/// 形状是刻意这样的：入队立即返回（一次原版安装要几分钟，让 ViewModel await 它等于把界面钉在向导页上），
/// 后台单worker 依次执行，界面在导航到 p06 时读快照。因此这里的读取端全是同步的，
/// 写入端在后台线程 —— 所有共享状态都在锁里，变更后发一次 <see cref="Changed"/>。
/// </para>
/// </summary>
public sealed class FileDownloadCatalog : IDownloadCatalog
{
    /// <summary>保留多少个已结束的任务（队列列表不该无限增长）。</summary>
    private const int FinishedHistoryLimit = 20;

    /// <summary>同时只跑一个安装任务。</summary>
    /// <remarks>
    /// 不是并发能力不足，而是**刻意**的：每个任务自己有几十路并发下载（见「并发下载数」设置），
    /// 同时跑两个安装会让磁盘随机写翻倍、速度反而更差，而任务之间也没有优先级可言。
    /// </remarks>
    private const int MaxConcurrentJobs = 1;

    /// <summary>
    /// 空闲时的进度快照：没有任务在跑时界面（p06）显示的就是它。
    /// </summary>
    /// <remarks>
    /// 文本格用「—」而不是空串：这一页的"没有值"一直是这个写法（同表的「当前速度」在
    /// 下载停下时也是「—」，见 <c>FormatSpeed</c>），空着的格子看起来像读取失败。
    /// </remarks>
    private static readonly DownloadProgress Idle = new(
        CurrentTask: TextTemplate.Of("—"),
        Percent: 0,
        AmountText: "—",
        SpeedText: "—",
        FilesText: "—",
        RemainingText: TextTemplate.Of("—"),
        ConnectionsText: "—",
        DataSource: "—");

    private readonly VanillaInstaller _installer;
    private readonly LoaderInstaller _loaderInstaller;
    private readonly ForgeInstaller _forgeInstaller;
    private readonly OptiFineInstaller _optiFineInstaller;
    private readonly JavaRuntimeInstaller _runtimeInstaller;
    private readonly ContentInstaller _contentInstaller;
    private readonly OptionalComponentInstaller _optionalComponents;
    private readonly MrpackInstaller _mrpackInstaller;
    private readonly ModrinthCatalog _modrinthCatalog;
    private readonly WorldBackupInstaller _worldBackupInstaller;
    private readonly IInstanceCatalog _instances;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly ISettingsCatalog _settings;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;

    private readonly object _gate = new();
    private readonly List<QueuedJob> _jobs = [];
    private readonly HashSet<InstallStageKind> _completedStages = [];

    private FileTransferProgress? _progress;

    private string _dataSource = string.Empty;
    private int _connections;
    private InstallStageKind? _activeStage;
    private int _workerRunning;
    private TaskCompletionSource? _idle;

    /// <summary>构造下载目录。</summary>
    /// <param name="installer">原版安装器。</param>
    /// <param name="loaderInstaller">Fabric / Quilt 安装器（M4-f）。</param>
    /// <param name="forgeInstaller">Forge / NeoForge 安装器（M4-g，跑官方安装器）。</param>
    /// <param name="optiFineInstaller">OptiFine 安装器（M4-i，沙箱里跑官方安装器再搬运）。</param>
    /// <param name="runtimeInstaller">Mojang 官方 Java 运行时安装器（§12.7）。</param>
    /// <param name="contentInstaller">Modrinth 内容安装器（M7-d：依赖递归 + 更新替换）。</param>
    /// <param name="optionalComponents">加载器安装后的可选组件（Forge→OptiFine、Fabric→Fabric API）。</param>
    /// <param name="mrpackInstaller">整合包导入器（M7-e-2：扫描 + 清单 + 覆盖层）。</param>
    /// <param name="modrinthCatalog">Modrinth 目录（直接从 Modrinth 安装整合包时先下载 .mrpack）。</param>
    /// <param name="worldBackupInstaller">存档备份 / 恢复器（M7-f：备份、复核、回收目录）。</param>
    /// <param name="instances">实例目录（整合包要先建实例，内容写进**那个实例**的目录）。</param>
    /// <param name="javaRuntimes">Java 运行时检测（装完运行时之后要强制重检一次）。</param>
    /// <param name="settings">设置（游戏目录、下载源、并发数）。</param>
    /// <param name="clock">时间源（排队时长要用真实时钟）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public FileDownloadCatalog(
        VanillaInstaller installer,
        LoaderInstaller loaderInstaller,
        ForgeInstaller forgeInstaller,
        OptiFineInstaller optiFineInstaller,
        JavaRuntimeInstaller runtimeInstaller,
        ContentInstaller contentInstaller,
        OptionalComponentInstaller optionalComponents,
        MrpackInstaller mrpackInstaller,
        ModrinthCatalog modrinthCatalog,
        WorldBackupInstaller worldBackupInstaller,
        IInstanceCatalog instances,
        IJavaRuntimeCatalog javaRuntimes,
        ISettingsCatalog settings,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(loaderInstaller);
        ArgumentNullException.ThrowIfNull(forgeInstaller);
        ArgumentNullException.ThrowIfNull(optiFineInstaller);
        ArgumentNullException.ThrowIfNull(runtimeInstaller);
        ArgumentNullException.ThrowIfNull(contentInstaller);
        ArgumentNullException.ThrowIfNull(optionalComponents);
        ArgumentNullException.ThrowIfNull(mrpackInstaller);
        ArgumentNullException.ThrowIfNull(modrinthCatalog);
        ArgumentNullException.ThrowIfNull(worldBackupInstaller);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(javaRuntimes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _installer = installer;
        _loaderInstaller = loaderInstaller;
        _forgeInstaller = forgeInstaller;
        _optiFineInstaller = optiFineInstaller;
        _runtimeInstaller = runtimeInstaller;
        _contentInstaller = contentInstaller;
        _optionalComponents = optionalComponents;
        _mrpackInstaller = mrpackInstaller;
        _modrinthCatalog = modrinthCatalog;
        _worldBackupInstaller = worldBackupInstaller;
        _instances = instances;
        _javaRuntimes = javaRuntimes;
        _settings = settings;
        _clock = clock;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public int PendingTaskCount
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count(static job => !job.Job.IsFinished);
            }
        }
    }

    /// <inheritdoc />
    public InstallJob EnqueueInstall(InstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Enqueue(TitleFor(request), new GameInstallTask(request));
    }

    /// <inheritdoc />
    public InstallJob EnqueueJavaRuntime(JavaRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 标题由**一个键**说出来（"获取 Java 运行时（适配 1.20.4）"）—— 不把 request.Subject 的键嵌进来，
        // 那样英文界面里会露出中文（见 AGENTS.md §5.3 第 ⑤ 条）。
        return Enqueue(
            string.IsNullOrWhiteSpace(request.GameVersion)
                ? TextTemplate.Of("获取 Java 运行时（最新一代）")
                : TextTemplate.Of("获取 Java 运行时（适配 {0}）", request.GameVersion),
            new JavaRuntimeTask(request));
    }

    /// <inheritdoc />
    public InstallJob EnqueueContentDownload(ContentDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Enqueue(ContentTitleFor(request), new ContentDownloadTask(request));
    }

    /// <inheritdoc />
    public InstallJob EnqueueMrpackImport(MrpackInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 标题只说**文件名**（数据）：包名要等扫描读完清单才知道，而队列行在入队那一刻就得有标题。
        return Enqueue(TextTemplate.Of("导入整合包 {0}", request.Subject), new MrpackInstallTask(request));
    }

    /// <inheritdoc />
    public InstallJob EnqueueModrinthModpack(ModrinthModpackRequest request, string title)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        // 下载 .mrpack 也放进队列：p08 点「安装」后立刻能在 p06 看到进度，
        // 而不是界面先等几十 MB 下完、队列里却只出现一个"导入"任务。
        return Enqueue(TextTemplate.Of("安装整合包 {0}", title), new ModrinthModpackTask(request, title));
    }

    /// <inheritdoc />
    public InstallJob EnqueueWorldBackup(WorldBackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 标题里是**显示名**（用户给存档起的名字），路径用的是目录名 —— 两者可以不一样，
        // 所以请求里两个都带着（见 WorldBackupTarget）。
        return Enqueue(TextTemplate.Of("备份存档 {0}", request.Subject), new WorldBackupTask(request));
    }

    /// <inheritdoc />
    public InstallJob EnqueueWorldRestore(WorldRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Enqueue(TextTemplate.Of("恢复存档 {0}", request.Subject), new WorldRestoreTask(request));
    }

    /// <summary>入队（几种任务共用的那一段：建任务、修剪历史、唤醒 worker）。</summary>
    /// <param name="title">队列行标题。</param>
    /// <param name="task">要做的事。</param>
    /// <returns>刚入队的任务。</returns>
    private InstallJob Enqueue(TextTemplate title, QueueTask task)
    {
        var job = new InstallJob(
            Id: Guid.NewGuid().ToString("N")[..8],
            Title: title,
            Task: task,
            State: InstallJobState.Queued,
            Detail: TextTemplate.Of("等待中"),
            Result: null);

        lock (_gate)
        {
            // 每个任务自己的取消源（M7-g）：取消一个任务不该影响同队列里的其它任务。
            _jobs.Add(new QueuedJob(job, _clock.Now, new CancellationTokenSource()));
            TrimFinished();
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        RaiseChanged();

        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
        {
            _ = Task.Run(ProcessQueueAsync);
        }

        return job;
    }

    /// <inheritdoc />
    public bool Cancel(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        var cancelled = false;

        lock (_gate)
        {
            var index = _jobs.FindIndex(entry => string.Equals(entry.Job.Id, jobId, StringComparison.Ordinal));

            if (index < 0 || _jobs[index].Job.IsFinished)
            {
                return false;
            }

            var entry = _jobs[index];

            if (entry.Job.State == InstallJobState.Queued)
            {
                // 还没开始：直接落终态 —— worker 是按"排队中"挑任务的，这样它再也看不到这一条。
                // 释放取消源也是安全的：只有 worker 会用它，而它还没碰过这一条。
                _jobs[index] = entry with
                {
                    Job = entry.Job with
                    {
                        State = InstallJobState.Cancelled,
                        Detail = TextTemplate.Of("已取消。"),
                    },
                };

                entry.Cancellation.Dispose();

                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Downloader,
                    "排队中的任务已取消：{0}",
                    [SubjectOf(entry.Job.Task)]);
            }
            else
            {
                // 正在执行：只**请求**取消，真正的停下发生在安装器下一次检查取消标记时 ——
                // 终态由 worker 那条路落（所以这里不能释放取消源）。
                entry.Cancellation.Cancel();
            }

            cancelled = true;
        }

        RaiseChanged();
        return cancelled;
    }

    /// <inheritdoc />
    public InstallJob? Retry(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        InstallJob previous;

        lock (_gate)
        {
            var entry = _jobs.FirstOrDefault(candidate => string.Equals(candidate.Job.Id, jobId, StringComparison.Ordinal));

            // 只在**失败或已取消**上重试：还在跑的没有"重试"可言，已经成功的不该被再做一遍。
            if (entry is null || entry.Job.State is not (InstallJobState.Failed or InstallJobState.Cancelled))
            {
                return null;
            }

            previous = entry.Job;
        }

        // 原样再排一次：标题与请求都不变（用户点的是这一行）。
        return Enqueue(previous.Title, previous.Task);
    }

    /// <inheritdoc />
    public int ClearFinished()
    {
        int removed;

        lock (_gate)
        {
            var finished = _jobs.Where(static entry => entry.Job.IsFinished).ToList();
            removed = finished.Count;

            foreach (var entry in finished)
            {
                _jobs.Remove(entry);
                entry.Cancellation.Dispose();
            }
        }

        if (removed > 0)
        {
            RaiseChanged();
        }

        return removed;
    }

    /// <inheritdoc />
    public IReadOnlyList<InstallJob> GetJobs()
    {
        lock (_gate)
        {
            return [.. _jobs.Select(static entry => entry.Job)];
        }
    }

    /// <inheritdoc />
    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? idle;

        lock (_gate)
        {
            if (_workerRunning == 0 && _jobs.All(static entry => entry.Job.IsFinished))
            {
                return Task.CompletedTask;
            }

            idle = _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return idle.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public DownloadProgress GetProgress()
    {
        lock (_gate)
        {
            // 只有**正在跑**的任务才有"当前任务/进度"可言。
            //
            // 2026-10-06 用户反馈（Bug4.png）：下载完成后这一页写着"0 个活动任务 · 0 个排队"，
            // 下面却还挂着上一单的 529.6 MB / 4,828 个文件 / 并发 64 / 数据源 —— 因为这里以前会
            // **回退到"最后那个已结束的任务"**。跑完就该清空项目信息（队列列表里的那一行仍然
            // 留着"已完成"，那是历史，不是进度）。
            var current = _jobs.FirstOrDefault(static entry => entry.Job.State == InstallJobState.Running)?.Job;

            if (current is null)
            {
                return Idle;
            }

            var progress = _progress ?? new FileTransferProgress(0, 0, 0, 0, string.Empty, 0, null);
            var percent = progress.TotalBytes > 0
                ? Math.Clamp(progress.CompletedBytes * 100d / progress.TotalBytes, 0, 100)
                : current.State == InstallJobState.Completed ? 100 : 0;

            return new DownloadProgress(
                CurrentTask: current.Title,
                Percent: percent,
                AmountText: FormatAmount(progress),
                SpeedText: FormatSpeed(progress.BytesPerSecond),
                FilesText: FormatFiles(progress),
                RemainingText: FormatRemaining(progress.Remaining),
                ConnectionsText: _connections.ToString(CultureInfo.CurrentCulture),
                DataSource: string.IsNullOrWhiteSpace(progress.SourceHost) ? _dataSource : progress.SourceHost)
            {
                IsIndeterminate = progress.IsIndeterminate,
            };
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DownloadQueueItem> GetQueue()
    {
        lock (_gate)
        {
            List<DownloadQueueItem> rows = [];

            foreach (var entry in _jobs)
            {
                var isActive = entry.Job.State == InstallJobState.Running;

                rows.Add(new DownloadQueueItem(
                    Id: entry.Job.Id,
                    Title: entry.Job.Title,
                    Detail: DescribeJob(entry),
                    State: StateOf(entry.Job.State),
                    IsActive: isActive));
            }

            return rows;
        }
    }


    /// <summary>Modrinth 的内容文件下载主机（元数据在 api.modrinth.com，文件在 cdn）。</summary>
    private const string ModrinthCdnHost = "cdn.modrinth.com";

    /// <summary>
    /// Mojang 的客户端 / 资源分发主机。
    /// <para>
    /// 不能从 <see cref="InstallPlanner.AssetBaseUrl"/> 换字符串推出来：那个基址是
    /// <c>resources.download.minecraft.net</c>，把 <c>resources.download</c> 换成 <c>piston-data</c>
    /// 会得到 <c>piston-data.minecraft.net</c> —— 一个**不存在的主机**（之前 p06 的「数据源」就是这么错的）。
    /// </para>
    /// </summary>
    private const string MojangDataHost = "piston-data.mojang.com";

    /// <summary>
    /// 某个任务在**还没有任何文件进度**时该显示的数据源主机。
    /// </summary>
    /// <remarks>
    /// 以前这里只看「下载源」设置：装 Forge / Fabric 时也显示 <c>piston-data.mojang.com</c>，
    /// 看起来像"加载器从 Mojang 官方源下载"—— 实际上加载器的库来自各家 Maven。
    /// 现在按任务种类给默认值；一旦真的有文件开始下载，进度里带回来的
    /// <see cref="FileTransferProgress.SourceHost"/>（真实用的主机，含镜像回退）会覆盖它。
    /// </remarks>
    /// <param name="task">队列任务。</param>
    /// <param name="source">下载源策略（只影响原版资源 / 运行时）。</param>
    /// <returns>主机名；本地任务为 <c>—</c>。</returns>
    public static string DataSourceFor(QueueTask task, DownloadSourcePolicy source)
    {
        ArgumentNullException.ThrowIfNull(task);

        _ = source;

        return task switch
        {
            GameInstallTask game => game.Request.Loader switch
            {
                // 原版 = Mojang 的官方分发（与 Neo 一样只走官方源）。
                LoaderKind.Vanilla => MojangDataHost,

                // 加载器自己的元数据 / 库：各家官方 Maven。
                LoaderKind.Fabric => HostOf(LoaderMetadataClient.FabricLoaderUrlPrefix),
                LoaderKind.Quilt => HostOf(LoaderMetadataClient.QuiltLoaderUrlPrefix),
                LoaderKind.Forge => HostOf(ForgeInstaller.ForgeMaven),
                LoaderKind.NeoForge => HostOf(ForgeInstaller.NeoForgeMaven),
                LoaderKind.OptiFine => HostOf(OptiFineCatalog.OfficialDownloadsPage),
                _ => "—",
            },

            JavaRuntimeTask => MojangDataHost,
            ContentDownloadTask => ModrinthCdnHost,
            MrpackInstallTask or ModrinthModpackTask => ModrinthCdnHost,
            WorldBackupTask or WorldRestoreTask => "—",
            _ => "—",
        };
    }

    private static string HostOf(string url) => new Uri(url).Host;

    /// <summary>当前正在跑的任务；没有在跑的就是最近结束的那个（阶段表据此决定显示哪一套阶段）。</summary>
    /// <remarks>
    /// 第三级兜底（<c>_jobs.LastOrDefault()</c>）不是装饰：入队到 worker 把状态置成"正在执行"之间
    /// 有一小段窗口，那时既没有 running 也没有 finished —— 少这一级，p06 会在这个窗口里
    /// 拿**别的任务**的阶段表去描述它（导入整合包显示成"解压 natives"）。
    /// </remarks>
    private QueueTask? ActiveTask() =>
        (_jobs.FirstOrDefault(static entry => entry.Job.State == InstallJobState.Running)
         ?? _jobs.LastOrDefault(static entry => entry.Job.IsFinished)
         ?? _jobs.LastOrDefault())?.Job.Task;

    /// <summary>队列处理循环（单 worker，顺序执行）。</summary>
    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                QueuedJob? next;

                lock (_gate)
                {
                    next = _jobs.FirstOrDefault(static entry => entry.Job.State == InstallJobState.Queued);

                    if (next is null)
                    {
                        break;
                    }

                    next = next with
                    {
                        Job = next.Job with
                        {
                            State = InstallJobState.Running,
                            Detail = next.Job.Task switch
                            {
                                JavaRuntimeTask => TextTemplate.Of("正在获取官方运行时索引"),
                                ContentDownloadTask => TextTemplate.Of("正在解析必需依赖"),
                                MrpackInstallTask => TextTemplate.Of("正在扫描整合包"),
                                ModrinthModpackTask => TextTemplate.Of("正在下载整合包"),
                                WorldBackupTask => TextTemplate.Of("正在统计存档文件"),
                                WorldRestoreTask => TextTemplate.Of("正在解压备份"),
                                _ => TextTemplate.Of("正在解析版本 JSON"),
                            },
                        },
                    };
                    Replace(next);
                    _progress = null;
                    _completedStages.Clear();
                    _activeStage = null;

                }

                RaiseChanged();

                InstallResult result;

                // M4-h：从这一刻起，游戏目录里新增的东西都算这次安装的 —— 失败就回滚掉。
                // 事务放在**任务执行这一层**：`RunOneAsync` 自己兜住了大部分失败，
                // 而漏出来的异常由下面的 catch 收口 —— 两条路都必须回到装之前。
                //
                // 只有**游戏版本安装**需要它：Java 运行时写的是数据目录下的 runtime，
                // 而它用的是"先写暂存目录、成功才搬进去"（见 JavaRuntimeInstaller），
                // 半成品根本不会被运行时发现扫到，因此不需要、也不该按游戏目录去记账。
                // 内容下载（M7-d）同样不需要：每个文件都过了官方 SHA-1，单独看都是完整的模组文件，
                // 回滚会把用户要的模组删掉 —— 而重试本来就只会补缺的那几个（下载器会跳过校验通过的）。
                var transaction = next.Job.Task is GameInstallTask
                    ? InstallTransaction.Begin(_settings.GetGlobal().GameDirectory)
                    : null;

                try
                {
                    result = await RunOneAsync(next).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 兜底：**任何**漏出来的异常都必须让任务进入终态。
                    // 一个无人 await 的后台队列里，任务卡在"正在执行"意味着界面永远显示一行在跑的任务、
                    // 页头徽标永远不归零 —— 而用户完全不知道发生了什么。
                    _diagnostics.Write(
                        LauncherEventLevel.Error,
                        DiagnosticsSources.Downloader,
                        "安装任务异常结束（{0}，{1}）",
                        [SubjectOf(next.Job.Task), ex.GetType().Name]);

                    result = new InstallResult(
                        SubjectOf(next.Job.Task),
                        false,
                        0,
                        0,
                        0,
                        0,
                        TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
                }

                // M7-g：**是不是用户取消的**由取消源说了算，不看失败原因那句话 ——
                // 靠比对 "已取消。" 这种文案来判定状态是最脆的一种写法（改一句文案就静默失效）。
                // `&& !result.Succeeded` 也是一个真实存在的窗口：取消请求可能刚好在任务**已经成功**
                // 之后才到达，那一次就是成功，不该被记成"取消"。
                var cancelled = next.Cancellation.IsCancellationRequested && !result.Succeeded;

                if (transaction is null)
                {
                    // 运行时任务：没有游戏目录事务要提交或回滚。
                }
                else if (result.Succeeded)
                {
                    transaction.Commit();
                }
                else
                {
                    // 取消走的是**与失败同一条**善后路：M4-h 的承诺是"没装完就回到装之前"，
                    // 而取消正是"没装完"。官方安装器（Forge / OptiFine）可能已经写下我们无法校验的
                    // 半成品版本目录，留着它会让实例显示成"已安装"然后启动失败。
                    // 代价是重试要重新下载 —— 这一条登记在 CHANGELOG 的残留里。
                    result = RollBack(transaction, SubjectOf(next.Job.Task), result);
                }

                lock (_gate)
                {
                    var finished = next.Job with
                    {
                        State = cancelled
                            ? InstallJobState.Cancelled
                            : result.Succeeded ? InstallJobState.Completed : InstallJobState.Failed,
                        // 取消时用 `result.Failure`：它可能已经挂上了 M4-h 那句「已回滚这次安装的写入」，
                        // 用户需要知道"取消之后磁盘上现在是什么样"。
                        Detail = cancelled
                            ? result.Failure ?? TextTemplate.Of("已取消。")
                            : result.Succeeded ? TextTemplate.Of("已完成") : result.Failure,
                        Result = result,
                    };

                    Replace(next with { Job = finished });
                    next.Cancellation.Dispose();

                    if (result.Succeeded)
                    {
                        _progress = new FileTransferProgress(
                            CompletedFiles: result.FilesDownloaded + result.FilesSkipped,
                            TotalFiles: result.FilesDownloaded + result.FilesSkipped,
                            CompletedBytes: result.Bytes,
                            TotalBytes: result.Bytes,
                            CurrentFile: string.Empty,
                            BytesPerSecond: 0,
                            Remaining: TimeSpan.Zero);
                    }

                    foreach (var stage in Enum.GetValues<InstallStageKind>())
                    {
                        if (result.Succeeded)
                        {
                            _completedStages.Add(stage);
                        }
                    }

                    _activeStage = null;
                }

                RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            // 队列循环自己绝不能把异常漏出去（这是一个无人 await 的后台任务）：
            // 漏出去会变成 UnobservedTaskException，用户只会看到"什么都没发生"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Downloader,
                "安装队列意外中断（{0}）",
                [ex.GetType().Name]);
        }
        finally
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _workerRunning, 0);
                _idle?.TrySetResult();
                _idle = null;
            }
        }
    }

    private async Task<InstallResult> RunOneAsync(QueuedJob entry)
    {
        var settings = _settings.GetGlobal();
        var task = entry.Job.Task;

        // M7-g：这个任务的取消标记（每个任务一个取消源，取消一个不影响同队列里的其它任务）。
        var token = entry.Cancellation.Token;

        // 用**同步转发**而不是 Progress<T>：Progress<T> 会把回调投递到线程池 / UI 同步上下文，
        // 于是"安装器已经开始跑了"与"进度快照更新了"之间存在一个不确定的窗口 ——
        // 对进度条来说只是慢半拍，对「不确定进度」来说却会晚到整整一个安装器进程。
        // 队列本身已经在后台线程；这里只写锁内字段，真正的 UI 刷新由 Changed + 节流器负责。
        var progress = new InlineProgress<FileTransferProgress>(value =>
        {
            lock (_gate)
            {
                _progress = value;
                _connections = settings.ConcurrentDownloads;
            }

            RaiseChanged();
        });

        var stages = new InlineProgress<InstallStageChange>(change =>
        {
            lock (_gate)
            {
                if (change.Completed)
                {
                    _completedStages.Add(change.Kind);
                    _activeStage = null;
                }
                else
                {
                    _activeStage = change.Kind;
                }

                if (_progress is not null)
                {
                    _progress = _progress with { CurrentFile = _progress.CurrentFile };
                }
            }

            RaiseChanged();
        });

        lock (_gate)
        {
            _connections = settings.ConcurrentDownloads;
            _dataSource = DataSourceFor(task, settings.DownloadSource);
        }

        if (task is JavaRuntimeTask runtime)
        {
            return await RunJavaRuntimeAsync(runtime.Request, settings, progress, stages, token).ConfigureAwait(false);
        }

        if (task is ContentDownloadTask content)
        {
            return await RunContentAsync(content.Request, settings, progress, stages, token).ConfigureAwait(false);
        }

        if (task is MrpackInstallTask mrpack)
        {
            return await RunMrpackAsync(mrpack.Request, settings, progress, stages, token).ConfigureAwait(false);
        }

        if (task is ModrinthModpackTask modrinthModpack)
        {
            return await RunModrinthModpackAsync(modrinthModpack, settings, progress, stages, token).ConfigureAwait(false);
        }

        if (task is WorldBackupTask worldBackup)
        {
            return await RunWorldBackupAsync(worldBackup.Request, progress, stages, token).ConfigureAwait(false);
        }

        if (task is WorldRestoreTask worldRestore)
        {
            return await RunWorldRestoreAsync(worldRestore.Request, progress, stages, token).ConfigureAwait(false);
        }

        if (task is not GameInstallTask install)
        {
            // 队列不可能收到别的任务（枚举不是开放的），但真发生了就必须**如实失败**：
            // 让任务卡在"正在执行"比报一个错糟得多。
            return new InstallResult(
                "queue",
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("队列不认识的任务种类：{0}。", task.GetType().Name));
        }

        return await RunGameInstallAsync(install.Request, settings, progress, stages, token).ConfigureAwait(false);
    }

    private async Task<InstallResult> RunGameInstallAsync(
        InstallRequest request,
        GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        var result = await RunGameInstallCoreAsync(request, settings, progress, stages, cancellationToken)
            .ConfigureAwait(false);

        var mirrored = MirrorIntoInstance(request, settings, result);

        if (mirrored.Succeeded && !string.IsNullOrWhiteSpace(request.InstanceName))
        {
            // 可选组件是“游戏与加载器装好之后”的附加项：失败只记警告，不把安装判失败。
            await _optionalComponents
                .InstallAsync(request, settings, progress, stages, cancellationToken)
                .ConfigureAwait(false);
        }

        return mirrored;
    }

    /// <summary>
    /// 装好之后，把**这个实例启动要用到的版本文件**复制进实例目录
    /// （<c>versions\&lt;实例&gt;\version.json</c> + 客户端 JAR + natives）。
    /// </summary>
    /// <remarks>
    /// 只在 <see cref="InstallRequest.InstanceName"/> 非空时做：p05 的「安装所选版本」只把版本装进共享游戏目录，
    /// 不涉及任何实例。实例目录里的版本 JSON 是启动链的第一来源（见 <c>VersionJsonStore</c>），
    /// 没有它实例会一直显示「未安装」。
    /// </remarks>
    private InstallResult MirrorIntoInstance(
        InstallRequest request,
        GlobalSettings settings,
        InstallResult result)
    {
        if (!result.Succeeded || string.IsNullOrWhiteSpace(request.InstanceName))
        {
            return result;
        }

        try
        {
            InstalledVersionMirror.Mirror(settings.GameDirectory, result.VersionId, request.InstanceName);

            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Downloader,
                "已把版本 {0} 写入实例目录：{1}",
                [result.VersionId, request.InstanceName],
                request.InstanceName);

            return result;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or DirectoryNotFoundException
                                   or FileNotFoundException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Downloader,
                "版本装好了但没能写进实例目录（{0}，{1}）",
                [request.InstanceName, ex.GetType().Name],
                request.InstanceName);

            return result with
            {
                Succeeded = false,
                Failure = TextTemplate.Of(
                    "游戏文件装好了，但没能写进实例目录（{0}）。请重试安装，或在实例设置里检查目录权限。",
                    ex.Message),
            };
        }
    }

    /// <summary>
    /// 装一个游戏版本（原版 / Fabric / Quilt / Forge / NeoForge / OptiFine）。
    /// </summary>
    /// <remarks>
    /// 从 <c>RunOneAsync</c> 里抽出来（M7-e-2）：整合包导入要先"用清单里声明的版本与加载器建实例、
    /// 把游戏装好"，而那段派发逻辑只有一份 —— 复制一份到整合包那条路上，
    /// "Forge 走官方安装器、Fabric 走 profile"这类判断就会有两处，迟早改一处漏一处。
    /// </remarks>
    /// <param name="request">安装请求。</param>
    /// <param name="settings">当前设置。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记（M7-g：p06 的「取消」）。</param>
    /// <returns>结果。</returns>
    private async Task<InstallResult> RunGameInstallCoreAsync(
        InstallRequest request,
        GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        // 事件里带上实例名（只装版本时为 null）：出了问题，"这是给哪个实例装的"是最先要回答的问题。
        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Downloader,
            "安装任务开始：{0}（实例 {1}）",
            [request.VersionId, request.InstanceName ?? "（未建实例）"]);

        // Forge / NeoForge / OptiFine 的安装器只负责加载器本身：先把原版完整装好
        // （客户端 JAR / libraries / assets），再跑加载器安装器。Fabric / Quilt 自己会合并并下载原版，
        // 不需要在这里重复一遍。
        if (LoaderNaming.IsInstallerLoader(request.Loader) || LoaderNaming.IsOptiFine(request.Loader))
        {
            InstallResult vanilla;

            try
            {
                vanilla = await _installer
                    .InstallAsync(
                        request.VersionId,
                        settings.GameDirectory,
                        GamePlatform.Current,
                        settings.DownloadSource,
                        settings.ConcurrentDownloads,
                        progress,
                        stages,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new InstallResult(request.VersionId, false, 0, 0, 0, 0, TextTemplate.Of("已取消。"));
            }
            catch (Exception exception) when (exception is VersionResolutionException
                                                or VersionDataException
                                                or IOException
                                                or UnauthorizedAccessException
                                                or InvalidOperationException
                                                or HttpRequestException)
            {
                return new InstallResult(
                    request.VersionId,
                    false,
                    0,
                    0,
                    0,
                    0,
                    TextTemplate.Of("先安装原版 {0} 时失败。", request.VersionId) with
                    {
                        Note = TextTemplate.Of(exception.Message),
                    });
            }

            if (!vanilla.Succeeded)
            {
                return new InstallResult(
                    request.VersionId,
                    false,
                    0,
                    0,
                    0,
                    0,
                    TextTemplate.Of("先安装原版 {0} 时失败。", request.VersionId) with { Note = vanilla.Failure });
            }
        }

        if (LoaderNaming.IsProfileLoader(request.Loader))
        {
            // Fabric / Quilt：profile 是纯 JSON，装法与"合并后的版本"完全一样（M4-f）。
            return await RunLoaderAsync(
                request,
                settings,
                progress,
                stages,
                (loader, game, version, directory, source, concurrency, p, s, token) =>
                    _loaderInstaller.InstallAsync(loader, game, version, directory, GamePlatform.Current, source, concurrency, p, s, token),
                cancellationToken)
                .ConfigureAwait(false);
        }

        if (LoaderNaming.IsInstallerLoader(request.Loader))
        {
            // Forge / NeoForge：跑官方安装器（M4-g）。
            return await RunLoaderAsync(
                request,
                settings,
                progress,
                stages,
                (loader, game, version, directory, source, concurrency, p, s, token) =>
                    _forgeInstaller.InstallAsync(loader, game, version, directory, source, p, s, token),
                cancellationToken)
                .ConfigureAwait(false);
        }

        if (LoaderNaming.IsOptiFine(request.Loader))
        {
            // OptiFine（M4-i）：沙箱里跑官方安装器（`optifine.Installer`），再把产物搬进游戏目录。
            // 版本段（`HD_U_I8` 这种）来自向导选的加载器版本；没有它就无从确定装哪一个。
            if (string.IsNullOrWhiteSpace(request.LoaderVersion))
            {
                return new InstallResult(
                    request.VersionId,
                    false,
                    0,
                    0,
                    0,
                    0,
                    TextTemplate.Of("这个实例没有记下 OptiFine 的版本号，无法确定要装哪一个。请在向导或「实例设置」里重新选一次。"));
            }

            return await RunLoaderAsync(
                request,
                settings,
                progress,
                stages,
                (loader, game, version, directory, source, concurrency, p, s, token) =>
                    _optiFineInstaller.InstallAsync(game, version ?? string.Empty, directory, source, p, s, token),
                cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Loader != LoaderKind.Vanilla)
        {
            // 走到这里说明枚举加了新的加载器却没接上安装路径。**如实失败并说明原因**，
            // 而不是悄悄只装原版（那样用户会得到一个"看起来装好了、其实没有加载器"的实例）。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.ModLoader,
                "加载器 {0} 尚未接入，任务未执行（{1}，实例 {2}）",
                [request.Loader.ToString(), request.VersionId, request.InstanceName ?? "（未建实例）"]);

            return new InstallResult(
                request.VersionId,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0} 的安装还没有接上（枚举里新加了加载器但没加安装路径）。", request.Loader.ToString()));
        }

        try
        {
            return await _installer
                .InstallAsync(
                    request.VersionId,
                    settings.GameDirectory,
                    GamePlatform.Current,
                    settings.DownloadSource,
                    settings.ConcurrentDownloads,
                    progress,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Downloader,
                "安装任务已取消：{0}",
                [request.VersionId]);

            return new InstallResult(request.VersionId, false, 0, 0, 0, 0, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex)
        {
            // 这里**故意**捕获得宽：安装链路上会抛的异常类型不止一两种
            // （清单读不出来、路径越界、磁盘、权限……），漏掉一种的后果就是任务卡在"正在执行"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Downloader,
                "安装任务异常结束（{0}，{1}）",
                [request.VersionId, ex.GetType().Name]);

            return new InstallResult(request.VersionId, false, 0, 0, 0, 0, TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
        }
    }

    /// <summary>
    /// 获取一个官方 Java 运行时（§12.7），并在**成功之后**强制重检一次 Java。
    /// </summary>
    /// <remarks>
    /// 强制重检是必需的，不是优化：p10 的运行时表与启动前的选 Java 都读检测结果，
    /// 而检测结果有 5 分钟新鲜度窗口 —— 不刷新的话，用户刚装好的运行时要等 5 分钟才会出现在表里，
    /// 期间他只会觉得"我点了，但它说没装"。
    /// </remarks>
    /// <param name="request">运行时请求。</param>
    /// <param name="settings">当前设置（游戏目录、下载源、并发数）。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记（M7-g：p06 的「取消」）。</param>
    /// <returns>结果。</returns>
    private async Task<InstallResult> RunJavaRuntimeAsync(
        JavaRuntimeRequest request,
        Settings.GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.JavaRuntime,
            "Java 运行时获取任务开始：{0}",
            [request.GameVersion ?? JavaRuntimeInstaller.SubjectFallback]);

        InstallResult result;

        try
        {
            result = await _runtimeInstaller
                .InstallAsync(
                    request,
                    settings.GameDirectory,
                    settings.DownloadSource,
                    settings.ConcurrentDownloads,
                    progress,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.JavaRuntime,
                "Java 运行时获取任务已取消：{0}",
                [request.GameVersion ?? JavaRuntimeInstaller.SubjectFallback]);

            return new InstallResult(JavaRuntimeInstaller.SubjectFallback, false, 0, 0, 0, 0, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex)
        {
            // 捕获得宽是刻意的：清单、网络、磁盘、路径各会抛不同的类型，漏掉一种就是"任务卡在正在执行"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.JavaRuntime,
                "Java 运行时获取任务异常结束（{0}，{1}）",
                [request.GameVersion ?? JavaRuntimeInstaller.SubjectFallback, ex.GetType().Name]);

            return new InstallResult(
                JavaRuntimeInstaller.SubjectFallback,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
        }

        if (result.Succeeded)
        {
            // 令牌**刻意不往下传**（CA2016 要的就是这句话写出来）：运行时已经装好了，
            // 把检测结果刷新一次不需要用户再等；而此刻若刚好有人按了取消，令牌会让这一句抛
            // `OperationCanceledException` 并一路冒到队列那条兜底 catch —— 一次**成功**的安装
            // 就被写成"已取消"了。重检本身没有任何副作用，不该参与取消。
            await _javaRuntimes.RedetectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 下载一批 Modrinth 内容（M7-d），并把"取消"与"任何异常都要有可读结论"收在一处。
    /// </summary>
    /// <param name="request">请求（实例 + 目标）。</param>
    /// <param name="settings">当前设置（下载源、并发数）。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记（M7-g：p06 的「取消」）。</param>
    /// <returns>结果。</returns>
    private async Task<InstallResult> RunContentAsync(
        ContentDownloadRequest request,
        Settings.GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _contentInstaller
                .InstallAsync(
                    request,
                    settings.DownloadSource,
                    settings.ConcurrentDownloads,
                    progress,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "内容下载任务已取消：{0}",
                [request.Subject]);

            return new InstallResult(request.Subject, false, 0, 0, 0, 0, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 捕获得宽是刻意的：解析、下载、替换各会抛不同的类型，漏掉一种就是"任务卡在正在执行"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Modrinth,
                "内容下载任务异常结束（{0}，{1}）",
                [request.Subject, ex.GetType().Name]);

            return new InstallResult(
                request.Subject,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
        }
    }

    /// <summary>
    /// 从 Modrinth 下载整合包并导入（2026-10-21）：下载发生在队列里，然后复用既有导入流程。
    /// </summary>
    /// <param name="task">任务（项目、兼容性条件、标题）。</param>
    /// <param name="settings">当前设置。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>导入结果。</returns>
    private async Task<InstallResult> RunModrinthModpackAsync(
        ModrinthModpackTask task,
        GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        ModrinthModpackResult prepared;

        try
        {
            prepared = await _modrinthCatalog.PrepareModpackAsync(task.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Modrinth,
                "下载整合包时异常结束（{0}，{1}）",
                [task.Title, ex.GetType().Name]);

            return new InstallResult(
                task.Title,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("下载整合包时出错（{0}）。", ex.GetType().Name));
        }

        if (!prepared.Succeeded || prepared.Archive is null)
        {
            return new InstallResult(
                task.Title,
                false,
                0,
                0,
                0,
                0,
                prepared.Failure ?? TextTemplate.Of("下载整合包失败。"));
        }

        return await RunMrpackAsync(
                new MrpackInstallRequest(prepared.Archive.ArchivePath),
                settings,
                progress,
                stages,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 导入一个整合包（M7-e-2）：扫描 → 装游戏与加载器 → 建实例 → 铺内容。
    /// </summary>
    /// <remarks>
    /// 顺序的判据（三条都不只是"顺手"）：
    /// <list type="number">
    /// <item><b>扫描在最前</b>：设计稿 p08 那行原文承诺"导入前会先做安全扫描"，
    /// 而且一份不合规的包不该在磁盘上留下任何东西。扫描结论由 <see cref="MrpackInstaller.Preview"/>
    /// 一次算出来、往下传给导入器 —— 同一份包不扫两遍（大包的条目数上万）。</item>
    /// <item><b>先装游戏，再建实例</b>：游戏装进共享游戏目录（与向导同一条路、同一套事务），
    /// 它失败时回滚掉，于是**不会留下一个"有实例没游戏"的空壳**。</item>
    /// <item><b>内容写进实例自己的目录</b>：目录只有一处解析（<c>IInstanceCatalog.GetLaunchInfo</c>）——
    /// 非隔离实例的答案与隔离实例不同，自己拼一次必然错一半。</item>
    /// </list>
    /// </remarks>
    /// <param name="request">请求（用户选中的包）。</param>
    /// <param name="settings">当前设置（游戏目录、下载源、并发数）。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记（M7-g：p06 的「取消」）。</param>
    /// <returns>结果（<c>VersionId</c> 是整合包名）。</returns>
    private async Task<InstallResult> RunMrpackAsync(
        MrpackInstallRequest request,
        GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        // ---- 阶段 1：扫描（读清单 + 四项安全检查；**不改动任何文件**）----
        ReportStage(stages, InstallStageKind.ScanArchive, completed: false);

        var preview = _mrpackInstaller.Preview(request.ArchivePath);

        if (preview.Failure is not null)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "整合包导入未开始（{0}）：安全扫描或清单没通过",
                [request.Subject]);

            return preview.Failure;
        }

        // IsUsable 已经保证了清单非空；`!` 只是把这个事实写下来。
        var index = preview.Index!;

        ReportStage(stages, InstallStageKind.ScanArchive, completed: true);

        if (string.IsNullOrWhiteSpace(index.GameVersion))
        {
            // 没有游戏版本就无从决定"装哪一版游戏"。宁可在这里如实停下，也不要建一个装不了游戏的实例。
            return new InstallResult(
                preview.Subject,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("这个整合包没有声明它用的 Minecraft 版本，本启动器无法决定要装哪一版游戏。"));
        }

        // ---- 阶段 2：装游戏与加载器 ----
        ReportStage(stages, InstallStageKind.InstallGame, completed: false);

        // 实例名先备好（失败信息里要用），但**实例此时还没建**。
        var gameRequest = new InstallRequest(null, index.GameVersion, index.Loader, index.LoaderVersion);
        var transaction = InstallTransaction.Begin(settings.GameDirectory);
        InstallResult game;

        try
        {
            // 内层阶段名（解析版本 JSON / 下载资源 / 解压 natives）**不进**整合包的表：
            // 那张表只有四行，把内层的 `Download` 透上来会让第 3 行"下载整合包里的文件"在装游戏时亮起来。
            game = await RunGameInstallAsync(gameRequest, settings, progress, stages: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RunGameInstallAsync 自己收口了大部分异常；兜住剩下的，任务绝不能卡在"正在执行"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Modrinth,
                "整合包装游戏时异常结束（{0}，{1}）",
                [request.Subject, ex.GetType().Name]);

            game = new InstallResult(
                index.GameVersion,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
        }

        if (game.Succeeded)
        {
            transaction.Commit();
        }
        else
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Modrinth,
                "整合包导入在装游戏这一步失败（{0}，未建实例）",
                [request.Subject]);

            return RollBack(transaction, preview.Subject, game);
        }

        ReportStage(stages, InstallStageKind.InstallGame, completed: true);

        // ---- 阶段 3：建实例（元数据落盘；此时游戏已经装好）----
        string instanceName;

        try
        {
            instanceName = InstanceNaming.SuggestUniqueNameForPack(
                index.Name,
                index.GameVersion,
                index.Loader,
                [.. _instances.GetInstances().Select(static record => record.Name)]);

            _instances.CreateInstance(new InstanceCreationRequest(
                Name: instanceName,
                GameVersion: index.GameVersion,
                Loader: index.Loader,
                LoaderVersion: index.LoaderVersion,
                IsolatedDirectory: true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Modrinth,
                "整合包导入建实例失败（{0}）：{1}",
                [request.Subject, ex.GetType().Name]);

            // 游戏已经装好、实例没建成：如实说清楚"内容一个字节都没铺"。
            return new InstallResult(
                preview.Subject,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("整合包要用的实例建不出来（{0}），内容没有铺进去。", ex.GetType().Name));
        }

        // ---- 阶段 3.5：把刚装好的共享版本写进实例目录（Neo 26.1 的一实例一目录结构）----
        // 游戏是先装进共享目录的（那时还不知道实例名），实例建好之后必须把 version.json / JAR / natives
        // 复制过来；否则启动链找不到实例自己的版本文件。
        try
        {
            InstalledVersionMirror.Mirror(settings.GameDirectory, game.VersionId, instanceName);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or DirectoryNotFoundException
                                   or FileNotFoundException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Modrinth,
                "整合包的游戏装好了但没能写进实例目录（{0}，{1}）",
                [instanceName, ex.GetType().Name]);

            return new InstallResult(
                preview.Subject,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("整合包的游戏文件装好了，但没能写进实例目录（{0}）。内容没有铺进去。", ex.Message));
        }

        // ---- 阶段 4：铺内容（清单里的文件 + 覆盖层）----
        var instancePath = _instances.GetLaunchInfo(instanceName).InstancePath;

        return await _mrpackInstaller
            .InstallAsync(
                new MrpackImportRequest(request.ArchivePath, instancePath),
                preview,
                settings.DownloadSource,
                settings.ConcurrentDownloads,
                progress,
                stages,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 备份一个存档（M7-f），并把"取消"与"任何异常都要有可读结论"收在一处。
    /// </summary>
    /// <param name="request">请求（实例 + 存档）。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记（M7-g：p06 的「取消」）。</param>
    /// <returns>结果。</returns>
    private async Task<InstallResult> RunWorldBackupAsync(
        WorldBackupRequest request,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        if (!TryResolveInstance(request.InstanceName, out var gameDirectory))
        {
            return MissingInstance(request.Subject, request.InstanceName);
        }

        try
        {
            return await _worldBackupInstaller
                .BackupAsync(gameDirectory, request.Target, progress, stages, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "备份存档任务已取消：{0}",
                [request.Subject]);

            return new InstallResult(request.Subject, false, 0, 0, 0, 0, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 捕获得宽是刻意的：路径、磁盘、权限各会抛不同的类型，漏掉一种就是"任务卡在正在执行"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Launcher,
                "备份存档任务异常结束（{0}，{1}）",
                [request.Subject, ex.GetType().Name]);

            return new InstallResult(
                request.Subject,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
        }
    }

    /// <summary>
    /// 恢复一个存档（M7-f），并把"取消"与"任何异常都要有可读结论"收在一处。
    /// </summary>
    /// <param name="request">请求（实例 + 存档）。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记（M7-g：p06 的「取消」）。</param>
    /// <returns>结果。</returns>
    private async Task<InstallResult> RunWorldRestoreAsync(
        WorldRestoreRequest request,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        if (!TryResolveInstance(request.InstanceName, out var gameDirectory))
        {
            return MissingInstance(request.Subject, request.InstanceName);
        }

        try
        {
            return await _worldBackupInstaller
                .RestoreAsync(gameDirectory, request.Target, progress, stages, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "恢复存档任务已取消：{0}",
                [request.Subject]);

            return new InstallResult(request.Subject, false, 0, 0, 0, 0, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Launcher,
                "恢复存档任务异常结束（{0}，{1}）",
                [request.Subject, ex.GetType().Name]);

            return new InstallResult(
                request.Subject,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
        }
    }

    /// <summary>
    /// 实例的游戏目录（<c>GetLaunchInfo</c> 的 <c>InstancePath</c>，与内容、整合包同一条解析路径）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="gameDirectory">游戏目录。</param>
    /// <returns>实例存在时为 <see langword="true"/>。</returns>
    private bool TryResolveInstance(string instanceName, out string gameDirectory)
    {
        gameDirectory = string.Empty;

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        try
        {
            gameDirectory = _instances.GetLaunchInfo(instanceName).InstancePath;
            return !string.IsNullOrWhiteSpace(gameDirectory);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>实例不在了（用户删掉实例之后又点了备份）：如实失败，而不是去动一个猜出来的目录。</summary>
    private static InstallResult MissingInstance(string subject, string instanceName) =>
        new(
            subject,
            false,
            0,
            0,
            0,
            0,
            TextTemplate.Of("找不到实例 {0}，这个任务没有执行。", instanceName));

    /// <summary>队列任务的目标名（事件与失败结果里用）。</summary>
    private static string SubjectOf(QueueTask task) => task switch
    {
        GameInstallTask install => install.Request.VersionId,
        JavaRuntimeTask runtime => runtime.Request.GameVersion ?? JavaRuntimeInstaller.SubjectFallback,
        ContentDownloadTask content => content.Request.Subject,
        MrpackInstallTask mrpack => mrpack.Request.Subject,
        WorldBackupTask backup => backup.Request.Subject,
        WorldRestoreTask restore => restore.Request.Subject,
        _ => "queue",
    };

    /// <summary>
    /// 失败就回到装之前（M4-h / B-11）。回滚是**尽力而为**的：删不掉的项如实计数，
    /// 而不是让"安装失败"再叠加一个"回滚也炸了"。
    /// </summary>
    /// <param name="transaction">这次安装的事务。</param>
    /// <param name="subject">目标名（事件里要带版本号）。</param>
    /// <param name="result">失败的结果（会被拼上一句回滚说明）。</param>
    /// <returns>带上回滚说明的结果。</returns>
    private InstallResult RollBack(InstallTransaction transaction, string subject, InstallResult result)
    {
        InstallRollbackResult rollback;

        try
        {
            rollback = transaction.Rollback();
        }
        catch (Exception exception)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Downloader,
                "安装失败后的回滚没有做完（{0}，{1}）",
                [subject, exception.GetType().Name]);

            return result;
        }

        var description = InstallTransaction.Describe(rollback);

        if (description is null)
        {
            return result;
        }

        _diagnostics.Write(
            rollback.IsClean ? LauncherEventLevel.Information : LauncherEventLevel.Warning,
            DiagnosticsSources.Downloader,
            "已回滚这次安装的写入（{0}：删除 {1} 个文件、{2} 个目录，恢复 {3} 个文件，失败 {4} 项）",
            [
                subject,
                rollback.RemovedFiles.ToString(CultureInfo.InvariantCulture),
                rollback.RemovedDirectories.ToString(CultureInfo.InvariantCulture),
                rollback.RestoredFiles.ToString(CultureInfo.InvariantCulture),
                rollback.FailedItems.ToString(CultureInfo.InvariantCulture),
            ]);

        // 两句话各是键：失败原因 + 一句"已回滚什么"。第二句挂在 Note 上（拼接的唯一合法方式）。
        return result with
        {
            Failure = (result.Failure ?? TextTemplate.Of("安装失败。")) with { Note = description },
        };
    }

    /// <summary>
    /// 加载器安装器的统一调用口：把"取消"与"任何异常都要有可读结论"这两件事收在一处。
    /// 两种加载器（纯 JSON / 官方安装器）的入参形状不同，所以这里用委托抹平。
    /// </summary>
    private async Task<InstallResult> RunLoaderAsync(
        InstallRequest request,
        Settings.GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        Func<LoaderKind, string, string?, string, DownloadSourcePolicy, int, IProgress<FileTransferProgress>?, IProgress<InstallStageChange>?, CancellationToken, Task<InstallResult>> install,
        CancellationToken cancellationToken)
    {
        try
        {
            return await install(
                request.Loader,
                request.VersionId,
                request.LoaderVersion,
                settings.GameDirectory,
                settings.DownloadSource,
                settings.ConcurrentDownloads,
                progress,
                stages,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Downloader,
                "安装任务已取消：{0}",
                [request.VersionId]);

            return new InstallResult(request.VersionId, false, 0, 0, 0, 0, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex)
        {
            // 捕获得宽是刻意的：加载器安装会抛的异常类型不止一两种，漏掉一种就是"任务卡在正在执行"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Downloader,
                "安装任务异常结束（{0}，{1}）",
                [request.VersionId, ex.GetType().Name]);

            return new InstallResult(request.VersionId, false, 0, 0, 0, 0, TextTemplate.Of("{0}：{1}", ex.GetType().Name, ex.Message));
        }
    }

    /// <summary>
    /// 队列行标题：装了加载器就要写在标题里，否则队列里两行长得一样、分不出哪个是加载器。
    /// </summary>
    private static TextTemplate TitleFor(InstallRequest request) =>
        request.Loader == LoaderKind.Vanilla
            ? TextTemplate.Of("安装 {0}", request.VersionId)
            : TextTemplate.Of(
                "安装 {0} · {1}",
                request.VersionId,
                // 排障/队列标题里的加载器名用 DataDisplay（ASCII、与语言无关），见 §5.3 第 ③ 条。
                InstanceNaming.DataDisplay(request.Loader, request.LoaderVersion));

    /// <summary>
    /// 内容下载的队列行标题。
    /// </summary>
    /// <remarks>
    /// 带上实例名：p06 的队列是全局的，而"这 5 个模组是给哪个实例更新的"是用户最先要回答的问题
    /// （与安装任务的事件里带实例名同一条理由）。
    /// </remarks>
    private static TextTemplate ContentTitleFor(ContentDownloadRequest request)
    {
        var count = request.Targets.Count.ToString(CultureInfo.InvariantCulture);

        if (request.Kind == ModrinthContentKind.ResourcePack)
        {
            return TextTemplate.Of("下载 {0} 个资源包 · {1}", count, request.Subject);
        }

        // 新装（没有任何 ReplacePath）与更新在队列里要分得清：一个是"下载"，一个是"更新"。
        return request.Targets.All(static target => target.ReplacePath is null)
            ? TextTemplate.Of("下载 {0} 个模组 · {1}", count, request.Subject)
            : TextTemplate.Of("更新 {0} 个模组 · {1}", count, request.Subject);
    }

    /// <summary>
    /// 把阶段变化报给进度接收者。
    /// </summary>
    /// <remarks>
    /// 安装器们自己报阶段（它们内部就是这么用的），而**整合包导入这条路由队列自己编排**：
    /// 阶段 1 / 2 与阶段 3 / 4 分属两个组件，中间还要建实例，所以由这里统一报。
    /// </remarks>
    /// <param name="stages">阶段接收者（可为空）。</param>
    /// <param name="kind">阶段。</param>
    /// <param name="completed">是否已完成。</param>
    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    private LaunchStage Stage(int order, TextTemplate title, TextTemplate detail, InstallStageKind kind)
    {
        var current = _jobs.Any(static entry => entry.Job.State == InstallJobState.Running);

        if (!current)
        {
            var finished = _jobs.LastOrDefault(static entry => entry.Job.State == InstallJobState.Completed)?.Job;

            return new LaunchStage(
                order,
                title,
                detail,
                finished is null ? LaunchStageState.Pending : LaunchStageState.Completed,
                IsActive: false);
        }

        var state = _completedStages.Contains(kind)
            ? LaunchStageState.Completed
            : _activeStage == kind
                ? LaunchStageState.Running
                : LaunchStageState.Pending;

        return new LaunchStage(order, title, detail, state, IsActive: state == LaunchStageState.Running);
    }

    private TextTemplate DescribeDownloadStage(TextTemplate idleDescription)
    {
        var progress = _progress;

        if (progress is null || progress.TotalFiles == 0)
        {
            return idleDescription;
        }

        var files = progress.CompletedFiles.ToString("N0", CultureInfo.CurrentCulture)
                    + " / "
                    + progress.TotalFiles.ToString("N0", CultureInfo.CurrentCulture);

        // 纯数据（文件计数 + 百分比）：键里没有可翻译的词，两种语言都是 `{0}` 那一个占位符。
        return progress.TotalBytes > 0
            ? TextTemplate.Of(
                "{0} · {1}%",
                files,
                Math.Clamp(progress.CompletedBytes * 100d / progress.TotalBytes, 0, 100).ToString("0", CultureInfo.CurrentCulture))
            : TextTemplate.Of("{0}", files);
    }

    private TextTemplate? DescribeJob(QueuedJob entry)
    {
        switch (entry.Job.State)
        {
            case InstallJobState.Queued:
            {
                var waiting = _clock.Now - entry.EnqueuedAt;

                return TextTemplate.Of(
                    "等待中 · 已排队 {0} 秒",
                    ((int)Math.Max(0, waiting.TotalSeconds)).ToString(CultureInfo.CurrentCulture));
            }

            case InstallJobState.Running:
            {
                var progress = _progress;

                // 官方安装器跑在子进程里：没有字节进度，用安装器自己给的一句话，而不是「 · —」。
                if (progress is { IsIndeterminate: true })
                {
                    return progress.Detail ?? entry.Job.Detail;
                }

                return progress is null
                    ? entry.Job.Detail
                    : TextTemplate.Of("{0} · {1}", FormatAmount(progress), FormatSpeed(progress.BytesPerSecond));
            }

            default:
                return entry.Job.Detail;
        }
    }

    private static DownloadQueueState StateOf(InstallJobState state) => state switch
    {
        InstallJobState.Queued => DownloadQueueState.Queued,
        InstallJobState.Running => DownloadQueueState.Running,
        InstallJobState.Failed => DownloadQueueState.Failed,
        InstallJobState.Cancelled => DownloadQueueState.Cancelled,
        _ => DownloadQueueState.Completed,
    };

    private static string FormatAmount(FileTransferProgress progress)
    {
        if (progress.TotalBytes <= 0 && progress.CompletedBytes <= 0)
        {
            return string.Empty;
        }

        // 加载器 / 可选组件会在基础版本之外继续下载，进度源给出的 TotalBytes 可能还没有
        // 把那一截算进去。显示时至少让总量不小于已完成量，否则会出现「32.6 MB / 22.0 MB」。
        var total = Math.Max(progress.TotalBytes, progress.CompletedBytes);
        return FormatBytes(progress.CompletedBytes) + " / " + FormatBytes(total);
    }

    private static string FormatFiles(FileTransferProgress progress) =>
        progress.TotalFiles <= 0
            ? string.Empty
            : progress.CompletedFiles.ToString("N0", CultureInfo.CurrentCulture)
              + " / "
              + progress.TotalFiles.ToString("N0", CultureInfo.CurrentCulture);

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "—" : FormatBytes((long)bytesPerSecond) + "/s";

    private static TextTemplate FormatRemaining(TimeSpan? remaining)
    {
        if (remaining is null)
        {
            return TextTemplate.Of("—");
        }

        return remaining.Value.TotalSeconds < 1
            ? TextTemplate.Of("即将完成")
            : TextTemplate.Of(
                "约 {0} 秒",
                ((int)remaining.Value.TotalSeconds).ToString(CultureInfo.CurrentCulture));
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };

    private void Replace(QueuedJob entry)
    {
        var index = _jobs.FindIndex(candidate => string.Equals(candidate.Job.Id, entry.Job.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _jobs[index] = entry;
        }
    }

    /// <summary>只保留最近若干个已结束的任务。</summary>
    private void TrimFinished()
    {
        var finished = _jobs.Where(static entry => entry.Job.IsFinished).ToList();
        if (finished.Count <= FinishedHistoryLimit)
        {
            return;
        }

        foreach (var entry in finished.Take(finished.Count - FinishedHistoryLimit))
        {
            _jobs.Remove(entry);
            entry.Cancellation.Dispose();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>队列里的任务 + 它的入队时间与取消源。</summary>
    /// <param name="Job">任务。</param>
    /// <param name="EnqueuedAt">入队时刻。</param>
    /// <param name="Cancellation">
    /// 这个任务自己的取消源（M7-g）：取消一个任务不影响同队列里的其它任务。
    /// 在**落终态时**释放（<c>ProcessQueueAsync</c> / <c>ClearFinished</c> / <c>TrimFinished</c>）。
    /// </param>
    private sealed record QueuedJob(InstallJob Job, DateTimeOffset EnqueuedAt, CancellationTokenSource Cancellation);

    /// <summary>同步转发的进度接收者（见 <c>RunOneAsync</c> 里为什么不用 <see cref="Progress{T}"/>）。</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        /// <inheritdoc />
        public void Report(T value) => report(value);
    }
}
