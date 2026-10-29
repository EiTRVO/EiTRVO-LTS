using System.Globalization;
using System.IO.Compression;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Versions;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// OptiFine 安装（B-9，M4-i）：跑**官方安装器**（无参主类 <c>optifine.Installer</c>），然后把产物搬进游戏目录。
/// <para>
/// 为什么在**沙箱**里装完再搬，而不是直接指向游戏目录：
/// </para>
/// <list type="number">
/// <item>安装器把目标目录**写死**成 <c>%APPDATA%\.minecraft</c>（真机核过：把 <c>-Duser.home</c> 与
/// <c>APPDATA</c> 指向不同目录时，只有 <c>%APPDATA%\.minecraft</c> 被写）。我们的游戏目录不叫这个名字，
/// 只有"造一个临时的 <c>.minecraft</c> 再搬出来"才能让它装到我们想装的地方。</item>
/// <item>它还会改 <c>launcher_profiles.json</c>。在沙箱里改，用户真实的那个文件就**一个字节都不会动**。</item>
/// <item>打补丁需要**原版客户端 JAR 真的在**（拿假 JAR 试过：<c>IOException: Base resource not found</c>），
/// 所以沙箱里先放一份原版的版本 JSON 与客户端 JAR 的**副本** —— 原版目录本身不被改写。</item>
/// </list>
/// <para>
/// 三条硬要求（与 Forge 安装器同源）：
/// </para>
/// <list type="number">
/// <item><b>必须 headless</b>：安装器的失败路径会弹 <c>JOptionPane</c> 模态框，非 headless 时它会一直等点击
/// ——在启动器里就是"看起来卡死"（本项目实测：没加这个参数的那次探针十分钟不返回）。
/// 加上 <c>-Djava.awt.headless=true</c> 后失败会快速返回并给出退出码 1。</item>
/// <item><b>不运行结构校验不过的东西</b>：optifine.net **不提供校验值**，
/// 所以在跑它之前先确认"这确实是一个 OptiFine 安装器"（含 <c>optifine/Installer.class</c>，
/// 且 MANIFEST 的 <c>Main-Class</c> 是 <c>optifine.InstallerFrame</c>）。
/// 这不是等价于哈希校验的保证，但它把"下载到的其实是个错误页"这类情况钉死了。</item>
/// <item><b>退出码 + 产出两道校验</b>：只看"目录在不在"会把半成品记成成功。</item>
/// </list>
/// </summary>
public sealed class OptiFineInstaller
{
    /// <summary>安装器最长跑多久（它只是打补丁，正常几十秒）。</summary>
    public static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(20);

    /// <summary>安装器 jar 里必须存在的类。</summary>
    public const string InstallerClassEntry = "optifine/Installer.class";

    /// <summary>MANIFEST 里应当出现的 <c>Main-Class</c>（GUI 入口；我们要跑的 <c>optifine.Installer</c> 是另一个无参主类）。</summary>
    public const string InstallerMainClass = "optifine.InstallerFrame";

    /// <summary>沙箱目录名。安装器只认 <c>%APPDATA%\.minecraft</c> 这个固定名字，不能改。</summary>
    public const string SandboxFolderName = ".minecraft";

    private readonly IToolRunner _tools;
    private readonly VanillaInstaller _vanilla;
    private readonly FileDownloader _downloader;
    private readonly VersionJsonStore _versions;
    private readonly OptiFineCatalogClient _catalog;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly ILauncherPaths _paths;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造 OptiFine 安装器。</summary>
    /// <param name="tools">外部工具运行器（跑 <c>java -cp 安装器 optifine.Installer</c>）。</param>
    /// <param name="vanilla">原版安装器（只借它取"这个游戏版本要哪个 Java"）。</param>
    /// <param name="downloader">下载器（下安装器 jar；带 <c>.part</c> 原子提交与重试）。</param>
    /// <param name="versions">版本 JSON 存储（校验原版已装、校验产出）。</param>
    /// <param name="catalog">OptiFine 列表（安装时据此解析取件地址）。</param>
    /// <param name="javaRuntimes">Java 运行时检测结果。</param>
    /// <param name="paths">数据目录（沙箱落在下载缓存下）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public OptiFineInstaller(
        IToolRunner tools,
        VanillaInstaller vanilla,
        FileDownloader downloader,
        VersionJsonStore versions,
        OptiFineCatalogClient catalog,
        IJavaRuntimeCatalog javaRuntimes,
        ILauncherPaths paths,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(vanilla);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(javaRuntimes);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _tools = tools;
        _vanilla = vanilla;
        _downloader = downloader;
        _versions = versions;
        _catalog = catalog;
        _javaRuntimes = javaRuntimes;
        _paths = paths;
        _diagnostics = diagnostics;
    }

    /// <summary>安装一个 OptiFine 版本（原版路线：产出一个独立的 <c>&lt;游戏版本&gt;-OptiFine_&lt;版本段&gt;</c>）。</summary>
    /// <param name="gameVersion">挂载的游戏版本。</param>
    /// <param name="edition">OptiFine 版本段，如 <c>HD_U_I6</c>（来自列表文件名）。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="source">下载源策略。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段变化接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>安装结果。</returns>
    public async Task<InstallResult> InstallAsync(
        string gameVersion,
        string edition,
        string gameDirectory,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(edition);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var versionId = LoaderNaming.VersionId(LoaderKind.OptiFine, edition, gameVersion);

        ReportStage(stages, InstallStageKind.ResolveVersion, completed: false);

        // ---- 原版必须在：安装器要在真客户端 JAR 上打补丁 ----
        var vanillaJson = _versions.TryReadInstalled(gameDirectory, gameVersion);
        var vanillaJar = PathSafety.CombineWithin(gameDirectory, "versions", gameVersion, gameVersion + ".jar");

        if (vanillaJson is null)
        {
            return Failed(
                versionId,
                TextTemplate.Of(
                    "装 OptiFine 之前必须先把原版 {0} 装好：官方安装器要在原版客户端 JAR 上打补丁，而本地没有它的版本 JSON。请先在「版本列表」里安装这个游戏版本。",
                    gameVersion));
        }

        if (!File.Exists(vanillaJar))
        {
            return Failed(
                versionId,
                TextTemplate.Of(
                    "原版 {0} 的客户端 JAR 不在（{1}），安装器无法打补丁。请先重新安装这个游戏版本。",
                    gameVersion,
                    vanillaJar));
        }

        // ---- 解析要装哪一个构建（版本段的唯一来源是列表，不由调用方给地址）----
        OptiFineBuild? build;

        try
        {
            var builds = await _catalog.FetchAsync(gameVersion, cancellationToken).ConfigureAwait(false);
            build = builds.FirstOrDefault(candidate =>
                string.Equals(candidate.Edition, edition.Trim(), StringComparison.Ordinal));
        }
        catch (VersionDataException exception)
        {
            return Failed(versionId, TextTemplate.Of("取 OptiFine 列表失败：{0}", exception.Message));
        }

        if (build is null)
        {
            return Failed(
                versionId,
                TextTemplate.Of(
                    "OptiFine 列表里没有 {0} 的 {1} 这个构建（可能刚被撤下，或列表变了）。请在向导里重新选一次。",
                    gameVersion,
                    edition.Trim()));
        }

        // ---- 官方下载是两跳：先解析 downloadx 一次性地址 ----
        string installerUrl;

        try
        {
            installerUrl = await _catalog.ResolveInstallerUrlAsync(build.FileName, cancellationToken).ConfigureAwait(false);
        }
        catch (VersionDataException exception)
        {
            return Failed(versionId, TextTemplate.Of("解析 OptiFine 官方下载地址失败：{0}", exception.Message));
        }

        // ---- Java：安装器本身要用它运行 ----
        var java = await ChooseJavaAsync(gameVersion, gameDirectory, cancellationToken).ConfigureAwait(false);

        if (java is null)
        {
            return Failed(
                versionId,
                TextTemplate.Of("装 OptiFine 需要先有一个能用的 Java（安装器本身要用它运行）。请到「Java 与设置」页里检测或获取。"));
        }

        var staging = PathSafety.CombineWithin(_paths.DownloadCacheDirectory, "optifine", versionId);
        var sandbox = PathSafety.CombineWithin(staging, SandboxFolderName);

        try
        {
            // 每次从干净沙箱开始：里面只有原版的副本，重来一次的代价远小于"上次的半成品影响这次"。
            DeleteTree(staging);
            Directory.CreateDirectory(sandbox);

            SeedSandbox(sandbox, gameVersion, vanillaJar, gameDirectory);

            ReportStage(stages, InstallStageKind.ResolveVersion, completed: true);
            ReportStage(stages, InstallStageKind.Download, completed: false);

            // ---- 下载安装器 jar（无发布方校验值，先下完再做结构校验）----
            var jarPath = PathSafety.CombineWithin(staging, build.FileName);

            var outcome = await _downloader
                .DownloadAsync(
                    [
                        new DownloadItem(
                            DownloadKind.Library,
                            versionId,
                            installerUrl,
                            Sha1: string.Empty,
                            Size: 0,
                            TargetPath: jarPath),
                    ],
                    new DownloadOptions { Source = source, Concurrency = 1 },
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.IsComplete)
            {
                var first = outcome.Failures.Count > 0 ? outcome.Failures[0] : null;
                return Failed(
                    versionId,
                    first is null
                        ? TextTemplate.Of("OptiFine 安装器下载未完成。")
                        : TextTemplate.Of("下载 OptiFine 安装器失败。") with { Note = first.Reason });
            }

            // ---- 结构校验：确认这确实是 OptiFine 安装器，再决定跑不跑 ----
            var structural = DescribeStructuralProblem(jarPath);

            if (structural is not null)
            {
                return Failed(
                    versionId,
                    TextTemplate.Of(
                        "下载到的文件不是 OptiFine 安装器（{0}）：{1}。已停止安装，未运行任何程序。",
                        structural,
                        build.DownloadUrl));
            }

            ReportStage(stages, InstallStageKind.Download, completed: true);
            ReportStage(stages, InstallStageKind.ExtractNatives, completed: false);

            // ---- 跑官方安装器（headless + 目标目录重定向到沙箱）----
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.ModLoader,
                "正在运行 OptiFine {0} 官方安装器（游戏 {1}，版本 {2}）",
                [build.Edition, gameVersion.Trim(), versionId]);

            // 安装器在沙箱里自己下载 / 打补丁，没有字节进度：报"不确定进度"，别让 p06 停在 0%。
            progress?.Report(new FileTransferProgress(
                CompletedFiles: 0,
                TotalFiles: 0,
                CompletedBytes: 0,
                TotalBytes: 0,
                CurrentFile: string.Empty,
                BytesPerSecond: 0,
                Remaining: null)
            {
                IsIndeterminate = true,
                SourceHost = new Uri(OptiFineCatalog.OfficialDownloadsPage).Host,
                Detail = TextTemplate.Of("正在运行官方安装器"),
            });

            var run = await _tools
                .RunAsync(
                    java.ExecutablePath,
                    [
                        // 失败路径会弹模态框，非 headless 会一直等点击（=看起来卡死）。
                        "-Djava.awt.headless=true",
                        "-Duser.home=" + staging,
                        "-cp",
                        jarPath,
                        "optifine.Installer",
                    ],
                    staging,
                    InstallerTimeout,
                    // 真正决定装到哪儿的变量（真机核过：user.home 只是摆设）。
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["APPDATA"] = staging },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!run.Succeeded)
            {
                var reason = run.TimedOut
                    ? TextTemplate.Of(
                        "OptiFine 安装器超过 {0} 分钟没有结束，已终止。",
                        InstallerTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture))
                    : TextTemplate.Of(
                        "OptiFine 安装器以退出码 {0} 结束。",
                        run.ExitCode.ToString(CultureInfo.InvariantCulture));

                _diagnostics.Write(
                    LauncherEventLevel.Error,
                    DiagnosticsSources.ModLoader,
                    "OptiFine {0} 官方安装器失败（游戏 {1}，退出码 {2}）",
                    [build.Edition, gameVersion.Trim(), run.ExitCode.ToString(CultureInfo.InvariantCulture)]);

                return Failed(
                    versionId,
                    reason with
                    {
                        Note = DescribeOutput(run.OutputTail) is { } output
                            ? output with { Note = DescribeSandbox(sandbox) }
                            : DescribeSandbox(sandbox),
                    });
            }

            // ---- 产出校验 ----
            var produced = PathSafety.CombineWithin(sandbox, "versions", versionId);

            if (!File.Exists(Path.Combine(produced, versionId + ".json")))
            {
                _diagnostics.Write(
                    LauncherEventLevel.Error,
                    DiagnosticsSources.ModLoader,
                    "OptiFine 安装器没有写出预期的版本 JSON：{0}",
                    [versionId]);

                return Failed(
                    versionId,
                    TextTemplate.Of(
                        "安装器报成功，但沙箱里没有 versions/{0}/{0}.json —— 这次安装不算数。",
                        versionId) with { Note = DescribeSandbox(sandbox) });
            }

            // ---- 搬进游戏目录（版本目录 + 新增的 libraries）----
            var promoteFailure = Promote(sandbox, gameDirectory, versionId, out var librariesMoved, out var librariesSkipped);

            if (promoteFailure is not null)
            {
                return Failed(versionId, promoteFailure);
            }

            ReportStage(stages, InstallStageKind.ExtractNatives, completed: true);

            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.ModLoader,
                "OptiFine {0} 安装完成（游戏 {1}，版本 {2}，{3} 个库，{4}）",
                [
                    build.Edition,
                    gameVersion.Trim(),
                    versionId,
                    librariesMoved.ToString(CultureInfo.InvariantCulture),
                    build.IsPreview ? "预览版" : "正式版",
                ]);

            return new InstallResult(
                versionId,
                true,
                FilesDownloaded: 1, // 我们自己下的只有那个安装器 jar；其余写入发生在安装器进程内部。
                FilesSkipped: librariesSkipped,
                NativeFilesExtracted: librariesMoved,
                Bytes: new FileInfo(jarPath).Length,
                Failure: null);
        }
        finally
        {
            // 沙箱与安装器 jar 都不再需要：装完（或失败）之后留着只会占空间。
            TryDeleteTree(staging);
        }
    }

    /// <summary>
    /// 沙箱里放一份原版副本：安装器要读 <c>versions\&lt;游戏版本&gt;\&lt;id&gt;.json</c> 并打补丁到客户端 JAR。
    /// </summary>
    private static void SeedSandbox(string sandbox, string gameVersion, string vanillaJar, string gameDirectory)
    {
        var versionDirectory = PathSafety.CombineWithin(sandbox, "versions", gameVersion);
        Directory.CreateDirectory(versionDirectory);

        var sourceJson = PathSafety.CombineWithin(gameDirectory, "versions", gameVersion, gameVersion + ".json");

        File.Copy(sourceJson, Path.Combine(versionDirectory, gameVersion + ".json"), overwrite: true);
        File.Copy(vanillaJar, Path.Combine(versionDirectory, gameVersion + ".jar"), overwrite: true);

        // 安装器要求目标目录里有 launcher_profiles.json（缺了直接拒绝干活）。
        // 这里写在**沙箱**里，所以它随后对 profile 的写入不会碰到用户真实的那个文件。
        File.WriteAllText(
            Path.Combine(sandbox, "launcher_profiles.json"),
            ForgeInstaller.MinimalLauncherProfiles,
            new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// 把沙箱里的产物搬进游戏目录：版本目录整体搬（同名旧目录先移进回收目录），
    /// <c>libraries</c> 下**只补缺失的文件**（已有的不动）。
    /// </summary>
    /// <returns>失败原因；成功时为 <see langword="null"/>。</returns>
    private TextTemplate? Promote(
        string sandbox,
        string gameDirectory,
        string versionId,
        out int librariesMoved,
        out int librariesSkipped)
    {
        librariesMoved = 0;
        librariesSkipped = 0;

        try
        {
            var source = PathSafety.CombineWithin(sandbox, "versions", versionId);
            var target = PathSafety.CombineWithin(gameDirectory, "versions", versionId);

            if (Directory.Exists(target))
            {
                // 重装：旧的先整目录移进回收目录（不直接删 —— 那是用户机器上已经能跑的东西）。
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var recycled = PathSafety.CombineWithin(_paths.RecycleDirectory, $"optifine-{versionId}-{stamp}");

                Directory.CreateDirectory(_paths.RecycleDirectory);
                MoveDirectoryOrCopy(target, recycled);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MoveDirectoryOrCopy(source, target);

            var sandboxLibraries = Path.Combine(sandbox, "libraries");

            if (Directory.Exists(sandboxLibraries))
            {
                var gameLibraries = PathSafety.CombineWithin(gameDirectory, "libraries");
                MergeLibraries(sandboxLibraries, gameLibraries, ref librariesMoved, ref librariesSkipped);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return TextTemplate.Of("把 OptiFine 产物搬进游戏目录失败：{0}", exception.Message);
        }
    }

    /// <summary>递归补齐缺失的库文件；目标已存在的**不动**（也不覆盖）。</summary>
    private static void MergeLibraries(string source, string target, ref int moved, ref int skipped)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));

            if (File.Exists(destination))
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination);
            moved++;
        }
    }

    /// <summary>同卷用 <see cref="Directory.Move(string, string)"/>；跨卷（自定义游戏目录在别的盘）退化成复制。</summary>
    private static void MoveDirectoryOrCopy(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            // 跨卷移动在 .NET 里就是 IOException：复制过去再删，语义一致。
            CopyTree(source, destination);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// 结构校验：是不是一个 OptiFine 安装器。
    /// <para>
    /// 这是**替代校验值**的一道检查：两个来源都不给发布方哈希，所以退一步确认"它确实是那个东西"
    /// （含 <c>optifine/Installer.class</c>，且 MANIFEST 声明 <c>Main-Class: optifine.InstallerFrame</c>）——
    /// 下载到 HTML 错误页、或者下到别的东西时，这条会拦住，而不是把未知内容当程序跑起来。
    /// </para>
    /// </summary>
    /// <returns>有问题时返回描述；没问题返回 <see langword="null"/>。</returns>
    private static string? DescribeStructuralProblem(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            if (archive.GetEntry(InstallerClassEntry) is null)
            {
                return $"缺少 {InstallerClassEntry}";
            }

            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");

            if (manifest is null)
            {
                return "缺少 META-INF/MANIFEST.MF";
            }

            using var reader = new StreamReader(manifest.Open());
            var text = reader.ReadToEnd();

            return text.Contains(InstallerMainClass, StringComparison.Ordinal)
                ? null
                : $"MANIFEST 里没有 {InstallerMainClass}";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"读不出这个文件（{exception.GetType().Name}）";
        }
    }

    /// <summary>挑一个能跑安装器的 Java（与 Forge 安装器同一套判定：按这个游戏版本的要求）。</summary>
    private async Task<JavaInstallation?> ChooseJavaAsync(
        string gameVersion,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        int? declaredJava = null;

        try
        {
            var resolved = await _vanilla.ResolveVersionAsync(gameVersion, gameDirectory, cancellationToken).ConfigureAwait(false);
            declaredJava = resolved.Merged.JavaVersion?.MajorVersion;
        }
        catch (Exception exception) when (exception is VersionResolutionException or VersionDataException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.ModLoader,
                "读不到原版 {0} 的版本 JSON（{1}），OptiFine 安装器将只按规则表挑 Java",
                [gameVersion, exception.GetType().Name]);
        }

        var check = JavaPreflight.Check(
            gameVersion,
            javaOverride: null,
            _javaRuntimes.GetInstallations(),
            _javaRuntimes.HasDetectionResult,
            declaredJava);

        return check.IsSatisfied ? check.Selected : null;
    }

    /// <summary>安装器在沙箱里写出了哪些版本目录（版本 id 与预期不符时，这句话就是全部线索）。</summary>
    private static TextTemplate? DescribeSandbox(string sandbox)
    {
        try
        {
            var versions = Path.Combine(sandbox, "versions");

            if (!Directory.Exists(versions))
            {
                return TextTemplate.Of("（沙箱里没有 versions 目录。）");
            }

            var names = Directory.GetDirectories(versions).Select(Path.GetFileName).ToArray();

            return names.Length == 0
                ? TextTemplate.Of("（沙箱的 versions 目录是空的。）")
                : TextTemplate.Of("沙箱里出现的版本目录：{0}。", string.Join(", ", names));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TextTemplate? DescribeOutput(string outputTail) =>
        string.IsNullOrWhiteSpace(outputTail)
            ? null
            : TextTemplate.Of("安装器最后说：{0}", outputTail.ReplaceLineEndings(" / "));

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            DeleteTree(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不该让一次成功的安装变成失败：文件留在下载缓存里，下次会被覆盖。
        }
    }

    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    private static InstallResult Failed(string versionId, TextTemplate reason) =>
        new(versionId, false, 0, 0, 0, 0, reason);
}
