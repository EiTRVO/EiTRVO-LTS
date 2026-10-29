namespace EiTRVO.Core.Launcher.Content;

using System.Globalization;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Text;

/// <summary>从 Modrinth 下载的内容种类。</summary>
public enum ModrinthContentKind
{
    /// <summary>模组（<c>mods\</c>，允许依赖递归）。</summary>
    Mod,

    /// <summary>资源包（<c>resourcepacks\</c>，不递归安装依赖）。</summary>
    ResourcePack,
}

/// <summary>内容来源。</summary>
public enum ContentSource
{
    /// <summary>从 Modrinth 下载。</summary>
    Modrinth,

    /// <summary>本地文件（jar/zip），来源未收录。</summary>
    LocalFile,

    /// <summary>随整合包导入。</summary>
    Modpack,
}

/// <summary>模组的启用状态（决定首列图标与状态列文本）。</summary>
public enum ModEnablement
{
    /// <summary>已启用。</summary>
    Enabled,

    /// <summary>已禁用。</summary>
    Disabled,

    /// <summary>未验证来源，仍启用但需提示。</summary>
    Unverified,
}

/// <summary>模组的功能健康度。</summary>
public enum ModState
{
    /// <summary>正常。</summary>
    Normal,

    /// <summary>缺失前置依赖。</summary>
    MissingDependency,

    /// <summary>有新版本可更新。</summary>
    Updatable,
}

/// <summary>模组表格行。</summary>
/// <param name="Name">模组名。</param>
/// <param name="FileName">
/// 磁盘上的**文件名**（<c>create-0.5.1f.jar</c> / <c>create-0.5.1f.jar.disabled</c>）。
/// <para>
/// M7-g-2：启用 / 禁用 / 删除都要**点名一个文件**，而显示名与版本都不可靠
/// （两个模组可以同名、版本也可以相同）—— 只有文件名在 <c>mods\</c> 里唯一。
/// 界面把这一格原样交回内核，内核自己再校验一次它是单段名。
/// </para>
/// </param>
/// <param name="Version">已安装版本。</param>
/// <param name="Source">来源。</param>
/// <param name="GameVersion">适配的游戏版本。</param>
/// <param name="Enablement">启用状态。</param>
/// <param name="State">健康度。</param>
/// <param name="Note">备注（键 + 参数），无备注时为 <see langword="null"/>。</param>
/// <param name="IsActive">是否被选中。</param>
/// <remarks>
/// **M6-e-3f**：`Note` 从「无则空串」改成 <see langword="null"/> + 「键 + 参数」——
/// `需要 {0}` / `最新 {0}` / `与 {0} 冲突` / `不在 Modrinth 收录` 都是**文案**，
/// 而空串是"有值但为空"的假象（同 §5.3 第 ⑨ / ⑩ 条）。
/// </remarks>
public sealed record ModEntry(
    string Name,
    string FileName,
    string Version,
    ContentSource Source,
    string GameVersion,
    ModEnablement Enablement,
    ModState State,
    TextTemplate? Note,
    bool IsActive);

/// <summary>资源包列表项（含加载优先级，序号越小越优先）。</summary>
/// <param name="Name">资源包名。</param>
/// <param name="FileName">
/// 磁盘上的**文件名**（zip 是 <c>Faithful 32x.zip</c>，文件夹形态是文件夹名）。
/// <para>
/// M7-g-2b：启用 / 停用 / 上移 / 下移 / 删除都要**点名一个包**，而显示名会重名
/// （同名 zip 与文件夹可以并存）。它同时决定 <c>options.txt</c> 里的 id（<c>file/&lt;FileName&gt;</c>）。
/// </para>
/// </param>
/// <param name="Detail">说明（键 + 参数），如 <c>32× · 18.4 MB · 优先级 1</c>。</param>
/// <param name="Priority">优先级序号（**1 = 最高**；显示顺序就是游戏里从高到低的顺序）。</param>
/// <param name="IsEnabled">是否启用。</param>
/// <param name="IsActive">是否被选中。</param>
public sealed record ResourcePackEntry(
    string Name,
    string FileName,
    TextTemplate Detail,
    int Priority,
    bool IsEnabled,
    bool IsActive);

/// <summary>存档表格行。</summary>
/// <param name="Name">存档名（<c>level.dat</c> 里的 <c>LevelName</c>；读不出来时是目录名）。</param>
/// <param name="Folder">
/// 存档的**目录名**。
/// <para>
/// 显示名与目录名可以不一样（两个存档也可以同名），而磁盘上的操作只认目录名：
/// 备份是 <c>backups\&lt;目录名&gt;.zip</c>、恢复的目标是 <c>saves\&lt;目录名&gt;</c>（M7-f）。
/// 界面把用户选中的那一行交回内核时带着它 —— 少了这一格，ViewModel 就只能拿显示名去拼路径。
/// </para>
/// </param>
/// <param name="Size">大小文本。</param>
/// <param name="LastPlayed">最后游玩时间文本（键 + 参数：`今天 {0}` 或纯数据的绝对时间）。</param>
/// <param name="GameMode">游戏模式（键 + 参数：`生存` / `创造`）。</param>
/// <param name="GameVersion">存档版本。</param>
/// <param name="IsBackedUp">是否已备份。</param>
/// <param name="IsActive">是否被选中。</param>
public sealed record WorldSaveEntry(
    string Name,
    string Folder,
    string Size,
    TextTemplate LastPlayed,
    TextTemplate GameMode,
    string GameVersion,
    bool IsBackedUp,
    bool IsActive);

/// <summary>已安装的整合包。</summary>
/// <param name="Name">整合包名。</param>
/// <param name="Version">版本。</param>
/// <param name="ModCount">模组数。</param>
/// <param name="Size">占用空间。</param>
/// <param name="InstanceName">对应实例名。</param>
/// <param name="StatusText">状态文本（键 + 参数，如 <c>已同步</c>）。</param>
/// <param name="StatusTone">状态色调。</param>
/// <param name="IsActive">是否被选中。</param>
public sealed record ModpackEntry(
    string Name,
    string Version,
    int ModCount,
    string Size,
    string InstanceName,
    TextTemplate StatusText,
    Tone StatusTone,
    bool IsActive)
{
    /// <summary>列表里那一行摘要：<c>v34 · 216 个模组 · 12.4 GB · 对应实例 1.20.4-Forge</c>。</summary>
    /// <remarks>
    /// <see cref="Version"/> 本身就带 <c>v</c> 前缀（例如 <c>v34</c>），这里不再重复加。
    /// 它是「键 + 参数」，由 App 层的 <c>ModpackRowViewModel</c> 拼成句子（AGENTS.md §5.3 第 ⑨ 条）。
    /// </remarks>
    public TextTemplate Meta => TextTemplate.Of(
        "{0} · {1} 个模组 · {2} · 对应实例 {3}",
        Version,
        ModCount.ToString(CultureInfo.InvariantCulture),
        Size,
        InstanceName);
}

/// <summary>Modrinth 浏览列表的一行。</summary>
/// <param name="Name">整合包名。</param>
/// <param name="Summary">一句话简介。</param>
/// <param name="Downloads">下载量文本，如 <c>12.4M</c>。</param>
/// <param name="Version">最新版本。</param>
/// <param name="ProjectId">Modrinth 项目 id（直接安装用；旧缓存快照可能没有）。</param>
/// <param name="VersionId">最新版本 id（直接安装用；旧缓存快照可能没有）。</param>
public sealed record ModrinthEntry(
    string Name,
    string Summary,
    string Downloads,
    string Version,
    string? ProjectId = null,
    string? VersionId = null)
{
    /// <summary>旧缓存快照里可能没有项目 id：这种行不能直接安装（如实置灰）。</summary>
    public bool CanInstall => !string.IsNullOrWhiteSpace(ProjectId);
}

/// <summary>从 Modrinth 直接安装整合包的请求（S11-c）。</summary>
/// <param name="ProjectId">Modrinth 项目 id。</param>
/// <param name="GameVersion">目标游戏版本；空表示不限制。</param>
/// <param name="Loader">目标加载器短名（<c>forge</c> / <c>fabric</c> / <c>neoforge</c> / <c>quilt</c>）；空表示不限制。</param>
public sealed record ModrinthModpackRequest(string ProjectId, string? GameVersion, string? Loader);

/// <summary>已下载到本地的整合包归档。</summary>
/// <param name="ArchivePath">本地 <c>.mrpack</c> 路径（导入器的输入）。</param>
/// <param name="Title">给队列行用的标题（项目名 + 版本号）。</param>
public sealed record ModrinthModpackArchive(string ArchivePath, string Title);

/// <summary>「从 Modrinth 直接安装」的准备结果。</summary>
/// <param name="Succeeded">是否拿到了可导入的归档。</param>
/// <param name="Archive">归档；失败时为 <see langword="null"/>。</param>
/// <param name="Failure">失败原因（键 + 参数）。</param>
public sealed record ModrinthModpackResult(bool Succeeded, ModrinthModpackArchive? Archive, TextTemplate? Failure);

/// <summary>「从 Modrinth 选一个模组 / 资源包下载」的准备结果。</summary>
/// <param name="Succeeded">是否挑出了兼容版本。</param>
/// <param name="Target">下载目标（项目 id / 版本 id / 显示名）；失败时为 <see langword="null"/>。</param>
/// <param name="Failure">失败原因（键 + 参数）。</param>
public sealed record ModrinthContentResult(
    bool Succeeded,
    ContentDownloadTarget? Target,
    TextTemplate? Failure);

/// <summary>「修复依赖」的准备结果：把缺失依赖解析成可入队的下载请求。</summary>
/// <param name="Succeeded">是否解析出了完整的下载请求。</param>
/// <param name="Request">下载请求；失败时为 <see langword="null"/>。</param>
/// <param name="Failure">失败原因（键 + 参数）。</param>
public sealed record ModDependencyFixResult(
    bool Succeeded,
    ContentDownloadRequest? Request,
    TextTemplate? Failure);

/// <summary>
/// Modrinth 浏览的一次查询条件（p08 的搜索框 + 游戏版本 / 加载器两个下拉）。
/// </summary>
/// <param name="Query">搜索词；空表示不按关键词搜。</param>
/// <param name="GameVersion">游戏版本（如 <c>1.20.4</c>）；空表示不限。</param>
/// <param name="Loader">加载器短名（<c>forge</c> / <c>fabric</c> / <c>neoforge</c> / <c>quilt</c>）；空表示不限。</param>
/// <param name="Sort">排序方式（默认相关性）。</param>
/// <remarks>
/// 这是**界面层筛选条件**的载体（不是持久化设置）：p08 用它拼 Modrinth 的 <c>query</c> 与 <c>facets</c>，
/// 结果只放在内存里给当前页面看，**不写进磁盘快照** —— 否则用户下次打开 p08 会看到"上次搜的词"，
/// 而设计稿的默认视图是"从 Modrinth 浏览"的完整列表。
/// </remarks>
public sealed record ModrinthSearchQuery(string? Query, string? GameVersion, string? Loader, ModrinthSort Sort = ModrinthSort.Relevance);

/// <summary>Modrinth 浏览的排序方式（对应官方 search 的 <c>index</c> 参数）。</summary>
public enum ModrinthSort
{
    /// <summary>相关性（默认）。</summary>
    Relevance,

    /// <summary>累计下载量。</summary>
    Downloads,

    /// <summary>关注数。</summary>
    Follows,

    /// <summary>发布时间（新 → 旧）。</summary>
    Newest,

    /// <summary>最近更新。</summary>
    Updated,
}



/// <summary>
/// 内容动作（启用 / 禁用 / 删除 / 优先级）的结果（M7-g-2）。
/// </summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Message">
/// 一句要交给界面的话（键 + 参数）：失败时是**原因**，成功时是需要交代的**去向**
/// （例如"已把它移进相对目录，可以从那里拿回来"）；不需要交代时为 <see langword="null"/>。
/// </param>
/// <remarks>
/// 为什么不是 <see cref="bool"/>：这些动作直接改用户的磁盘（改名、搬到备份目录），
/// 失败原因必须让用户看见 —— 文件被游戏占用、目标名已存在、文件已经不在了是三种完全不同的处置。
/// 只说 <c>false</c> 等于让界面显示"没反应"，而用户会以为点了没生效又点一次。
/// </remarks>
public sealed record ContentActionResult(bool Succeeded, TextTemplate? Message)
{
    /// <summary>成功且无需交代。</summary>
    public static ContentActionResult Success { get; } = new(true, null);

    /// <summary>成功，但要告诉用户结果去了哪里。</summary>
    /// <param name="message">那句话（键 + 参数）。</param>
    /// <returns>结果。</returns>
    public static ContentActionResult Done(TextTemplate message) => new(true, message);

    /// <summary>失败，并给出原因。</summary>
    /// <param name="message">原因（键 + 参数）。</param>
    /// <returns>结果。</returns>
    public static ContentActionResult Failure(TextTemplate message) => new(false, message);
}
