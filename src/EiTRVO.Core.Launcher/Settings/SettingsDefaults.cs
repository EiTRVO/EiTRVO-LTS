namespace EiTRVO.Core.Launcher.Settings;

/// <summary>
/// 设置的默认值与"回落"规则。
/// <para>
/// 单独一个类型的原因：默认值必须在**读文件、合并缺字段、界面首次打开**这三个地方
/// 用同一份。散在三处的话，某天改了默认内存，另一个入口还是旧值 ——
/// 这类不一致最终会表现为"设置页显示 4096，启动时按 2048 分配"。
/// </para>
/// </summary>
public static class SettingsDefaults
{
    /// <summary>并发下载数下限。</summary>
    public const int MinConcurrentDownloads = 1;

    /// <summary>并发下载数上限。</summary>
    public const int MaxConcurrentDownloads = 256;

    /// <summary>最大内存下限（MB）。再低 Minecraft 起不来。</summary>
    public const int MinMemoryMb = 512;

    /// <summary>最大内存上限（MB）。再高必然是手改错了（或写成了字节数）。</summary>
    public const int MaxMemoryMb = 262144;

    /// <summary>窗口边长下限（像素）。</summary>
    public const int MinWindowSide = 320;

    /// <summary>窗口边长上限（像素）。</summary>
    public const int MaxWindowSide = 16384;

    /// <summary>
    /// 默认游戏根目录：当前用户的 <c>Documents\EiTRVO LTS\.minecraft</c>。
    /// </summary>
    /// <remarks>
    /// 2026-10-26 用户指定：默认放到 Documents 下，而不是启动器目录、便携目录或 MSIX 的
    /// <c>LocalCache</c> 里。这样游戏存档不会落在包重定向的缓存目录中，设置页显示的路径
    /// 也与实际落盘位置一致。
    /// 已存过设置的安装不会被改写（设置文件里的 <c>gameDirectory</c> 优先）；不做旧路径迁移。
    /// </remarks>
    public static string DefaultGameDirectory =>
        Path.Combine(DefaultDocumentsDirectory, "EiTRVO LTS", ".minecraft");

    /// <summary>
    /// 当前用户的 Documents 目录；系统未返回时回落到用户目录下的 Documents。
    /// </summary>
    /// <returns>Documents 的绝对路径。</returns>
    public static string DefaultDocumentsDirectory
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
            {
                return documents;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(profile)
                ? Path.Combine(AppContext.BaseDirectory, "Documents")
                : Path.Combine(profile, "Documents");
        }
    }

    /// <summary>全局设置的出厂默认值。</summary>
    /// <returns>默认全局设置。</returns>
    public static GlobalSettings CreateGlobal() => new(
        GameDirectory: DefaultGameDirectory,
        DownloadSource: DownloadSourcePolicy.Official,
        ConcurrentDownloads: 64,
        MinimizeAfterLaunch: true,
        // 空串 = 用户还没选过语言，启动语言交给 appsettings.json（见 LocalizationStartup）。
        Language: string.Empty,
        KeepLogsOnCrash: true,
        MaxMemoryMb: 4096,
        JvmArguments: string.Empty,
        WindowWidth: 1280,
        WindowHeight: 720,
        Fullscreen: false,
        ExtraGameArguments: string.Empty,
        AutoFetchJava: true,
        JavaOverride: null,
        ForceSelectedJava: false,
        ForcedJavaHome: null);

    /// <summary>某个实例尚未写过覆盖项时的继承值。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="global">全局设置（窗口与"启动后最小化"从这里继承）。</param>
    /// <returns>继承型的覆盖项。</returns>
    public static InstanceOverrides CreateInstanceOverrides(string instanceName, GlobalSettings global)
    {
        ArgumentNullException.ThrowIfNull(global);

        return new InstanceOverrides(
            InstanceName: instanceName,
            JavaOverride: null,
            MaxMemoryMb: null,
            JvmArguments: null,
            WindowWidth: global.WindowWidth,
            WindowHeight: global.WindowHeight,
            LaunchMode: global.Fullscreen ? WindowLaunchMode.Fullscreen : WindowLaunchMode.Windowed,
            ExtraGameArguments: global.ExtraGameArguments,
            MinimizeAfterLaunch: global.MinimizeAfterLaunch);
    }

    /// <summary>把磁盘上的全局设置（可能缺字段、可能有非法数值）合并成一份可用的设置。</summary>
    /// <param name="entry">磁盘内容；整份文件缺失时为 <see langword="null"/>。</param>
    /// <returns>合并后的设置。</returns>
    public static GlobalSettings Merge(GlobalSettingsEntry? entry)
    {
        var defaults = CreateGlobal();
        if (entry is null)
        {
            return defaults;
        }

        return new GlobalSettings(
            GameDirectory: Fallback(entry.GameDirectory, defaults.GameDirectory),
            DownloadSource: entry.DownloadSource ?? defaults.DownloadSource,
            ConcurrentDownloads: Clamp(
                entry.ConcurrentDownloads, defaults.ConcurrentDownloads, MinConcurrentDownloads, MaxConcurrentDownloads),
            MinimizeAfterLaunch: entry.MinimizeAfterLaunch ?? defaults.MinimizeAfterLaunch,
            // 语言空串是有意义的取值（"用户还没选过"），所以只回落 null。
            Language: entry.Language ?? defaults.Language,
            KeepLogsOnCrash: entry.KeepLogsOnCrash ?? defaults.KeepLogsOnCrash,
            MaxMemoryMb: Clamp(entry.MaxMemoryMb, defaults.MaxMemoryMb, MinMemoryMb, MaxMemoryMb),
            JvmArguments: entry.JvmArguments ?? defaults.JvmArguments,
            WindowWidth: Clamp(entry.WindowWidth, defaults.WindowWidth, MinWindowSide, MaxWindowSide),
            WindowHeight: Clamp(entry.WindowHeight, defaults.WindowHeight, MinWindowSide, MaxWindowSide),
            Fullscreen: entry.Fullscreen ?? defaults.Fullscreen,
            ExtraGameArguments: entry.ExtraGameArguments ?? defaults.ExtraGameArguments,
            AutoFetchJava: entry.AutoFetchJava ?? defaults.AutoFetchJava,
            JavaOverride: entry.JavaOverride,
            ForceSelectedJava: entry.ForceSelectedJava ?? defaults.ForceSelectedJava,
            ForcedJavaHome: entry.ForcedJavaHome);
    }

    /// <summary>把磁盘上的实例覆盖项合并成一份可用的覆盖项。</summary>
    /// <param name="file">磁盘内容；文件缺失时为 <see langword="null"/>。</param>
    /// <param name="instanceName">实例名。</param>
    /// <param name="global">全局设置（缺字段时从这里继承）。</param>
    /// <returns>合并后的覆盖项。</returns>
    public static InstanceOverrides Merge(OverridesFile? file, string instanceName, GlobalSettings global)
    {
        ArgumentNullException.ThrowIfNull(global);

        var inherited = CreateInstanceOverrides(instanceName, global);
        if (file is null)
        {
            return inherited;
        }

        return inherited with
        {
            JavaOverride = file.JavaOverride,
            // 覆盖项里 null 是**有意义的**（"继承全局"），所以不能像全局设置那样把 null 补齐，
            // 只对"写了但写了非法值"的情况做钳制。
            MaxMemoryMb = file.MaxMemoryMb is null ? null : Math.Clamp(file.MaxMemoryMb.Value, MinMemoryMb, MaxMemoryMb),
            JvmArguments = file.JvmArguments,
            WindowWidth = Clamp(file.WindowWidth, inherited.WindowWidth, MinWindowSide, MaxWindowSide),
            WindowHeight = Clamp(file.WindowHeight, inherited.WindowHeight, MinWindowSide, MaxWindowSide),
            LaunchMode = file.LaunchMode ?? inherited.LaunchMode,
            ExtraGameArguments = file.ExtraGameArguments ?? inherited.ExtraGameArguments,
            MinimizeAfterLaunch = file.MinimizeAfterLaunch ?? inherited.MinimizeAfterLaunch,
        };
    }

    /// <summary>把设置写回磁盘形状。</summary>
    /// <param name="settings">全局设置。</param>
    /// <returns>落盘形状。</returns>
    public static SettingsFile ToFile(GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new SettingsFile
        {
            Global = new GlobalSettingsEntry
            {
                GameDirectory = settings.GameDirectory,
                DownloadSource = settings.DownloadSource,
                ConcurrentDownloads = settings.ConcurrentDownloads,
                MinimizeAfterLaunch = settings.MinimizeAfterLaunch,
                Language = settings.Language,
                KeepLogsOnCrash = settings.KeepLogsOnCrash,
                MaxMemoryMb = settings.MaxMemoryMb,
                JvmArguments = settings.JvmArguments,
                WindowWidth = settings.WindowWidth,
                WindowHeight = settings.WindowHeight,
                Fullscreen = settings.Fullscreen,
                ExtraGameArguments = settings.ExtraGameArguments,
                AutoFetchJava = settings.AutoFetchJava,
                JavaOverride = settings.JavaOverride,
                ForceSelectedJava = settings.ForceSelectedJava,
                ForcedJavaHome = settings.ForcedJavaHome,
            },
        };
    }

    /// <summary>把实例覆盖项写回磁盘形状。</summary>
    /// <param name="overrides">覆盖项。</param>
    /// <returns>落盘形状。</returns>
    public static OverridesFile ToFile(InstanceOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        return new OverridesFile
        {
            JavaOverride = overrides.JavaOverride,
            MaxMemoryMb = overrides.MaxMemoryMb,
            JvmArguments = overrides.JvmArguments,
            WindowWidth = overrides.WindowWidth,
            WindowHeight = overrides.WindowHeight,
            LaunchMode = overrides.LaunchMode,
            ExtraGameArguments = overrides.ExtraGameArguments,
            MinimizeAfterLaunch = overrides.MinimizeAfterLaunch,
        };
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static int Clamp(int? value, int? fallback, int minimum, int maximum)
    {
        var effective = value ?? fallback ?? minimum;
        return Math.Clamp(effective, minimum, maximum);
    }
}
