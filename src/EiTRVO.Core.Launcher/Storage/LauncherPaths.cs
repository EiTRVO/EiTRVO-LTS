using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.Launcher.Storage;

/// <summary>
/// <see cref="ILauncherPaths"/> 的默认实现：固定布局，根目录可替换。
/// </summary>
public sealed class LauncherPaths : ILauncherPaths
{
    /// <summary>实例名长度上限。Windows 单段路径名上限 255，这里取一个远低于它的值。</summary>
    private const int MaxInstanceNameLength = 64;

    /// <summary>用默认根目录（**启动器程序所在目录**）构造。</summary>
    public LauncherPaths()
        : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>用指定根目录构造（单测与便携模式用）。</summary>
    /// <param name="root">数据根目录。</param>
    public LauncherPaths(string root)
        : this(root, javaRuntimeDirectory: null)
    {
    }

    /// <summary>
    /// 用指定根目录和可选的 Java 运行时目录构造。
    /// </summary>
    /// <param name="root">数据根目录（设置、账户、日志、缓存）。</param>
    /// <param name="javaRuntimeDirectory">
    /// Java 运行时目录；为 <see langword="null"/> 时回落到 <c>&lt;root&gt;\runtime</c>。
    /// App 的 MSIX 模式会把该参数指向用户 Documents 下的运行时目录。
    /// </param>
    public LauncherPaths(string root, string? javaRuntimeDirectory)
        : this(root, javaRuntimeDirectory, accountsDirectory: null)
    {
    }

    /// <summary>
    /// 用指定根目录、可选 Java 运行时目录和可选的账户目录构造。
    /// </summary>
    /// <param name="root">数据根目录。</param>
    /// <param name="javaRuntimeDirectory">Java 运行时目录。</param>
    /// <param name="accountsDirectory">
    /// 账户文件目录；为 <see langword="null"/> 时回落到 <c>&lt;root&gt;\accounts.json</c>。
    /// App 的 MSIX 模式会把它指向用户 Documents 下的 <c>EiTRVO LTS</c>。
    /// </param>
    public LauncherPaths(string root, string? javaRuntimeDirectory, string? accountsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _javaRuntimeDirectory = string.IsNullOrWhiteSpace(javaRuntimeDirectory)
            ? Path.Combine(Root, "runtime")
            : Path.GetFullPath(javaRuntimeDirectory);
        _accountsDirectory = string.IsNullOrWhiteSpace(accountsDirectory)
            ? null
            : Path.GetFullPath(accountsDirectory);
    }

    private readonly string _javaRuntimeDirectory;
    private readonly string? _accountsDirectory;

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <inheritdoc />
    public string AccountsFile => Path.Combine(_accountsDirectory ?? Root, "accounts.json");

    /// <inheritdoc />
    public string LogDirectory => Path.Combine(Root, "log");

    /// <summary>
    /// **旧布局**的实例元数据目录（<c>&lt;启动器目录&gt;\instances</c>）。
    /// <para>
    /// 新布局不再往这里写：一个实例的全部落盘都在 <c>&lt;游戏目录&gt;\versions\&lt;实例&gt;</c>
    /// （见 <see cref="Instances.InstancePaths"/>）。保留它只为**只读迁移**——
    /// <see cref="Instances.FileInstanceCatalog"/> 会把旧目录里的 <c>instance.json</c> /
    /// <c>overrides.json</c> 复制到新位置，旧文件原样保留，绝不删除。
    /// </para>
    /// </summary>
    public string InstancesDirectory => Path.Combine(Root, "instances");

    /// <inheritdoc />
    public string DownloadCacheDirectory => Path.Combine(Root, "cache", "downloads");

    /// <inheritdoc />
    public string VersionCacheDirectory => Path.Combine(Root, "cache", "versions");

    /// <inheritdoc />
    public string JavaRuntimeCacheFile => Path.Combine(Root, "cache", "java-runtimes.json");

    /// <inheritdoc />
    public string ContentCacheDirectory => Path.Combine(Root, "cache", "content");

    /// <inheritdoc />
    public string JavaRuntimeDirectory => _javaRuntimeDirectory;

    /// <inheritdoc />
    public string RecycleDirectory => Path.Combine(Root, "recycle");

    /// <summary>
    /// 实例名是否可用作单段目录名。
    /// <para>
    /// 规则本身在 <see cref="Security.PathSafety.IsSafePathSegment"/>：版本 id（来自清单的远端数据）
    /// 与实例名（用户输入）拼路径的场合是同一类风险，规则只留一份。
    /// </para>
    /// </summary>
    /// <param name="instanceName">待检查的实例名。</param>
    /// <returns>可用返回 <see langword="true"/>。</returns>
    public static bool IsValidInstanceName(string? instanceName) =>
        Security.PathSafety.IsSafePathSegment(instanceName, MaxInstanceNameLength);

    /// <inheritdoc />
    public string InstanceDirectory(string instanceName)
    {
        if (!IsValidInstanceName(instanceName))
        {
            throw new ArgumentException(
                $"实例名不能用作目录名：'{instanceName}'。",
                nameof(instanceName));
        }

        return Path.Combine(InstancesDirectory, instanceName);
    }

    /// <inheritdoc />
    public string InstanceMetadataFile(string instanceName) =>
        Path.Combine(InstanceDirectory(instanceName), "instance.json");

    /// <summary>实例元数据文件名。</summary>
    public const string InstanceMetadataFileName = "instance.json";

    /// <summary>实例启动参数覆盖文件名。</summary>
    public const string InstanceOverridesFileName = "overrides.json";

    /// <inheritdoc />
    public string InstanceOverridesFile(string instanceName) =>
        Path.Combine(InstanceDirectory(instanceName), InstanceOverridesFileName);

    /// <inheritdoc />
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(DownloadCacheDirectory);
        Directory.CreateDirectory(VersionCacheDirectory);
        Directory.CreateDirectory(ContentCacheDirectory);
        Directory.CreateDirectory(RecycleDirectory);
    }
}
