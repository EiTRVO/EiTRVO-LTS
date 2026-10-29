namespace EiTRVO.Core.Launcher.Instances;

/// <summary>实例的安装完整度。</summary>
public enum InstanceInstallState
{
    /// <summary>只有元数据，游戏文件还没下（M2 装完之后会变成 <see cref="Installed"/>）。</summary>
    NotInstalled,

    /// <summary>装了一部分（缺文件 / 校验没过），需要修复。</summary>
    Partial,

    /// <summary>文件齐全。</summary>
    Installed,
}

/// <summary>
/// <c>instances/&lt;实例&gt;/instance.json</c> 的落盘形状（A-5）。
/// <para>
/// 字段全部"可缺省"：这个文件会由不同里程碑的代码写入（M1 建实例、M2 记安装状态、
/// M4 记游玩时长），任何一个字段缺省都不该让整个实例读不出来。
/// </para>
/// </summary>
public sealed record InstanceFile
{
    /// <summary>当前 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>文件格式版本。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>游戏版本，如 <c>1.20.4</c>。</summary>
    public string? GameVersion { get; init; }

    /// <summary>模组加载器种类。</summary>
    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>加载器版本，如 <c>49.0.30</c>；原版为 <see langword="null"/>。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>是否启用隔离目录（游戏数据都在实例目录内）。</summary>
    public bool IsolatedDirectory { get; init; } = true;

    /// <summary>创建时间。</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>上次启动时间（M4 写入）。</summary>
    public DateTimeOffset? LastLaunchedAt { get; init; }

    /// <summary>累计游玩时长（秒，M4 写入）。</summary>
    public long TotalPlaySeconds { get; init; }

    /// <summary>启动次数（M4 写入）。</summary>
    public int LaunchCount { get; init; }

    /// <summary>最后一次退出码（M4 写入）。</summary>
    public int LastExitCode { get; init; }

    /// <summary>绑定的账户名（M5 写入；未绑定为 <see langword="null"/>）。</summary>
    public string? Account { get; init; }

    /// <summary>安装完整度（M2 写入）。</summary>
    public InstanceInstallState InstallState { get; init; } = InstanceInstallState.NotInstalled;

    /// <summary>上次资源校验时间（M2 写入）。</summary>
    public DateTimeOffset? ResourceVerifiedAt { get; init; }
}

/// <summary>
/// 回收目录里那份"这是什么、为什么在这"的说明（<c>recycle/&lt;条目&gt;/recycle.json</c>）。
/// <para>
/// 没有它的话，回收目录里就只剩一堆带时间戳的文件夹，用户不敢删也不知道能不能删。
/// </para>
/// </summary>
public sealed record RecycleMarker
{
    /// <summary>当前 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>文件格式版本。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>原实例名。</summary>
    public string? InstanceName { get; init; }

    /// <summary>删除时间。</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>是否保留了存档与配置（保留下来的内容在 <c>kept/</c> 子目录）。</summary>
    public bool KeptSavesAndConfig { get; init; }

    /// <summary>删除时是否要求一并解除账户绑定。</summary>
    public bool DeleteAccountBinding { get; init; }

    /// <summary>删除前该实例绑定的账户（若有），交给账户层解除绑定。</summary>
    public string? Account { get; init; }
}
