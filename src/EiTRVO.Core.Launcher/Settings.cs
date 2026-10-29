namespace EiTRVO.Core.Launcher.Settings;

/// <summary>下载源策略（2026-10-23 起只有官方源；其余值仅为兼容旧设置文件而保留）。</summary>
public enum DownloadSourcePolicy
{
    /// <summary>官方源（piston-meta.mojang.com）。</summary>
    Official,

    /// <summary>旧设置文件里的值：现在与 <see cref="Official"/> 完全等价。</summary>
    Mirror,

    /// <summary>旧设置文件里的值：现在与 <see cref="Official"/> 完全等价。</summary>
    Automatic,
}

/// <summary>游戏窗口启动方式。</summary>
public enum WindowLaunchMode
{
    /// <summary>窗口化。</summary>
    Windowed,

    /// <summary>全屏。</summary>
    Fullscreen,

    /// <summary>无边框全屏。</summary>
    Borderless,
}

/// <summary>全局设置（「Java 与设置」页）。所有实例默认继承这些值。</summary>
/// <param name="GameDirectory">游戏根目录。</param>
/// <param name="DownloadSource">下载源策略。</param>
/// <param name="ConcurrentDownloads">并发下载数。</param>
/// <param name="MinimizeAfterLaunch">启动游戏后是否最小化启动器。</param>
/// <param name="Language">界面语言。</param>
/// <param name="KeepLogsOnCrash">崩溃时是否保留日志并提示查看诊断。</param>
/// <param name="MaxMemoryMb">最大内存（MB）。</param>
/// <param name="JvmArguments">JVM 参数。</param>
/// <param name="WindowWidth">窗口宽。</param>
/// <param name="WindowHeight">窗口高。</param>
/// <param name="Fullscreen">是否全屏启动。</param>
/// <param name="ExtraGameArguments">启动附加参数。</param>
/// <param name="AutoFetchJava">缺失 Java 时是否自动下载 Mojang 官方 JRE。</param>
/// <param name="JavaOverride">全局 Java 覆盖（<see langword="null"/> 表示自动匹配）。</param>
/// <param name="ForceSelectedJava">是否强制使用选定的 Java 启动，忽略实例与版本要求。</param>
/// <param name="ForcedJavaHome">强制启动时使用的 Java 运行时根目录。</param>
public sealed record GlobalSettings(
    string GameDirectory,
    DownloadSourcePolicy DownloadSource,
    int ConcurrentDownloads,
    bool MinimizeAfterLaunch,
    string Language,
    bool KeepLogsOnCrash,
    int MaxMemoryMb,
    string JvmArguments,
    int WindowWidth,
    int WindowHeight,
    bool Fullscreen,
    string ExtraGameArguments,
    bool AutoFetchJava,
    string? JavaOverride,
    bool ForceSelectedJava = false,
    string? ForcedJavaHome = null);

/// <summary>逐实例覆盖项（「实例设置」页）。未覆盖的项继承 <see cref="GlobalSettings"/>。</summary>
/// <param name="InstanceName">实例名。</param>
/// <param name="JavaOverride">该实例的 Java 覆盖（<see langword="null"/> 表示继承全局）。</param>
/// <param name="MaxMemoryMb">该实例的最大内存；<see langword="null"/> 表示继承全局。</param>
/// <param name="JvmArguments">该实例的 JVM 参数；<see langword="null"/> 表示继承全局。</param>
/// <param name="WindowWidth">窗口宽。</param>
/// <param name="WindowHeight">窗口高。</param>
/// <param name="LaunchMode">启动方式。</param>
/// <param name="ExtraGameArguments">附加参数。</param>
/// <param name="MinimizeAfterLaunch">启动后最小化启动器。</param>
public sealed record InstanceOverrides(
    string InstanceName,
    string? JavaOverride,
    int? MaxMemoryMb,
    string? JvmArguments,
    int WindowWidth,
    int WindowHeight,
    WindowLaunchMode LaunchMode,
    string ExtraGameArguments,
    bool MinimizeAfterLaunch);
