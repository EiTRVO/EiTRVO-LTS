using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Security;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>运行时清单里的一条链接（Windows 上退化成复制一份目标文件）。</summary>
/// <param name="RelativePath">链接自身的相对路径。</param>
/// <param name="Target">目标相对路径（相对运行时根目录，可以带 <c>..</c>，但必须仍在根目录内）。</param>
public sealed record JavaRuntimeLink(string RelativePath, string Target);

/// <summary>
/// 一次 Java 运行时安装要做的全部事情（**只是计划**，不碰网络也不碰磁盘）。
/// <para>
/// 与 <c>InstallPlan</c> 同样的理由：路径、下载项、目录、链接都能在离线单测里逐个钉住。
/// 这里多一条：清单来自远端，它的键会直接拼进磁盘路径，因此**每个相对路径都要过一遍安全校验**
/// （越界、含驱动器号、含反斜杠的键一律拒绝整份清单 —— 一份带穿越路径的清单只能是被改过的）。
/// </para>
/// </summary>
/// <param name="Component">组件名（<c>java-runtime-gamma</c>）。</param>
/// <param name="VersionName">组件版本名（<c>17.0.15</c>）。</param>
/// <param name="Platform">平台标识（<c>windows-x64</c>）。</param>
/// <param name="InstallDirectory">最终安装目录（<c>&lt;数据目录&gt;\runtime\&lt;组件&gt;\&lt;平台&gt;\&lt;组件&gt;</c>）。</param>
/// <param name="StagingDirectory">暂存目录（下载与布局都先落在这里，成功后才整体搬进安装目录）。</param>
/// <param name="ExecutableRelativePath">可执行文件的相对路径（<c>bin/javaw.exe</c> 优先）；清单里没有则为 <see langword="null"/>。</param>
/// <param name="Files">要下载的文件。</param>
/// <param name="Directories">要建出来的目录（绝对路径，位于暂存目录下）。</param>
/// <param name="Links">要物化的链接。</param>
public sealed record JavaRuntimePlan(
    string Component,
    string VersionName,
    string Platform,
    string InstallDirectory,
    string StagingDirectory,
    string? ExecutableRelativePath,
    IReadOnlyList<DownloadItem> Files,
    IReadOnlyList<string> Directories,
    IReadOnlyList<JavaRuntimeLink> Links)
{
    /// <summary>预计下载的总字节数。</summary>
    public long TotalBytes => DiskSpaceChecker.TotalBytes(Files);

    /// <summary>暂存目录里的可执行文件路径（下载完成后、搬运之前用它确认布局完整）。</summary>
    public string? StagingExecutablePath => Combine(StagingDirectory, ExecutableRelativePath);

    /// <summary>安装目录里的可执行文件路径（搬运之后用它确认结果）。</summary>
    public string? InstalledExecutablePath => Combine(InstallDirectory, ExecutableRelativePath);

    private static string? Combine(string root, string? relativePath) =>
        string.IsNullOrEmpty(relativePath) ? null : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// 把一份运行时文件清单变成安装计划。
/// <para>
/// 与官方布局保持一致：<c>&lt;数据目录&gt;\runtime\&lt;组件&gt;\&lt;平台&gt;\&lt;组件&gt;</c>。
/// 最里面那一层重复组件名不是笔误 —— 官方启动器就是这个形状（参照实现
/// <c>minecraft-launcher-lib</c> 的 <c>install_jvm_runtime</c> 也是
/// <c>runtime/&lt;组件&gt;/&lt;平台&gt;/&lt;组件&gt;</c>），而 M3 的目录扫描深度 4 正是照它准备的。
/// </para>
/// </summary>
public static class JavaRuntimePlanner
{
    /// <summary>暂存目录在下载缓存下的子目录名。</summary>
    public const string StagingFolderName = "java-runtime";

    /// <summary>生成安装计划。</summary>
    /// <param name="manifest">文件清单。</param>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="candidate">索引里选中的候选（决定组件名与版本名）。</param>
    /// <returns>安装计划。</returns>
    /// <exception cref="JavaRuntimeDataException">组件名/平台不可用作目录名、条目路径不安全、条目缺地址或类型不认识。</exception>
    public static JavaRuntimePlan Plan(
        JavaRuntimeManifest manifest,
        ILauncherPaths paths,
        JavaRuntimeCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!JavaRuntimePathSafety.IsSafeComponent(candidate.Component))
        {
            throw new JavaRuntimeDataException($"官方清单里的组件名不能用作目录名：'{candidate.Component}'。");
        }

        if (!JavaRuntimePathSafety.IsSafeComponent(candidate.Platform))
        {
            throw new JavaRuntimeDataException($"官方清单里的平台名不能用作目录名：'{candidate.Platform}'。");
        }

        var installDirectory = PathSafety.CombineWithin(
            paths.JavaRuntimeDirectory,
            candidate.Component,
            candidate.Platform,
            candidate.Component);

        var stagingDirectory = PathSafety.CombineWithin(
            paths.DownloadCacheDirectory,
            StagingFolderName,
            candidate.Component,
            candidate.Platform);

        List<DownloadItem> files;
        List<string> directories;
        List<JavaRuntimeLink> links;

        try
        {
            (files, directories, links) = ReadEntries(manifest, candidate, stagingDirectory);
        }
        catch (InvalidDataException ex)
        {
            // PathSafety 的越界 / 重解析点检查：拼路径时才发现的那一类（含路径里的符号链接）。
            // 换成类型化异常，调用方才有一句能读的结论，而不是一个没有出处的 InvalidDataException。
            throw new JavaRuntimeDataException($"官方清单里的路径不可用（拒绝整份清单）：{ex.Message}", ex);
        }

        return new JavaRuntimePlan(
            Component: candidate.Component,
            VersionName: candidate.VersionName,
            Platform: candidate.Platform,
            InstallDirectory: installDirectory,
            StagingDirectory: stagingDirectory,
            ExecutableRelativePath: ExecutableOf(files, stagingDirectory),
            Files: files,
            Directories: directories,
            Links: links);
    }

    /// <summary>把清单条目分成"要下载的文件 / 要建的目录 / 要物化的链接"，并把路径拼到暂存目录下。</summary>
    /// <param name="manifest">文件清单。</param>
    /// <param name="candidate">选中的候选。</param>
    /// <param name="stagingDirectory">暂存目录（相对布局的基准）。</param>
    /// <returns>三份清单。</returns>
    /// <exception cref="JavaRuntimeDataException">路径不安全、条目缺地址、链接缺目标、条目类型不认识。</exception>
    private static (List<DownloadItem> Files, List<string> Directories, List<JavaRuntimeLink> Links) ReadEntries(
        JavaRuntimeManifest manifest,
        JavaRuntimeCandidate candidate,
        string stagingDirectory)
    {
        List<DownloadItem> files = [];
        List<string> directories = [];
        List<JavaRuntimeLink> links = [];

        foreach (var entry in manifest.Entries)
        {
            if (!JavaRuntimePathSafety.IsSafe(entry.RelativePath))
            {
                throw new JavaRuntimeDataException(
                    $"官方清单里有不安全的路径（拒绝整份清单）：'{entry.RelativePath}'。");
            }

            switch (entry.Kind)
            {
                case JavaRuntimeFileKind.File:
                    if (string.IsNullOrWhiteSpace(entry.Url))
                    {
                        throw new JavaRuntimeDataException(
                            $"官方清单里「{entry.RelativePath}」没有下载地址。");
                    }

                    files.Add(new DownloadItem(
                        DownloadKind.JavaRuntime,
                        candidate.Component,
                        entry.Url,
                        entry.Sha1 ?? string.Empty,
                        entry.Size,
                        PathSafety.CombineWithin(
                            stagingDirectory,
                            JavaRuntimePathSafety.Segments(entry.RelativePath))));

                    break;

                case JavaRuntimeFileKind.Directory:
                    directories.Add(PathSafety.CombineWithin(
                        stagingDirectory,
                        JavaRuntimePathSafety.Segments(entry.RelativePath)));
                    break;

                case JavaRuntimeFileKind.Link:
                    if (string.IsNullOrWhiteSpace(entry.Target))
                    {
                        throw new JavaRuntimeDataException(
                            $"官方清单里「{entry.RelativePath}」是链接但没有 target。");
                    }

                    // 链接目标允许带 ..（官方清单里就是这样写的），但**必须仍在运行时根目录内** ——
                    // 借用 CombineWithin 的越界与重解析点检查，而不是自己写一套相对路径解析。
                    // 越界时它抛 InvalidDataException；这里换成带上下文的类型化异常，
                    // 否则调用方只会看到一句没有出处的"路径穿越检测"。
                    try
                    {
                        PathSafety.CombineWithin(
                            stagingDirectory,
                            JavaRuntimePathSafety.Segments(entry.Target));
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new JavaRuntimeDataException(
                            $"官方清单里链接「{entry.RelativePath}」的目标越出了运行时目录：'{entry.Target}'。",
                            ex);
                    }

                    links.Add(new JavaRuntimeLink(entry.RelativePath, entry.Target));
                    break;

                default:
                    throw new JavaRuntimeDataException(
                        $"官方清单里「{entry.RelativePath}」的条目类型本启动器不认识（拒绝整份清单）。");
            }
        }

        return (files, directories, links);
    }

    /// <summary>挑出"要用它来启动游戏"的那个可执行文件（返回它在运行时根目录下的相对路径）。</summary>
    /// <remarks>
    /// 只认 <c>javaw.exe</c> 与 <c>java.exe</c>（大小写不敏感），<c>javaw.exe</c> 优先 ——
    /// 与 <c>JavaDiscovery</c> 的取舍一致：GUI 子系统，不会有控制台窗口。
    /// 按"下载项里真的有这个文件"判断，而不是假设它在清单里（清单是远端数据）；
    /// 返回的也是**它在清单里的实际位置**，不硬写 <c>bin/</c>。
    /// </remarks>
    private static string? ExecutableOf(IReadOnlyList<DownloadItem> files, string stagingDirectory)
    {
        string? java = null;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file.TargetPath);

            if (string.Equals(name, "javaw.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Relative(file.TargetPath);
            }

            if (java is null && string.Equals(name, "java.exe", StringComparison.OrdinalIgnoreCase))
            {
                java = Relative(file.TargetPath);
            }
        }

        return java;

        string Relative(string targetPath) =>
            Path.GetRelativePath(stagingDirectory, targetPath).Replace(Path.DirectorySeparatorChar, '/');
    }
}
