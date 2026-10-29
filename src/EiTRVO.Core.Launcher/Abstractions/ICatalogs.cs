using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Events;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Abstractions;

/// <summary>实例目录（列表、详情、快切概览）。</summary>
public interface IInstanceCatalog
{
    /// <summary>
    /// 当前实例（「当前实例」属性、面包屑上下文、以及所有「带实例上下文」的页面都以它为准）。
    /// <para>
    /// **可写**：p02 本地实例列表里换一行选中时，界面就是把它改掉，
    /// 于是实例详情 / 实例设置 / 正在启动 / 模组 / 资源包这些页面再次进入时都会跟着变。
    /// </para>
    /// </summary>
    string ActiveInstanceName { get; set; }

    /// <summary>仪表板的实例快切列表。</summary>
    /// <returns>快切项。</returns>
    IReadOnlyList<InstanceOverview> GetOverview();

    /// <summary>「本地实例」表格的全部行。</summary>
    /// <returns>实例行。</returns>
    IReadOnlyList<InstanceRecord> GetInstances();

    /// <summary>某个实例的完整属性。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>实例详情。</returns>
    InstanceDetail GetDetail(string instanceName);

    /// <summary>某个实例的操作入口。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>操作入口。</returns>
    IReadOnlyList<InstanceAction> GetActions(string instanceName);

    /// <summary>
    /// 某个实例的内容数量统计。
    /// <para>
    /// 单独一个成员（而不是让调用方去解析 <see cref="GetActions"/> 的摘要文本）的理由：
    /// p13 的删除确认框要写「包含 N 个模组与 M 个存档」，这句话是**破坏性操作**的依据 ——
    /// 数字错了会让用户以为没东西可丢。
    /// </para>
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>数量统计。</returns>
    InstanceCounts GetCounts(string instanceName);

    /// <summary>
    /// 新建一个本地实例（M1：建立目录骨架与 <c>instance.json</c>）。
    /// <para>
    /// 这里**不下载任何游戏文件** —— 下载属于安装流程（M2），
    /// 建实例与装游戏是两件事：用户可以先建好一个实例，之后再决定装哪个版本。
    /// </para>
    /// </summary>
    /// <param name="request">新建请求。</param>
    /// <returns>新实例的详情。</returns>
    /// <exception cref="ArgumentException">实例名不能用作目录名。</exception>
    /// <exception cref="InvalidOperationException">同名实例已存在。</exception>
    InstanceDetail CreateInstance(InstanceCreationRequest request);

    /// <summary>
    /// 启动一个实例要用的**原始**元数据（M4 的启动编排读它，而不是去解析
    /// <see cref="InstanceDetail.LoaderDisplay"/> 那种显示文本）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>原始元数据。</returns>
    /// <exception cref="KeyNotFoundException">实例不存在。</exception>
    InstanceLaunchInfo GetLaunchInfo(string instanceName);

    /// <summary>
    /// 把一次启动的结果写回实例元数据（启动时间、次数、退出码、计入的游玩秒数）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="launchedAt">启动时间。</param>
    /// <param name="exitCode">退出码。</param>
    /// <param name="playSeconds">本次计入的游玩秒数（不足一次会话下限时传 0）。</param>
    /// <returns>
    /// 写入成功返回 <see langword="true"/>；**元数据文件存在但读不出来时返回 <see langword="false"/> 且一个字都不写**
    /// （AGENTS.md §6：读失败一律不得写文件 —— 否则一次启动就会把用户那份可能还有救的元数据覆盖成默认值）。
    /// </returns>
    bool RecordLaunch(string instanceName, DateTimeOffset launchedAt, int exitCode, long playSeconds);

    /// <summary>
    /// 删除实例：整个目录**移动**到回收目录（不是递归删除），因此可以回滚。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="options">删除选项。</param>
    /// <returns>删除结果（含回收位置）。</returns>
    /// <exception cref="KeyNotFoundException">实例不存在。</exception>
    InstanceDeletionResult DeleteInstance(string instanceName, InstanceDeletionOptions options);

    /// <summary>
    /// 重命名实例：把 <c>versions\&lt;旧名&gt;</c> 整个目录移到 <c>versions\&lt;新名&gt;</c>。
    /// </summary>
    /// <param name="instanceName">旧实例名。</param>
    /// <param name="newName">新实例名（已 Trim）。</param>
    /// <returns>重命名后的详情。</returns>
    /// <exception cref="ArgumentException">新名字不能用作目录名。</exception>
    /// <exception cref="InvalidOperationException">新名字已经被占用。</exception>
    /// <exception cref="KeyNotFoundException">旧实例不存在。</exception>
    InstanceDetail RenameInstance(string instanceName, string newName);
}

/// <summary>
/// 把一个本地资源包（<c>.zip</c>）导入实例的 <c>resourcepacks\</c>。
/// </summary>
/// <remarks>
/// 导入前要过 zip 安全扫描：路径穿越（zip slip）、压缩炸弹（条目数 / 总解压量 / 压缩比 / 实际解压复核）
/// 与资源包结构（根目录 <c>pack.mcmeta</c>）。这些检查与 <c>MrpackInstaller</c> 的归档检查同一目的，
/// 但资源包是**直接放进游戏目录**的，不能只靠“不落盘就不会穿越”。
/// </remarks>
public interface IResourcePackImporter
{
    /// <summary>导入一个本地资源包。</summary>
    /// <param name="instanceName">目标实例。</param>
    /// <param name="sourcePath">用户选择的本地 zip。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时带“已导入”的说明；失败时带原因。</returns>
    Task<ContentActionResult> ImportAsync(
        string instanceName,
        string sourcePath,
        CancellationToken cancellationToken = default);
}

/// <summary>版本清单（<c>version_manifest_v2.json</c>）。</summary>
public interface IVersionCatalog
{
    /// <summary>版本总数。</summary>
    int TotalCount { get; }

    /// <summary>
    /// 「当前版本」：p05 表格里高亮的那一行，也是新建实例向导默认带的版本。
    /// <para>
    /// 写它是用户在 p05 选中一行后按「用所选版本新建实例」的结果（Q12 决策 (b)）。
    /// 读它是**已经解析过**的值：写空串表示交还给自动判断（最新已安装 → 最新正式版 → 第一行），
    /// 所以调用方不需要自己再跑一遍回落链。
    /// </para>
    /// <para>
    /// 注意「安装所选版本」**不写**它：那个动作只是把版本装进游戏根目录，
    /// 与"向导默认带哪个版本"是两件事（用户在表格行上点「下载」不该顺手改掉向导的默认值）。
    /// </para>
    /// </summary>
    string ActiveVersionId { get; set; }

    /// <summary>快照数量。</summary>
    int SnapshotCount { get; }

    /// <summary>正式版数量。</summary>
    int ReleaseCount { get; }

    /// <summary>当前页展示的版本行。</summary>
    /// <returns>版本行。</returns>
    IReadOnlyList<MinecraftVersion> GetVersions();

    /// <summary>
    /// 估算安装某个版本需要下载多少（向导第 2 步的体积提示）。
    /// </summary>
    /// <remarks>
    /// **异步且可失败**：版本 JSON 可能在缓存里，也可能要联网取一次（几 KB）；
    /// 拿不到时返回 <see langword="null"/>，界面应显示"暂时无法估算"而不是编一个数字。
    /// 只估算版本 JSON 能确定的部分，**不含加载器自己的库**。
    /// </remarks>
    /// <param name="versionId">版本 id。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>估算；拿不到版本 JSON 时为 <see langword="null"/>。</returns>
    Task<VersionDownloadEstimate?> EstimateDownloadAsync(
        string versionId,
        CancellationToken cancellationToken = default);

    /// <summary>指定游戏版本可用的加载器选项（向导第 2 步）。</summary>
    /// <param name="gameVersion">游戏版本。</param>
    /// <returns>加载器选项。</returns>
    IReadOnlyList<LoaderOption> GetLoaders(string gameVersion);

    /// <summary>新建实例向导的步骤定义。</summary>
    /// <returns>步骤。</returns>
    IReadOnlyList<WizardStep> GetWizardSteps();

    /// <summary>
    /// 刷新版本清单（联网；失败时回落到本地缓存并记录事件）。
    /// <para>
    /// 两个 <c>Refresh</c> 成员存在的原因：这个接口的读取端是**同步**的（界面绑定只认同步属性），
    /// 而清单与加载器元数据都来自网络。与其让读取端去阻塞等网络（`.Result` 被 §2.2 明令禁止），
    /// 不如让调用方显式"先刷新、再读"：缓存快照在读取时同步返回，刷新是异步且可失败的。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>刷新某个游戏版本的加载器列表（联网；失败时保留上一次的结果）。</summary>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    Task RefreshLoadersAsync(string gameVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// 只刷新**一个**加载器（向导在用户选中它时调用）。
    /// </summary>
    /// <remarks>
    /// 四个端点互不依赖：某一家失败时只把那一行标成「可重试」，其它行保持原样。
    /// 这是 Neo 26.1 的做法（它只在加载器卡片被点击后取该家的版本列表），
    /// 也是"一个加载不出来不影响其它"的实现方式。
    /// </remarks>
    /// <param name="loader">加载器种类。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    Task RefreshLoaderAsync(LoaderKind loader, string gameVersion, CancellationToken cancellationToken = default);
}

/// <summary>
/// Java 运行时检测（p10 的运行时表、p04/p17 的「所需 Java」、M4 启动前的检查都读它）。
/// <para>
/// 形状与 <see cref="IVersionCatalog"/> 一致：**读取端同步**（界面绑定只认同步属性），
/// 而检测本身要跑若干次 <c>java -version</c>，所以"先刷新再读"——
/// 检测结果同时缓存到 <c>cache\java-runtimes.json</c>，冷启动时同步读缓存即可填上表，
/// 不必等进程扫描。
/// </para>
/// </summary>
public interface IJavaRuntimeCatalog
{
    /// <summary>最近一次检测到的运行时（同步快照）。</summary>
    /// <returns>运行时事实；还没检测出来时为空列表。</returns>
    IReadOnlyList<JavaInstallation> GetInstallations();

    /// <summary>
    /// 是否**已经有过检测结果**（包括从缓存恢复的）。
    /// <para>
    /// 这个成员存在的理由很重要：没有它就无法区分「本机没有 Java」与「还没查过」。
    /// 前者该在 p05 上显示「缺少 Java 21」，后者只能说「未安装」——
    /// 把查都没查过说成缺 Java，等于凭空报了一个用户会去处理的假故障。
    /// </para>
    /// </summary>
    bool HasDetectionResult { get; }

    /// <summary>按新鲜度刷新（页面被导航到时用；刚检测过就不重复跑进程）。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>强制重新检测（用户按了「重新检测 Java」时必须真的重跑，不能被新鲜度窗口挡掉）。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    Task RedetectAsync(CancellationToken cancellationToken = default);

    /// <summary>检查一个用户选择的自有 Java，但不写入列表。</summary>
    /// <param name="executablePath">java / javaw 可执行文件路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>检查结果（含风险位）。</returns>
    /// <exception cref="JavaRuntimeInspectionException">文件不存在或不是有效 Java。</exception>
    Task<JavaRuntimeInspection> InspectCustomAsync(
        string executablePath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("这个 Java 运行时目录不支持手动添加。");

    /// <summary>把一个检查通过的自有 Java 加入列表。</summary>
    /// <param name="installation">检查结果里的安装事实。</param>
    void AddCustom(JavaInstallation installation) =>
        throw new NotSupportedException("这个 Java 运行时目录不支持手动添加。");
}

/// <summary>内容目录（模组、资源包、存档、整合包）。</summary>
public interface IContentCatalog
{
    /// <summary>某实例的模组。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>模组行。</returns>
    IReadOnlyList<ModEntry> GetMods(string instanceName);

    /// <summary>某实例的资源包。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>资源包项。</returns>
    IReadOnlyList<ResourcePackEntry> GetResourcePacks(string instanceName);

    /// <summary>某实例的存档。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>存档行。</returns>
    IReadOnlyList<WorldSaveEntry> GetWorldSaves(string instanceName);

    /// <summary>
    /// 删除一个存档（S9-f，p09 的「删除所选」）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="directoryName">磁盘上的存档目录名（不是显示名：两个存档可以同名）。</param>
    /// <returns>结果；成功时带"移到了哪里"，失败时带原因。</returns>
    /// <remarks>
    /// **不是真删**：整个存档目录被移进 <c>&lt;游戏目录&gt;\backups\saves\&lt;时间戳&gt;\&lt;目录名&gt;\</c>
    /// —— 与 M7-f 恢复时换下来的旧存档同一个回收目录，用户随时能拿回来。
    /// 因此这一步**没有不可逆的分支**：不需要模态确认框，按钮 ToolTip 与完成后的提示就是那句说明。
    /// </remarks>
    ContentActionResult DeleteWorldSave(string instanceName, string directoryName);

    /// <summary>已安装的整合包。</summary>
    /// <returns>整合包项。</returns>
    IReadOnlyList<ModpackEntry> GetModpacks();

    /// <summary>
    /// Modrinth 浏览结果（p08 的「从 Modrinth 浏览」）。
    /// </summary>
    /// <returns>整合包行。</returns>
    /// <remarks>
    /// **同步读**：数据来自磁盘快照（<c>cache\content\modrinth-browse.json</c>），
    /// 联网发生在 <see cref="RefreshModrinthAsync"/> 里。与 <see cref="IVersionCatalog"/> 同一个道理 ——
    /// 界面绑定只认同步属性，所以读取端不阻塞等网络。
    /// </remarks>
    IReadOnlyList<ModrinthEntry> GetModrinthCatalog();

    /// <summary>
    /// Modrinth 浏览结果是否**取到过**（含从缓存恢复）。
    /// <para>
    /// "还没查过"与"查过但没有结果"必须分开：前者该说"联网后会自动获取"，
    /// 后者该说"Modrinth 没有返回任何整合包"。把没查过说成"没有结果"就是在编。
    /// </para>
    /// </summary>
    bool HasModrinthCatalog { get; }

    /// <summary>
    /// 刷新 Modrinth 浏览列表（联网；新鲜度窗口内不重复请求、失败后退避两分钟）。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    /// <remarks>
    /// **不抛异常**：调用方是页面导航，网络失败不该让页面挂掉。失败原因由实现记成事件，
    /// 手上那份列表**保持不变**（宁可显示上一次的结果，也不清空成"没有"）。
    /// </remarks>
    Task RefreshModrinthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按搜索词 / 游戏版本 / 加载器查一次 Modrinth（p08 的搜索与筛选；S9-e）。
    /// </summary>
    /// <param name="query">查询条件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>
    /// 结果行；<see langword="null"/> 表示这次请求失败（调用方应保留上一次结果并如实说明）。
    /// **空列表是合法结果**：那表示"查到了，但没有匹配"。
    /// </returns>
    /// <remarks>
    /// 结果**只放内存**、不写浏览快照（见 <see cref="ModrinthSearchQuery"/> 的备注）；
    /// 失败原因由实现记成事件，不向界面抛异常。
    /// </remarks>
    Task<IReadOnlyList<ModrinthEntry>?> SearchModrinthAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 准备一次「从 Modrinth 直接安装」：挑兼容版本 → 下载 .mrpack → 校验 SHA-1。
    /// </summary>
    /// <param name="request">项目与筛选条件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>可交给导入器的本地归档；失败时带原因（不抛异常）。</returns>
    Task<ModrinthModpackResult> PrepareModrinthModpackAsync(
        ModrinthModpackRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按种类查一次 Modrinth（模组 / 资源包；p07 / p09 的下载对话框用）。
    /// </summary>
    /// <param name="instanceName">目标实例（游戏版本与加载器由它决定，不用界面自己拼）。</param>
    /// <param name="kind">内容种类。</param>
    /// <param name="query">关键词；空表示不按关键词搜。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果；<see langword="null"/> 表示请求失败（调用方保留旧结果）。</returns>
    Task<IReadOnlyList<ModrinthEntry>?> SearchModrinthContentAsync(
        string instanceName,
        ModrinthContentKind kind,
        string? query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为某个实例的「缺失依赖」组一次下载请求（p07 的「修复依赖」）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>下载请求；没有缺失依赖或解析不出来时带原因（不抛异常）。</returns>
    /// <remarks>
    /// 依赖只在 jar 元数据里写了 mod id（如 <c>flywheel</c>），没有 Modrinth 项目 id；
    /// 这里用 mod id 搜 Modrinth，挑第一个可安装项目，再按实例的游戏版本 / 加载器选兼容版本。
    /// </remarks>
    Task<ModDependencyFixResult> PrepareDependencyFixAsync(
        string instanceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为某个实例挑一个 Modrinth 模组 / 资源包的兼容版本，生成可入队的下载目标。
    /// </summary>
    /// <param name="instanceName">目标实例（游戏版本与加载器由它决定）。</param>
    /// <param name="kind">内容种类。</param>
    /// <param name="projectId">Modrinth 项目 id。</param>
    /// <param name="displayName">显示名（通常是项目名）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>目标；没有兼容版本时带原因（不抛异常）。</returns>
    Task<ModrinthContentResult> PrepareModrinthContentAsync(
        string instanceName,
        ModrinthContentKind kind,
        string projectId,
        string displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一个实例导出成 <c>.mrpack</c>（D：内容全部放进 <c>overrides/</c>）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="destinationPath">目标 <c>.mrpack</c> 路径。</param>
    /// <returns>结果；成功时带目标路径，失败时带原因。</returns>
    /// <remarks>
    /// 只导出**内容**（mods / config / resourcepacks / shaderpacks / saves / options.txt 等），
    /// 不导出 logs / crash-reports / backups，也不把游戏文件塞进归档；依赖写在
    /// <c>modrinth.index.json</c> 里，导入时由导入器按依赖装游戏与加载器。
    /// </remarks>
    ContentActionResult ExportInstance(string instanceName, string destinationPath);

    /// <summary>某实例的模组统计（用于 props 区与横幅）。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>统计。</returns>
    ModSummary GetModSummary(string instanceName);

    /// <summary>
    /// 把某实例的模组与 Modrinth 对一次账（联网；算 SHA-1、查来源、查更新）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    /// <remarks>
    /// <para>
    /// 与 <see cref="RefreshModrinthAsync"/> 分开：那一件是**浏览列表**（p08，与本地内容无关），
    /// 这一件是**本机的 jar**（p07）。两件事的缓存、时间窗口与失败语义都不一样，合成一个方法
    /// 只会让调用方为了其中一件而等另一件。
    /// </para>
    /// <para>
    /// **不抛异常**，也**不阻塞 UI**：算哈希是几十 MB 到几 GB 的磁盘读，实现方必须放到后台线程
    /// （AGENTS.md §6）。失败时保留上一次的对账结果 —— 拿不到 Modrinth 就说"未知"，
    /// 绝不说"不在 Modrinth 收录"。
    /// </para>
    /// </remarks>
    Task RefreshModsAsync(string instanceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 组一次"更新这个实例里所有非最新模组"的下载请求（p07 的「一键更新」，M7-d）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>
    /// 请求；**没有可更新的模组时为 <see langword="null"/>**（不是"返回一个空请求" ——
    /// 调用方要据此决定按钮可不可按，空请求会让"没什么可更新"变成一次看起来在跑、其实什么都没做的任务）。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 为什么由内容目录来组、而不是由 ViewModel 从表格行里凑：要拿到的三样东西
    /// （本地 jar 的绝对路径、Modrinth 上的最新版本 id、本机已装项目的 id）分别来自
    /// 扫描结果、哈希索引与对账索引，而它们都在这里；界面那一层拿不到，也不该为了拼一个请求去读三份缓存。
    /// </para>
    /// <para>
    /// **同步**：三份数据都在内存/磁盘缓存里（M7-c 起），不联网。对账还没跑过时如实返回
    /// <see langword="null"/>（"还不知道有没有更新"不等于"可以更新"）。
    /// </para>
    /// </remarks>
    ContentDownloadRequest? CreateModUpdateRequest(string instanceName);

    /// <summary>
    /// 启用 / 禁用一个模组（M7-g-2 / P1-14）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="fileName">磁盘上的文件名（<see cref="ModEntry.FileName"/>）。</param>
    /// <param name="enabled">要改成启用还是禁用。</param>
    /// <returns>结果；失败时带原因。</returns>
    /// <remarks>
    /// <para>
    /// 实现是**改名**：启用 = 去掉 <c>.disabled</c>，禁用 = 加上 <c>.disabled</c>。
    /// 这是游戏自己的约定（加载器只认 <c>*.jar</c>），文件**不删**，所以禁用永远可逆。
    /// </para>
    /// <para>
    /// <b>目标名已存在时拒绝</b>：<c>foo.jar</c> 与 <c>foo.jar.disabled</c> 同时存在时，
    /// 改名会覆盖其中一个 —— 那是用户的文件，不是我们能替他决定删哪个的。
    /// 已经是目标状态时视为成功（幂等），不写任何东西。
    /// </para>
    /// <para>
    /// <paramref name="fileName"/> 来自界面，**必须当不可信输入**：只接受单段文件名
    /// （<see cref="Security.PathSafety.IsSafePathSegment"/>），且解析后的路径必须仍在 <c>mods\</c> 里。
    /// </para>
    /// </remarks>
    ContentActionResult SetModEnabled(string instanceName, string fileName, bool enabled);

    /// <summary>
    /// 删除一个模组（M7-g-2 / P1-14）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="fileName">磁盘上的文件名（<see cref="ModEntry.FileName"/>）。</param>
    /// <returns>结果；成功时带"移到了哪里"，失败时带原因。</returns>
    /// <remarks>
    /// <b>不是真删</b>：按用户数据安全那一条（AGENTS.md §6），文件被移进
    /// <c>&lt;游戏目录&gt;\backups\mods\&lt;时间戳&gt;\</c>（与 M7-d 替换旧版本同一个备份目录），
    /// 用户随时能拿回来。所以这一页的「删除所选」没有模态确认框 —— 没有一步不可逆。
    /// </remarks>
    ContentActionResult DeleteMod(string instanceName, string fileName);

    /// <summary>
    /// 启用 / 停用一个资源包（M7-g-2b）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="fileName">磁盘上的文件名（<see cref="ResourcePackEntry.FileName"/>）。</param>
    /// <param name="enabled">要改成启用还是停用。</param>
    /// <returns>结果；失败时带原因。</returns>
    /// <remarks>
    /// <para>
    /// 实现是改写 <c>options.txt</c> 的 <c>resourcePacks</c> 数组（**资源包的启用只有它说了算**）。
    /// 数组顺序是**低 → 高**优先级；新启用的包插在**内置包之后**（也就是用户区的最低优先级）——
    /// 启用一个包不该顺手覆盖用户已经排好的那些。
    /// </para>
    /// <para>
    /// **读不懂的 <c>options.txt</c> 一律不写**（用户手改坏的文件只能如实报告）；
    /// 已经是目标状态时幂等成功。写入走原子写，其余设置行原样保留。
    /// </para>
    /// </remarks>
    ContentActionResult SetResourcePackEnabled(string instanceName, string fileName, bool enabled);

    /// <summary>
    /// 上移 / 下移一个**已启用**资源包的优先级（M7-g-2b）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="fileName">磁盘上的文件名。</param>
    /// <param name="direction"><c>-1</c> = 上移（更高优先级，界面列表往上），<c>+1</c> = 下移。</param>
    /// <returns>结果；失败时带原因。</returns>
    /// <remarks>
    /// <para>
    /// **方向容易写反**：<c>options.txt</c> 的数组是低 → 高存的，所以"上移"（更高优先级）是往**数组末尾**挪。
    /// 已到顶 / 到底时幂等成功（界面那边按钮本来就是灰的）。
    /// </para>
    /// <para>
    /// **不会挪到内置包下面**（<c>vanilla</c> 等在最前、是最低一层）：用户包的最低优先级就是"内置包之后"。
    /// 未启用的包没有优先级可言，调用它会失败并说明原因。
    /// </para>
    /// </remarks>
    ContentActionResult MoveResourcePack(string instanceName, string fileName, int direction);

    /// <summary>
    /// 删除一个资源包（M7-g-2b）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="fileName">磁盘上的文件名。</param>
    /// <returns>结果；成功时带"移到了哪里"，失败时带原因。</returns>
    /// <remarks>
    /// **不是真删**：文件 / 文件夹被移进 <c>&lt;游戏目录&gt;\backups\resourcepacks\&lt;时间戳&gt;\</c>
    /// （与模组的删除同一个回收目录形状），用户随时能拿回来；若它本来在启用列表里，
    /// 同时把它从 <c>resourcePacks</c> 里去掉（否则游戏会留着一条指向不存在文件的 id）。
    /// </remarks>
    ContentActionResult DeleteResourcePack(string instanceName, string fileName);
}

/// <summary>模组统计。</summary>
/// <param name="Enabled">已启用数量。</param>
/// <param name="Disabled">已禁用数量。</param>
/// <param name="MissingDependency">缺失依赖数量。</param>
/// <param name="Updatable">可更新数量。</param>
/// <param name="LoaderMatch">加载器匹配情况（键 + 参数：`原版` 是文案，其余是专有名词 + 版本号）。</param>
/// <param name="GameVersionMatch">游戏版本匹配情况（键 + 参数）。</param>
/// <param name="MissingDependencyDetail">缺失依赖的详情（横幅用，键 + 参数），无则为 <see langword="null"/>。</param>
public sealed record ModSummary(
    int Enabled,
    int Disabled,
    int MissingDependency,
    int Updatable,
    TextTemplate LoaderMatch,
    TextTemplate GameVersionMatch,
    TextTemplate? MissingDependencyDetail);

/// <summary>下载队列与启动阶段。</summary>
public interface IDownloadCatalog
{
    /// <summary>队列状态发生变化（进度、任务增删）。实现方在后台线程触发。</summary>
    event EventHandler? Changed;

    /// <summary>未完成任务数（页头队列徽标）。</summary>
    int PendingTaskCount { get; }

    /// <summary>总体进度。</summary>
    /// <returns>进度。</returns>
    DownloadProgress GetProgress();

    /// <summary>队列内容。</summary>
    /// <returns>队列项。</returns>
    IReadOnlyList<DownloadQueueItem> GetQueue();


    /// <summary>把一个安装任务排进队列。**立即返回**，不等它跑完。</summary>
    /// <param name="request">安装请求。</param>
    /// <returns>刚入队的任务。</returns>
    /// <remarks>
    /// 刻意做成"入队即返回"而不是 <c>Task&lt;InstallResult&gt;</c>：一次原版安装要几分钟，
    /// 让 ViewModel 去 await 它等于把界面钉在向导页上；队列 + 进度才是它该有的形状。
    /// </remarks>
    InstallJob EnqueueInstall(InstallRequest request);

    /// <summary>
    /// 把一个「获取 Mojang 官方 Java 运行时」任务排进队列（§12.7）。**立即返回**，不等它跑完。
    /// </summary>
    /// <param name="request">请求（要适配的游戏版本；为空表示取最新一代）。</param>
    /// <returns>刚入队的任务。</returns>
    /// <remarks>
    /// 与安装走同一条队列、同一种任务终态：p10 的「获取 Java 25」按设计稿的连通性表跳 p06，
    /// 用户要能在那里看见进度与结果 —— 一个"点了之后什么都不发生"的按钮正是 M3 的残留。
    /// </remarks>
    InstallJob EnqueueJavaRuntime(JavaRuntimeRequest request);

    /// <summary>
    /// 把一个「下载这些 Modrinth 内容」任务排进队列（M7-d：p07 的「更新所选」）。**立即返回**，不等它跑完。
    /// </summary>
    /// <param name="request">请求（实例 + 目标；依赖由队列里的安装器递归解析）。</param>
    /// <returns>刚入队的任务。</returns>
    /// <remarks>
    /// 与安装、运行时走同一条队列、同一种任务终态：更新的进度、失败原因与"旧版本移到哪去了"
    /// 都要能在 p06 看见 —— 一个下完却没说旧文件去哪儿的按钮，对用户来说就是"它把我的模组弄没了"。
    /// </remarks>
    InstallJob EnqueueContentDownload(ContentDownloadRequest request);

    /// <summary>
    /// 把一个「导入整合包」任务排进队列（M7-e-2：p08 的「从文件导入 .mrpack」）。**立即返回**，不等它跑完。
    /// </summary>
    /// <param name="request">请求（用户选中的 <c>.mrpack</c> 路径；实例名与游戏版本从清单里读）。</param>
    /// <returns>刚入队的任务。</returns>
    /// <remarks>
    /// 顺序是**扫描 → 建实例 → 装游戏与加载器 → 铺内容**：扫描在**建实例之前**（设计稿 p08 承诺的
    /// "先安全扫描，再建任务"），否则一个不合规的包会在实例列表里留下一个空壳实例；
    /// 而"建实例"必须在铺内容之前，因为内容要写进那个实例自己的目录
    /// （<c>IInstanceCatalog.GetLaunchInfo</c> 的 <c>InstancePath</c>）。
    /// </remarks>
    InstallJob EnqueueMrpackImport(MrpackInstallRequest request);

    /// <summary>
    /// 把一个「从 Modrinth 下载并安装整合包」任务排进队列（2026-10-21）。**立即返回**，不等它跑完。
    /// </summary>
    /// <param name="request">项目与兼容性条件。</param>
    /// <param name="title">队列行标题（项目名 + 版本号）。</param>
    /// <returns>刚入队的任务。</returns>
    /// <remarks>
    /// 与「导入本地 .mrpack」分开：下载发生在**队列里**，所以 p08 点「安装」之后进度条与失败原因
    /// 都能在 p06 看到，而不是界面上先静默下载几十 MB。
    /// </remarks>
    InstallJob EnqueueModrinthModpack(ModrinthModpackRequest request, string title);

    /// <summary>
    /// 把一个「备份这个存档」任务排进队列（M7-f：p09 的「备份所选」）。**立即返回**，不等它跑完。
    /// </summary>
    /// <param name="request">请求（实例 + 存档目录名）。</param>
    /// <returns>刚入队的任务。</returns>
    /// <remarks>
    /// 走队列而不是同步做：一个存档可以是几个 GB，压缩要几十秒到几分钟 ——
    /// 停在 p09 上等它，界面看起来就是卡死了。设计稿 p09 的「备份所选 / 备份全部存档」两个按钮
    /// 在 <c>mockup/pages/p09-resources.html</c> 里指的正是 p06，所以进度、失败原因都在那一页读。
    /// <para>
    /// 「备份全部存档」是**每个存档一个任务**（由 ViewModel 逐个入队），不是一个批量任务：
    /// 队列本来就是顺序执行的，而进度、失败原因与（M7-g 之后的）取消都因此是**按存档**的粒度。
    /// </para>
    /// </remarks>
    InstallJob EnqueueWorldBackup(WorldBackupRequest request);

    /// <summary>
    /// 把一个「恢复这个存档」任务排进队列（M7-f：p09 的「恢复所选」）。**立即返回**，不等它跑完。
    /// </summary>
    /// <param name="request">请求（实例 + 存档目录名）。</param>
    /// <returns>刚入队的任务。</returns>
    /// <remarks>
    /// 恢复要先解压、再把当前存档整目录改名进 <c>backups\saves\&lt;时间戳&gt;\</c>、最后把备份放回原位，
    /// 所以它是队列里最长的一种"写"—— 单 worker 顺序执行顺带保证了
    /// "同一个存档的备份与恢复不会同时跑"。
    /// </remarks>
    InstallJob EnqueueWorldRestore(WorldRestoreRequest request);

    /// <summary>
    /// 取消一个**还没结束**的任务（排队中或正在执行，M7-g：p06 的行内「取消」与「取消全部」）。
    /// </summary>
    /// <param name="jobId">任务 id（<c>DownloadQueueItem.Id</c>）。</param>
    /// <returns>确实做了事（任务存在且还没结束）时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 两条路径不一样，但结果一样：**排队中**的直接落终态（worker 再也看不到它），
    /// **正在执行**的只是"请求取消"—— 真正的停下发生在安装器下一次检查取消标记时，
    /// 之后的落终态由队列那条单 worker 循环完成（所以这个方法是立即返回的）。
    /// </para>
    /// <para>
    /// **取消不是失败**：终态是「已取消」而不是「失败」，用户自己点的取消不该显示成红色故障。
    /// 但**失败路径的善后照旧执行** —— 游戏版本安装仍然回滚这次写入（M4-h 的承诺：
    /// "没装完就回到装之前"），内容下载仍然保留已校验通过的文件（M7-d 的承诺）。
    /// </para>
    /// </remarks>
    bool Cancel(string jobId);

    /// <summary>
    /// 重试一个**已经结束**的任务（失败或已取消，M7-g）。**立即返回**，不等它跑完。
    /// </summary>
    /// <param name="jobId">任务 id。</param>
    /// <returns>新入队的任务；任务不存在、还在跑、或者已经**成功**时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 「重试」是**同一个任务原样再排一次**（标题与请求都不变），不是"跳过失败重来一遍流程"：
    /// 用户点的是这一行，他心里那一行就是这件事。已经成功的任务不给重试 —— 那只会把同一件事
    /// 再做一遍，而用户真正想要的通常是"再装一次"，那该走原来的入口。
    /// </remarks>
    InstallJob? Retry(string jobId);

    /// <summary>
    /// 清空队列里所有**已经结束**的行（p06 的「清空已完成」）。
    /// </summary>
    /// <returns>清掉了几行。</returns>
    /// <remarks>
    /// 只删历史，不碰排队与正在执行的任务：这一页的「队列」是"还要做什么"，
    /// 而清空历史不该顺手取消用户排着的事。
    /// </remarks>
    int ClearFinished();

    /// <summary>队列里的全部任务（含已完成的近期任务）。</summary>
    /// <returns>任务。</returns>
    IReadOnlyList<InstallJob> GetJobs();

    /// <summary>等待队列清空（测试与退出前用）。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    Task WhenIdleAsync(CancellationToken cancellationToken = default);
}

/// <summary>事件日志。</summary>
public interface IEventLog
{
    /// <summary>事件总数（表头「共 N 个」）。</summary>
    int TotalCount { get; }

    /// <summary>全局事件（仪表板与下载页）。</summary>
    /// <returns>事件行。</returns>
    IReadOnlyList<EventEntry> GetEvents();

    /// <summary>某实例的事件（实例详情页）。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>事件行。</returns>
    IReadOnlyList<EventEntry> GetInstanceEvents(string instanceName);

    /// <summary>某实例的配置变更记录（实例设置页）。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>事件行。</returns>
    IReadOnlyList<EventEntry> GetSettingsEvents(string instanceName);

    /// <summary>
    /// 有新事件写入时触发（**已经切回 UI 线程**，且已按刷新节流合并过）。
    /// </summary>
    /// <remarks>
    /// **M6-f**：订阅者只需要重读自己那几个属性，不碰任何计时器/线程类型 ——
    /// "什么时候刷、在哪个线程刷"由实现方（App 的 <c>FileEventLog</c>）负责：
    /// 它订阅内核的 <c>IDiagnosticsLog.Changed</c>（后台线程），经 <c>IUiRefreshThrottle</c>
    /// 合并（首帧立即、窗口内只跑一次、**窗口末尾补跑最后一次**）后切回 UI 线程再抛这个事件。
    /// </remarks>
    event EventHandler? Changed;
}

/// <summary>设置读写。</summary>
public interface ISettingsCatalog
{
    /// <summary>全局默认设置。</summary>
    /// <returns>全局设置。</returns>
    GlobalSettings GetGlobal();

    /// <summary>某实例的覆盖项。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>覆盖项。</returns>
    InstanceOverrides GetInstanceOverrides(string instanceName);

    /// <summary>保存全局默认设置。</summary>
    /// <param name="settings">新的全局设置。</param>
    void SaveGlobal(GlobalSettings settings);

    /// <summary>保存某实例的覆盖项。</summary>
    /// <param name="instanceOverrides">新的覆盖项。</param>
    void SaveInstanceOverrides(InstanceOverrides instanceOverrides);
}

/// <summary>本机信息（仪表板属性区右半部分）。</summary>
public interface ISystemInfoProvider
{
    /// <summary>操作系统版本文本。</summary>
    string OperatingSystem { get; }

    /// <summary>处理器文本。</summary>
    string Processor { get; }

    /// <summary>已安装内存文本（含可用量）。</summary>
    string InstalledMemory { get; }

    /// <summary>物理内存总量文本（不含可用量，用于「推荐区间」这类提示）。</summary>
    string TotalMemory { get; }

    /// <summary>
    /// 内存推荐值（默认 / 上限 / 安全区，以及计算它们的物理内存总量）。
    /// <para>
    /// 单独一个成员而不是让界面拿 <see cref="TotalMemory"/> 字符串去解析百分比：
    /// 「推荐多少」是一条**规则**（含 2 GB 以下机器的下限收敛），
    /// 放在内核里才能被 M4 的启动前校验复用、也才能被单测钉住。
    /// </para>
    /// </summary>
    MemoryRecommendation Memory { get; }

    /// <summary>显卡文本。</summary>
    string GraphicsCard { get; }
}

/// <summary>启动器自身的元信息（「关于」页）。</summary>
public interface ILauncherInfo
{
    /// <summary>产品名。</summary>
    string ProductName { get; }

    /// <summary>版本号文本。</summary>
    string Version { get; }

    /// <summary>界面基线说明。</summary>
    string UiBaseline { get; }

    /// <summary>界面风格来源说明。</summary>
    string UiProvenance { get; }

    /// <summary>目标运行时文本。</summary>
    string Runtime { get; }

    /// <summary>目标平台文本。</summary>
    string TargetPlatform { get; }

    /// <summary>架构文本。</summary>
    string Architecture { get; }

    /// <summary>认证方式文本。</summary>
    string Authentication { get; }

    /// <summary>版本清单来源文本。</summary>
    string VersionManifest { get; }

    /// <summary>依赖政策文本。</summary>
    string DependencyPolicy { get; }

    /// <summary>数据目录文本。</summary>
    string DataDirectory { get; }

    /// <summary>日志目录文本。</summary>
    string LogDirectory { get; }

    /// <summary>许可证文本。</summary>
    string License { get; }

    /// <summary>第三方声明文本。</summary>
    string ThirdPartyNotices { get; }
}
