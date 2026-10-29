using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// <see cref="IJavaRuntimeCatalog"/> 的真实实现（M3 的 C-6…C-8）。
/// <para>
/// 与 <c>FileVersionCatalog</c> 同一个形状：**读取端同步、刷新端异步、结果落缓存**。
/// 缓存（<c>cache\java-runtimes.json</c>）不是可有可无的优化 —— 检测要跑 N 次
/// <c>java -version</c>（每次几十到几百毫秒），冷启动时若同步做，打开启动器就要卡一秒以上。
/// </para>
/// <para>
/// 缓存里的条目在读取时会**核对文件是否还在**：用户卸载 Java 之后，
/// 缓存里那条记录会让界面继续显示一个不存在的运行时，而点"启动"时才崩。
/// 核对之后若一条都不剩，则连同"检测过"这件事一起作废（见 <see cref="HasDetectionResult"/>）——
/// 依据已经不成立的结果不该继续支撑"缺少 Java"这种结论。
/// </para>
/// </summary>
public sealed class FileJavaRuntimeCatalog : IJavaRuntimeCatalog, IDisposable
{
    /// <summary>检测结果的新鲜度窗口（与版本清单 15 分钟窗口同一个思路，短一些因为跑进程更贵）。</summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly JavaDiscovery _discovery;
    private readonly IJavaProcessProbe _probe;
    private readonly ILauncherPaths _paths;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStampGate _corruptWarnings = new();

    private IReadOnlyList<JavaInstallation> _installations;
    private IReadOnlyList<JavaInstallation> _detectedInstallations = [];
    private List<JavaInstallation> _customInstallations = [];
    private DateTimeOffset? _detectedAt;
    private bool _hasDetectionResult;

    /// <summary>上一次记过事件的检测结果签名；<see langword="null"/> 表示还没记过。</summary>
    /// <remarks>
    /// 必须是可空：零个运行时的签名是空串，而"空串"正是"还没记过"的默认值 ——
    /// 用空串当"没记过"会让**最该记的那一条**（一个 Java 都没找到）被静默吃掉。
    /// </remarks>
    private string? _lastLoggedSignature;

    /// <summary>构造 Java 运行时目录。</summary>
    /// <param name="discovery">可执行文件发现（PATH / JAVA_HOME / 注册表 / 常见目录）。</param>
    /// <param name="probe">版本探测（跑 <c>java -version</c>）。</param>
    /// <param name="paths">数据目录布局（缓存文件在这里）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    /// <param name="clock">时间源（新鲜度窗口要可测）。</param>
    public FileJavaRuntimeCatalog(
        JavaDiscovery discovery,
        IJavaProcessProbe probe,
        ILauncherPaths paths,
        IDiagnosticsLog diagnostics,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(clock);

        _discovery = discovery;
        _probe = probe;
        _paths = paths;
        _diagnostics = diagnostics;
        _clock = clock;

        (_installations, _detectedAt, _hasDetectionResult) = ReadCache();
    }

    /// <inheritdoc />
    public IReadOnlyList<JavaInstallation> GetInstallations() => _installations;

    /// <inheritdoc />
    public bool HasDetectionResult => _hasDetectionResult;

    /// <inheritdoc />
    public async Task<JavaRuntimeInspection> InspectCustomAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
        if (!File.Exists(fullPath))
        {
            throw new JavaRuntimeInspectionException($"找不到 Java 可执行文件：{fullPath}");
        }

        var output = await _probe.ReadVersionOutputAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var info = JavaVersionParser.TryParse(output);
        if (info is null)
        {
            throw new JavaRuntimeInspectionException($"这个文件不是有效的 Java 运行时：{fullPath}");
        }

        var installation = new JavaInstallation(
            ExecutablePath: fullPath,
            HomeDirectory: HomeDirectoryOf(fullPath),
            VersionText: info.VersionText,
            MajorVersion: info.MajorVersion,
            Vendor: info.Vendor,
            Bits: info.Bits);

        var risk = JavaRuntimeRisk.None;
        if (string.IsNullOrWhiteSpace(info.Vendor)
            || string.Equals(info.Vendor, "—", StringComparison.Ordinal))
        {
            risk |= JavaRuntimeRisk.UnknownVendor;
        }

        if (IsUserWritableLocation(fullPath))
        {
            risk |= JavaRuntimeRisk.UserWritableLocation;
        }

        return new JavaRuntimeInspection(installation, risk);
    }

    /// <inheritdoc />
    public void AddCustom(JavaInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        // 同名路径 / 同一个 home 只保留一条：用户重新选一次同一份 Java 时不应该出现重复行。
        _customInstallations.RemoveAll(existing =>
            string.Equals(existing.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing.HomeDirectory, installation.HomeDirectory, StringComparison.OrdinalIgnoreCase));
        _customInstallations.Add(installation);

        _installations = Merge(_detectedInstallations, _customInstallations);
        WriteCache(_detectedInstallations, _detectedAt, _customInstallations);
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsFresh())
        {
            return;
        }

        await DetectAsync(force: false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RedetectAsync(CancellationToken cancellationToken = default) =>
        await DetectAsync(force: true, cancellationToken).ConfigureAwait(false);

    /// <summary>结果是否还在新鲜度窗口内。</summary>
    private bool IsFresh() =>
        _hasDetectionResult && _detectedAt is { } detectedAt && _clock.Now - detectedAt < Freshness;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>真正跑一次检测。</summary>
    /// <param name="force">为 <see langword="true"/> 时忽略新鲜度窗口（用户按了「重新检测」）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    private async Task DetectAsync(bool force, CancellationToken cancellationToken)
    {
        // 单飞：两处同时来（p05 与 p10 都在导航时刷新）时不会同时跑两批 java -version。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 等锁期间别人刚检测完：直接用它，别重复跑一遍进程。
            if (!force && IsFresh())
            {
                return;
            }

            var executables = _discovery.FindExecutables();
            List<JavaInstallation> found = [];
            var seenHomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 同一个运行时常常有**好几条路可达**：Oracle 的 javapath 垫片、JDK 安装目录、
            // <c>latest</c> 联接、JAVA_HOME、以及 Program Files 下的目录扫描。
            // 实机上一台装了 JDK 25 + JDK 21 的机器就是这样显示出**四行**（三行都是 25.0.3），
            // 用户看到的是"我装了四个 JDK"，而且每多一行就多一条假的"可选运行时"。
            // 因此按运行时身份（主版本 + 版本原文 + 供应商 + 位数）去重：对用户来说那就是同一样东西。
            var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var executable in executables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var output = await _probe.ReadVersionOutputAsync(executable, cancellationToken).ConfigureAwait(false);
                var info = JavaVersionParser.TryParse(output);
                if (info is null)
                {
                    // 文件叫 javaw.exe 但跑不起来／不是 Java：跳过，别在界面上编一条记录。
                    continue;
                }

                var home = HomeDirectoryOf(executable);
                if (!seenHomes.Add(home))
                {
                    continue;
                }

                var identity = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{info.MajorVersion}|{info.VersionText}|{info.Vendor}|{info.Bits}");

                if (!seenIdentities.Add(identity))
                {
                    continue;
                }

                found.Add(new JavaInstallation(
                    ExecutablePath: executable,
                    HomeDirectory: home,
                    VersionText: info.VersionText,
                    MajorVersion: info.MajorVersion,
                    Vendor: info.Vendor,
                    Bits: info.Bits));
            }

            // 主版本从高到低：设计稿的表格就是 25 → 17 → 8，且"最新的那个"通常最值得先看到。
            found.Sort(static (left, right) =>
            {
                var byMajor = right.MajorVersion.CompareTo(left.MajorVersion);
                if (byMajor != 0)
                {
                    return byMajor;
                }

                var byVersion = string.CompareOrdinal(right.VersionText, left.VersionText);
                return byVersion != 0 ? byVersion : string.CompareOrdinal(left.ExecutablePath, right.ExecutablePath);
            });

            _detectedInstallations = [.. found];
            _installations = Merge(found, _customInstallations);
            _detectedAt = _clock.Now;
            _hasDetectionResult = true;

            WriteCache(found, _detectedAt.Value, _customInstallations);
            LogOutcome(found);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把检测结果记一条事件 —— 但只在结果**变了**的时候记，否则每 5 分钟就刷一条。</summary>
    private void LogOutcome(List<JavaInstallation> found)
    {
        var signature = string.Join(
            '|',
            found.Select(static installation =>
                $"{installation.ExecutablePath}#{installation.VersionText}"));

        if (string.Equals(signature, _lastLoggedSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedSignature = signature;

        // 两条分开写：`未检测到任何 Java 运行时` 没有参数，而"检测完成"要带上找到几个 ——
        // 合并成一条三元表达式会让"键里的占位符"和"参数个数"对不上（M6-b 的对账测试会红）。
        if (found.Count == 0)
        {
            _diagnostics.Write(LauncherEventLevel.Warning, DiagnosticsSources.JavaRuntime, "未检测到任何 Java 运行时");
            return;
        }

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.JavaRuntime,
            "Java 运行时检测完成（{0} 个）",
            [found.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }

    /// <summary>可执行文件所在的运行时根目录：<c>…\bin\javaw.exe</c> → <c>…</c>。</summary>
    private static string HomeDirectoryOf(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(directory))
        {
            return executablePath;
        }

        return string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) ?? directory
            : directory;
    }

    /// <summary>读缓存；条目里的可执行文件已经不在时丢弃。</summary>
    private (IReadOnlyList<JavaInstallation> Installations, DateTimeOffset? DetectedAt, bool HasResult) ReadCache()
    {
        var path = _paths.JavaRuntimeCacheFile;

        try
        {
            var json = AtomicFile.ReadAllTextOrNull(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return ([], null, false);
            }

            var cache = LauncherJson.Deserialize<JavaRuntimeCacheFile>(json, LauncherJson.Indented);
            if (cache is null)
            {
                return ([], null, false);
            }

            List<JavaInstallation> custom = [];
            foreach (var installation in cache.CustomRuntimes ?? [])
            {
                if (!string.IsNullOrWhiteSpace(installation.ExecutablePath)
                    && File.Exists(installation.ExecutablePath))
                {
                    custom.Add(installation);
                }
            }

            _customInstallations = custom;

            List<JavaInstallation> alive = [];
            foreach (var installation in cache.Runtimes ?? [])
            {
                if (!string.IsNullOrWhiteSpace(installation.ExecutablePath)
                    && File.Exists(installation.ExecutablePath))
                {
                    alive.Add(installation);
                }
            }

            _detectedInstallations = alive;

            // 缓存里有记录、但现在一条都不在了（卸载掉了）：连同"检测过"一起作废，
            // 因为"缺少 Java"这个结论必须建立在**当前**证据上。
            return alive.Count == 0 && (cache.Runtimes?.Count ?? 0) > 0
                ? (Merge([], custom), null, false)
                : (Merge(alive, custom), cache.DetectedAt, cache.DetectedAt.HasValue);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            // 缓存是可再生成的产物：读坏了就当没有，**不删文件**（下一次检测会原子覆盖它）。
            if (_corruptWarnings.ShouldReport(path))
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.JavaRuntime,
                    "Java 检测缓存不可用（{0}），本次重新检测（原文件未改动）",
                    [ex.GetType().Name]);
            }

            return ([], null, false);
        }
    }

    /// <summary>写缓存。</summary>
    private void WriteCache(
        IReadOnlyList<JavaInstallation> detected,
        DateTimeOffset? detectedAt,
        IReadOnlyList<JavaInstallation> custom)
    {
        try
        {
            var cache = new JavaRuntimeCacheFile
            {
                DetectedAt = detectedAt,
                Runtimes = detected,
                CustomRuntimes = custom,
            };

            AtomicFile.WriteAllText(
                _paths.JavaRuntimeCacheFile,
                LauncherJson.Serialize(cache, LauncherJson.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去只影响"下次冷启动要重新检测"，不影响这次的结果。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.JavaRuntime,
                "Java 检测结果写入缓存失败（{0}），下次启动会重新检测",
                [ex.GetType().Name]);
        }
    }

    /// <summary>缓存文件形状。</summary>
    private sealed record JavaRuntimeCacheFile
    {
        /// <summary>检测时刻；只有用户手动添加的条目时为空。</summary>
        public DateTimeOffset? DetectedAt { get; init; }

        /// <summary>自动检测到的运行时。</summary>
        public IReadOnlyList<JavaInstallation>? Runtimes { get; init; }

        /// <summary>用户手动添加的运行时（与自动检测结果合并展示）。</summary>
        public IReadOnlyList<JavaInstallation>? CustomRuntimes { get; init; }
    }

    /// <summary>把自动检测结果与用户自有条目合并；同一路径/同一 home 只保留先出现的那一条。</summary>
    private static List<JavaInstallation> Merge(
        IReadOnlyList<JavaInstallation> detected,
        IReadOnlyList<JavaInstallation> custom)
    {
        List<JavaInstallation> merged = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var installation in detected.Concat(custom))
        {
            if (seen.Add(installation.ExecutablePath)
                && seen.Add(installation.HomeDirectory))
            {
                merged.Add(installation);
            }
        }

        merged.Sort(static (left, right) =>
        {
            var byMajor = right.MajorVersion.CompareTo(left.MajorVersion);
            if (byMajor != 0)
            {
                return byMajor;
            }

            var byVersion = string.CompareOrdinal(right.VersionText, left.VersionText);
            return byVersion != 0 ? byVersion : string.CompareOrdinal(left.ExecutablePath, right.ExecutablePath);
        });

        return merged;
    }

    /// <summary>常见用户可写目录：临时目录、下载目录、桌面。</summary>
    private static bool IsUserWritableLocation(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] candidates =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
            Path.Combine(userProfile, "Downloads"),
        ];

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var prefix = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
