using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>安装任务的阶段（p06 的阶段表）。</summary>
/// <remarks>
/// 前三个是**游戏版本安装**的阶段（M2/M4 的安装器），接着两个是**Java 运行时**的阶段（§12.7），
/// 再两个是**内容下载**的阶段（M7-d），接着两个是**整合包导入**的阶段（M7-e），
/// 最后六个是**存档备份 / 恢复**的阶段（M7-f）。它们共用 <see cref="Download"/>：
/// 队列的阶段表按任务种类挑三条来显示（见 <c>FileDownloadCatalog.GetLaunchStages</c>），
/// 而进度与事件两边完全一样。
/// </remarks>
public enum InstallStageKind
{
    /// <summary>解析版本 JSON（含继承链合并）。</summary>
    ResolveVersion,

    /// <summary>下载并校验资源。</summary>
    Download,

    /// <summary>解压 natives。</summary>
    ExtractNatives,

    /// <summary>读取官方 Java 运行时索引并选定组件。</summary>
    ResolveRuntime,

    /// <summary>把下载好的运行时搬进最终目录。</summary>
    PromoteRuntime,

    /// <summary>解析内容版本与它的必需依赖（M7-d：这一步要把依赖**全部**解出来才开下）。</summary>
    ResolveContent,

    /// <summary>把被替换掉的旧版本文件移进备份目录（M7-d）。</summary>
    ReplaceContent,

    /// <summary>扫描整合包（zip 炸弹 / 路径穿越 / 危险扩展名 / 下载源白名单，M7-e）。</summary>
    ScanArchive,

    /// <summary>
    /// 为整合包建实例，并把它需要的那一版游戏与加载器装好（M7-e-2）。
    /// </summary>
    /// <remarks>
    /// 这一步复用原版 / 加载器安装器，所以它做的事与 <see cref="ResolveVersion"/> +
    /// <see cref="Download"/> + <see cref="ExtractNatives"/> 是同一批 —— 但阶段表一行只能有一个状态，
    /// 而"整合包还没有游戏可跑"是用户最需要看见的那件事（它也是最慢的一步）。
    /// 用三个游戏安装阶段去顶替它，会让阶段表看起来像在装整合包自己的文件。
    /// </remarks>
    InstallGame,

    /// <summary>展开整合包的覆盖层（<c>overrides/</c> → <c>client-overrides/</c>，M7-e）。</summary>
    ApplyOverrides,

    /// <summary>统计要打包的存档文件（M7-f：进度要有分母，读不动的文件在这里就暴露）。</summary>
    MeasureWorld,

    /// <summary>把存档压进 <c>backups\&lt;存档目录名&gt;.zip</c>（M7-f）。</summary>
    BackupWorld,

    /// <summary>复核刚写好的归档（M7-f：条目数与字节数对不上就不替换旧备份）。</summary>
    VerifyArchive,

    /// <summary>把备份解到暂存目录（M7-f：这一步还没动用户的存档）。</summary>
    ExtractBackup,

    /// <summary>把当前存档整目录改名进回收目录（M7-f：<c>backups\saves\&lt;时间戳&gt;\</c>，不是删掉）。</summary>
    StashWorld,

    /// <summary>把解压好的存档改名回 <c>saves\</c>（M7-f）。</summary>
    PromoteWorld,
}

/// <summary>阶段变化。</summary>
/// <param name="Kind">阶段。</param>
/// <param name="Completed">该阶段是否已经完成（否则表示刚开始）。</param>
public sealed record InstallStageChange(InstallStageKind Kind, bool Completed);

/// <summary>安装任务状态。</summary>
public enum InstallJobState
{
    /// <summary>排队中。</summary>
    Queued,

    /// <summary>正在执行。</summary>
    Running,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>
    /// 被用户取消（M7-g）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Failed"/> 分开的理由同 <c>DownloadQueueState.Cancelled</c>：
    /// 用户自己点的取消不该在队列里显示成一行红色失败，而且「重试」要能分辨
    /// "它坏了"与"我一直没让它跑完"。
    /// </remarks>
    Cancelled,
}

/// <summary>一个安装请求。</summary>
/// <param name="InstanceName">
/// 实例名（事件里用）。**只装版本、还没建实例**时为 <see langword="null"/> ——
/// p05 的「安装所选版本 / 下载」就是这种情形，它把版本装进游戏根目录，不涉及任何实例。
/// </param>
/// <param name="VersionId">要安装的游戏版本。</param>
/// <param name="Loader">加载器。</param>
/// <param name="LoaderVersion">加载器版本（原版为空）。</param>
/// <remarks>
/// 刻意**不带游戏目录**：目录来自设置，由队列自己读（<c>ISettingsCatalog</c>）。
/// 让调用方传进来会出现"两个地方各自记住一份目录"的经典问题 —— 用户在设置页改了目录，
/// 而向导手里还是旧值。
/// </remarks>
public sealed record InstallRequest(
    string? InstanceName,
    string VersionId,
    LoaderKind Loader,
    string? LoaderVersion)
{
    /// <summary>Forge 实例：同时把 OptiFine 作为模组放进实例 mods/（Neo 的共存做法）。</summary>
    public bool WithOptiFine { get; init; }

    /// <summary>Fabric 实例：同时安装 Fabric API（Modrinth 的最新兼容版本）。</summary>
    public bool WithFabricApi { get; init; }
}

/// <summary>
/// 获取一个 Mojang 官方 Java 运行时（§12.7 / M3 的残留）。
/// </summary>
/// <param name="GameVersion">
/// 要适配的游戏版本：用它**自己声明的** <c>javaVersion</c> 决定取哪一代。
/// <see langword="null"/> 表示没有版本上下文（还没选过版本），这时取官方索引里最新的一代 ——
/// p10 的「获取 Java 25」就是这个语义。
/// </param>
/// <remarks>
/// 刻意**不带组件名**：组件名与可用版本全部来自官方索引，让调用方先决定组件名等于把一张
/// 会过期的对照表复制到界面层。这里只说"给哪个游戏版本用"，其余由安装器按事实决定。
/// </remarks>
public sealed record JavaRuntimeRequest(string? GameVersion)
{
    /// <summary>队列行与事件里用的目标名（键 + 参数）。</summary>
    public TextTemplate Subject => string.IsNullOrWhiteSpace(GameVersion)
        ? TextTemplate.Of("最新一代")
        : TextTemplate.Of("适配 {0}", GameVersion);
}

/// <summary>
/// 队列里的一件事：装一个游戏版本、获取一个 Java 运行时、下载一批 Modrinth 内容（M7-d）、
/// 导入一个 <c>.mrpack</c>（M7-e）或备份 / 恢复一个存档（M7-f）。
/// <para>
/// 做成判别式子类型而不是几条并行的队列：它们的进度、事件、失败原因、终态完全一样，
/// 差别只在"执行那一步调谁"。几套状态机意味着任务计数、等待空闲、队列行各要写几遍，
/// 迟早在其中一遍上漏一点（例如忘了把"正在执行"放回终态）。
/// </para>
/// </summary>
public abstract record QueueTask;

/// <summary>装一个游戏版本（原版或加载器）。</summary>
/// <param name="Request">安装请求。</param>
public sealed record GameInstallTask(InstallRequest Request) : QueueTask;

/// <summary>获取一个 Java 运行时。</summary>
/// <param name="Request">请求。</param>
public sealed record JavaRuntimeTask(JavaRuntimeRequest Request) : QueueTask;

/// <summary>下载一批 Modrinth 内容（M7-d：模组更新与它们的必需依赖）。</summary>
/// <param name="Request">请求。</param>
public sealed record ContentDownloadTask(ContentDownloadRequest Request) : QueueTask;

/// <summary>
/// 导入一个 <c>.mrpack</c>（M7-e-2）。
/// </summary>
/// <param name="ArchivePath">用户选中的整合包文件。</param>
/// <remarks>
/// 刻意**只带路径**：实例名、游戏版本、加载器三样都在整合包的清单里，
/// 而游戏目录来自实例（<c>IInstanceCatalog.GetLaunchInfo</c>）—— 让界面先算这些，
/// 等于把"清单怎么读"复制一份到 ViewModel（与 <see cref="InstallRequest"/> 同一条理由）。
/// </remarks>
public sealed record MrpackInstallRequest(string ArchivePath)
{
    /// <summary>队列行与事件里的目标名（文件名是数据，原样显示）。</summary>
    public string Subject
    {
        get
        {
            var name = System.IO.Path.GetFileName(ArchivePath);
            return string.IsNullOrWhiteSpace(name) ? ArchivePath : name;
        }
    }
}

/// <summary>导入一个整合包（M7-e-2：扫描 → 建实例 → 装游戏与加载器 → 铺内容）。</summary>
/// <param name="Request">请求。</param>
public sealed record MrpackInstallTask(MrpackInstallRequest Request) : QueueTask;

/// <summary>
/// 从 Modrinth 直接安装一个整合包（S11-c / 2026-10-21）：把"下载 .mrpack"也放进队列，
/// 而不是在界面上先下完再入队。
/// </summary>
/// <param name="Request">项目与兼容性条件。</param>
/// <param name="Title">队列行标题（项目名 + 版本号）。</param>
public sealed record ModrinthModpackTask(ModrinthModpackRequest Request, string Title) : QueueTask;

/// <summary>
/// 要备份 / 恢复的那个存档。
/// </summary>
/// <param name="Folder">
/// 存档的**目录名**（磁盘上的事实）：备份文件名是 <c>&lt;目录名&gt;.zip</c>，恢复的目标也是
/// <c>saves\&lt;目录名&gt;</c>。刻意不用显示名 —— 显示名来自 <c>level.dat</c>，
/// 两个存档可以同名，目录名不会。
/// </param>
/// <param name="Name">
/// 显示名（来自 <c>level.dat</c> 的 <c>LevelName</c>）：队列行与事件里给用户看的那个名字。
/// 它是数据（用户的存档叫什么就是什么），中英文界面都原样显示。
/// </param>
public sealed record WorldBackupTarget(string Folder, string Name);

/// <summary>备份一个存档（M7-f：<c>saves\&lt;目录名&gt;</c> → <c>backups\&lt;目录名&gt;.zip</c>）。</summary>
/// <param name="InstanceName">目标实例（游戏目录由它决定，见 <c>IInstanceCatalog.GetLaunchInfo</c>）。</param>
/// <param name="Target">要备份的存档。</param>
/// <remarks>
/// 刻意**不带游戏目录**：目录来自实例目录，与 <see cref="InstallRequest"/> 同一条理由 ——
/// 让调用方传进来就会出现"两个地方各自记住一份目录"。
/// </remarks>
public sealed record WorldBackupRequest(string InstanceName, WorldBackupTarget Target)
{
    /// <summary>队列行与事件里的目标名（显示名；没有就退回目录名）。</summary>
    public string Subject => string.IsNullOrWhiteSpace(Target.Name) ? Target.Folder : Target.Name;
}

/// <summary>恢复一个存档（M7-f：<c>backups\&lt;目录名&gt;.zip</c> → <c>saves\&lt;目录名&gt;</c>）。</summary>
/// <param name="InstanceName">目标实例。</param>
/// <param name="Target">要恢复的存档。</param>
public sealed record WorldRestoreRequest(string InstanceName, WorldBackupTarget Target)
{
    /// <summary>队列行与事件里的目标名（显示名；没有就退回目录名）。</summary>
    public string Subject => string.IsNullOrWhiteSpace(Target.Name) ? Target.Folder : Target.Name;
}

/// <summary>备份一个存档（M7-f：统计 → 压缩 → 复核）。</summary>
/// <param name="Request">请求。</param>
public sealed record WorldBackupTask(WorldBackupRequest Request) : QueueTask;

/// <summary>恢复一个存档（M7-f：解压到暂存 → 移走当前存档 → 放回备份）。</summary>
/// <param name="Request">请求。</param>
public sealed record WorldRestoreTask(WorldRestoreRequest Request) : QueueTask;

/// <summary>
/// 一个 Modrinth 内容的下载目标（要装的**确切版本**，以及它替换掉本地的哪个文件）。
/// </summary>
/// <param name="ProjectId">项目 id（解析依赖时用来判断"这个项目已经装了"；可空）。</param>
/// <param name="VersionId">要下载的确切版本 id。</param>
/// <param name="DisplayName">给用户看的名字（队列行与事件里用，如 <c>Sodium</c>）。</param>
/// <param name="ReplacePath">
/// 装好之后要移走的**本地文件**绝对路径（更新时为被替换的那个 jar；新装时为 <see langword="null"/>）。
/// <para>
/// 为什么必须有它：更新是把新的 jar 放进 <c>mods\</c>，而旧版本的**文件名通常不一样** ——
/// 只下不删的结果是同一个模组的两个版本同时在 <c>mods\</c> 里，游戏起不来或行为随机。
/// </para>
/// </param>
/// <param name="KeepDisabled">
/// 更新一个被禁用的模组时，新文件也要以 <c>.disabled</c> 落盘（「一键更新」要覆盖所有非最新模组，
/// 但不能因为升级就把用户关掉的模组顺手启用）。
/// </param>
public sealed record ContentDownloadTarget(
    string? ProjectId,
    string VersionId,
    string DisplayName,
    string? ReplacePath,
    bool KeepDisabled = false);

/// <summary>
/// 一次内容下载请求（M7-d）。
/// </summary>
/// <param name="InstanceName">目标实例（游戏目录与 <c>mods\</c> 由它决定，见 <c>IInstanceCatalog.GetLaunchInfo</c>）。</param>
/// <param name="Targets">要装的目标（更新时就是那些"有更新"的模组）。</param>
/// <param name="InstalledProjectIds">
/// 本机**已经装着**的项目 id。
/// <para>
/// 解析依赖时用它做过滤：某个必需依赖的项目已经在本机（不管哪个版本），就不再下一份 ——
/// 拿一个"和当前实例不匹配"的版本去覆盖用户已有的模组，比缺一个可选依赖糟得多。
/// 这份清单只有内容目录知道（对账结果里有项目 id），所以由调用方传进来。
/// </para>
/// </param>
/// <remarks>
/// 刻意**不带游戏目录**：目录来自实例目录（<c>IInstanceCatalog.GetLaunchInfo</c> 的 <c>InstancePath</c>），
/// 与 <see cref="InstallRequest"/> 同一条理由 —— 让调用方传进来就会出现"两个地方各自记住一份目录"。
/// </remarks>
public sealed record ContentDownloadRequest(
    string InstanceName,
    IReadOnlyList<ContentDownloadTarget> Targets,
    IReadOnlyList<string> InstalledProjectIds)
{
    /// <summary>
    /// 内容种类（2026-10-21）：模组写 <c>mods\</c> 并递归依赖；资源包写 <c>resourcepacks\</c>、不递归。
    /// </summary>
    public ModrinthContentKind Kind { get; init; } = ModrinthContentKind.Mod;

    /// <summary>没有实例名时的目标名（<c>InstallResult.VersionId</c> 不能为空）。</summary>
    public const string SubjectFallback = "content";

    /// <summary>事件与结果里的目标名。</summary>
    public string Subject => string.IsNullOrWhiteSpace(InstanceName) ? SubjectFallback : InstanceName;
}

/// <summary>队列里的一个任务。</summary>
/// <param name="Id">任务 id。</param>
/// <param name="Title">标题（如「安装 1.20.4」/「获取 Java 运行时 · 适配 1.20.4」；键 + 参数）。</param>
/// <param name="Task">要做的那件事（装游戏版本 / 获取 Java 运行时 / 下载 Modrinth 内容）。</param>
/// <param name="State">状态。</param>
/// <param name="Detail">状态说明（排队时间、当前文件、失败原因等；键 + 参数；没有时为 <see langword="null"/>）。</param>
/// <param name="Result">完成后的结果；未完成时为 <see langword="null"/>。</param>
public sealed record InstallJob(
    string Id,
    TextTemplate Title,
    QueueTask Task,
    InstallJobState State,
    TextTemplate? Detail,
    InstallResult? Result)
{
    /// <summary>是否已结束（成功、失败或已取消）。</summary>
    public bool IsFinished => State is InstallJobState.Completed or InstallJobState.Failed or InstallJobState.Cancelled;
}
