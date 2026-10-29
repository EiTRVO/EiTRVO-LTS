using System.Globalization;
using System.IO.Compression;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// 存档的备份与恢复（M7-f / P1-9）：一个存档目录 ↔ <c>&lt;游戏目录&gt;\backups\&lt;存档目录名&gt;.zip</c>。
/// </summary>
/// <remarks>
/// <para>
/// 备份位置只有一处约定，而且**读取端（<c>FileContentCatalog.HasBackup</c>）与写入端是同一份**：
/// <c>backups\&lt;存档目录名&gt;.zip</c>，文件名用**目录名**而不是显示名 —— 显示名来自
/// <c>level.dat</c> 里的 <c>LevelName</c>，两个存档可以同名，目录名不会。
/// </para>
/// <para>
/// 六条刻意的设计，每条对应一类真实事故：
/// </para>
/// <list type="number">
/// <item><b>先写 <c>.part</c>，复核通过才改名</b>：一份写了一半的 zip 会被读取端当成"已备份"，
/// 而用户真正需要它的时候（存档坏了）才发现里面是空的。改名是原子的，所以要么是旧的完整备份、
/// 要么是新的完整备份。</item>
/// <item><b>复核不是装饰</b>：写完立刻重新打开归档、核对条目数与字节数，不一致就**不动**旧备份。</item>
/// <item><b>读不到文件就失败，不静默跳过</b>（与 Neo 的同名实现有意不同：它 <c>catch { }</c> 跳过被占用的文件）。
/// 少几个文件的备份看起来"成功"，用户拿它恢复时会丢掉那部分存档 —— 宁可失败并说清
/// "游戏正在运行时存档会被占用，请先退出游戏"。</item>
/// <item><b>恢复先把当前存档整目录改名到 <c>backups\saves\&lt;时间戳&gt;\</c></b>，再放回备份 ——
/// 不是覆盖、也不是删除。恢复是破坏性最小的写法：任何一步失败都把原存档搬回原位；
/// 真的搬不回去时，如实说它现在在哪。</item>
/// <item><b>解压先落到暂存目录</b>：备份读完、文件都落地之前，一个字节都没碰用户的存档。
/// 暂存目录在 <c>backups\.staging</c> 下（同一个卷才谈得上改名）。</item>
/// <item><b>归档条目是不可信输入</b>：条目名逐条过 <see cref="PathSafety"/>（同一条"路径穿越"防线，
/// 与整合包解压共用），并带条目数与解压字节数的保险丝 —— 手动放进 <c>backups\</c> 的 zip
/// 不是我们写的。</item>
/// </list>
/// <para>
/// **每个存档只保留一份备份**：再次备份会覆盖上一次（<c>File.Move(overwrite: true)</c>）——
/// 文件名就是约定，没有"第二份"的位置。这条限制登记在 CHANGELOG 与 App README 里。
/// </para>
/// </remarks>
public sealed class WorldBackupInstaller
{
    /// <summary>存档目录名。</summary>
    public const string SavesDirectoryName = "saves";

    /// <summary>备份目录名（与 <c>FileContentCatalog</c> 的读取端、M7-d 的模组备份同一个 <c>backups\</c>）。</summary>
    public const string BackupDirectoryName = "backups";



    /// <summary>解压暂存目录（成功或失败都会清掉）。</summary>
    private const string StagingDirectoryName = ".staging";

    private const string ZipSuffix = ".zip";
    private const string PartialSuffix = ".part";
    private const int CopyBufferSize = 81920;

    /// <summary>归档条目数上限（保险丝：一个存档几万个文件已经很夸张了）。</summary>
    private const int MaxEntries = 200_000;

    /// <summary>解压后总字节上限（保险丝：64 GB）。</summary>
    private const long MaxUncompressedBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>递归枚举：不跟重解析点（一个指向上级的 Junction 能让遍历走不完）。</summary>
    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造存档备份器。</summary>
    /// <param name="clock">时间源（回收目录名要唯一）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public WorldBackupInstaller(IClock clock, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _clock = clock;
        _diagnostics = diagnostics;
    }

    /// <summary>把一个存档目录打包成 <c>&lt;游戏目录&gt;\backups\&lt;存档目录名&gt;.zip</c>。</summary>
    /// <param name="gameDirectory">实例的游戏目录（存档在它下面的 <c>saves\</c> 里）。</param>
    /// <param name="target">要备份的存档（目录名 + 显示名）。</param>
    /// <param name="progress">进度接收者（文件数 + 字节数）。</param>
    /// <param name="stages">阶段变化接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果（成功时 <c>NativeFilesExtracted</c> = 写进归档的文件数）。</returns>
    /// <exception cref="OperationCanceledException">用户取消（队列把它翻成"已取消。"）。</exception>
    public async Task<InstallResult> BackupAsync(
        string gameDirectory,
        WorldBackupTarget target,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(target);

        if (!PathSafety.IsSafePathSegment(target.Folder))
        {
            return Failed(target.Name, TextTemplate.Of("存档目录名不可用（{0}），这次没有备份。", target.Folder));
        }

        var source = Path.Combine(gameDirectory, SavesDirectoryName, target.Folder);

        if (!Directory.Exists(source))
        {
            return Failed(
                target.Name,
                TextTemplate.Of("找不到这个存档的目录（{0}）——它可能已经被删掉或改名了。", target.Folder));
        }

        // ---- 阶段 1：统计（进度要有分母；同时把"读不动的文件"在这里就暴露出来）----
        ReportStage(stages, InstallStageKind.MeasureWorld, completed: false);

        Measurement measurement;

        try
        {
            measurement = await Task.Run(() => Measure(source, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "备份存档未开始（{0}）：读不到文件（{1}）",
                [target.Name, ex.GetType().Name]);

            return Failed(
                target.Name,
                TextTemplate.Of("读不到这个存档的文件（{0}）。游戏正在运行时存档会被占用，请先退出游戏再试。", ex.GetType().Name));
        }

        ReportStage(stages, InstallStageKind.MeasureWorld, completed: true);

        var backupDirectory = Path.Combine(gameDirectory, BackupDirectoryName);
        var destination = Path.Combine(backupDirectory, target.Folder + ZipSuffix);
        var partial = destination + PartialSuffix;

        // ---- 阶段 2：压缩 ----
        ReportStage(stages, InstallStageKind.BackupWorld, completed: false);

        try
        {
            Directory.CreateDirectory(backupDirectory);

            await Task.Run(
                () => Write(measurement, source, partial, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DeleteFile(partial);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DeleteFile(partial);

            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Launcher,
                "备份存档失败（{0}，{1}）",
                [target.Name, ex.GetType().Name]);

            return Failed(target.Name, TextTemplate.Of("写入备份失败（{0}：{1}）。", ex.GetType().Name, ex.Message));
        }

        ReportStage(stages, InstallStageKind.BackupWorld, completed: true);

        // ---- 阶段 3：复核（重新打开刚写好的归档）----
        ReportStage(stages, InstallStageKind.VerifyArchive, completed: false);

        if (!Verify(partial, measurement.Files.Count, measurement.Bytes))
        {
            DeleteFile(partial);

            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "备份复核没通过（{0}）：写出来的归档与源目录不一致",
                [target.Name]);

            return Failed(target.Name, TextTemplate.Of("备份写完之后复核没通过，这次没有替换掉旧的备份。"));
        }

        // 覆盖上一次的同名备份：每个存档只留一份（见 FileContentCatalog.HasBackup）。
        File.Move(partial, destination, overwrite: true);

        ReportStage(stages, InstallStageKind.VerifyArchive, completed: true);

        _diagnostics.Write(
            LauncherEventLevel.Success,
            DiagnosticsSources.Launcher,
            "备份存档完成：{0}（{1} 个文件，{2} 字节）",
            [
                target.Name,
                measurement.Files.Count.ToString(CultureInfo.InvariantCulture),
                measurement.Bytes.ToString(CultureInfo.InvariantCulture),
            ]);

        return new InstallResult(target.Name, true, 0, 0, measurement.Files.Count, measurement.Bytes, null);
    }

    /// <summary>把 <c>&lt;游戏目录&gt;\backups\&lt;存档目录名&gt;.zip</c> 放回 <c>saves\</c>。</summary>
    /// <param name="gameDirectory">实例的游戏目录。</param>
    /// <param name="target">要恢复的存档（目录名 + 显示名）。</param>
    /// <param name="progress">进度接收者（解压的文件数 + 字节数）。</param>
    /// <param name="stages">阶段变化接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果（成功时 <c>NativeFilesExtracted</c> = 解出来的文件数）。</returns>
    /// <exception cref="OperationCanceledException">用户取消（队列把它翻成"已取消。"）。</exception>
    public async Task<InstallResult> RestoreAsync(
        string gameDirectory,
        WorldBackupTarget target,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(target);

        if (!PathSafety.IsSafePathSegment(target.Folder))
        {
            return Failed(target.Name, TextTemplate.Of("存档目录名不可用（{0}），这次没有动你的存档。", target.Folder));
        }

        var savesDirectory = Path.Combine(gameDirectory, SavesDirectoryName);
        var backupDirectory = Path.Combine(gameDirectory, BackupDirectoryName);
        var archivePath = Path.Combine(backupDirectory, target.Folder + ZipSuffix);

        if (!File.Exists(archivePath))
        {
            return Failed(target.Name, TextTemplate.Of("这个存档还没有备份，先在「备份所选」里做一次。"));
        }

        var stagingRoot = Path.Combine(backupDirectory, StagingDirectoryName);
        var staging = CreateUniqueDirectory(stagingRoot);
        var current = Path.Combine(savesDirectory, target.Folder);
        string? stashed = null;

        try
        {
            // ---- 阶段 1：解压到暂存目录（此时还没动用户的存档）----
            ReportStage(stages, InstallStageKind.ExtractBackup, completed: false);

            var extraction = await Task.Run(
                () => Extract(archivePath, staging, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (extraction.Failure is not null)
            {
                return Failed(target.Name, extraction.Failure);
            }

            if (extraction.Files == 0)
            {
                // 空归档不是"恢复成功"：用户会得到一个空的存档目录，而备份里什么都没有。
                return Failed(target.Name, TextTemplate.Of("这个备份里一个文件都没有，这次没有动你的存档。"));
            }

            ReportStage(stages, InstallStageKind.ExtractBackup, completed: true);

            // ---- 阶段 2：把当前存档整目录改名进回收目录（不是删掉）----
            ReportStage(stages, InstallStageKind.StashWorld, completed: false);

            if (Directory.Exists(current))
            {
                // 回收目录刻意叫 <c>backups\saves\</c>：与 M7-d 的 <c>backups\mods\</c> 同一形状
                // （"被这次操作换下来的 saves / mods 里的东西"），而读取端只认 <c>&lt;目录名&gt;.zip</c>，
                // 所以这个目录永远不会被算成"已备份"。
                var replaceRoot = CreateUniqueDirectory(Path.Combine(backupDirectory, SavesDirectoryName));
                stashed = Path.Combine(replaceRoot, target.Folder);
                await Task.Run(() => Directory.Move(current, stashed), cancellationToken).ConfigureAwait(false);
            }

            ReportStage(stages, InstallStageKind.StashWorld, completed: true);

            // ---- 阶段 3：把解压好的存档改名回原位 ----
            ReportStage(stages, InstallStageKind.PromoteWorld, completed: false);

            Directory.CreateDirectory(savesDirectory);
            await Task.Run(() => Directory.Move(staging, current), cancellationToken).ConfigureAwait(false);

            ReportStage(stages, InstallStageKind.PromoteWorld, completed: true);

            _diagnostics.Write(
                LauncherEventLevel.Success,
                DiagnosticsSources.Launcher,
                "恢复存档完成：{0}（{1} 个文件，{2} 字节）",
                [
                    target.Name,
                    extraction.Files.ToString(CultureInfo.InvariantCulture),
                    extraction.Bytes.ToString(CultureInfo.InvariantCulture),
                ]);

            return new InstallResult(target.Name, true, 0, 0, extraction.Files, extraction.Bytes, null);
        }
        catch (OperationCanceledException)
        {
            MoveBack(stashed, current, target.Folder);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MoveBack(stashed, current, target.Folder);

            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Launcher,
                "恢复存档失败（{0}，{1}）",
                [target.Name, ex.GetType().Name]);

            return Failed(target.Name, TextTemplate.Of("恢复存档失败（{0}：{1}）。", ex.GetType().Name, ex.Message));
        }
        finally
        {
            // 暂存目录在任何路径下都要清掉：失败时它是一份半成品，留在 backups 里只会让人误会。
            DeleteDirectory(staging);
            DeleteEmptyDirectory(stagingRoot);
        }
    }

    /// <summary>统计要打包的文件（确定性顺序 + 总字节数）。</summary>
    /// <param name="directory">存档目录。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>文件清单与总字节数。</returns>
    private static Measurement Measure(string directory, CancellationToken cancellationToken)
    {
        List<string> files = [];
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*", RecursiveFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            total += new FileInfo(file).Length;
            files.Add(file);
        }

        // 确定性顺序：两次备份同一个没变的存档应当得到逐字节相同的归档（便于比对与排障）。
        files.Sort(StringComparer.OrdinalIgnoreCase);

        return new Measurement(files, total);
    }

    /// <summary>把统计出来的文件写进 <c>.part</c> 归档。</summary>
    /// <param name="measurement">文件清单。</param>
    /// <param name="source">存档目录（用来算相对路径）。</param>
    /// <param name="partial">目标 <c>.part</c> 文件。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private static void Write(
        Measurement measurement,
        string source,
        string partial,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.Open(partial, ZipArchiveMode.Create);

        long completed = 0;
        var index = 0;

        foreach (var file in measurement.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');

            // CompressionLevel.Fastest 是有意的：存档里主要是已经压过的 region 文件（.mca 里是 zlib 块），
            // Optimal 换不来体积，只换来几分钟 CPU。
            var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);

            using var entryStream = entry.Open();
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

            input.CopyTo(entryStream);
            completed += input.Length;
            index++;

            progress?.Report(new FileTransferProgress(
                CompletedFiles: index,
                TotalFiles: measurement.Files.Count,
                CompletedBytes: completed,
                TotalBytes: measurement.Bytes,
                CurrentFile: relative,
                BytesPerSecond: 0,
                Remaining: null));
        }
    }

    /// <summary>复核归档：重新打开它，核对条目数与声明的字节数。</summary>
    /// <param name="partial">刚写好的归档。</param>
    /// <param name="expectedFiles">期望的条目数。</param>
    /// <param name="expectedBytes">期望的字节数。</param>
    /// <returns>一致时为 <see langword="true"/>。</returns>
    private static bool Verify(string partial, int expectedFiles, long expectedBytes)
    {
        try
        {
            using var archive = ZipFile.OpenRead(partial);

            var files = 0;
            long bytes = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                files++;
                bytes += entry.Length;
            }

            return files == expectedFiles && bytes == expectedBytes;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>把归档解到暂存目录（逐条目校验路径穿越，并带条目数与字节数保险丝）。</summary>
    /// <param name="archivePath">归档。</param>
    /// <param name="staging">暂存目录。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>解压结果（失败原因是键 + 参数，不抛异常）。</returns>
    private static Extraction Extract(
        string archivePath,
        string staging,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ZipArchive archive;

        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return Extraction.Failed(
                TextTemplate.Of("这个备份已经损坏或不是 zip（{0}）。", ex.GetType().Name));
        }

        using (archive)
        {
            // 先量一遍（进度要有分母），顺手用声明的体积挡一次明显的炸弹。
            var totalFiles = 0;
            long totalBytes = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                totalFiles++;
                totalBytes += Math.Max(0, entry.Length);

                if (totalFiles > MaxEntries)
                {
                    return Extraction.Failed(TextTemplate.Of(
                        "这个备份里的文件超过 {0} 个，看起来不像一个存档备份。",
                        MaxEntries.ToString(CultureInfo.InvariantCulture)));
                }

                if (totalBytes > MaxUncompressedBytes)
                {
                    return Extraction.Failed(TextTemplate.Of(
                        "这个备份解压后超过 {0} GB，看起来不像一个存档备份。",
                        (MaxUncompressedBytes / (1024L * 1024 * 1024)).ToString(CultureInfo.InvariantCulture)));
                }
            }

            var buffer = new byte[CopyBufferSize];
            var files = 0;
            long bytes = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var destination = Path.Combine(staging, entry.FullName);

                if (!PathSafety.IsContained(staging, destination))
                {
                    // 手动放进 backups\ 的 zip 不是我们写的：条目名同样要过穿越检查。
                    return Extraction.Failed(
                        TextTemplate.Of("备份里的条目越出了这个存档的目录（{0}）。", entry.FullName));
                }

                var parent = Path.GetDirectoryName(destination);

                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bytes += read;

                    // 声明的体积可以撒谎：真正写出去的字节数才作数。
                    if (bytes > MaxUncompressedBytes)
                    {
                        return Extraction.Failed(TextTemplate.Of(
                            "这个备份解压后超过 {0} GB，看起来不像一个存档备份。",
                            (MaxUncompressedBytes / (1024L * 1024 * 1024)).ToString(CultureInfo.InvariantCulture)));
                    }

                    output.Write(buffer, 0, read);
                }

                files++;

                progress?.Report(new FileTransferProgress(
                    CompletedFiles: files,
                    TotalFiles: totalFiles,
                    CompletedBytes: bytes,
                    TotalBytes: totalBytes,
                    CurrentFile: entry.FullName,
                    BytesPerSecond: 0,
                    Remaining: null));
            }

            return new Extraction(files, bytes, null);
        }
    }

    /// <summary>恢复失败时把换下来的旧存档搬回原位（搬不动就如实说它现在在哪）。</summary>
    /// <param name="stashed">旧存档现在的位置；没换过就是 <see langword="null"/>。</param>
    /// <param name="current">旧存档本来的位置。</param>
    /// <param name="folder">存档目录名（事件里用）。</param>
    private void MoveBack(string? stashed, string current, string folder)
    {
        if (stashed is null || !Directory.Exists(stashed) || Directory.Exists(current))
        {
            return;
        }

        try
        {
            Directory.Move(stashed, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Launcher,
                "恢复失败后没能把原来的存档放回原位（{0}，它还在备份目录的 saves 里）：{1}",
                [folder, ex.GetType().Name]);
        }
    }

    /// <summary>建一个不重名的目录（同一秒内连续两次操作也不会撞）。</summary>
    /// <param name="root">父目录。</param>
    /// <returns>已经建好的目录。</returns>
    private string CreateUniqueDirectory(string root)
    {
        var stamp = _clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(root, stamp);

        for (var index = 2; Directory.Exists(directory); index++)
        {
            directory = Path.Combine(root, stamp + "-" + index.ToString(CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉一个 .part 不该盖过真正的失败原因。
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 同上：暂存目录留着顶多多占点地方，不该盖过真正的失败原因。
        }
    }

    /// <summary>清掉空的暂存根目录（它只为放子目录而存在，留着只会让人猜它是干什么的）。</summary>
    /// <param name="path">暂存根目录。</param>
    private static void DeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着：它里面是空的。
        }
    }

    private static InstallResult Failed(string subject, TextTemplate reason) =>
        new(subject, false, 0, 0, 0, 0, reason);

    /// <summary>上报阶段变化。</summary>
    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    /// <summary>要打包的文件清单与总字节数。</summary>
    /// <param name="Files">文件绝对路径（已排序）。</param>
    /// <param name="Bytes">总字节数。</param>
    private sealed record Measurement(IReadOnlyList<string> Files, long Bytes);

    /// <summary>
    /// 解压结果。
    /// </summary>
    /// <param name="Files">解出来的文件数。</param>
    /// <param name="Bytes">解出来的字节数（真实写盘的量，不是归档声明的）。</param>
    /// <param name="Failure">失败原因（键 + 参数）；成功时为 <see langword="null"/>。</param>
    private sealed record Extraction(int Files, long Bytes, TextTemplate? Failure)
    {
        /// <summary>一个只有失败原因的结果。</summary>
        /// <param name="reason">原因。</param>
        /// <returns>结果。</returns>
        public static Extraction Failed(TextTemplate reason) => new(0, 0, reason);
    }
}
