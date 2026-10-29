using System.Globalization;
using System.IO.Compression;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content.Modpack;

/// <summary>
/// 一次 <c>.mrpack</c> 导入（M7-e）。
/// </summary>
/// <param name="ArchivePath">整合包文件（<c>.mrpack</c>，本质是 zip）。</param>
/// <param name="GameDirectory">
/// 目标实例的**游戏目录**（<c>IInstanceCatalog.GetLaunchInfo</c> 的 <c>InstancePath</c>）。
/// <para>
/// 刻意不带实例名：导入器只负责"把包的内容铺进这个目录"，建实例与装游戏/加载器是队列那一层的事
/// （与 <c>ContentInstaller</c> 同一条分工）—— 混在一起之后，"先建实例还是先扫包"这种顺序问题
/// 会散落在两处。
/// </para>
/// </param>
public sealed record MrpackImportRequest(string ArchivePath, string GameDirectory);

/// <summary>
/// 一次扫描的结论（M7-e-2）：清单 + 覆盖层之外的零碎统计，可以交给
/// <see cref="MrpackInstaller.InstallAsync(MrpackImportRequest, MrpackPreview?, DownloadSourcePolicy, int, IProgress{FileTransferProgress}?, IProgress{InstallStageChange}?, CancellationToken)"/>
/// 以**跳过重复扫描**。
/// </summary>
/// <remarks>
/// 为什么要让"先扫一次"成为一件独立的事：导入一个整合包的第一件事不是铺文件，而是**建实例** ——
/// 而实例名、游戏版本、加载器三样都在清单里。也就是说包必须先被读懂一次，
/// 而那份结论如果扔掉，队列里紧接着的导入又会把同一份包扫第二遍（大包的条目数上万）。
/// 于是扫描一次、把结论往下传：<b>扫描仍然发生在建实例之前</b>（设计稿 p08 承诺的"先安全扫描，再建任务"），
/// 而"扫过"这个事实只算一次。
/// </remarks>
/// <param name="Failure">非空表示这份包不能用（原因就是它；调用方直接把它当成失败结果返回）。</param>
/// <param name="Index">解析出来的清单；<see cref="Failure"/> 非空时为 <see langword="null"/>。</param>
/// <param name="Subject">目标名（整合包名，取不到时 <see cref="MrpackInstaller.SubjectFallback"/>）。</param>
/// <param name="StrayRootEntries">不在标准目录里的根条目数（只作提示，不阻断）。</param>
public sealed record MrpackPreview(
    InstallResult? Failure,
    ModpackIndex? Index,
    string Subject,
    int StrayRootEntries)
{
    /// <summary>这份包能不能用（能用才允许建实例、才允许碰文件）。</summary>
    public bool IsUsable => Failure is null && Index is not null;
}

/// <summary>
/// 导入一个 <c>.mrpack</c>（M7-e）：安全扫描 → 解析清单 → 下载并校验 → 展开覆盖层 → 复核。
/// </summary>
/// <remarks>
/// 五条刻意的设计，每条都对应一类真实事故：
/// <list type="number">
/// <item><b>扫描在解析之前、下载之前</b>：设计稿承诺过"导入前会先做安全扫描"，而扫描通过之后
/// 才允许碰任何文件 —— 一个越界的 <c>path</c> 或一个不在白名单里的下载源，必须在写出第一个字节之前拦下。</item>
/// <item><b>先下清单里的文件，再展开覆盖层</b>：覆盖层是"层叠"语义（规范原文），后应用的覆盖先前的；
/// 覆盖层覆盖了清单里的文件时**如实计数**（那意味着该文件不再等于清单里声明的哈希）。</item>
/// <item><b>只应用 <c>overrides/</c> 与 <c>client-overrides/</c></b>：<c>server-overrides/</c> 是给
/// 专用服务端的（规范原文），装进客户端实例只会多出一堆服务端专用的东西。</item>
/// <item><b>客户端不支持的文件不下</b>（<c>env.client == "unsupported"</c>）：规范说服务端专用的文件
/// 不该出现在客户端；<c>env</c> 缺失或写 <c>required</c> / <c>optional</c> 都照装。</item>
/// <item><b>失败不回滚、可重跑</b>：每个文件落地都是"先写 <c>.part</c> 再原子改名"（与下载器同一套），
/// 中断只会留下完整文件或没有文件；重跑时下载器会跳过已经校验通过的那些。</item>
/// </list>
/// </remarks>
public sealed class MrpackInstaller
{
    /// <summary>拿不到整合包名字时的目标名（<c>InstallResult.VersionId</c> 不能为空）。</summary>
    public const string SubjectFallback = "mrpack";

    private const string PartSuffix = ".part";

    private readonly ArchiveSafetyScanner _scanner;
    private readonly FileDownloader _downloader;
    private readonly DiskSpaceChecker _diskSpace;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造导入器。</summary>
    /// <param name="scanner">安全扫描器（与下载器共用同一个出站白名单）。</param>
    /// <param name="downloader">下载器（sha1 + sha512 两个摘要都校验）。</param>
    /// <param name="diskSpace">磁盘预检。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public MrpackInstaller(
        ArchiveSafetyScanner scanner,
        FileDownloader downloader,
        DiskSpaceChecker diskSpace,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(diskSpace);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _scanner = scanner;
        _downloader = downloader;
        _diskSpace = diskSpace;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 只扫一遍、只读清单：**不改动任何文件**，先看清楚这份包是什么（M7-e-2）。
    /// </summary>
    /// <param name="archivePath">整合包路径。</param>
    /// <returns>扫描结论；失败时 <see cref="MrpackPreview.Failure"/> 就是给用户看的原因。</returns>
    /// <remarks>
    /// 它**不抛异常**：读不出来、格式不对、扫描不通过都变成 <see cref="MrpackPreview.Failure"/> ——
    /// 队列那一层要拿它决定"建不建实例"，用一个可能抛的方法会逼出 try/catch 包着一半流程的写法。
    /// </remarks>
    public MrpackPreview Preview(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var subject = SubjectFallback;
        ModpackIndex index;
        ArchiveSafetyReport report;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            var entry = archive.GetEntry(MrpackReader.IndexEntryName);

            if (entry is null)
            {
                return new MrpackPreview(
                    Failed(subject, TextTemplate.Of("这不是一个整合包：压缩包里没有 {0}。", MrpackReader.IndexEntryName)),
                    null,
                    subject,
                    0);
            }

            using (var stream = entry.Open())
            {
                index = MrpackReader.Read(stream);
            }

            subject = string.IsNullOrWhiteSpace(index.Name) ? SubjectFallback : index.Name.Trim();

            report = _scanner.Scan(archivePath, index);
        }
        catch (ModpackException ex)
        {
            // 原因本身就是一条给用户看的结论（格式版本 / game 类型 / 路径越界 / 缺校验值）。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "整合包清单读不出来（{0}）：{1}",
                [Path.GetFileName(archivePath), ex.Reason.Key]);

            return new MrpackPreview(Failed(subject, ex.Reason), null, subject, 0);
        }
        catch (InvalidDataException ex)
        {
            return new MrpackPreview(
                Failed(subject, TextTemplate.Of("这个压缩包已经损坏或不是 zip（{0}）。", ex.GetType().Name)),
                null,
                subject,
                0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MrpackPreview(
                Failed(subject, TextTemplate.Of("读不到这个整合包（{0}）。", ex.GetType().Name)),
                null,
                subject,
                0);
        }

        if (report.HasFindings)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "整合包没有通过安全扫描（{0}）：{1}",
                [
                    Path.GetFileName(archivePath),
                    report.Findings[0].Key,
                ]);

            // 两句话各是键，用 Note 接（§5.3 第 ⑦ 条）：第一句说"几项"，第二条是**第一项**的具体原因。
            return new MrpackPreview(
                Failed(
                    subject,
                    TextTemplate.Of(
                        "整合包没有通过安全扫描（{0} 项）。",
                        report.Findings.Count.ToString(CultureInfo.InvariantCulture)) with
                    {
                        Note = report.Findings[0],
                    }),
                null,
                subject,
                report.StrayRootEntries);
        }

        return new MrpackPreview(null, index, subject, report.StrayRootEntries);
    }

    /// <summary>把一个整合包铺进目标游戏目录。</summary>
    /// <param name="request">请求。</param>
    /// <param name="preview">
    /// 已经做过的扫描结论（M7-e-2 的队列会传）；<see langword="null"/> 表示这里自己扫一遍。
    /// </param>
    /// <param name="source">下载源策略。</param>
    /// <param name="concurrency">并发下载数。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果（<c>VersionId</c> 是整合包名）。</returns>
    public async Task<InstallResult> InstallAsync(
        MrpackImportRequest request,
        MrpackPreview? preview = null,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        int concurrency = 8,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GameDirectory);

        var options = new DownloadOptions { Source = source, Concurrency = concurrency };

        // ---- 阶段 1：拿到清单与扫描结论（队列已经扫过就直接用）----
        ModpackIndex index;
        string subject;

        if (preview is null)
        {
            ReportStage(stages, InstallStageKind.ScanArchive, completed: false);

            preview = Preview(request.ArchivePath);

            if (preview.Failure is not null)
            {
                return preview.Failure;
            }

            ReportStage(stages, InstallStageKind.ScanArchive, completed: true);
        }
        else if (!preview.IsUsable)
        {
            // 调用方递进来一份"不能用"的结论：如实返回它，**不要**在这里重新扫一遍 ——
            // 重扫只会把同一条失败原因再算一次，还多一次打开用户文件。
            return preview.Failure ?? Failed(preview.Subject, TextTemplate.Of("这个整合包没有可用的清单。"));
        }

        index = preview.Index!;
        subject = preview.Subject;

        if (preview.StrayRootEntries > 0)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Modrinth,
                "整合包里有 {0} 个不在标准目录里的条目，已忽略。",
                [preview.StrayRootEntries.ToString(CultureInfo.InvariantCulture)]);
        }

        if (index.UnknownDependencies is { Count: > 0 })
        {
            // 规范明说依赖键随时可能加新的：**忽略但要说出**——否则"装完了却起不来"没有线索。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "整合包声明了本启动器不认识的依赖，已忽略：{0}",
                [string.Join(", ", index.UnknownDependencies)]);
        }

        var clientSkipped = index.Files.Count(static file => !file.ClientSupported);

        if (clientSkipped > 0)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Modrinth,
                "整合包里有 {0} 个文件标明客户端不装，已跳过。",
                [clientSkipped.ToString(CultureInfo.InvariantCulture)]);
        }

        // ---- 阶段 2：下载清单里的文件（sha1 + sha512 两个都校验）----
        var wanted = index.Files.Where(static file => file.ClientSupported).ToList();
        var downloadable = wanted.Where(static file => file.Downloads.Count > 0).ToList();

        var items = downloadable
            .Select(file => new DownloadItem(
                DownloadKind.Content,
                VersionId: string.Empty,
                file.Downloads[0],
                file.Sha1,
                file.Size,
                Target(request.GameDirectory, file),
                file.Sha512))
            .ToList();

        ReportStage(stages, InstallStageKind.Download, completed: false);

        DownloadOutcome outcome = new(0, 0, 0, 0, []);

        if (items.Count > 0)
        {
            try
            {
                _diskSpace.EnsureAvailable(request.GameDirectory, DiskSpaceChecker.TotalBytes(items));
            }
            catch (InsufficientDiskSpaceException ex)
            {
                return Failed(
                    subject,
                    TextTemplate.Of(
                        "磁盘空间不足：还差 {0} MB。",
                        (ex.Report.ShortfallBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)));
            }

            outcome = await _downloader
                .DownloadAsync(items, options, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.IsComplete)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.Modrinth,
                    "整合包的文件没有下完（{0} 个失败，{1} 个已跳过）",
                    [
                        outcome.FailedFiles.ToString(CultureInfo.InvariantCulture),
                        outcome.SkippedFiles.ToString(CultureInfo.InvariantCulture),
                    ]);

                return Failed(subject, Describe(outcome));
            }
        }

        ReportStage(stages, InstallStageKind.Download, completed: true);

        // ---- 阶段 3：展开覆盖层（overrides/ → client-overrides/）----
        ReportStage(stages, InstallStageKind.ApplyOverrides, completed: false);

        int overrides;
        int overwritten;
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = ZipFile.OpenRead(request.ArchivePath);
            (overrides, overwritten) = await ExtractOverridesAsync(
                    archive,
                    request.GameDirectory,
                    provided,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Failed(
                subject,
                TextTemplate.Of("展开整合包的覆盖层失败（{0}）：{1}", ex.GetType().Name, ex.Message));
        }

        ReportStage(stages, InstallStageKind.ApplyOverrides, completed: true);

        if (overrides > 0)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Modrinth,
                "整合包的覆盖层写入了 {0} 个文件（其中 {1} 个覆盖了清单里的文件）",
                [
                    overrides.ToString(CultureInfo.InvariantCulture),
                    overwritten.ToString(CultureInfo.InvariantCulture),
                ]);
        }

        // 清单里"没有下载地址"的文件只能由覆盖层提供：规范没给它们哈希校验的机会，
        // 但至少要说清"它不在" —— 否则用户得到的是一个缺文件的实例。
        foreach (var file in wanted.Where(static file => file.Downloads.Count == 0))
        {
            if (!File.Exists(Target(request.GameDirectory, file)))
            {
                return Failed(
                    subject,
                    TextTemplate.Of(
                        "清单里的 {0} 没有下载地址，覆盖层里也没有这个文件。",
                        file.Path));
            }
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Modrinth,
            "整合包导入完成：{0}（下载 {1} 个文件，覆盖层 {2} 个文件）",
            [
                subject,
                outcome.CompletedFiles.ToString(CultureInfo.InvariantCulture),
                overrides.ToString(CultureInfo.InvariantCulture),
            ]);

        return new InstallResult(
            subject,
            true,
            outcome.CompletedFiles,
            outcome.SkippedFiles,
            overrides,
            outcome.DownloadedBytes,
            null);
    }

    /// <summary>清单里的相对路径 → 实例游戏目录下的绝对路径（**经路径包容性校验**）。</summary>
    private static string Target(string gameDirectory, ModpackFile file) =>
        PathSafety.CombineWithin(gameDirectory, [.. file.Segments]);

    /// <summary>
    /// 按顺序展开两个覆盖层目录，把文件铺进游戏目录。
    /// </summary>
    /// <param name="archive">整合包。</param>
    /// <param name="gameDirectory">目标游戏目录。</param>
    /// <param name="provided">收集覆盖层写入的相对路径（给"没有下载地址"的文件做核对）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>写入的文件数、其中覆盖了已有文件的个数。</returns>
    private static async Task<(int Written, int Overwritten)> ExtractOverridesAsync(
        ZipArchive archive,
        string gameDirectory,
        HashSet<string> provided,
        CancellationToken cancellationToken)
    {
        var written = 0;
        var overwritten = 0;
        var entries = 0;
        long totalBytes = 0;

        // **顺序就是语义**：后一个目录覆盖前一个（规范原文的 layer based approach）。
        foreach (var prefix in ArchiveSafetyScanner.OverrideDirectories)
        {
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');

                if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = normalized[prefix.Length..];

                if (relative.Length == 0 || relative.EndsWith('/'))
                {
                    continue;
                }

                // 扫描阶段已经拦过越界与危险类型；这里是第二道（扫描器是共用组件，不能假设调用方一定先跑过它）。
                if (!ModpackFilePaths.TryNormalize(relative, out var safe, out var segments))
                {
                    continue;
                }

                var target = PathSafety.CombineWithin(gameDirectory, [.. segments]);

                if (File.Exists(target))
                {
                    overwritten++;
                }

                // `.part` 与目标同目录：目录要先建出来（`CommitDownload` 只建目标那一次，
                // 而写 `.part` 发生在它之前 —— 少这一句就是 DirectoryNotFoundException）。
                var directory = Path.GetDirectoryName(target);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var part = target + PartSuffix;
                entries++;

                if (entries > ArchiveSafetyScanner.MaxEntries
                    || entry.Length > ArchiveSafetyScanner.MaxSingleEntryBytes)
                {
                    throw new InvalidDataException(
                        "整合包覆盖层的条目数或单个条目大小超过安全上限，已拒绝展开。");
                }

                // 扫描器只信 zip 中央目录声明的大小；真正写盘时按实际读出的字节数再挡一次。
                long entryBytes = 0;

                try
                {
                    await using (var source = entry.Open())
                    await using (var destination = new FileStream(
                                     part,
                                     FileMode.Create,
                                     FileAccess.Write,
                                     FileShare.None,
                                     bufferSize: 81920,
                                     useAsync: true))
                    {
                        var buffer = new byte[81920];
                        int read;

                        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            entryBytes += read;
                            totalBytes += read;

                            if (entryBytes > ArchiveSafetyScanner.MaxSingleEntryBytes
                                || totalBytes > ArchiveSafetyScanner.MaxTotalUncompressedBytes)
                            {
                                throw new InvalidDataException(
                                    "整合包覆盖层解压后超过安全上限，已拒绝展开（可能是 zip 炸弹）。");
                            }

                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    TryDelete(part);
                    throw;
                }

                AtomicFile.CommitDownload(part, target);
                provided.Add(safe);
                written++;
            }
        }

        return (written, overwritten);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理是尽力而为；原始异常更重要。
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
}
