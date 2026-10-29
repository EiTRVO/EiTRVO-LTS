using System.Globalization;
using EiTRVO.Core.Launcher.Storage;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>一次安装回滚的结果。</summary>
/// <param name="RemovedFiles">删掉的、这次安装新增的文件数。</param>
/// <param name="RemovedDirectories">删掉的、这次安装新增的空目录数。</param>
/// <param name="RestoredFiles">恢复回安装前内容的文件数（目前只有 <c>launcher_profiles.json</c>）。</param>
/// <param name="FailedItems">没能处理掉的项数（文件被占用等；回滚是**尽力而为**的）。</param>
public sealed record InstallRollbackResult(int RemovedFiles, int RemovedDirectories, int RestoredFiles, int FailedItems)
{
    /// <summary>实际处理掉的项数。</summary>
    public int Total => RemovedFiles + RemovedDirectories + RestoredFiles;

    /// <summary>是否干净地回到了安装前（没有残留删不掉的东西）。</summary>
    public bool IsClean => FailedItems == 0;
}

/// <summary>
/// 一次安装的**事务**（M4-h / B-11）：装之前记下游戏目录里"本来有什么"，失败时把这次新增的东西清掉。
/// <para>
/// 为什么要它：下载器已经有"文件级原子写"（<c>.part</c> → 改名），但那是**每个文件**的完整性，
/// 而用户看到的是**一次安装**。装到一半失败时，目录里会留下一堆"这次下的库 + 没写完的版本目录"，
/// 而 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 才是"已安装"的唯一依据 —— 于是界面显示"未安装"、
/// 目录里却躺着几百 MB，重试还会因为文件在而走校验分支。事务把这一层补齐：
/// **失败就是回到装之前**，重试与第一次装走同一条路。
/// </para>
/// <para>
/// 记的是**路径快照**而不是"每写一个文件就登记一次"：官方安装器（Forge / NeoForge）是个黑盒，
/// 它往哪儿写由它自己决定，只有"装前 / 装后各扫一遍"才覆盖得到。三个根目录
/// （<c>versions</c> / <c>libraries</c> / <c>assets</c>）加一个 <c>launcher_profiles.json</c>
/// 就是一次安装能碰到的全部地方。
/// </para>
/// <para>
/// 两条刻意的地方：**只删这次新增的**（装前就存在的文件与目录一个都不动 —— 用户手放进
/// <c>libraries</c> 的东西不该被我们清掉），以及**回滚是尽力而为的**：删不掉的项要如实计数，
/// 而不是抛异常把"安装失败"变成"回滚也炸了"。
/// </para>
/// </summary>
public sealed class InstallTransaction
{
    /// <summary>一次安装会写入的三个根目录。</summary>
    public static readonly IReadOnlyList<string> TrackedRoots = ["versions", "libraries", "assets"];

    /// <summary>官方安装器要求存在、且可能被它改写的那个文件。</summary>
    public const string LauncherProfilesFileName = "launcher_profiles.json";

    private readonly string _gameDirectory;
    private readonly Dictionary<string, HashSet<string>> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rootsThatExisted = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _profilesExisted;
    private readonly string? _profilesContent;

    private bool _finished;

    private InstallTransaction(
        string gameDirectory,
        Dictionary<string, HashSet<string>> snapshot,
        HashSet<string> rootsThatExisted,
        bool profilesExisted,
        string? profilesContent)
    {
        _gameDirectory = gameDirectory;
        _snapshot = snapshot;
        _rootsThatExisted = rootsThatExisted;
        _profilesExisted = profilesExisted;
        _profilesContent = profilesContent;
    }

    /// <summary>在动手之前拍一张快照。</summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <returns>事务句柄。</returns>
    public static InstallTransaction Begin(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var root = Path.GetFullPath(gameDirectory);
        Dictionary<string, HashSet<string>> snapshot = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> rootsThatExisted = new(StringComparer.OrdinalIgnoreCase);

        foreach (var name in TrackedRoots)
        {
            var directory = Path.Combine(root, name);

            if (Directory.Exists(directory))
            {
                rootsThatExisted.Add(name);
            }

            snapshot[name] = Snapshot(directory);
        }

        var profilesPath = Path.Combine(root, LauncherProfilesFileName);
        var existed = File.Exists(profilesPath);

        return new InstallTransaction(
            root,
            snapshot,
            rootsThatExisted,
            existed,
            existed ? AtomicFile.ReadAllTextOrNull(profilesPath) : null);
    }

    /// <summary>安装成功：丢掉快照，什么都不用做。</summary>
    public void Commit() => _finished = true;

    /// <summary>安装失败：把这次新增的东西清掉，并把被改写的那个文件恢复回去。</summary>
    /// <returns>回滚结果（删了什么、什么没删掉）。</returns>
    /// <exception cref="InvalidOperationException">事务已经结束过（提交或回滚）。</exception>
    public InstallRollbackResult Rollback()
    {
        if (_finished)
        {
            throw new InvalidOperationException("这次安装的事务已经结束了。");
        }

        _finished = true;

        var removedFiles = 0;
        var removedDirectories = 0;
        var restoredFiles = 0;
        var failedItems = 0;

        foreach (var name in TrackedRoots)
        {
            var directory = Path.Combine(_gameDirectory, name);

            if (!Directory.Exists(directory))
            {
                continue;
            }

            var known = _snapshot[name];

            // 先删文件：目录要等它空了才能删。
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (known.Contains(Path.GetRelativePath(directory, file)))
                {
                    continue;
                }

                if (TryDeleteFile(file))
                {
                    removedFiles++;
                }
                else
                {
                    failedItems++;
                }
            }

            // 再删空目录，**深的先删**（否则父目录永远非空）。
            foreach (var child in Directory
                .EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                .OrderByDescending(static path => path.Length))
            {
                if (known.Contains(Path.GetRelativePath(directory, child)))
                {
                    continue;
                }

                if (TryDeleteEmptyDirectory(child))
                {
                    removedDirectories++;
                }
            }

            // 根目录自己在装前不存在的话，这次建出来的空壳也要清掉
            // （它不在上面的枚举结果里 —— 那只列子目录）。
            if (!_rootsThatExisted.Contains(name) && TryDeleteEmptyDirectory(directory))
            {
                removedDirectories++;
            }
        }

        var profilesPath = Path.Combine(_gameDirectory, LauncherProfilesFileName);

        if (!_profilesExisted)
        {
            // 这个文件是我们为了满足官方安装器而补的：既然装失败了，它也不该留下。
            if (File.Exists(profilesPath))
            {
                if (TryDeleteFile(profilesPath))
                {
                    removedFiles++;
                }
                else
                {
                    failedItems++;
                }
            }
        }
        else if (_profilesContent is not null
                 && !string.Equals(AtomicFile.ReadAllTextOrNull(profilesPath), _profilesContent, StringComparison.Ordinal))
        {
            // 官方安装器会往里注入 profile —— 那是**它的**改动，装失败就得还回去。
            try
            {
                AtomicFile.WriteAllText(profilesPath, _profilesContent);
                restoredFiles++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failedItems++;
            }
        }

        return new InstallRollbackResult(removedFiles, removedDirectories, restoredFiles, failedItems);
    }

    /// <summary>一句话说清回滚做了什么（交给调用方拼进失败原因里）。</summary>
    /// <param name="result">回滚结果。</param>
    /// <returns>键 + 参数；没有可说的内容时为 <see langword="null"/>。</returns>
    public static TextTemplate? Describe(InstallRollbackResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Total == 0 && result.IsClean)
        {
            return null;
        }

        // 参数**逐个写出来**（不用数组变量、也不用集合表达式）：门禁是按源码里的实参个数数占位符的，
        // 传一个数组变量会让它数成 1 个，于是"占位符对不上"这条检查就废了（AGENTS.md §5.3 第 ② 条）。
        var total = result.Total.ToString(CultureInfo.InvariantCulture);
        var removedFiles = result.RemovedFiles.ToString(CultureInfo.InvariantCulture);
        var removedDirectories = result.RemovedDirectories.ToString(CultureInfo.InvariantCulture);
        var restoredFiles = result.RestoredFiles.ToString(CultureInfo.InvariantCulture);

        // 两句话各是一个键："删干净了" 与 "有几项没删掉" 不是同一件事。
        return result.IsClean
            ? TextTemplate.Of(
                "已回滚这次安装写入的 {0} 项（文件 {1}、目录 {2}、恢复 {3}）。",
                total,
                removedFiles,
                removedDirectories,
                restoredFiles)
            : TextTemplate.Of(
                "已回滚这次安装写入的 {0} 项（文件 {1}、目录 {2}、恢复 {3}）；有 {4} 项没删掉（可能被占用）。",
                total,
                removedFiles,
                removedDirectories,
                restoredFiles,
                result.FailedItems.ToString(CultureInfo.InvariantCulture));
    }

    private static HashSet<string> Snapshot(string directory)
    {
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(directory))
        {
            return known;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            known.Add(Path.GetRelativePath(directory, entry));
        }

        return known;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 被占用 / 只读：如实算作"没删掉"，不把回滚变成第二个故障。
            return false;
        }
    }

    private static bool TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return false;
            }

            Directory.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
