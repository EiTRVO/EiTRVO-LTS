using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// <see cref="IJavaEnvironment"/> 的真实实现：<c>PATH</c> / <c>JAVA_HOME</c> / 注册表 / 常见安装目录。
/// <para>
/// 厂商清单是按"实际会装到哪"列的，注册表键路径同理。两处都**宁可多找一个目录、不愿漏掉一个 JDK** ——
/// 漏掉的表现是 p10 说"没有 Java"，而用户明明装了，接着他会去重装一遍。
/// </para>
/// </summary>
public sealed class WindowsJavaEnvironment : IJavaEnvironment
{
    /// <summary>注册表里会写"安装到哪"的位置：子键路径 + 该子键下存路径的值名。</summary>
    private static readonly (string SubKey, string ValueName)[] RegistryHomeSources =
    [
        (@"SOFTWARE\JavaSoft\Java Runtime Environment", "JavaHome"),
        (@"SOFTWARE\JavaSoft\Java Development Kit", "JavaHome"),
        (@"SOFTWARE\JavaSoft\JRE", "JavaHome"),
        (@"SOFTWARE\JavaSoft\JDK", "JavaHome"),
        (@"SOFTWARE\Eclipse Adoptium\JDK", @"hotspot\MSI\Path"),
        (@"SOFTWARE\Eclipse Foundation\JDK", @"hotspot\MSI\Path"),
        (@"SOFTWARE\Microsoft\JDK", @"hotspot\MSI\Path"),
        (@"SOFTWARE\Azul Systems\Zulu", "InstallationPath"),
        (@"SOFTWARE\BellSoft\LibericaJDK", "InstallationPath"),
    ];

    /// <summary>常见的厂商目录名（在 Program Files 与 <c>%LOCALAPPDATA%\Programs</c> 下各查一遍）。</summary>
    private static readonly string[] VendorFolders =
    [
        "Java",
        "Eclipse Adoptium",
        "Eclipse Temurin",
        "Microsoft",
        "BellSoft",
        "Zulu",
        "Amazon Corretto",
        "Semeru",
        "GraalVM",
        "SapMachine",
    ];

    /// <summary>启动器自己下载的官方 JRE 的目录名（<c>&lt;数据目录&gt;\runtime</c>）。</summary>
    private readonly ILauncherPaths _paths;

    /// <summary>构造环境来源。</summary>
    /// <param name="paths">数据目录布局（启动器自己的 <c>runtime</c> 目录也要扫）。</param>
    public WindowsJavaEnvironment(ILauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        List<string> directories = [];
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            directories.Add(entry);
        }

        return directories;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetJavaHomes()
    {
        List<string> homes = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNew(homes, seen, Environment.GetEnvironmentVariable("JAVA_HOME"));
        AddIfNew(homes, seen, Environment.GetEnvironmentVariable("JDK_HOME"));

        foreach (var (subKey, valueName) in RegistryHomeSources)
        {
            foreach (var version in WindowsRegistry.EnumerateLocalMachineSubKeyNames(subKey))
            {
                AddIfNew(homes, seen, WindowsRegistry.ReadLocalMachineString($@"{subKey}\{version}", valueName));
            }

            // 也有把路径直接写在父键上的安装器（没有版本子键）。
            AddIfNew(homes, seen, WindowsRegistry.ReadLocalMachineString(subKey, valueName));
        }

        return homes;
    }

    /// <inheritdoc />
    public IReadOnlyList<JavaSearchRoot> GetSearchRoots()
    {
        List<JavaSearchRoot> roots = [];

        // 厂商目录：<Program Files>\<厂商>\<JDK> 与 <Program Files>\<厂商>\<JDK>\bin，深度 2 足够。
        foreach (var baseDirectory in BaseDirectories())
        {
            foreach (var vendor in VendorFolders)
            {
                roots.Add(new JavaSearchRoot(Path.Combine(baseDirectory, vendor), MaxDepth: 2));
            }
        }

        // 启动器自己的运行时目录：runtime\<组件>\<平台>\<组件>\bin，深度要给够，
        // 否则"界面显示未检测到、但文件就在磁盘上"（官方 JRE 的目录结构就是深三层）。
        // 路径来自 ILauncherPaths：安装器（§12.7）写的是同一个属性，两边不会各记一份字符串。
        roots.Add(new JavaSearchRoot(_paths.JavaRuntimeDirectory, MaxDepth: 4));

        return roots;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetExecutableNames() => OperatingSystem.IsWindows()
        ? ["javaw.exe", "java.exe"]
        : ["java", "javaw"];

    /// <summary>可能装着 JDK 的基础目录。</summary>
    private static List<string> BaseDirectories()
    {
        List<string> directories = [];

        AddIfNotEmpty(directories, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddIfNotEmpty(directories, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            directories.Add(Path.Combine(localAppData, "Programs"));
        }

        return directories;
    }

    private static void AddIfNotEmpty(List<string> directories, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            directories.Add(value);
        }
    }

    private static void AddIfNew(List<string> homes, HashSet<string> seen, string? candidate)
    {
        var trimmed = candidate?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
        {
            return;
        }

        homes.Add(trimmed);
    }
}
