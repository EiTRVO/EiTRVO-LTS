using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// Java 可执行文件的发现（C-6）：<c>PATH</c> → <c>JAVA_HOME</c>／注册表登记的根目录 → 常见安装目录。
/// <para>
/// 只做"找出候选可执行文件"这一件事，**不解析版本**（那是 <see cref="JavaVersionParser"/> 的活），
/// 也不判断哪个能用（那是 <see cref="JavaPreflight"/> 的活）—— 因为这三件事各有各的坑：
/// 这里的坑是"目录去重与 <c>javaw.exe</c> 优先"，混在一起就测不清了。
/// </para>
/// <para>
/// <b>为什么优先 <c>javaw.exe</c></b>：它是 GUI 子系统的可执行文件。用 <c>java.exe</c> 起游戏会多出一个
/// 控制台窗口、任务栏图标也不是游戏的（Neo 踩过，注释里写着"proper Minecraft window icon"）。
/// 同一目录里两个都有时只保留 <c>javaw.exe</c> —— 否则界面上会出现两条同一个 JDK 的记录。
/// </para>
/// </summary>
public sealed class JavaDiscovery
{
    private readonly IJavaEnvironment _environment;

    /// <summary>构造发现器。</summary>
    /// <param name="environment">环境来源（PATH / JAVA_HOME / 注册表 / 常见目录）。</param>
    public JavaDiscovery(IJavaEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>找出本机全部候选 Java 可执行文件。</summary>
    /// <returns>可执行文件路径（按发现顺序、每个目录最多一个、<c>javaw.exe</c> 优先）。</returns>
    public IReadOnlyList<string> FindExecutables()
    {
        var executables = _environment.GetExecutableNames();
        if (executables.Count == 0)
        {
            return [];
        }

        var found = new Candidates();

        // ① PATH：最常见的来源，先看它（用户特意放进 PATH 的那个通常就是他想要的）。
        foreach (var directory in _environment.GetPathDirectories())
        {
            var cleaned = Clean(directory);
            if (cleaned is null)
            {
                continue;
            }

            foreach (var name in executables)
            {
                found.Add(cleaned, name);
            }
        }

        // ② JAVA_HOME 与注册表登记的安装根目录：下一级就是 bin。
        foreach (var home in _environment.GetJavaHomes())
        {
            var cleaned = Clean(home);
            if (cleaned is null)
            {
                continue;
            }

            foreach (var name in executables)
            {
                found.Add(Path.Combine(cleaned, "bin"), name);
            }
        }

        // ③ 厂商目录：递归到各自声明的深度（启动器自己的 runtime\ 目录比厂商目录深两级）。
        foreach (var root in _environment.GetSearchRoots())
        {
            var cleaned = Clean(root.Path);
            if (cleaned is null)
            {
                continue;
            }

            Scan(cleaned, Math.Max(0, root.MaxDepth), executables, found);
        }

        return found.Result();
    }

    /// <summary>递归扫描一个根目录：每层都看"本层的可执行文件"与"本层 bin\ 下的可执行文件"。</summary>
    private static void Scan(string directory, int remainingDepth, IReadOnlyList<string> executables, Candidates found)
    {
        foreach (var name in executables)
        {
            found.Add(directory, name);
            found.Add(Path.Combine(directory, "bin"), name);
        }

        if (remainingDepth == 0)
        {
            return;
        }

        IReadOnlyList<string> children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不进去的目录（权限、被占用、已卸载的盘）不是错误：跳过它继续找别的。
            return;
        }

        foreach (var child in children)
        {
            Scan(child, remainingDepth - 1, executables, found);
        }
    }

    /// <summary>清理环境变量里的路径写法：去引号、去空白；空值返回 <see langword="null"/>。</summary>
    private static string? Clean(string? value)
    {
        var cleaned = value?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    /// <summary>候选收集器：按目录去重，并记住该目录里优先级最高的那个文件名。</summary>
    private sealed class Candidates
    {
        private readonly Dictionary<string, string> _byDirectory = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _rankByDirectory = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = [];

        /// <summary>登记一个候选（目录 + 文件名），文件不存在则忽略。</summary>
        /// <param name="directory">所在目录。</param>
        /// <param name="fileName">可执行文件名。</param>
        public void Add(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                return;
            }

            var key = Path.GetFullPath(directory);
            var rank = fileName.StartsWith("javaw", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            if (_byDirectory.TryGetValue(key, out var existing))
            {
                // 同一个目录里 java.exe 与 javaw.exe 都存在：保留 javaw.exe。
                if (rank < _rankByDirectory[key])
                {
                    _byDirectory[key] = path;
                    _rankByDirectory[key] = rank;
                }

                return;
            }

            _byDirectory[key] = path;
            _rankByDirectory[key] = rank;
            _order.Add(key);
        }

        /// <summary>按发现顺序给出结果。</summary>
        /// <returns>可执行文件路径。</returns>
        public List<string> Result()
        {
            if (_order.Count == 0)
            {
                return [];
            }

            List<string> result = new(_order.Count);
            foreach (var key in _order)
            {
                result.Add(_byDirectory[key]);
            }

            return result;
        }
    }
}
