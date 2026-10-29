using EiTRVO.Core.Launcher.Diagnostics;

namespace EiTRVO.Core.Launcher.Downloads;

/// <summary>磁盘空间不足（B-13）。</summary>
public sealed class InsufficientDiskSpaceException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="report">预检结果（界面据此显示"差多少"）。</param>
    public InsufficientDiskSpaceException(DiskSpaceReport report)
        : base($"磁盘空间不足：还需要 {report.ShortfallBytes / (1024 * 1024)} MB。")
    {
        Report = report;
    }

    /// <summary>预检结果。</summary>
    public DiskSpaceReport Report { get; }
}

/// <summary>磁盘空间预检结果。</summary>
/// <param name="Path">检查的目录。</param>
/// <param name="RequiredBytes">下载与解压预计需要的字节数。</param>
/// <param name="SafetyMarginBytes">预留余量。</param>
/// <param name="FreeBytes">当前可用字节数。</param>
public sealed record DiskSpaceReport(
    string Path,
    long RequiredBytes,
    long SafetyMarginBytes,
    long FreeBytes)
{
    /// <summary>是否够用。</summary>
    public bool IsEnough => FreeBytes >= RequiredBytes + SafetyMarginBytes;

    /// <summary>还差多少字节（够用时为 0）。</summary>
    public long ShortfallBytes => Math.Max(0, RequiredBytes + SafetyMarginBytes - FreeBytes);
}

/// <summary>
/// 安装前的磁盘空间预检（B-13）。
/// <para>
/// 为什么必须**下载前**检查：一次原版安装是几百 MB 到 1 GB 的下载。磁盘在 90% 处写满时，
/// 用户已经花掉十几分钟流量，而失败信息往往只是一句 <c>IOException</c>。
/// 提前算一次总大小 + 余量，就能在开始前明确告诉他"还差 1.2 GB"。
/// </para>
/// </summary>
public sealed class DiskSpaceChecker
{
    /// <summary>默认预留余量：512 MB。</summary>
    /// <remarks>
    /// 余量不是保守估计，它有具体来源：natives 解压（几十 MB）、下载期间的 <c>.part</c> 与
    /// 最终文件短暂共存（单个最大文件的大小）、以及游戏启动后写日志与存档的空间。
    /// 留得太小等于没留 —— 用户会因为"刚好差 20 MB"在 99% 处失败。
    /// </remarks>
    public const long DefaultSafetyMarginBytes = 512L * 1024 * 1024;

    private readonly IDiagnosticsLog _diagnostics;
    private readonly Func<string, long> _freeSpaceProbe;

    /// <summary>构造预检器。</summary>
    /// <param name="diagnostics">诊断事件。</param>
    /// <param name="freeSpaceProbe">
    /// 卷可用空间的探测函数（入参是卷根，如 <c>E:\</c>）；缺省读真实磁盘。
    /// <para>
    /// 之所以留这个接缝：余量逻辑（<c>FreeBytes >= Required + Margin</c>）应当能被**确定性地**测，
    /// 而真实磁盘的可用空间在测试运行期间随时会变 —— 同一条命令里的构建/测试会删掉临时文件，
    /// 可用空间甚至可能**变大**。原先那条"恰好等于可用空间"的用例正是因此会偶发翻车。
    /// </para>
    /// </param>
    public DiskSpaceChecker(IDiagnosticsLog diagnostics, Func<string, long>? freeSpaceProbe = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        _diagnostics = diagnostics;
        _freeSpaceProbe = freeSpaceProbe ?? ProbeDrive;
    }

    /// <summary>缺省探测：读真实卷的可用空间（不可用的卷按 0 计，于是"不够用"而不是"够用"）。</summary>
    private static long ProbeDrive(string root)
    {
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
        }
        catch (ArgumentException ex)
        {
            throw new IOException($"无法确定驱动器 '{root}'。", ex);
        }

        return drive.IsReady ? drive.AvailableFreeSpace : 0;
    }

    /// <summary>检查某个目录所在卷的可用空间。</summary>
    /// <param name="targetDirectory">目标目录（可以还不存在）。</param>
    /// <param name="requiredBytes">预计需要的字节数。</param>
    /// <param name="safetyMarginBytes">预留余量；缺省 <see cref="DefaultSafetyMarginBytes"/>。</param>
    /// <returns>预检结果。</returns>
    /// <exception cref="IOException">无法确定目标所在的卷（例如路径指向一个不存在的驱动器）。</exception>
    public DiskSpaceReport Check(
        string targetDirectory,
        long requiredBytes,
        long safetyMarginBytes = DefaultSafetyMarginBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(safetyMarginBytes);

        var fullPath = Path.GetFullPath(targetDirectory);

        // 目标目录可能还不存在，所以往上找到第一个存在的祖先 —— 用它判断属于哪个卷。
        var probe = fullPath;
        while (!Directory.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
            {
                break;
            }

            probe = parent;
        }

        var root = Path.GetPathRoot(probe);
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException($"无法确定 '{targetDirectory}' 所在的驱动器。");
        }

        var free = _freeSpaceProbe(root);
        var report = new DiskSpaceReport(fullPath, requiredBytes, safetyMarginBytes, free);

        if (!report.IsEnough)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Downloader,
                "磁盘空间不足：{0} 还缺 {1} MB",
                [
                    fullPath,
                    (report.ShortfallBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ]);
        }

        return report;
    }

    /// <summary>检查并要求够用，不够就抛 <see cref="InsufficientDiskSpaceException"/>。</summary>
    /// <param name="targetDirectory">目标目录。</param>
    /// <param name="requiredBytes">预计需要的字节数。</param>
    /// <param name="safetyMarginBytes">预留余量。</param>
    /// <returns>预检结果。</returns>
    public DiskSpaceReport EnsureAvailable(
        string targetDirectory,
        long requiredBytes,
        long safetyMarginBytes = DefaultSafetyMarginBytes)
    {
        var report = Check(targetDirectory, requiredBytes, safetyMarginBytes);

        if (!report.IsEnough)
        {
            throw new InsufficientDiskSpaceException(report);
        }

        return report;
    }

    /// <summary>把一批下载项的大小加总（未知大小按 0 计）。</summary>
    /// <param name="items">下载项。</param>
    /// <returns>总字节数。</returns>
    public static long TotalBytes(IEnumerable<DownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        long total = 0;
        foreach (var item in items)
        {
            total += Math.Max(0, item.Size);
        }

        return total;
    }
}
