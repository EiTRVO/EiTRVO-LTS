using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// Forge / NeoForge 安装（B-7 / B-8，M4-g）：下载**官方安装器** → 用它自己装 → 校验产出。
/// <para>
/// 与 Fabric / Quilt（<see cref="LoaderInstaller"/>）的根本区别：那两家的"安装"就是写一份 JSON，
/// 而 Forge 的安装是**一堆处理器**（拆分客户端 JAR、打二进制补丁、注入 profile）。
/// 自己重写那套处理器既不现实也不忠实，所以这里跑官方安装器本身 ——
/// 它是发布方给的工具，做的是发布方定义的事。
/// </para>
/// <para>
/// 三条硬要求：
/// </para>
/// <list type="number">
/// <item><b>不运行校验不过的远端程序</b>：安装器 jar 先按发布方的 <c>.sha1</c> 校验；
/// 拿不到校验值就**拒绝安装**，而不是"先跑起来再说"。</item>
/// <item><b>退出码说了算</b>：官方安装器可能在**已经写出版本 JSON 之后**才失败
/// （实测 NeoForge 20.4.237 就是这样：<c>versions/neoforge-20.4.237</c> 出现了，但退出码 1、
/// "These libraries failed to download"）。只看"版本 JSON 在不在"会把它记成成功，
/// 于是用户得到一个装着半份库、点启动就崩的实例。</item>
/// <item><b>不动用户既有的 <c>launcher_profiles.json</c></b>：安装器要求它存在（实测缺了会直接报
/// "you need to run the launcher first"），所以缺了就补一个最小的；已经有了就**一个字都不改**
/// —— 那是官方启动器与用户自己的文件。</item>
/// </list>
/// </summary>
public sealed class ForgeInstaller
{
    /// <summary>Forge 的 Maven 根。</summary>
    public const string ForgeMaven = "https://maven.minecraftforge.net/";

    /// <summary>NeoForge 的 Maven 根。</summary>
    public const string NeoForgeMaven = "https://maven.neoforged.net/releases/";

    /// <summary>官方安装器最长跑多久。实测 Forge 1.20.4 的首次安装要 8 分半（它自己下几百 MB）。</summary>
    public static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(40);

    /// <summary>安装器要求存在的最小 profile 文件（缺了它安装器会拒绝干活）。</summary>
    public const string MinimalLauncherProfiles = "{\"profiles\":{}}";

    private readonly IToolRunner _tools;
    private readonly VanillaInstaller _vanilla;
    private readonly FileDownloader _downloader;
    private readonly VersionJsonStore _versions;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly ILauncherPaths _paths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造 Forge / NeoForge 安装器。</summary>
    /// <param name="tools">外部工具运行器（跑 <c>java -jar 安装器</c>）。</param>
    /// <param name="vanilla">原版安装器（只借它取"这个游戏版本要哪个 Java"）。</param>
    /// <param name="downloader">下载器（下安装器 jar，带 SHA-1 校验与 <c>.part</c> 原子提交）。</param>
    /// <param name="versions">版本 JSON 存储（校验产出）。</param>
    /// <param name="javaRuntimes">Java 运行时检测结果（安装器要用一个能跑的 Java）。</param>
    /// <param name="paths">数据目录（安装器 jar 缓存落在 <c>cache\downloads</c>）。</param>
    /// <param name="httpClientFactory">命名客户端工厂（取 <c>.sha1</c>）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public ForgeInstaller(
        IToolRunner tools,
        VanillaInstaller vanilla,
        FileDownloader downloader,
        VersionJsonStore versions,
        IJavaRuntimeCatalog javaRuntimes,
        ILauncherPaths paths,
        IHttpClientFactory httpClientFactory,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(vanilla);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(javaRuntimes);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _tools = tools;
        _vanilla = vanilla;
        _downloader = downloader;
        _versions = versions;
        _javaRuntimes = javaRuntimes;
        _paths = paths;
        _httpClientFactory = httpClientFactory;
        _diagnostics = diagnostics;
    }

    /// <summary>算出官方安装器 jar 的地址（纯函数，便于逐字符断言）。</summary>
    /// <param name="kind">加载器种类（Forge / NeoForge）。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loaderVersion">加载器版本。</param>
    /// <returns>完整地址。</returns>
    /// <exception cref="ArgumentOutOfRangeException">不是 Forge / NeoForge。</exception>
    public static string InstallerUrl(LoaderKind kind, string gameVersion, string loaderVersion)
    {
        var game = RequireUrlSegment(gameVersion, nameof(gameVersion));
        var loader = RequireUrlSegment(loaderVersion, nameof(loaderVersion));

        return kind switch
        {
            LoaderKind.Forge =>
                $"{ForgeMaven}net/minecraftforge/forge/{game}-{loader}/forge-{game}-{loader}-installer.jar",

            // 1.20.2 起：artifact 是 neoforge，版本号里不含游戏版本。
            LoaderKind.NeoForge when LoaderNaming.IsLegacyNeoForge(game) =>
                $"{NeoForgeMaven}net/neoforged/forge/{game}-{loader}/forge-{game}-{loader}-installer.jar",

            LoaderKind.NeoForge =>
                $"{NeoForgeMaven}net/neoforged/neoforge/{loader}/neoforge-{loader}-installer.jar",

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "只有 Forge / NeoForge 用官方安装器；Fabric / Quilt 走纯 JSON profile。"),
        };
    }

    /// <summary>安装一个 Forge / NeoForge 版本。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="gameVersion">它要挂载的游戏版本。</param>
    /// <param name="loaderVersion">加载器版本。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="source">下载源策略。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段变化接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>安装结果。</returns>
    public async Task<InstallResult> InstallAsync(
        LoaderKind kind,
        string gameVersion,
        string? loaderVersion,
        string gameDirectory,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        if (!LoaderNaming.IsInstallerLoader(kind))
        {
            return new InstallResult(
                gameVersion,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0} 的安装不走官方安装器：Fabric / Quilt 走纯 JSON profile（M4-f），OptiFine 走它自己的安装器（M4-i）。", kind.ToString()));
        }

        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            return new InstallResult(
                gameVersion,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("这个实例没有记下 {0} 的版本号，无法确定要装哪一个。请在向导或「实例设置」里重新选一次。", kind.ToString()));
        }

        var versionId = LoaderNaming.VersionId(kind, loaderVersion, gameVersion);

        // 1.7.10 及以前的 Forge 安装器**没有客户端安装选项**，而且它的 maven 路径还多带一个
        // 游戏版本后缀：`.../forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10-installer.jar`。
        // 这两个事实决定了后面"用哪个地址"和"要不要跑安装器"。
        var legacyClient = RequiresLegacyClientInstall(gameVersion);

        ReportStage(stages, InstallStageKind.ResolveVersion, completed: false);

        // ---- 安装器的地址与校验值 ----
        var installerUrl = InstallerUrl(kind, gameVersion, loaderVersion);
        string? sha1;

        try
        {
            sha1 = await FetchSha1Async(installerUrl + ".sha1", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return Failed(versionId, TextTemplate.Of("取安装器校验值失败：{0}", exception.Message));
        }

        if (sha1 is null && legacyClient && kind == LoaderKind.Forge)
        {
            // 老 Forge 的另一条真实路径（带游戏版本后缀）；Neo 也有这个 fallback。
            installerUrl = LegacyInstallerUrl(gameVersion, loaderVersion);

            try
            {
                sha1 = await FetchSha1Async(installerUrl + ".sha1", cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                return Failed(versionId, TextTemplate.Of("取安装器校验值失败：{0}", exception.Message));
            }
        }

        if (sha1 is null)
        {
            // 远端程序不能"没校验就运行"：拿不到发布方的校验值宁可停下。
            return Failed(
                versionId,
                TextTemplate.Of("拿不到官方安装器的校验值（.sha1），已停止安装 —— 无法校验的远端程序不运行。"));
        }

        // ---- Java：官方安装器自己也要用 Java 跑 ----
        // 1.7.10 及以前是**不跑安装器**的（它没有客户端安装选项，见 InstallLegacyClientAsync），
        // 因此这一步不能拦住它们：那些版本只要能下文件就能装，硬要一个 Java 8 是假门槛。
        JavaInstallation? installerJava = null;

        if (!legacyClient)
        {
            installerJava = await ChooseJavaAsync(gameVersion, gameDirectory, cancellationToken).ConfigureAwait(false);

            if (installerJava is null)
            {
                return Failed(versionId, TextTemplate.Of("装 {0} 需要先有一个能用的 Java（安装器本身要用它运行）。请到「Java 与设置」页里检测或获取。", kind.ToString()));
            }
        }

        // ---- 下载安装器 jar（SHA-1 校验 + .part 原子提交）----
        var fileName = Path.GetFileName(new Uri(installerUrl).AbsolutePath);
        var jarPath = PathSafety.CombineWithin(_paths.DownloadCacheDirectory, fileName);

        ReportStage(stages, InstallStageKind.ResolveVersion, completed: true);
        ReportStage(stages, InstallStageKind.Download, completed: false);

        var options = new DownloadOptions { Source = source, Concurrency = 4 };

        Directory.CreateDirectory(_paths.DownloadCacheDirectory);

        var outcome = await _downloader
            .DownloadAsync(
                [
                    new DownloadItem(
                        DownloadKind.Library,
                        versionId,
                        installerUrl,
                        sha1,
                        Size: 0,
                        TargetPath: jarPath),
                ],
                options,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.IsComplete)
        {
            var first = outcome.Failures.Count > 0 ? outcome.Failures[0] : null;

            return Failed(
                versionId,
                first is null
                    ? TextTemplate.Of("安装器下载未完成。")
                    : TextTemplate.Of("下载安装器失败。") with { Note = first.Reason });
        }

        // ---- 安装器要求目标目录里有一个 launcher_profiles.json ----
        var profileFailure = EnsureLauncherProfiles(gameDirectory);

        if (profileFailure is not null)
        {
            return Failed(versionId, profileFailure);
        }

        // ---- 1.7.10 及以前：安装器**没有 --installClient**（只有 --extract / --installServer）----
        // 实测 `java -jar forge-1.7.10-...-installer.jar --installClient <dir>` 直接
        // `joptsimple.UnrecognizedOptionException` 退出 1。这一代的客户端安装只能从安装器 jar 里
        // 取出 install_profile.json / versionInfo / universal jar 自己做 —— Neo 也是这么装的。
        if (legacyClient)
        {
            return await InstallLegacyClientAsync(
                versionId,
                kind,
                gameVersion,
                loaderVersion.Trim(),
                gameDirectory,
                jarPath,
                source,
                concurrency: 8,
                progress,
                stages,
                cancellationToken).ConfigureAwait(false);
        }

        // ---- 跑官方安装器 ----
        // 它的下载发生在这个子进程内部，我们拿不到字节数：如实报"不确定进度"，
        // 让 p06 的进度条动起来，而不是停在一根永远 0% 的条上。
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
            SourceHost = new Uri(installerUrl).Host,
            Detail = TextTemplate.Of("正在运行官方安装器"),
        });

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.ModLoader,
            "正在运行 {0} 官方安装器（{1} / {2}）",
            [kind.ToString(), loaderVersion.Trim(), gameVersion.Trim()]);

        // 走到这里说明上面不是旧版路径，installerJava 一定已经被选出来了。
        var java = installerJava
            ?? throw new InvalidOperationException("官方安装器路径缺少 Java 运行时。");

        var run = await _tools
            .RunAsync(
                java.ExecutablePath,
                ["-jar", jarPath, "--installClient", Path.GetFullPath(gameDirectory)],
                Path.GetFullPath(gameDirectory),
                InstallerTimeout,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!run.Succeeded)
        {
            var reason = run.TimedOut
                ? TextTemplate.Of(
                    "官方安装器超过 {0} 分钟没有结束，已终止（网络慢或对方的源不可用）。",
                    InstallerTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture))
                : TextTemplate.Of(
                    "官方安装器以退出码 {0} 结束。",
                    run.ExitCode.ToString(CultureInfo.InvariantCulture));

            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.ModLoader,
                "{0} 官方安装器失败（{1}，退出码 {2}）",
                [kind.ToString(), loaderVersion.Trim(), run.ExitCode.ToString(CultureInfo.InvariantCulture)]);

            return Failed(versionId, reason with { Note = Describe(run.OutputTail) });
        }

        // ---- 校验产出：退出码说成功，还得真的写出那个版本 JSON ----
        if (_versions.TryReadInstalled(gameDirectory, versionId) is null)
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.ModLoader,
                "官方安装器没有写出预期的版本 JSON：{0}",
                [versionId]);

            return Failed(
                versionId,
                TextTemplate.Of(
                    "官方安装器报成功，但本地没有 versions/{0}/{0}.json —— 这次安装不算数（可能是版本号与产出对不上）。",
                    versionId));
        }

        ReportStage(stages, InstallStageKind.Download, completed: true);
        ReportStage(stages, InstallStageKind.ExtractNatives, completed: true);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.ModLoader,
            "{0} {1} 安装完成（游戏 {2}，版本 {3}）",
            [kind.ToString(), loaderVersion.Trim(), gameVersion.Trim(), versionId]);

        return new InstallResult(
            versionId,
            true,
            FilesDownloaded: 1, // 我们自己下的只有那个安装器 jar；它的下载量发生在安装器进程内部。
            FilesSkipped: 0,
            NativeFilesExtracted: 0,
            Bytes: new FileInfo(jarPath).Length,
            Failure: null);
    }

    /// <summary>取发布方的 <c>.sha1</c>；404 或不是 40 位十六进制时返回 <see langword="null"/>（不校验就不装）。</summary>
    private async Task<string?> FetchSha1Async(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();

        // 有的仓库把 sha1 写成 "hash  filename"：取第一段。
        var separator = text.IndexOf(' ', StringComparison.Ordinal);
        if (separator > 0)
        {
            text = text[..separator];
        }

        return text.Length == 40 && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 挑一个能跑安装器的 Java：按**这个游戏版本的要求**选（与启动用的是同一套判定），
    /// 于是"本机没有能跑的 Java"在安装这一步就说清楚，而不是等到启动时。
    /// </summary>
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
            // 版本 JSON 取不到不影响装加载器本身（安装器自己会去取原版信息）；
            // 那就只按规则表挑 Java。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.ModLoader,
                "读不到原版 {0} 的版本 JSON（{1}），安装器将只按规则表挑 Java",
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

    /// <summary>
    /// 官方安装器要求目标目录里有 <c>launcher_profiles.json</c>，否则直接拒绝干活
    /// （实测：<c>There is no minecraft launcher profile in "…", you need to run the launcher first!</c>）。
    /// **只在缺失时**补一个最小的；已有内容一个字都不动（那是官方启动器与用户自己的文件）。
    /// </summary>
    private static TextTemplate? EnsureLauncherProfiles(string gameDirectory)
    {
        try
        {
            Directory.CreateDirectory(gameDirectory);

            var path = Path.Combine(Path.GetFullPath(gameDirectory), "launcher_profiles.json");

            if (File.Exists(path))
            {
                return null;
            }

            File.WriteAllText(path, MinimalLauncherProfiles, new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TextTemplate.Of("无法在游戏目录里准备 launcher_profiles.json（官方安装器要求它存在）：{0}", exception.Message);
        }
    }

    /// <summary>把安装器的输出尾部拼成一句人能读的话（没有输出时为 <see langword="null"/>）。</summary>
    private static TextTemplate? Describe(string outputTail) =>
        string.IsNullOrWhiteSpace(outputTail)
            ? null
            : TextTemplate.Of("安装器最后说：{0}", outputTail.ReplaceLineEndings(" / "));

    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    private static InstallResult Failed(string versionId, TextTemplate reason) =>
        new(versionId, false, 0, 0, 0, 0, reason);

    /// <summary>
    /// 老 Forge（≤1.7.10）安装器的真实 maven 路径：目录名与文件名都多带一次游戏版本。
    /// </summary>
    /// <remarks>
    /// 例：<c>1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10-installer.jar</c>。
    /// 不带后缀的路径对这些版本是 404；不试这条就永远装不上它们。
    /// </remarks>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loaderVersion">Forge 版本。</param>
    /// <returns>完整地址。</returns>
    public static string LegacyInstallerUrl(string gameVersion, string loaderVersion)
    {
        var game = RequireUrlSegment(gameVersion, nameof(gameVersion));
        var loader = RequireUrlSegment(loaderVersion, nameof(loaderVersion));
        var full = game + "-" + loader + "-" + game;

        return $"{ForgeMaven}net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
    }

    /// <summary>
    /// 这个游戏版本的 Forge 是否必须走"从安装器 jar 里自己装"的旧版路径。
    /// </summary>
    /// <remarks>
    /// 实测 1.7.10 与 1.8.9 的安装器（<c>SimpleInstaller</c>）都**没有** <c>--installClient</c>：
    /// 只列 <c>--extract</c> / <c>--installServer</c> / <c>--offline</c>，传客户端参数直接
    /// <c>UnrecognizedOptionException</c> 退出 1。1.13 起安装器换成带 processor 的新一代，
    /// 才有客户端安装参数。因此 1.12.2 及以前统一走提取路径（与 Neo 的 legacy 判定一致）。
    /// </remarks>
    /// <param name="gameVersion">游戏版本。</param>
    /// <returns>需要旧版提取路径返回 <see langword="true"/>。</returns>
    public static bool RequiresLegacyClientInstall(string gameVersion) =>
        Version.TryParse(gameVersion, out var version) && version.Major == 1 && version.Minor <= 12;

    /// <summary>
    /// 从旧版安装器 jar 里读出 <c>install_profile.json</c>（以及它指向的版本 JSON 与 universal jar）。
    /// </summary>
    /// <param name="installerPath">安装器 jar。</param>
    /// <returns>版本 JSON 与 universal jar 的文件名 / 仓库相对路径。</returns>
    /// <exception cref="InvalidDataException">安装器里没有可用的安装描述。</exception>
    private static LegacyInstallProfile ReadLegacyProfile(string installerPath)
    {
        using var zip = ZipFile.OpenRead(installerPath);

        var profileEntry = zip.GetEntry("install_profile.json")
            ?? throw new InvalidDataException("安装器 jar 里没有 install_profile.json。");

        using var document = JsonDocument.Parse(ReadZipEntry(profileEntry));
        var root = document.RootElement;

        string versionJsonText;

        if (root.TryGetProperty("versionInfo", out var versionInfo))
        {
            versionJsonText = versionInfo.GetRawText();
        }
        else if (root.TryGetProperty("json", out var jsonReference)
                 && jsonReference.GetString() is { Length: > 0 } jsonPath)
        {
            var versionEntry = zip.GetEntry(jsonPath.TrimStart('/'))
                ?? throw new InvalidDataException($"安装器 jar 里没有 {jsonPath}。");
            versionJsonText = ReadZipEntry(versionEntry);
        }
        else
        {
            throw new InvalidDataException("安装器的 install_profile.json 既没有 versionInfo 也没有 json。");
        }

        var version = LauncherJson.Deserialize<VersionJson>(versionJsonText, LauncherJson.Indented)
            ?? throw new InvalidDataException("安装器里的版本 JSON 解析结果为空。");

        string? filePath = null;
        string? mavenPath = null;

        if (root.TryGetProperty("install", out var install))
        {
            if (install.TryGetProperty("filePath", out var file) && file.GetString() is { Length: > 0 } fileText)
            {
                filePath = fileText;
            }

            if (install.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } pathText)
            {
                mavenPath = pathText;
            }
        }

        return new LegacyInstallProfile(version, filePath, mavenPath);
    }

    private static string ReadZipEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>旧版 Forge 的客户端安装：原版先装好，再把安装器里的版本 JSON + universal jar 铺开。</summary>
    private async Task<InstallResult> InstallLegacyClientAsync(
        string versionId,
        LoaderKind kind,
        string gameVersion,
        string loaderVersion,
        string gameDirectory,
        string installerPath,
        DownloadSourcePolicy source,
        int concurrency,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        LegacyInstallProfile profile;

        try
        {
            profile = ReadLegacyProfile(installerPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            return Failed(versionId, TextTemplate.Of("读不出旧版安装器里的安装描述：{0}", exception.Message));
        }

        // 先把原版装好：旧版 versionInfo 里那些"没有 URL"的库就是原版的库，装完才能确定它们都在。
        var vanilla = await _vanilla
            .InstallAsync(
                gameVersion,
                gameDirectory,
                GamePlatform.Current,
                source,
                concurrency,
                progress,
                stages,
                cancellationToken)
            .ConfigureAwait(false);

        if (!vanilla.Succeeded)
        {
            // 原版的失败原因自己也是一句「键 + 参数」：用 Note 拼成第二句，
            // 不把它的键当参数塞进来（那样会把占位符原样显示给用户）。
            return Failed(
                versionId,
                TextTemplate.Of("先安装原版 {0} 时失败。", gameVersion) with
                {
                    Note = vanilla.Failure,
                });
        }

        ResolvedVersion parent;

        try
        {
            parent = await _vanilla
                .ResolveVersionAsync(gameVersion, gameDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is VersionResolutionException or VersionDataException)
        {
            return Failed(versionId, TextTemplate.Of("读不到原版 {0} 的版本 JSON：{1}", gameVersion, exception.Message));
        }

        var universalRelative = ExtractLegacyUniversal(profile, gameDirectory, installerPath);
        var normalized = NormalizeLegacyVersion(
            profile.Version,
            versionId,
            gameVersion,
            universalRelative,
            parent.Merged.Libraries);

        ResolvedVersion merged;

        try
        {
            merged = VersionInheritanceResolver.Resolve(
                normalized,
                id => string.Equals(id, gameVersion, StringComparison.OrdinalIgnoreCase)
                    ? parent.Merged
                    : _versions.Load(gameDirectory, id));
        }
        catch (VersionResolutionException exception)
        {
            return Failed(versionId, exception.Reason);
        }

        var result = await _vanilla
            .InstallResolvedAsync(
                merged,
                gameDirectory,
                GamePlatform.Current,
                source,
                concurrency,
                progress,
                stages,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return result;
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.ModLoader,
            "{0} {1} 安装完成（游戏 {2}，版本 {3}，旧版安装器：从安装器 jar 里取 universal jar 与版本 JSON）",
            [kind.ToString(), loaderVersion, gameVersion, versionId]);

        return result;
    }

    /// <summary>从安装器 zip 里取出 Forge universal jar（旧版安装器把它放在根目录）。</summary>
    /// <returns>它被写到的仓库相对路径；取不到时为 <see langword="null"/>。</returns>
    private static string? ExtractLegacyUniversal(
        LegacyInstallProfile profile,
        string gameDirectory,
        string installerPath)
    {
        var forgeLibrary = (profile.Version.Libraries ?? [])
            .FirstOrDefault(static library => library.Name is not null
                && library.Name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase));

        string? relative = ToLibraryRelative(profile.MavenPath);

        if (relative is null && forgeLibrary?.Name is not null && MavenPath.TryFromName(forgeLibrary.Name, out var mavenPath))
        {
            relative = mavenPath;
        }

        if (relative is null && profile.UniversalFileName is not null)
        {
            relative = "net/minecraftforge/forge/" + profile.UniversalFileName;
        }

        if (relative is null)
        {
            return null;
        }

        using var zip = ZipFile.OpenRead(installerPath);

        // 旧版安装器把 universal jar 放在根目录（install.filePath 就是它的文件名）；
        // 1.12.2 的安装器则把它放在 maven/ 下，而且文件名就是普通的 <artifact>-<version>.jar
        // （没有 -universal 后缀）。三种位置都试一遍，不能只认根目录那一种。
        var expectedFileName = Path.GetFileName(relative);

        var entry = (profile.UniversalFileName is null ? null : zip.GetEntry(profile.UniversalFileName))
            ?? zip.GetEntry("maven/" + relative)
            ?? (profile.UniversalFileName is null
                ? null
                : zip.Entries.FirstOrDefault(candidate =>
                    string.Equals(Path.GetFileName(candidate.FullName), profile.UniversalFileName, StringComparison.OrdinalIgnoreCase)))
            ?? zip.Entries.FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate.FullName), expectedFileName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        var destination = PathSafety.CombineWithin(gameDirectory, "libraries", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: true);

        return relative;
    }

    /// <summary>旧版版本 JSON 的库列表：补 URL、改写 http 的 Forge 地址、并把 universal 库改成"已由安装器铺好"。</summary>
    private static VersionJson NormalizeLegacyVersion(
        VersionJson version,
        string versionId,
        string gameVersion,
        string? universalRelative,
        IReadOnlyList<Library>? parentLibraries)
    {
        List<Library> libraries = [];

        // 旧版 Forge 的 versionInfo 把原版的库也列了一遍，但不带下载地址；真正的地址在父版本 JSON 里
        // （twitch 这类库在 Mozilla 主机上的旧位置，猜不出来）。按名字取父版本那一份的下载信息。
        var parentByName = new Dictionary<string, Library>(StringComparer.OrdinalIgnoreCase);

        foreach (var parentLibrary in parentLibraries ?? [])
        {
            if (!string.IsNullOrWhiteSpace(parentLibrary.Name))
            {
                parentByName[parentLibrary.Name] = parentLibrary;
            }
        }

        foreach (var library in version.Libraries ?? [])
        {
            if (string.IsNullOrWhiteSpace(library.Name))
            {
                continue;
            }

            var normalized = library;
            var isUniversal = universalRelative is not null
                && MavenPath.TryFromName(library.Name, out var libraryPath)
                && string.Equals(libraryPath.Replace('\\', '/'), universalRelative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

            if (isUniversal)
            {
                // 文件已经由 ExtractLegacyUniversal 铺好；不再让下载器去 Maven 找那个不存在的非 universal 名字。
                normalized = normalized with { Url = null, Downloads = null };
            }
            else if (!string.IsNullOrWhiteSpace(normalized.Url))
            {
                normalized = normalized with { Url = NormalizeLegacyMaven(normalized.Url) };
            }
            else if (normalized.Downloads?.Artifact?.Url is null)
            {
                if (parentByName.TryGetValue(library.Name, out var parentLibrary)
                    && (parentLibrary.Downloads?.Artifact?.Url is not null
                        || parentLibrary.Downloads?.Classifiers is { Count: > 0 }))
                {
                    // 父版本这一份可能是"只有分类器、没有主构件"的旧式 natives 条目
                    // （lwjgl-platform / jinput-platform / twitch-platform 都是这个形状）。
                    normalized = normalized with
                    {
                        Downloads = parentLibrary.Downloads,
                        Natives = normalized.Natives ?? parentLibrary.Natives,
                        Extract = normalized.Extract ?? parentLibrary.Extract,
                    };
                }
                else if (!string.IsNullOrWhiteSpace(parentLibrary?.Url))
                {
                    normalized = normalized with { Url = NormalizeLegacyMaven(parentLibrary.Url) };
                }
                else
                {
                    normalized = normalized with { Url = LegacyLibraryBase(library.Name) };
                }
            }

            libraries.Add(normalized);
        }

        return version with
        {
            Id = versionId,
            InheritsFrom = gameVersion,
            Libraries = libraries,
        };
    }

    /// <summary>把旧 profile 里的 http / files.minecraftforge.net 地址改成 https 的 maven.minecraftforge.net。</summary>
    private static string NormalizeLegacyMaven(string url) =>
        url.Replace("http://files.minecraftforge.net/maven", ForgeMaven, StringComparison.OrdinalIgnoreCase)
           .Replace("http://maven.minecraftforge.net", "https://maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
           .Replace("https://files.minecraftforge.net/maven", ForgeMaven, StringComparison.OrdinalIgnoreCase);

    /// <summary>旧版 profile 里没有 URL 的库：Forge 独有的一组去 Forge Maven，其余按原版库处理。</summary>
    private static string LegacyLibraryBase(string mavenName)
    {
        var parts = mavenName.Split(':');
        var group = parts.Length > 0 ? parts[0] : string.Empty;

        // 实测（2026-10-21，逐个 HEAD）：1.7.10 profile 里"没有 URL"的库里，
        // 只有 Forge 自己发布的 net.minecraftforge / org.scala-lang 在 Forge Maven 上；
        // launchwrapper / lzma / asm-all / jopt-simple 都在 Mojang 的 libraries 主机上。
        if (group is "net.minecraftforge" or "org.scala-lang")
        {
            return ForgeMaven;
        }

        return "https://libraries.minecraft.net/";
    }

    private static string? ToLibraryRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim().Replace('\\', '/');

        if (text.Contains('/', StringComparison.Ordinal))
        {
            return text;
        }

        return MavenPath.TryFromName(text, out var mavenPath) ? mavenPath : null;
    }

    private sealed record LegacyInstallProfile(VersionJson Version, string? UniversalFileName, string? MavenPath);

    /// <summary>版本串会被拼进请求地址，因此不允许任何能改变 URL 结构的字符。</summary>
    private static string RequireUrlSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("版本串不能为空。", parameterName);
        }

        var text = value.Trim();

        if (text.IndexOfAny(['/', '\\', '?', '#', '&', '=']) >= 0 || text.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"版本串不能用于拼请求地址：'{value}'。", parameterName);
        }

        return text;
    }
}
