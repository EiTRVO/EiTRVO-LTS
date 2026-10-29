using System.Globalization;
using System.IO.Compression;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content.Modpack;

/// <summary>压缩包安全扫描的结论。</summary>
/// <param name="Findings">拦下来的原因（键 + 参数）；任何一条都足以拒绝导入。</param>
/// <param name="FileCount">压缩包里的文件条目数（不含目录条目）。</param>
/// <param name="TotalUncompressedBytes">声明的解压后总字节数（只用于显示，不是真实解压结果）。</param>
/// <param name="StrayRootEntries">不在 <c>overrides/</c> / <c>client-overrides/</c> 里、也不认识的条目数（**忽略但记账**）。</param>
public sealed record ArchiveSafetyReport(
    IReadOnlyList<TextTemplate> Findings,
    int FileCount,
    long TotalUncompressedBytes,
    int StrayRootEntries)
{
    /// <summary>有没有拦下来的问题。</summary>
    public bool HasFindings => Findings.Count > 0;
}

/// <summary>
/// 压缩包的安全扫描（M7-e）：zip 炸弹、路径穿越、危险扩展名、下载源白名单。
/// </summary>
/// <remarks>
/// <para>
/// 这四项正是设计稿 p08 上那行说明写的东西（"导入 <c>.mrpack</c> 前会先做安全扫描：检查 zip 炸弹、
/// 路径穿越、危险扩展名与外部下载源白名单"），所以它们是**阻断**而不是提醒 ——
/// 设计稿承诺过这件事，我们就得真的做到。Neo 的同名实现把"下载源不在白名单"只记成警告
/// （见迁移评估 §5.1 允许借鉴内核逻辑的条目），这里刻意改成阻断：那类文件在下载器那一层
/// 本来就会被 `HttpHostPolicy` 拒掉，留到那时才失败只会给出一句看不懂的"所有候选地址都不可用"。
/// </para>
/// <para>
/// <b>故意不检查的东西</b>：覆盖层（<c>overrides/</c>）里的文件**没有校验值可用**，
/// 这是格式本身的限制（<c>files[]</c> 才带 <c>hashes</c>），不是我们漏了检查 ——
/// 能做的只有把危险类型挡住（可执行文件、原生库）。所以导入完成的界面文案不能声称
/// "所有文件都校验过"，只能说"清单里的文件都校验过"。
/// </para>
/// </remarks>
public sealed class ArchiveSafetyScanner
{
    /// <summary>压缩包本身的大小上限（500 MB）。</summary>
    public const long MaxArchiveBytes = 500L * 1024 * 1024;

    /// <summary>解压后总大小的上限（2 GB）。</summary>
    public const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>单个条目解压后的大小上限（200 MB）。</summary>
    public const long MaxSingleEntryBytes = 200L * 1024 * 1024;

    /// <summary>条目数上限。</summary>
    public const int MaxEntries = 10_000;

    /// <summary>单个条目的压缩比上限（解压后 / 压缩后）。</summary>
    public const double MaxCompressionRatio = 200d;

    /// <summary>
    /// 只有解压后达到这个大小才看压缩比。
    /// </summary>
    /// <remarks>
    /// 小文件的压缩比天然很高（一个 1 KB 的重复文本能压到几十字节，比值 40+），
    /// 一刀切会让正常整合包被误判 —— 而 zip 炸弹的杀伤力来自**巨大**的解压后体积。
    /// </remarks>
    public const long CompressionRatioFloorBytes = 10L * 1024 * 1024;

    /// <summary>
    /// 不允许出现在整合包里的扩展名。
    /// </summary>
    /// <remarks>
    /// 判据是"这个类型在 Minecraft 实例目录里没有合法用途"：可执行文件、脚本、安装包、
    /// 快捷方式，以及原生库（<c>.dll</c> / <c>.so</c> / <c>.dylib</c> —— 真正要跑代码的模组是
    /// <c>.jar</c>，它必须放行）。
    /// </remarks>
    public static readonly IReadOnlySet<string> BlockedExtensions = new HashSet<string>(
        [
            ".exe", ".dll", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".wsf", ".wsh",
            ".msi", ".msp", ".scr", ".pif", ".reg", ".com", ".cpl", ".hta", ".jse", ".sct",
            ".so", ".dylib", ".lnk", ".url", ".js", ".ws",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>认识的覆盖层目录（按**应用顺序**：先 <c>overrides/</c>，再 <c>client-overrides/</c>）。</summary>
    /// <remarks>
    /// <c>server-overrides/</c> 刻意不在列表里：它是给**专用服务端**的（规范原文），
    /// 装进客户端实例只会多出一堆服务端专用的配置与模组。
    /// </remarks>
    public static readonly IReadOnlyList<string> OverrideDirectories = ["overrides/", "client-overrides/"];

    private readonly HttpHostPolicy _hostPolicy;

    /// <summary>构造扫描器。</summary>
    /// <param name="hostPolicy">
    /// 出站域名策略（与下载器**同一个**策略对象）：扫描判"这个下载源能不能用"的尺子，
    /// 必须和真正发请求时的尺子一致，否则扫描通过、下载被拒。
    /// </param>
    public ArchiveSafetyScanner(HttpHostPolicy hostPolicy)
    {
        ArgumentNullException.ThrowIfNull(hostPolicy);
        _hostPolicy = hostPolicy;
    }

    /// <summary>扫描一个压缩包。</summary>
    /// <param name="archivePath">压缩包路径。</param>
    /// <param name="index">清单（可选：给了就一并检查 <c>files[]</c> 的路径、扩展名与下载源）。</param>
    /// <returns>结论（不抛异常：拦下来的原因都在 <see cref="ArchiveSafetyReport.Findings"/> 里）。</returns>
    public ArchiveSafetyReport Scan(string archivePath, ModpackIndex? index = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        List<TextTemplate> findings = [];
        int fileCount = 0;
        long totalBytes = 0;
        var stray = 0;

        var info = new FileInfo(archivePath);

        if (!info.Exists)
        {
            return new ArchiveSafetyReport(
                [TextTemplate.Of("找不到这个整合包文件。")],
                0,
                0,
                0);
        }

        if (info.Length > MaxArchiveBytes)
        {
            return new ArchiveSafetyReport(
                [TextTemplate.Of(
                    "整合包文件太大（{0} MB），超过 {1} MB 的上限。",
                    Megabytes(info.Length),
                    Megabytes(MaxArchiveBytes))],
                0,
                0,
                0);
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            foreach (var entry in archive.Entries)
            {
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

                if (!isDirectory)
                {
                    fileCount++;

                    if (fileCount > MaxEntries)
                    {
                        findings.Add(TextTemplate.Of(
                            "压缩包里的文件超过 {0} 个，看起来像 zip 炸弹。",
                            MaxEntries.ToString(CultureInfo.InvariantCulture)));
                        return new ArchiveSafetyReport(findings, fileCount, totalBytes, stray);
                    }

                    totalBytes += Math.Max(0, entry.Length);

                    if (totalBytes > MaxTotalUncompressedBytes)
                    {
                        findings.Add(TextTemplate.Of(
                            "压缩包解压后超过 {0} MB，看起来像 zip 炸弹。",
                            Megabytes(MaxTotalUncompressedBytes)));
                        return new ArchiveSafetyReport(findings, fileCount, totalBytes, stray);
                    }

                    if (entry.Length > MaxSingleEntryBytes)
                    {
                        findings.Add(TextTemplate.Of(
                            "压缩包里的 {0} 解压后超过 {1} MB。",
                            entry.FullName,
                            Megabytes(MaxSingleEntryBytes)));
                    }

                    if (entry.Length >= CompressionRatioFloorBytes
                        && entry.CompressedLength > 0
                        && (double)entry.Length / entry.CompressedLength > MaxCompressionRatio)
                    {
                        findings.Add(TextTemplate.Of(
                            "压缩包里的 {0} 压缩比异常（{1}:1），看起来像 zip 炸弹。",
                            entry.FullName,
                            ((long)((double)entry.Length / entry.CompressedLength)).ToString(CultureInfo.InvariantCulture)));
                    }
                }

                // ---- 路径穿越（zip 条目名这一面）----
                var raw = entry.FullName.Replace('\\', '/').TrimEnd('/');

                if (raw.Length == 0)
                {
                    continue;
                }

                if (!ModpackFilePaths.TryNormalize(raw, out _, out _))
                {
                    findings.Add(TextTemplate.Of("压缩包里的条目路径越出了实例目录（{0}）。", entry.FullName));
                    continue;
                }

                if (!isDirectory)
                {
                    var extension = Path.GetExtension(entry.Name);

                    if (!string.IsNullOrEmpty(extension) && BlockedExtensions.Contains(extension))
                    {
                        findings.Add(TextTemplate.Of(
                            "压缩包里有不允许的文件类型（{0}：{1}）。",
                            extension,
                            entry.FullName));
                    }

                    if (!IsKnownEntry(raw))
                    {
                        stray++;
                    }
                }
            }
        }
        catch (InvalidDataException ex)
        {
            return new ArchiveSafetyReport(
                [TextTemplate.Of("这个压缩包已经损坏或不是 zip（{0}）。", ex.GetType().Name)],
                fileCount,
                totalBytes,
                stray);
        }

        if (index is not null)
        {
            ScanIndex(index, findings);
        }

        return new ArchiveSafetyReport(findings, fileCount, totalBytes, stray);
    }

    /// <summary>清单侧的检查：路径的扩展名与每个下载地址的合法性。</summary>
    private void ScanIndex(ModpackIndex index, List<TextTemplate> findings)
    {
        foreach (var file in index.Files)
        {
            if (!file.ClientSupported)
            {
                // 客户端根本不装的文件：**不检查它的下载源** —— 拿一个永远不会被访问的地址
                // 把整个导入拦下来，是在用自己的规矩去否决规范允许的包。
                continue;
            }

            var extension = Path.GetExtension(file.Path);

            if (!string.IsNullOrEmpty(extension) && BlockedExtensions.Contains(extension))
            {
                findings.Add(TextTemplate.Of(
                    "清单里有不允许的文件类型（{0}：{1}）。",
                    extension,
                    file.Path));
            }

            foreach (var url in file.Downloads)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    findings.Add(TextTemplate.Of("清单里 {0} 的下载地址不是合法地址（{1}）。", file.Path, url));
                    continue;
                }

                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(TextTemplate.Of("清单里 {0} 的下载地址不是 https（{1}）。", file.Path, url));
                    continue;
                }

                if (!_hostPolicy.IsAllowed(uri))
                {
                    findings.Add(TextTemplate.Of(
                        "清单里 {0} 的下载源不在允许的域名内（{1}）。",
                        file.Path,
                        uri.Host));
                }
            }
        }
    }

    /// <summary>这个条目是不是"我们认识的位置"（清单本身或两个覆盖层）。</summary>
    private static bool IsKnownEntry(string normalizedPath) =>
        string.Equals(normalizedPath, MrpackReader.IndexEntryName, StringComparison.OrdinalIgnoreCase)
        || OverrideDirectories.Any(directory => normalizedPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase));

    private static string Megabytes(long bytes) =>
        (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
}
