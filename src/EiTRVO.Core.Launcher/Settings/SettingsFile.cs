namespace EiTRVO.Core.Launcher.Settings;

/// <summary>
/// <c>settings.json</c> 的落盘形状。
/// <para>
/// 这些类型是**磁盘契约**，所以字段全部可空：真实的用户文件可能来自旧版本、
/// 被手工编辑、或写到一半断电。<see cref="SettingsDefaults"/> 负责把"缺哪个字段"
/// 逐个回落到默认值 —— 绝不能因为少了一个字段就把整份设置丢掉。
/// </para>
/// </summary>
public sealed record SettingsFile
{
    /// <summary>当前 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>文件格式版本（用于将来迁移；缺省视为 1）。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>全局默认设置。</summary>
    public GlobalSettingsEntry? Global { get; init; }
}

/// <summary>全局设置的落盘形状（全部可空，缺字段即继承默认值）。</summary>
public sealed record GlobalSettingsEntry
{
    /// <summary>游戏根目录。</summary>
    public string? GameDirectory { get; init; }

    /// <summary>下载源策略。</summary>
    public DownloadSourcePolicy? DownloadSource { get; init; }

    /// <summary>并发下载数。</summary>
    public int? ConcurrentDownloads { get; init; }

    /// <summary>启动后是否最小化启动器。</summary>
    public bool? MinimizeAfterLaunch { get; init; }

    /// <summary>界面语言；空串表示"用户还没选过"。</summary>
    public string? Language { get; init; }

    /// <summary>崩溃时是否保留日志。</summary>
    public bool? KeepLogsOnCrash { get; init; }

    /// <summary>最大内存（MB）。</summary>
    public int? MaxMemoryMb { get; init; }

    /// <summary>JVM 参数。</summary>
    public string? JvmArguments { get; init; }

    /// <summary>窗口宽。</summary>
    public int? WindowWidth { get; init; }

    /// <summary>窗口高。</summary>
    public int? WindowHeight { get; init; }

    /// <summary>是否全屏。</summary>
    public bool? Fullscreen { get; init; }

    /// <summary>启动附加参数。</summary>
    public string? ExtraGameArguments { get; init; }

    /// <summary>缺失 Java 时是否自动获取官方 JRE。</summary>
    public bool? AutoFetchJava { get; init; }

    /// <summary>全局 Java 覆盖；<see langword="null"/> 表示自动匹配。</summary>
    public string? JavaOverride { get; init; }

    /// <summary>是否强制使用选定的 Java 启动。</summary>
    public bool? ForceSelectedJava { get; init; }

    /// <summary>强制启动时使用的 Java 运行时根目录。</summary>
    public string? ForcedJavaHome { get; init; }
}

/// <summary>
/// 某个实例的启动参数覆盖（<c>instances/&lt;实例&gt;/overrides.json</c>）。
/// <para>
/// 这里**不存实例名**：实例名就是目录名。存一份副本只会产生
/// "目录改名了但文件里还是旧名"这种不一致。
/// </para>
/// </summary>
public sealed record OverridesFile
{
    /// <summary>当前 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>文件格式版本。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>该实例的 Java 覆盖。</summary>
    public string? JavaOverride { get; init; }

    /// <summary>该实例的最大内存；<see langword="null"/> 表示继承全局。</summary>
    public int? MaxMemoryMb { get; init; }

    /// <summary>该实例的 JVM 参数；<see langword="null"/> 表示继承全局。</summary>
    public string? JvmArguments { get; init; }

    /// <summary>窗口宽。</summary>
    public int? WindowWidth { get; init; }

    /// <summary>窗口高。</summary>
    public int? WindowHeight { get; init; }

    /// <summary>启动方式。</summary>
    public WindowLaunchMode? LaunchMode { get; init; }

    /// <summary>附加参数。</summary>
    public string? ExtraGameArguments { get; init; }

    /// <summary>启动后是否最小化启动器。</summary>
    public bool? MinimizeAfterLaunch { get; init; }
}
