using System.Globalization;
using System.Security.Cryptography;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Versions;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// 获取 Mojang 官方 Java 运行时（M3 的残留，§12.7）：索引 → 挑组件 → 下载并校验 → 布局 → 搬运。
/// <para>
/// 五个刻意的选择：
/// </para>
/// <list type="number">
/// <item><b>先下到暂存目录，成功后才整体搬进 <c>runtime</c></b>。探测是按目录扫 <c>bin\javaw.exe</c> 的，
/// 直接往最终目录里下会在中途留下一个"半个运行时"：文件够多时它甚至能跑起 <c>java -version</c>，
/// 于是 p10 会把它当成一个可用的 Java 列出来，而游戏在真正用它的那一刻才失败。
/// 暂存目录在下载缓存下（不在扫描根里），因此半成品**不可能**被发现成"已安装"。</item>
/// <item><b>已经装好就什么都不做</b>：按清单逐条核对（存在 + SHA-1，无 sha1 的比字节数），
/// 全部对得上就直接报成功。重复点按钮、或装完再点一次都不该重下 100 MB。
/// 判别依据是**清单**而不是一个标记文件：标记文件只省一次哈希，却多出一份可能与实际不符的状态。</item>
/// <item><b>失败不回滚、暂存目录保留</b>：下载器逐文件校验，第二次尝试会跳过已正确的那些
/// （断网续下就是最常见的重试场景）。代价是 Mojang 更新某个组件版本时这一次要整份重下 ——
/// 换来的是"升级装到一半失败，不会留下一个半新半旧的运行时"。</item>
/// <item><b>挑哪一代由数据决定</b>：版本 JSON 声明的 <c>javaVersion</c> 优先（Mojang 自己写的答案），
/// 组件名与可用版本全部来自官方索引，本地不维护一张"Java 21 对应哪个组件名"的表 ——
/// 那张表迟早会过期，而索引每周都在更新。</item>
/// <item><b>Windows 上不需要 <c>chmod</c></b>：清单里的 <c>executable</c> 标记只如实记账，不处理。</item>
/// </list>
/// </summary>
public sealed class JavaRuntimeInstaller
{
    /// <summary>还没挑出组件时，失败结果里的目标名（<c>InstallResult.VersionId</c> 不能为空）。</summary>
    public const string SubjectFallback = "java-runtime";

    private readonly JavaRuntimeManifestClient _client;
    private readonly VersionJsonStore _versions;
    private readonly FileDownloader _downloader;
    private readonly DiskSpaceChecker _diskSpace;
    private readonly ILauncherPaths _paths;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly IClock _clock;

    /// <summary>构造运行时安装器。</summary>
    /// <param name="client">官方运行时清单客户端。</param>
    /// <param name="versions">版本 JSON 存储（读声明的 <c>javaVersion</c>）。</param>
    /// <param name="downloader">下载器（校验优先、<c>.part</c> 原子落盘）。</param>
    /// <param name="diskSpace">磁盘预检。</param>
    /// <param name="paths">数据目录布局（运行时目录、下载缓存、回收目录）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    /// <param name="clock">时间源（回收目录名要唯一）。</param>
    public JavaRuntimeInstaller(
        JavaRuntimeManifestClient client,
        VersionJsonStore versions,
        FileDownloader downloader,
        DiskSpaceChecker diskSpace,
        ILauncherPaths paths,
        IDiagnosticsLog diagnostics,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(diskSpace);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(clock);

        _client = client;
        _versions = versions;
        _downloader = downloader;
        _diskSpace = diskSpace;
        _paths = paths;
        _diagnostics = diagnostics;
        _clock = clock;
    }

    /// <summary>获取一个官方 Java 运行时。</summary>
    /// <param name="request">请求（要适配的游戏版本；为空表示取最新一代）。</param>
    /// <param name="gameDirectory">游戏根目录（读那里面已安装版本的版本 JSON）。</param>
    /// <param name="source">下载源策略（2026-10-23 起只走官方地址）。</param>
    /// <param name="concurrency">并发下载数。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段变化接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果（<c>VersionId</c> 是组件名，如 <c>java-runtime-gamma</c>）。</returns>
    public async Task<InstallResult> InstallAsync(
        JavaRuntimeRequest request,
        string gameDirectory,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        int concurrency = 8,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var options = new DownloadOptions
        {
            Source = source,
            Concurrency = concurrency,
        };

        // ---- 阶段 1：读官方索引并挑一个组件 ----
        ReportStage(stages, InstallStageKind.ResolveRuntime, completed: false);

        var (requiredMajor, componentHint) = ResolveRequirement(gameDirectory, request.GameVersion);

        JavaRuntimePlan plan;
        JavaRuntimeCandidate candidate;

        try
        {
            var index = await _client.FetchIndexAsync(cancellationToken).ConfigureAwait(false);

            candidate = index.Select(JavaRuntimePlatform.Current, requiredMajor, componentHint)
                        ?? throw new JavaRuntimeDataException(
                            requiredMajor is { } major
                                ? $"官方索引里没有能提供 Java {major}（{JavaRuntimePlatform.Current}）的组件。"
                                : $"官方索引里没有 {JavaRuntimePlatform.Current} 平台的运行时组件。");

            var manifest = await _client.FetchManifestAsync(candidate, cancellationToken).ConfigureAwait(false);

            plan = JavaRuntimePlanner.Plan(manifest, _paths, candidate);
        }
        catch (JavaRuntimeDataException ex)
        {
            // 还没挑出组件：目标名只能是这个占位（`InstallResult.VersionId` 不能为空）。
            return Failed(SubjectFallback, TextTemplate.Of("获取官方运行时索引失败：{0}", ex.Message));
        }

        ReportStage(stages, InstallStageKind.ResolveRuntime, completed: true);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.JavaRuntime,
            "已选定官方 Java 运行时：{0} {1}（主版本 {2}，需要 {3}）",
            [
                candidate.Component,
                candidate.VersionName,
                candidate.MajorVersion.ToString(CultureInfo.InvariantCulture),
                requiredMajor?.ToString(CultureInfo.InvariantCulture) ?? "（最新一代）",
            ]);

        // ---- 已经装好就什么都不做 ----
        if (await IsCompleteAsync(plan.InstallDirectory, plan, cancellationToken).ConfigureAwait(false))
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.JavaRuntime,
                "Java 运行时已是最新（{0} {1}），跳过下载：{2}",
                [plan.Component, plan.VersionName, plan.InstallDirectory]);

            return new InstallResult(plan.Component, true, 0, plan.Files.Count, 0, 0, null);
        }

        // ---- 阶段 2：下载到暂存目录 ----
        ReportStage(stages, InstallStageKind.Download, completed: false);

        if (!TryPreflight(plan.StagingDirectory, plan.TotalBytes, out var diskFailure))
        {
            return Failed(plan.Component, diskFailure ?? TextTemplate.Of("磁盘空间不足。"));
        }

        var outcome = await _downloader
            .DownloadAsync(plan.Files, options, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.IsComplete)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.JavaRuntime,
                "{0} 的下载没有完成（{1} 个文件失败；已下载的部分留在暂存目录，重试会接着下）",
                [plan.Component, outcome.FailedFiles.ToString(CultureInfo.InvariantCulture)]);

            return Failed(plan.Component, Describe(outcome));
        }

        try
        {
            MaterializeDirectories(plan);
            MaterializeLinks(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Failed(plan.Component, TextTemplate.Of("运行时文件布局失败：{0}", ex.Message));
        }

        if (plan.StagingExecutablePath is null || !File.Exists(plan.StagingExecutablePath))
        {
            return Failed(
                plan.Component,
                TextTemplate.Of(
                    "「{0}」的文件都下载完了，但没有找到可执行的 java（清单里既没有 bin/javaw.exe 也没有 bin/java.exe）。",
                    plan.Component));
        }

        ReportStage(stages, InstallStageKind.Download, completed: true);

        // ---- 阶段 3：搬进最终目录 ----
        ReportStage(stages, InstallStageKind.PromoteRuntime, completed: false);

        try
        {
            Promote(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(plan.Component, TextTemplate.Of("把运行时搬到 {0} 失败：{1}", plan.InstallDirectory, ex.Message));
        }

        ReportStage(stages, InstallStageKind.PromoteRuntime, completed: true);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.JavaRuntime,
            "{0} {1} 已就绪：{2}（新下 {3} 个、跳过 {4} 个文件）",
            [
                plan.Component,
                plan.VersionName,
                plan.InstallDirectory,
                outcome.CompletedFiles.ToString(CultureInfo.InvariantCulture),
                outcome.SkippedFiles.ToString(CultureInfo.InvariantCulture),
            ]);

        return new InstallResult(
            plan.Component,
            true,
            outcome.CompletedFiles,
            outcome.SkippedFiles,
            0,
            outcome.DownloadedBytes,
            null);
    }

    /// <summary>
    /// 这个游戏版本要哪一代 Java：**版本 JSON 里声明的优先**（C-9 的同一条规则），
    /// 没有本地 JSON 时才用规则表。
    /// </summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="gameVersion">游戏版本（可为 <see langword="null"/> = 没有上下文）。</param>
    /// <returns>所需主版本（无上下文时为 <see langword="null"/>）与声明的组件名。</returns>
    private (int? RequiredMajor, string? ComponentHint) ResolveRequirement(string gameDirectory, string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return (null, null);
        }

        // 版本 id 会被拼成一个文件路径，因此不安全的 id 一律不读文件；规则表仍然给出答案。
        var declared = PathSafety.IsSafePathSegment(gameVersion)
            ? _versions.Load(gameDirectory, gameVersion)?.JavaVersion
            : null;

        var major = declared?.MajorVersion is > 0 and var value ? value : JavaRequirement.RequiredMajor(gameVersion);

        return (major, declared?.Component);
    }

    /// <summary>
    /// 最终目录里是不是已经有一份**与这份清单逐条对得上**的运行时。
    /// </summary>
    /// <remarks>
    /// 逐条核对而不是只看"目录在不在"：过期的运行时（Mojang 换了版本）与被人删掉几个文件的运行时
    /// 都必须重装，否则用户会得到一个"界面说已装好、启动时崩"的 Java。
    /// </remarks>
    /// <param name="directory">要核对的目录。</param>
    /// <param name="plan">计划（相对布局的唯一依据）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>完整返回 <see langword="true"/>。</returns>
    private static async Task<bool> IsCompleteAsync(
        string directory,
        JavaRuntimePlan plan,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var planDirectory in plan.Directories)
        {
            if (!Directory.Exists(ReRoot(planDirectory, plan, directory)))
            {
                return false;
            }
        }

        foreach (var file in plan.Files)
        {
            var path = ReRoot(file.TargetPath, plan, directory);

            if (!File.Exists(path))
            {
                return false;
            }

            if (!file.IsVerifiable)
            {
                // 没有 sha1 的条目（清单没给）：只能比字节数，比不了就只认"文件在"。
                if (file.Size > 0 && new FileInfo(path).Length != file.Size)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(await ComputeSha1Async(path, cancellationToken).ConfigureAwait(false), file.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把"暂存目录下的某个路径"换算成"同相对位置在目标目录下的路径"。</summary>
    private static string ReRoot(string path, JavaRuntimePlan plan, string directory) =>
        Path.Combine(directory, Path.GetRelativePath(plan.StagingDirectory, path));

    /// <summary>建出清单声明的目录（可能有空目录）。</summary>
    private static void MaterializeDirectories(JavaRuntimePlan plan)
    {
        foreach (var directory in plan.Directories)
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// 物化清单里的链接。
    /// <para>
    /// Windows 上创建符号链接需要特权（或开发者模式），而 <c>windows-x64</c> 的清单里目前**一条链接都没有**
    /// （实测：484 条 = 402 文件 + 82 目录）。所以这里是防御性的：退化成"复制一份目标"，
    /// 目标不存在就**如实失败**，而不是留下一条指向空气的链接让 JVM 在启动时才报错。
    /// </para>
    /// </summary>
    private static void MaterializeLinks(JavaRuntimePlan plan)
    {
        foreach (var link in plan.Links)
        {
            var source = PathSafety.CombineWithin(
                plan.StagingDirectory,
                JavaRuntimePathSafety.Segments(link.Target));

            var target = PathSafety.CombineWithin(
                plan.StagingDirectory,
                JavaRuntimePathSafety.Segments(link.RelativePath));

            if (Directory.Exists(source))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (!File.Exists(source))
            {
                throw new IOException($"链接「{link.RelativePath}」的目标不存在：{link.Target}。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }

    /// <summary>
    /// 把暂存目录整体搬进最终目录。目标已存在时**先改名搬进回收目录**，而不是递归删除 ——
    /// 与实例删除同一条纪律：我们造出来的东西也不该被无声抹掉，何况里面可能有下次能复用的文件。
    /// </summary>
    private void Promote(JavaRuntimePlan plan)
    {
        var parent = Path.GetDirectoryName(plan.InstallDirectory);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (Directory.Exists(plan.InstallDirectory))
        {
            Directory.Move(plan.InstallDirectory, UniqueRecyclePath(plan));
        }

        Directory.Move(plan.StagingDirectory, plan.InstallDirectory);
    }

    /// <summary>回收目录里一个不重名的目标路径。</summary>
    private string UniqueRecyclePath(JavaRuntimePlan plan)
    {
        var stamp = _clock.Now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = $"java-runtime-{plan.Component}-{plan.Platform}-{stamp}";
        var candidate = Path.Combine(_paths.RecycleDirectory, baseName);

        for (var suffix = 2; Directory.Exists(candidate) && suffix < 100; suffix++)
        {
            candidate = Path.Combine(_paths.RecycleDirectory, $"{baseName}-{suffix}");
        }

        return candidate;
    }

    private bool TryPreflight(string directory, long requiredBytes, out TextTemplate? failure)
    {
        try
        {
            // 余量沿用同一条规则（natives、.part 与最终文件短暂共存、启动后写日志）：
            // 这里没有 natives，但少留一点等于让用户"刚好差 20 MB"在 99% 处失败。
            _diskSpace.EnsureAvailable(directory, requiredBytes);
            failure = null;
            return true;
        }
        catch (InsufficientDiskSpaceException ex)
        {
            failure = TextTemplate.Of("磁盘空间不足：还差 {0} MB。", (ex.Report.ShortfallBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return false;
        }
    }

    private static TextTemplate Describe(DownloadOutcome outcome)
    {
        var first = outcome.Failures.Count > 0 ? outcome.Failures[0] : null;

        if (first is null)
        {
            return TextTemplate.Of("下载未完成。");
        }

        return TextTemplate.Of(
            "{0} 个文件失败。",
            outcome.FailedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)) with
        {
            Note = first.Reason,
        };
    }

    private static InstallResult Failed(string subject, TextTemplate reason) =>
        new(subject, false, 0, 0, 0, 0, reason);

    /// <summary>上报阶段变化。</summary>
    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    /// <summary>计算文件的 SHA-1（清单协议规定的完整性字段，不是我们选的安全机制）。</summary>
    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

#pragma warning disable CA5350
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5350

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
