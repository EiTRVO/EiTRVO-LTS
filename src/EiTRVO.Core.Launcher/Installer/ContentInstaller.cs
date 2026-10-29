using System.Globalization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// 从 Modrinth 下载内容（M7-d）：解析必需依赖 → 下载并校验 → 把被替换的旧版本移进备份。
/// <para>
/// 六条刻意的设计，每条都对应一类真实事故：
/// </para>
/// <list type="number">
/// <item><b>先把依赖全部解出来，再下第一个字节</b>：解析到一半发现某个必需依赖在 Modrinth 上查不到时，
/// 已经下好的那几个文件只会让实例处于"半套"状态。先解析完再下载，失败时 <c>mods\</c> 一个字节都没动。</item>
/// <item><b>只有 <c>required</c> 被递归</b>：<c>optional</c> 是可选增强（替用户做决定是错的）、
/// <c>embedded</c> 已经打进 jar 里、<c>incompatible</c> 是"别装"。而 <c>required</c> 里也有两种：
/// 带 <c>version_id</c> 的才下（官方直接给了答案），只带 <c>project_id</c> 的那种放下 —
/// 替用户挑一个版本，比缺一个依赖更容易把实例弄坏。</item>
/// <item><b><c>visited</c> 集合同时按版本与项目去重</b>：依赖图可以有环（A 依赖 B、B 依赖 A），
/// 也可以有两个模组依赖同一个库。按项目去重还负责另一件事 —— 同一个项目的两个版本不会都被下下来
/// （那正是"一个模组两份"的另一种形态）。</item>
/// <item><b>本机已装着这个项目就不再下它的依赖</b>：依赖要的是"这个项目在不在"，
/// 而不是"哪个版本在"。拿一个和当前实例不匹配的版本去覆盖用户已有的模组，比缺一个依赖糟得多。</item>
/// <item><b>更新 = 下新的 + 把旧的**移走**</b>。旧版本的文件名通常和新版本不同，只下不删的后果是
/// 同一个模组的两份 jar 同时在 <c>mods\</c> 里（游戏起不来，或行为随机）。移动而不是删除：
/// 目标是 <c>&lt;游戏目录&gt;\backups\mods\&lt;时间戳&gt;\</c>，用户随时能拿回来 ——
/// 换版本是常规操作，不该有"点错了就没了"的代价。</item>
/// <item><b>远端给的文件名是不可信输入</b>：落盘前按单段路径名校验（去穿越、去非法字符、去保留设备名），
/// 落地路径还要再证一次"确实在 <c>mods\</c> 里"。与整合包解压（P1-6）是同一类防护。</item>
/// </list>
/// <para>
/// <b>失败不回滚已下好的文件</b>：每个文件都过了官方给的 SHA-1，单独看都是完整的模组文件；
/// 重试时下载器会跳过校验通过的那些，只补缺的（与 <c>JavaRuntimeInstaller</c> 同一条纪律）。
/// </para>
/// </summary>
public sealed class ContentInstaller
{
    /// <summary>模组目录名。</summary>
    private const string ModsDirectoryName = "mods";
    private const string ResourcePacksDirectoryName = "resourcepacks";

    /// <summary>备份目录名（与存档备份同一个 <c>backups\</c>，见 <c>FileContentCatalog.HasBackup</c>）。</summary>
    private const string BackupDirectoryName = "backups";

    /// <summary>一次任务最多下多少个文件（依赖图的硬上限，防病态展开）。</summary>
    private const int MaxPlannedFiles = 256;

    /// <summary>依赖递归的最大层数（同样只是保险丝：正常依赖图不超过两三层）。</summary>
    private const int MaxDependencyDepth = 8;

    /// <summary>文件名长度上限（Modrinth 上确实有很长的名字，但再长就不像个文件名了）。</summary>
    private const int MaxFileNameLength = 128;

    private readonly ModrinthClient _client;
    private readonly FileDownloader _downloader;
    private readonly DiskSpaceChecker _diskSpace;
    private readonly IInstanceCatalog _instances;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造内容安装器。</summary>
    /// <param name="client">Modrinth 客户端（批量取版本，含文件与依赖）。</param>
    /// <param name="downloader">下载器（校验优先、<c>.part</c> 原子落盘）。</param>
    /// <param name="diskSpace">磁盘预检。</param>
    /// <param name="instances">实例目录（游戏目录的唯一来源）。</param>
    /// <param name="clock">时间源（备份目录名要唯一）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public ContentInstaller(
        ModrinthClient client,
        FileDownloader downloader,
        DiskSpaceChecker diskSpace,
        IInstanceCatalog instances,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(diskSpace);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _client = client;
        _downloader = downloader;
        _diskSpace = diskSpace;
        _instances = instances;
        _clock = clock;
        _diagnostics = diagnostics;
    }

    /// <summary>下载一批内容到目标实例的 <c>mods\</c> 目录。</summary>
    /// <param name="request">请求（实例 + 目标 + 本机已装的项目）。</param>
    /// <param name="source">下载源策略（2026-10-23 起只走官方地址）。</param>
    /// <param name="concurrency">并发下载数。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段变化接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果。</returns>
    public async Task<InstallResult> InstallAsync(
        ContentDownloadRequest request,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        int concurrency = 8,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        InstanceLaunchInfo launch;

        try
        {
            launch = _instances.GetLaunchInfo(request.InstanceName);
        }
        catch (KeyNotFoundException)
        {
            // 实例在排队期间被删掉了：如实说，而不是抛一个 KeyNotFoundException 出去。
            return Failed(
                request.Subject,
                TextTemplate.Of("实例 {0} 已经不在了，这次没有下载任何模组。", request.InstanceName));
        }

        var contentDirectory = Path.Combine(
            launch.InstancePath,
            request.Kind == ModrinthContentKind.ResourcePack ? ResourcePacksDirectoryName : ModsDirectoryName);
        var options = new DownloadOptions { Source = source, Concurrency = concurrency };

        // ---- 阶段 1：解析（依赖全部解出来才开下）----
        ReportStage(stages, InstallStageKind.ResolveContent, completed: false);

        ResolutionOutcome resolution;

        try
        {
            resolution = await ResolveAsync(request, contentDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (ModrinthException)
        {
            // 客户端已经记过一条带端点与原因的事件（见 ModrinthClient.Fail）。
            return Failed(request.Subject, TextTemplate.Of("读取 Modrinth 上的版本信息失败，这次没有下载任何文件。"));
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "模组下载任务已取消：{0}",
                [request.Subject]);

            return Failed(request.Subject, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 兜底：调用方是队列，异常漏出去只会让任务卡在"正在执行"。
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Modrinth,
                "模组内容解析异常结束（{0}，{1}）",
                [request.Subject, ex.GetType().Name]);

            return Failed(request.Subject, TextTemplate.Of("解析要下载的模组时出错（{0}）。", ex.GetType().Name));
        }

        if (resolution.Failure is { } reason)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "模组内容解析失败（{0}）：{1}",
                [request.Subject, reason.Key]);

            return Failed(request.Subject, reason);
        }

        ReportStage(stages, InstallStageKind.ResolveContent, completed: true);

        if (resolution.Files.Count == 0)
        {
            return new InstallResult(request.Subject, true, 0, 0, 0, 0, null);
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Modrinth,
            "模组下载任务开始：{0}（目标 {1} 个，含依赖共 {2} 个文件，跳过已装的依赖 {3} 个）",
            [
                request.Subject,
                resolution.TargetCount.ToString(CultureInfo.InvariantCulture),
                resolution.Files.Count.ToString(CultureInfo.InvariantCulture),
                resolution.SkippedInstalled.ToString(CultureInfo.InvariantCulture),
            ]);

        var items = resolution.Files
            .Select(file => new DownloadItem(
                DownloadKind.Content,
                VersionId: string.Empty,
                file.Url,
                file.Sha1,
                file.Size,
                Path.Combine(contentDirectory, StoredFileName(file)),
                file.Sha512))
            .ToList();

        // ---- 阶段 2：下载并校验 ----
        ReportStage(stages, InstallStageKind.Download, completed: false);

        try
        {
            _diskSpace.EnsureAvailable(contentDirectory, DiskSpaceChecker.TotalBytes(items));
        }
        catch (InsufficientDiskSpaceException ex)
        {
            return Failed(
                request.Subject,
                TextTemplate.Of(
                    "磁盘空间不足：还差 {0} MB。",
                    (ex.Report.ShortfallBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)));
        }

        var outcome = await _downloader
            .DownloadAsync(items, options, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.IsComplete)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "模组下载没有完成（{0} 个文件失败，目标 {1}）",
                [
                    outcome.FailedFiles.ToString(CultureInfo.InvariantCulture),
                    request.Subject,
                ]);

            return Failed(request.Subject, Describe(outcome));
        }

        ReportStage(stages, InstallStageKind.Download, completed: true);

        // ---- 阶段 3：把被替换的旧版本移进备份 ----
        ReportStage(stages, InstallStageKind.ReplaceContent, completed: false);

        var replacement = Replace(launch.InstancePath, contentDirectory, resolution.Files);

        ReportStage(stages, InstallStageKind.ReplaceContent, completed: true);

        if (replacement.Failure is not null)
        {
            // 新版本已经下好了，但旧的没移走 —— 必须如实失败：mods 目录里此刻有两份同一个模组。
            return new InstallResult(
                request.Subject,
                false,
                outcome.CompletedFiles,
                outcome.SkippedFiles,
                0,
                outcome.DownloadedBytes,
                replacement.Failure);
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Modrinth,
            "模组下载完成：{0}（下载 {1} 个文件，跳过 {2} 个，替换旧版本 {3} 个）",
            [
                request.Subject,
                outcome.CompletedFiles.ToString(CultureInfo.InvariantCulture),
                outcome.SkippedFiles.ToString(CultureInfo.InvariantCulture),
                replacement.Moved.ToString(CultureInfo.InvariantCulture),
            ]);

        if (replacement.Directory is { Length: > 0 } backup)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Modrinth,
                "被替换的旧版本已移进备份（{0} 个文件 → {1}）",
                [replacement.Moved.ToString(CultureInfo.InvariantCulture), backup]);
        }

        return new InstallResult(
            request.Subject,
            true,
            outcome.CompletedFiles,
            outcome.SkippedFiles,
            0,
            outcome.DownloadedBytes,
            null);
    }

    /// <summary>
    /// 把要下载的文件解析出来：根目标 + 递归的必需依赖。
    /// </summary>
    /// <param name="request">请求。</param>
    /// <param name="contentDirectory">目标 <c>mods\</c> 目录（落地路径要在这里面）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>计划；解析不出来时 <see cref="ResolutionOutcome.Failure"/> 非空（**不抛异常**）。</returns>
    /// <exception cref="ModrinthException">网络或响应失败（由客户端抛出并记事件）。</exception>
    /// <remarks>
    /// 失败走返回值而不是异常：这些都是"能说清原因"的失败（某个版本查不到、文件名不可用），
    /// 与"网络炸了"是两回事 —— 用一个私有异常类型来区分，还得给它配一个能把中文当参数传的构造。
    /// </remarks>
    private async Task<ResolutionOutcome> ResolveAsync(
        ContentDownloadRequest request,
        string contentDirectory,
        CancellationToken cancellationToken)
    {
        // 按**版本**去重挡环（A→B→A），按**项目**去重挡"同一个模组的两个版本"。
        HashSet<string> visitedVersions = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visitedProjects = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> replaceByVersion = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> keepDisabledVersions = new(StringComparer.OrdinalIgnoreCase);

        List<string> level = [];

        foreach (var target in request.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.VersionId))
            {
                continue;
            }

            var versionId = target.VersionId.Trim();
            if (!visitedVersions.Add(versionId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(target.ProjectId))
            {
                visitedProjects.Add(target.ProjectId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(target.ReplacePath))
            {
                replaceByVersion[versionId] = target.ReplacePath;
            }

            if (target.KeepDisabled)
            {
                keepDisabledVersions.Add(versionId);
            }

            level.Add(versionId);
        }

        var targetCount = level.Count;

        // 本机已经装着的项目：**依赖**碰到它们就跳过（根目标不受影响 —— 更新就是要替换它们）。
        foreach (var project in request.InstalledProjectIds)
        {
            if (!string.IsNullOrWhiteSpace(project))
            {
                visitedProjects.Add(project.Trim());
            }
        }

        List<PlannedFile> files = [];
        HashSet<string> fileNames = new(StringComparer.OrdinalIgnoreCase);
        var skippedInstalled = 0;

        for (var depth = 0; level.Count > 0; depth++)
        {
            if (depth >= MaxDependencyDepth)
            {
                return ResolutionOutcome.Failed(TextTemplate.Of(
                    "依赖的层级超过了 {0} 层，这次没有下载任何文件。",
                    MaxDependencyDepth.ToString(CultureInfo.InvariantCulture)));
            }

            var versions = await _client.GetVersionsAsync(level, cancellationToken).ConfigureAwait(false);

            Dictionary<string, ModrinthVersion> found = new(StringComparer.OrdinalIgnoreCase);

            foreach (var version in versions)
            {
                if (!string.IsNullOrWhiteSpace(version.Id))
                {
                    found[version.Id] = version;
                }
            }

            List<string> next = [];

            foreach (var id in level)
            {
                if (!found.TryGetValue(id, out var version))
                {
                    return ResolutionOutcome.Failed(TextTemplate.Of(
                        "Modrinth 上没有找到要装的那个版本（{0}）。",
                        id));
                }

                var (planned, failure) = Planned(version, contentDirectory);

                if (planned is null)
                {
                    return ResolutionOutcome.Failed(failure!);
                }

                if (!fileNames.Add(StoredFileName(planned)))
                {
                    return ResolutionOutcome.Failed(TextTemplate.Of(
                        "这一批里有两个文件同名（{0}），装下去会互相覆盖。",
                        planned.FileName));
                }

                files.Add(planned with
                {
                    ReplacePath = replaceByVersion.GetValueOrDefault(id),
                    KeepDisabled = keepDisabledVersions.Contains(id),
                });

                if (files.Count > MaxPlannedFiles)
                {
                    return ResolutionOutcome.Failed(TextTemplate.Of(
                        "要下载的文件超过 {0} 个，这次没有下载任何文件。",
                        MaxPlannedFiles.ToString(CultureInfo.InvariantCulture)));
                }

                // 资源包没有"模组依赖"这回事：Modrinth 的 resourcepack 版本若挂了依赖，
                // 按模组递归会把别的东西下进 resourcepacks\，所以这里只对模组递归。
                if (request.Kind == ModrinthContentKind.ResourcePack)
                {
                    continue;
                }

                foreach (var dependency in version.Dependencies ?? [])
                {
                    if (!string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(dependency.VersionId))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(dependency.ProjectId)
                        && visitedProjects.Contains(dependency.ProjectId.Trim()))
                    {
                        skippedInstalled++;
                        continue;
                    }

                    var dependencyVersion = dependency.VersionId.Trim();
                    if (!visitedVersions.Add(dependencyVersion))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                    {
                        visitedProjects.Add(dependency.ProjectId.Trim());
                    }

                    next.Add(dependencyVersion);
                }
            }

            level = next;
        }

        return new ResolutionOutcome(files, targetCount, skippedInstalled, null);
    }

    /// <summary>从一个版本里挑出要下载的那个文件（主文件），并校验文件名与落地路径。</summary>
    /// <param name="version">版本。</param>
    /// <param name="contentDirectory">目标 <c>mods\</c> 目录。</param>
    /// <returns>计划里的那一条；挑不出来时第一项为 <see langword="null"/>、第二项是原因。</returns>
    private static (PlannedFile? Planned, TextTemplate? Failure) Planned(ModrinthVersion version, string contentDirectory)
    {
        var id = version.Id ?? string.Empty;

        var candidates = (version.Files ?? [])
            .Where(static file => !string.IsNullOrWhiteSpace(file.Url) && !string.IsNullOrWhiteSpace(file.FileName))
            .ToList();

        // 一个版本可以挂多个文件（主 jar + sources 之类），"第一个"是猜的，官方用 primary 标了答案。
        var file = candidates.FirstOrDefault(static candidate => candidate.Primary)
                   ?? candidates.FirstOrDefault();

        if (file is null)
        {
            return (null, TextTemplate.Of("Modrinth 上的这个版本没有可下载的文件（{0}）。", id));
        }

        var fileName = SafeFileName(file.FileName!);

        if (fileName is null)
        {
            return (null, TextTemplate.Of("Modrinth 给的文件名不能用（{0}）。", file.FileName!.Trim()));
        }

        var targetPath = Path.Combine(contentDirectory, fileName);

        // 文件名已经按单段校验过，这里是第二道：万一拼接结果还是跑出去了，宁可失败也不写。
        if (!PathSafety.IsContained(contentDirectory, targetPath))
        {
            return (null, TextTemplate.Of("Modrinth 给的文件名不能用（{0}）。", fileName));
        }

        return (
            new PlannedFile(
                FileName: fileName,
                Url: file.Url!,
                Sha1: file.Hashes?.Sha1 ?? string.Empty,
                Sha512: file.Hashes?.Sha512 ?? string.Empty,
                Size: file.Size,
                ReplacePath: null,
                KeepDisabled: false),
            null);
    }

    /// <summary>
    /// 远端给的文件名落盘前的校验。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="PathSafety.IsSafePathSegment"/> 而不是自己写一套：单段名（不带分隔符与 <c>..</c>）、
    /// 非 Windows 非法字符、非保留设备名、首尾空格与结尾点是同一条规则，
    /// 而它已经被实例名与版本 id 用过（规则只有一份，改一处不会漏另一处）。
    /// </remarks>
    /// <param name="fileName">远端给的文件名。</param>
    /// <returns>可用的文件名；不可用时为 <see langword="null"/>。</returns>
    private static string? SafeFileName(string fileName)
    {
        var trimmed = fileName.Trim();
        return PathSafety.IsSafePathSegment(trimmed, MaxFileNameLength) ? trimmed : null;
    }

    /// <summary>把被替换掉的旧版本文件移进备份目录。</summary>
    /// <param name="instancePath">实例的游戏目录。</param>
    /// <param name="contentDirectory">该实例的 <c>mods\</c> 目录。</param>
    /// <param name="files">这次下好的文件（带它们各自的 <c>ReplacePath</c>）。</param>
    /// <returns>移走了几个、移到哪里、以及第一个失败原因。</returns>
    private Replacement Replace(string instancePath, string contentDirectory, IReadOnlyList<PlannedFile> files)
    {
        var pending = files.Where(static file => !string.IsNullOrWhiteSpace(file.ReplacePath)).ToList();

        if (pending.Count == 0)
        {
            return new Replacement(0, null, null);
        }

        string? backupDirectory = null;
        var moved = 0;
        TextTemplate? failure = null;

        foreach (var file in pending)
        {
            var source = file.ReplacePath!;
            var target = Path.Combine(contentDirectory, StoredFileName(file));

            // 新版本的文件名和旧的一模一样（同版本重下、或作者改了内容没改名字）：
            // 下载器已经把它覆盖了，没有"旧文件"要移走。
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(source))
            {
                // 用户自己删了/改了名：不是错误，如实跳过。
                continue;
            }

            if (!PathSafety.IsContained(contentDirectory, source))
            {
                // 只允许动 mods 目录里的东西 —— 这个路径来自对账结果，但"来自内部"不是不校验的理由。
                failure ??= TextTemplate.Of("要替换的旧版本不在这个实例的 mods 目录里（{0}），这次没有动它。", source);
                continue;
            }

            try
            {
                backupDirectory ??= BackupDirectory(instancePath);
                File.Move(source, Path.Combine(backupDirectory, Path.GetFileName(source)));
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Modrinth,
                    "替换旧版本文件失败（{0}）：{1}",
                    [file.FileName, ex.GetType().Name]);

                failure ??= TextTemplate.Of(
                    "已经下载了新版本，但旧的 {0} 没能移走（现在 mods 目录里会有同一个模组的两份）。",
                    file.FileName);
            }
        }

        return new Replacement(moved, backupDirectory, failure);
    }

    /// <summary>这次任务要用的备份目录（<c>&lt;游戏目录&gt;\backups\mods\&lt;时间戳&gt;</c>，同一秒内不重名）。</summary>
    /// <param name="instancePath">实例的游戏目录。</param>
    /// <returns>已经创建好的目录。</returns>
    private string BackupDirectory(string instancePath)
    {
        var root = Path.Combine(instancePath, BackupDirectoryName, "mods");
        var stamp = _clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(root, stamp);

        for (var index = 2; Directory.Exists(directory); index++)
        {
            directory = Path.Combine(
                root,
                stamp + "-" + index.ToString(CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(directory);
        return directory;
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
            outcome.FailedFiles.ToString(CultureInfo.InvariantCulture)) with
        {
            Note = first.Reason,
        };
    }

    private static InstallResult Failed(string subject, TextTemplate reason) =>
        new(subject, false, 0, 0, 0, 0, reason);

    /// <summary>上报阶段变化。</summary>
    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    /// <summary>计划里要下载的一个文件。</summary>
    /// <param name="FileName">落盘文件名（已校验）。</param>
    /// <param name="Url">下载地址。</param>
    /// <param name="Sha1">官方给的 SHA-1（为空表示这一项不可校验，下载器会如实标记）。</param>
    /// <param name="Sha512">官方给的 SHA-512；为空表示来源没给（下载器只校验 SHA-1）。</param>
    /// <param name="Size">字节数。</param>
    /// <param name="ReplacePath">装好之后要移走的本地文件；没有时为 <see langword="null"/>。</param>
    /// <param name="KeepDisabled">更新一个被禁用的模组时，新文件也要以 <c>.disabled</c> 落盘。</param>
    private sealed record PlannedFile(
        string FileName,
        string Url,
        string Sha1,
        string Sha512,
        long Size,
        string? ReplacePath,
        bool KeepDisabled);

    /// <summary>落盘文件名：被禁用的模组要保留 <c>.disabled</c> 后缀。</summary>
    private static string StoredFileName(PlannedFile file) =>
        file.KeepDisabled ? file.FileName + DisabledSuffix : file.FileName;

    /// <summary>禁用标记（与内容目录里的命名规则一致）。</summary>
    private const string DisabledSuffix = ".disabled";

    /// <summary>
    /// 解析结果。
    /// </summary>
    /// <param name="Files">要下载的文件。</param>
    /// <param name="TargetCount">用户点的那几个目标（不含递归出来的依赖）。</param>
    /// <param name="SkippedInstalled">因为"本机已经装着这个项目"而跳过的依赖数。</param>
    /// <param name="Failure">
    /// 解析失败的原因（键 + 参数）；成功时为 <see langword="null"/>。
    /// <para>
    /// 把"失败"放进结果里而不是抛异常：这些都是**能说清原因**的失败（某个版本查不到、文件名不可用），
    /// 与"网络炸了"是两回事，前者要原样显示给用户。
    /// </para>
    /// </param>
    private sealed record ResolutionOutcome(
        IReadOnlyList<PlannedFile> Files,
        int TargetCount,
        int SkippedInstalled,
        TextTemplate? Failure)
    {
        /// <summary>一个只有失败原因的解析结果。</summary>
        /// <param name="reason">原因。</param>
        /// <returns>结果。</returns>
        public static ResolutionOutcome Failed(TextTemplate reason) => new([], 0, 0, reason);
    }

    /// <summary>替换旧版本的结果。</summary>
    /// <param name="Moved">移走了几个文件。</param>
    /// <param name="Directory">备份目录（一个都没移时为 <see langword="null"/>）。</param>
    /// <param name="Failure">失败原因（没有失败时为 <see langword="null"/>）。</param>
    private sealed record Replacement(int Moved, string? Directory, TextTemplate? Failure);
}
