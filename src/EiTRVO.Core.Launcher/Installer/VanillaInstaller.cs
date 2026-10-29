using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>一次安装的结果。</summary>
/// <param name="VersionId">版本 id；装 Java 运行时（§12.7）时为组件名，还没挑出组件时为 <c>java-runtime</c>。</param>
/// <param name="Succeeded">是否全部成功（含 natives 解压与版本 JSON 落盘）。</param>
/// <param name="FilesDownloaded">本次真正下载的文件数。</param>
/// <param name="FilesSkipped">本地已正确、因而跳过的文件数。</param>
/// <param name="NativeFilesExtracted">解压 / 展开出来的文件数（natives，或整合包的覆盖层，见 M7-e）。</param>
/// <param name="Bytes">本次写入的字节数。</param>
/// <param name="Failure">失败原因（键 + 参数）；成功时为 <see langword="null"/>。</param>
public sealed record InstallResult(
    string VersionId,
    bool Succeeded,
    int FilesDownloaded,
    int FilesSkipped,
    int NativeFilesExtracted,
    long Bytes,
    TextTemplate? Failure);

/// <summary>
/// 原版安装（B-4 的执行部分）：磁盘预检 → 下载 → natives 解压 → 资源布局 → 落版本 JSON。
/// <para>
/// 顺序上有三条硬要求：
/// </para>
/// <list type="number">
/// <item><b>先预检再下载</b>：一次原版安装是几百 MB，磁盘在 90% 处写满时用户已经花掉十几分钟流量。</item>
/// <item><b>全部成功才写版本 JSON</b>：那个文件是"这个版本已安装"的**唯一依据**（见
/// <see cref="VersionJsonStore.TryReadInstalled"/>）。下载失败却先落 JSON 的后果是
/// 界面显示"已安装"、点启动却崩 —— 比直接报错糟得多。</item>
/// <item><b>资源索引要下下来才知道要下哪些资源</b>：所以是两段式（先索引、再对象），
/// 并且**每段之前各自预检一次**。</item>
/// </list>
/// </summary>
public sealed class VanillaInstaller
{
    private readonly VersionJsonStore _versions;
    private readonly VersionManifestClient _manifest;
    private readonly FileDownloader _downloader;
    private readonly DiskSpaceChecker _diskSpace;
    private readonly NativeExtractor _natives;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造安装器。</summary>
    /// <param name="versions">版本 JSON 存储（读取 + 落盘）。</param>
    /// <param name="manifest">版本清单（本地没有版本 JSON 时按 id 找它的地址）。</param>
    /// <param name="downloader">下载器。</param>
    /// <param name="diskSpace">磁盘预检。</param>
    /// <param name="natives">natives 解压。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public VanillaInstaller(
        VersionJsonStore versions,
        VersionManifestClient manifest,
        FileDownloader downloader,
        DiskSpaceChecker diskSpace,
        NativeExtractor natives,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(diskSpace);
        ArgumentNullException.ThrowIfNull(natives);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _versions = versions;
        _manifest = manifest;
        _downloader = downloader;
        _diskSpace = diskSpace;
        _natives = natives;
        _diagnostics = diagnostics;
    }

    /// <summary>安装一个版本。</summary>
    /// <param name="versionId">版本 id。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="platform">平台（库与 natives 的筛选依据）。</param>
    /// <param name="source">下载源策略。</param>
    /// <param name="concurrency">并发下载数。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段变化接收者（p06 的阶段表用）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>安装结果。</returns>
    public async Task<InstallResult> InstallAsync(
        string versionId,
        string gameDirectory,
        GamePlatform platform,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        int concurrency = 8,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(platform);

        var options = new DownloadOptions
        {
            Source = source,
            Concurrency = concurrency,
        };

        // ---- 版本 JSON 必须先在本地；本地没有就按 id 去清单里找它的地址取回来 ----
        ReportStage(stages, InstallStageKind.ResolveVersion, completed: false);

        ResolvedVersion resolved;
        try
        {
            resolved = await ResolveVersionAsync(versionId, gameDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (VersionResolutionException ex)
        {
            return Failed(versionId, ex.Reason);
        }
        catch (VersionDataException ex)
        {
            // 版本数据层（清单 / 版本 JSON / 加载器 JSON）的消息是**技术细节**：
            // 它作为参数挂在一句用户能读懂的话后面（同 BCL 的 ex.Message）。
            return Failed(versionId, TextTemplate.Of("获取版本数据失败：{0}", ex.Message));
        }

        ReportStage(stages, InstallStageKind.ResolveVersion, completed: true);

        return await InstallResolvedAsync(
            resolved,
            gameDirectory,
            platform,
            source,
            concurrency,
            progress,
            stages,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 安装一份**已经在内存里合并好的**版本：下载 → natives 解压 → 落版本 JSON。
    /// <para>
    /// 单独一个公开成员的原因：加载器安装（M4-f）拿到的是"远端 profile + 原版"合并出来的版本，
    /// 它**不在磁盘上**，因此没法走 <see cref="InstallAsync"/> 的"按 id 解析"那一段。
    /// 把下载与落盘这一段共用，是为了让"下载全部成功才写版本 JSON"这条规则只有一份实现。
    /// </para>
    /// </summary>
    /// <param name="resolved">已合并继承链的版本（<c>Merged.Id</c> 决定落盘目录名）。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="platform">平台。</param>
    /// <param name="source">下载源策略。</param>
    /// <param name="concurrency">并发下载数。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段变化接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>安装结果。</returns>
    public async Task<InstallResult> InstallResolvedAsync(
        ResolvedVersion resolved,
        string gameDirectory,
        GamePlatform platform,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        int concurrency = 8,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(platform);

        var versionId = string.IsNullOrWhiteSpace(resolved.Merged.Id) ? resolved.BaseVersionId : resolved.Merged.Id;
        var options = new DownloadOptions
        {
            Source = source,
            Concurrency = concurrency,
        };

        ReportStage(stages, InstallStageKind.Download, completed: false);

        var plan = InstallPlanner.Plan(resolved, gameDirectory, platform);

        var aggregator = new ProgressAggregator(progress);
        var downloaded = 0;
        var skipped = 0;
        long bytes = 0;

        // ---- 第一段：客户端 JAR、库、natives 归档、资源索引、日志配置 ----
        aggregator.StartPhase(plan.Files.Count, plan.TotalBytes);

        if (!TryPreflight(plan.GameDirectory, plan.TotalBytes, out var diskFailure))
        {
            return Failed(versionId, diskFailure ?? TextTemplate.Of("磁盘空间不足。"));
        }

        var first = await _downloader
            .DownloadAsync(plan.Files, options, aggregator, cancellationToken)
            .ConfigureAwait(false);

        downloaded += first.CompletedFiles;
        skipped += first.SkippedFiles;
        bytes += first.DownloadedBytes;

        if (!first.IsComplete)
        {
            return Failed(versionId, Describe(first));
        }

        // ---- 第二段：资源对象（要先把索引读出来）----
        var (assets, assetIndex) = await PlanAssetsAsync(plan, cancellationToken).ConfigureAwait(false);

        if (assets.Count > 0)
        {
            var assetBytes = DiskSpaceChecker.TotalBytes(assets);

            aggregator.StartPhase(assets.Count, assetBytes, addToPrevious: true);

            if (!TryPreflight(plan.GameDirectory, assetBytes, out diskFailure))
            {
                return Failed(versionId, diskFailure ?? TextTemplate.Of("磁盘空间不足。"));
            }

            var second = await _downloader
                .DownloadAsync(assets, options, aggregator, cancellationToken)
                .ConfigureAwait(false);

            downloaded += second.CompletedFiles;
            skipped += second.SkippedFiles;
            bytes += second.DownloadedBytes;

            if (!second.IsComplete)
            {
                return Failed(versionId, Describe(second));
            }

            ApplyAssetLayout(plan, assetIndex, gameDirectory);
        }

        // ---- natives 解压 ----
        ReportStage(stages, InstallStageKind.Download, completed: true);
        ReportStage(stages, InstallStageKind.ExtractNatives, completed: false);

        var nativeFiles = 0;
        foreach (var extraction in plan.Natives)
        {
            try
            {
                var result = _natives.Extract(extraction.ArchivePath, extraction.TargetDirectory, extraction.Exclude);
                nativeFiles += result.Extracted;

                if (result.Extracted == 0)
                {
                    // 一个 natives 归档里一个文件都没有，基本只有一个原因：归档拿错了。
                    _diagnostics.Write(
                        LauncherEventLevel.Warning,
                        DiagnosticsSources.Downloader,
                        "{0} 里没有可解压的文件",
                        [Path.GetFileName(extraction.ArchivePath)]);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Failed(versionId, TextTemplate.Of("natives 解压失败：{0}", ex.Message));
            }
        }

        // ---- 最后才写版本 JSON：它是"已安装"的唯一依据 ----
        try
        {
            WriteVersionJson(plan, resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(versionId, TextTemplate.Of("写入版本 JSON 失败：{0}", ex.Message));
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Downloader,
            "版本 {0} 安装完成（新下 {1} 个文件、跳过 {2} 个）",
            [
                versionId,
                downloaded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                skipped.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);

        ReportStage(stages, InstallStageKind.ExtractNatives, completed: true);

        return new InstallResult(versionId, true, downloaded, skipped, nativeFiles, bytes, null);
    }

    /// <summary>
    /// 拿到这个版本的（已合并继承链的）版本 JSON：本地有就用本地的，否则按 id 去清单里找地址取回来。
    /// <para>
    /// 让安装器自己完成这一步，是为了让 <see cref="InstallAsync"/> 对调用方就是一个
    /// "装这个版本"的动作 —— 否则"首次安装"会得到一个莫名其妙的"本地没有版本 JSON"。
    /// </para>
    /// <para>
    /// 加载器安装（M4-f）也用它：profile 只声明 <c>inheritsFrom</c>，父版本的完整 JSON 得从这里来，
    /// 而它**不要求原版已经装好** —— 库与客户端 JAR 会在同一次下载里补齐。
    /// </para>
    /// </summary>
    /// <param name="versionId">版本 id。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>已合并的版本。</returns>
    /// <exception cref="VersionResolutionException">清单里没有这个版本，或继承链缺父版本。</exception>
    public async Task<ResolvedVersion> ResolveVersionAsync(
        string versionId,
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        if (_versions.Load(gameDirectory, versionId) is not null)
        {
            return _versions.Resolve(gameDirectory, versionId);
        }

        var url = FindVersionUrl(versionId);

        if (url is null)
        {
            // 本机还没缓存清单：取一次，再找。
            await _manifest.FetchAsync(cancellationToken).ConfigureAwait(false);
            url = FindVersionUrl(versionId);
        }

        if (url is null)
        {
            throw new VersionResolutionException(
                TextTemplate.Of("版本清单里没有「{0}」，无法安装。", versionId),
                [versionId]);
        }

        await _versions.FetchAsync(versionId, url, cancellationToken).ConfigureAwait(false);
        return _versions.Resolve(gameDirectory, versionId);
    }

    /// <summary>上报阶段变化。</summary>
    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    private string? FindVersionUrl(string versionId)
    {
        var manifest = _manifest.TryReadCache();
        if (manifest is null)
        {
            return null;
        }

        foreach (var version in manifest.Versions)
        {
            if (string.Equals(version.Id, versionId, StringComparison.Ordinal))
            {
                return version.Url;
            }
        }

        return null;
    }

    /// <summary>读取资源索引并展开成下载项（连同索引本身，供后续布局副本使用）。</summary>
    private static async Task<(IReadOnlyList<DownloadItem> Items, AssetIndexContents? Index)> PlanAssetsAsync(
        InstallPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.AssetIndexPath is null || plan.AssetIndexId is null || !File.Exists(plan.AssetIndexPath))
        {
            return ([], null);
        }

        var json = await File.ReadAllTextAsync(plan.AssetIndexPath, cancellationToken).ConfigureAwait(false);
        var index = LauncherJson.Deserialize<AssetIndexContents>(json, LauncherJson.Indented);

        if (index is null)
        {
            throw new VersionDataException($"资源索引（{plan.AssetIndexId}）解析结果为空。");
        }

        return (InstallPlanner.PlanAssetObjects(index, plan.GameDirectory, plan.VersionId), index);
    }

    /// <summary>老版本需要的虚拟 / resources 布局副本。</summary>
    private void ApplyAssetLayout(InstallPlan plan, AssetIndexContents? index, string gameDirectory)
    {
        if (plan.AssetIndexId is null || index is null)
        {
            return;
        }

        var links = InstallPlanner.PlanAssetLayout(index, gameDirectory, plan.AssetIndexId);

        foreach (var link in links)
        {
            try
            {
                if (!File.Exists(link.SourcePath))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(link.TargetPath)!);
                File.Copy(link.SourcePath, link.TargetPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Downloader,
                    "资源布局副本写入失败，已跳过（{0}，{1}）",
                    [Path.GetFileName(link.TargetPath), ex.GetType().Name]);
            }
        }
    }

    /// <summary>
    /// 写版本 JSON（合并后的完整一份）。
    /// <para>
    /// 写的是**继承链已展开**的版本：M4 读到的就是自足的一份，不必再依赖父版本还在磁盘上，
    /// 也避免了"加载器版本还在、原版目录被删了"这种半坏状态。
    /// </para>
    /// </summary>
    private static void WriteVersionJson(InstallPlan plan, ResolvedVersion resolved)
    {
        var json = LauncherJson.Serialize(resolved.Merged, LauncherJson.Indented);
        AtomicFile.WriteAllText(plan.VersionJsonPath, json);
    }

    private bool TryPreflight(string gameDirectory, long requiredBytes, out TextTemplate? failure)
    {
        try
        {
            _diskSpace.EnsureAvailable(gameDirectory, requiredBytes);
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

        // 两句话各自是键，第二句挂在 Note 上（把渲染出来的中文塞进参数会让英文界面露出中文）。
        return TextTemplate.Of(
            "{0} 个文件失败。",
            outcome.FailedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)) with
        {
            Note = first.Reason,
        };
    }

    private static InstallResult Failed(string versionId, TextTemplate reason) =>
        new(versionId, false, 0, 0, 0, 0, reason);

    /// <summary>
    /// 把两段下载的进度合成一条总体进度。
    /// <para>
    /// 第二段的总量在读到资源索引之前是未知的，所以总量会在中途"长大"（进度条先走到 40% 再继续）。
    /// 这是如实反映事实：我们事先确实不知道要下多少资源对象。
    /// </para>
    /// </summary>
    private sealed class ProgressAggregator : IProgress<FileTransferProgress>
    {
        private readonly IProgress<FileTransferProgress>? _inner;
        private int _baseFiles;
        private long _baseBytes;
        private int _phaseTotalFiles;
        private long _phaseTotalBytes;

        public ProgressAggregator(IProgress<FileTransferProgress>? inner) => _inner = inner;

        /// <summary>开始一个新阶段。</summary>
        /// <param name="totalFiles">该阶段文件数。</param>
        /// <param name="totalBytes">该阶段字节数。</param>
        /// <param name="addToPrevious">是否把已完成的上一阶段算进总量（资源阶段为真）。</param>
        public void StartPhase(int totalFiles, long totalBytes, bool addToPrevious = false)
        {
            if (addToPrevious)
            {
                _baseFiles += _phaseTotalFiles;
                _baseBytes += _phaseTotalBytes;
            }
            else
            {
                _baseFiles = 0;
                _baseBytes = 0;
            }

            _phaseTotalFiles = totalFiles;
            _phaseTotalBytes = totalBytes;
        }

        /// <inheritdoc />
        public void Report(FileTransferProgress value)
        {
            if (_inner is null)
            {
                return;
            }

            _inner.Report(value with
            {
                CompletedFiles = _baseFiles + value.CompletedFiles,
                TotalFiles = _baseFiles + _phaseTotalFiles,
                CompletedBytes = _baseBytes + value.CompletedBytes,
                TotalBytes = _baseBytes + _phaseTotalBytes,
            });
        }
    }
}
