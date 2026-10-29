using System.Globalization;

// 说明（同样适用于 Versions / Content / Downloads / Events 各文件）：
// 这里除了原始值，还带有若干 **可直接显示的文本**（如 StatusText / LocalStateText / BadgeText）
// 与 **语义色调**（Tone）。这是刻意的：色调表达“好 / 坏 / 待确认”，具体颜色由 View 决定；
// 而这些文本是数据源对自身的汇总（“该实例当前的状态说明”），不是排版。
// 这样 View 不需要为每一行再包一层转换，ViewModel 也能保持轻薄。
//
// M6-e-3c 起这些“可直接显示的文本”一律是 TextTemplate（键 + 参数）：它们的键是简体中文原文，
// 句子由 App 层用 .resx 拼（AGENTS.md §5.3，Q8）。**界面不能直接绑这些字段** ——
// 字符串内插只调 ToString()，拿到的是键、参数全丢（同 §5.3 第 ⑨ 条）。

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Instances;

/// <summary>模组加载器种类。</summary>
public enum LoaderKind
{
    /// <summary>原版，不装任何加载器。</summary>
    Vanilla,

    /// <summary>Forge。</summary>
    Forge,

    /// <summary>Fabric。</summary>
    Fabric,

    /// <summary>NeoForge。</summary>
    NeoForge,

    /// <summary>Quilt。</summary>
    Quilt,

    /// <summary>OptiFine。</summary>
    OptiFine,
}

/// <summary>实例列表 / 快切列表用的概览行。</summary>
/// <param name="Name">实例名，同时是实例目录名。</param>
/// <param name="LoaderDisplay">加载器显示名（键 + 参数）。</param>
/// <param name="ModCount">模组数量（含已禁用的）。</param>
/// <param name="LastLaunched">上次启动时间文本（键 + 参数）。</param>
/// <param name="BadgeText">右侧状态徽标文本（键 + 参数，如 <c>就绪</c> / <c>需要修复</c>）。</param>
/// <param name="BadgeTone">右侧状态徽标色调。</param>
/// <param name="IsActive">是否为当前选中 / 当前实例。</param>
/// <remarks>
/// **M6-e-3d 去掉了原来的 <c>Summary</c>**：摘要 = 「加载器 · N 个模组 · 上次 &lt;时间&gt;」，
/// 而加载器那一段自己就是一个模板 —— 内核渲染不了它，把它当参数传出去只会露出**键**
/// （AGENTS.md §5.3 第 ⑤ / ⑨ 条）。所以内核给三段**原料**，由 App 层先渲染内层再拼句子
/// （<c>DashboardViewModel</c>，见那里的注释）。
/// </remarks>
public sealed record InstanceOverview(
    string Name,
    TextTemplate LoaderDisplay,
    int ModCount,
    TextTemplate LastLaunched,
    TextTemplate BadgeText,
    Tone BadgeTone,
    bool IsActive);

/// <summary>本地实例表格行。</summary>
/// <param name="Name">实例名。</param>
/// <param name="GameVersion">游戏版本显示文本（键 + 参数）；未指定时是 <c>未指定</c> 那个键。</param>
/// <param name="LoaderDisplay">加载器显示名（键 + 参数），如 <c>Forge 49.0.30</c>。</param>
/// <param name="Loader">加载器种类（**数据**：比显示文本可靠，见 §5.3 第 ⑩ 条）。</param>
/// <param name="SizeOnDisk">占用空间文本，如 <c>12.4 GB</c>。</param>
/// <param name="LastLaunched">上次启动时间文本（键 + 参数）。</param>
/// <param name="StatusText">状态列文本（键 + 参数，如 <c>需要修复</c>）。</param>
/// <param name="StatusTone">状态列色调。</param>
/// <param name="IsActive">是否被选中（表格高亮行）。</param>
public sealed record InstanceRecord(
    string Name,
    TextTemplate GameVersion,
    TextTemplate LoaderDisplay,
    LoaderKind Loader,
    string SizeOnDisk,
    TextTemplate LastLaunched,
    TextTemplate StatusText,
    Tone StatusTone,
    bool IsActive);

/// <summary>实例操作入口的去向。语义化的目标，不依赖 App 层的页面枚举。</summary>
public enum InstanceActionTarget
{
    /// <summary>模组管理。</summary>
    Mods,

    /// <summary>资源包 / 存档。</summary>
    Resources,

    /// <summary>实例设置（逐实例）。</summary>
    InstanceSettings,

    /// <summary>下载与队列。</summary>
    Downloads,
}

/// <summary>实例详情页的一项操作入口。</summary>
/// <param name="Title">入口标题（键 + 参数），如 <c>模组管理</c>。</param>
/// <param name="Summary">入口摘要（键 + 参数），由数据源给出。</param>
/// <param name="Target">目标（语义化）。</param>
public sealed record InstanceAction(TextTemplate Title, TextTemplate Summary, InstanceActionTarget Target);

/// <summary>实例内容的数量统计（删除确认框、列表摘要用）。</summary>
/// <param name="Mods">模组数量（含已禁用的）。</param>
/// <param name="ResourcePacks">资源包数量。</param>
/// <param name="Saves">存档数量。</param>
public sealed record InstanceCounts(int Mods, int ResourcePacks, int Saves);

/// <summary>
/// 启动一个实例所需的**原始**元数据。
/// <para>
/// 与 <see cref="InstanceDetail"/> 的区别很重要：那是给界面看的（版本是 <c>未指定</c>、
/// 加载器是 <c>Forge 49.0.30</c> 这样的文本），而启动要的是能直接用的值 ——
/// 从 <c>LoaderDisplay</c> 里反解加载器种类与版本是"拿显示文本当数据用"，
/// 一旦显示文案为了本地化改一个字，启动就跟着坏。
/// </para>
/// </summary>
/// <param name="Name">实例名。</param>
/// <param name="GameVersion">游戏版本；未指定时为空串。</param>
/// <param name="Loader">加载器种类。</param>
/// <param name="LoaderVersion">加载器版本；原版为 <see langword="null"/>。</param>
/// <param name="IsolatedDirectory">是否启用隔离目录。</param>
/// <param name="InstancePath">该实例的目录（命令行里的 <c>${game_directory}</c>）。</param>
/// <param name="GameDirectory">游戏根目录（<c>assets</c> / <c>libraries</c> / <c>versions</c> 的基准）。</param>
/// <param name="Account">绑定的账户名；未绑定为 <see langword="null"/>。</param>
public sealed record InstanceLaunchInfo(
    string Name,
    string GameVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    bool IsolatedDirectory,
    string InstancePath,
    string GameDirectory,
    string? Account);

/// <summary>新建实例的请求（M1 只落盘元数据；游戏文件由安装流程在 M2 补齐）。</summary>
/// <param name="Name">实例名，同时是目录名。</param>
/// <param name="GameVersion">游戏版本。</param>
/// <param name="Loader">加载器种类。</param>
/// <param name="LoaderVersion">加载器版本；原版为 <see langword="null"/>。</param>
/// <param name="IsolatedDirectory">是否启用隔离目录。</param>
public sealed record InstanceCreationRequest(
    string Name,
    string GameVersion,
    LoaderKind Loader,
    string? LoaderVersion = null,
    bool IsolatedDirectory = true);

/// <summary>删除实例的选项（对应 p13 确认框上的两个勾选）。</summary>
/// <param name="KeepSavesAndConfig">保留存档与配置（默认勾选）。</param>
/// <param name="DeleteAccountBinding">同时解除该实例的账户绑定。</param>
public sealed record InstanceDeletionOptions(
    bool KeepSavesAndConfig = true,
    bool DeleteAccountBinding = false);

/// <summary>删除结果。</summary>
/// <param name="InstanceName">被删除的实例名。</param>
/// <param name="RecyclePath">回收条目目录（内容都在这里，可回滚）。</param>
/// <param name="PreservedDirectory">保留的存档与配置所在目录；未保留时为 <see langword="null"/>。</param>
/// <param name="AccountBinding">删除前绑定的账户；未绑定或未要求解除时为 <see langword="null"/>。</param>
public sealed record InstanceDeletionResult(
    string InstanceName,
    string RecyclePath,
    string? PreservedDirectory,
    string? AccountBinding);


/// <summary>实例详情（属性页 + 操作入口）。</summary>
/// <remarks>
/// <b>M6-e-3c</b>：展示字段（版本、Java、账户、模组摘要、时长、窗口、内存、退出码、隔离目录）
/// 一律是「键 + 参数」（<see cref="TextTemplate"/>），由 App 层的
/// <c>InstanceDetailPanelViewModel</c> 拼成句子 —— 直接绑这些字段会显示**键**（AGENTS.md §5.3 第 ⑨ 条）。
/// <para>
/// 想拿**原始值**（不是给人看的）请走 <see cref="InstanceLaunchInfo"/> /
/// <c>IInstanceCatalog.GetLaunchInfo</c>：那是启动参数要用的那一份。
/// </para>
/// </remarks>
/// <param name="Name">实例名。</param>
/// <param name="GameVersion">游戏版本显示文本（键 + 参数；未指定时是 <c>未指定</c>）。</param>
/// <param name="LoaderDisplay">加载器显示名（键 + 参数）。</param>
/// <param name="JavaRuntime">Java 运行时显示文本（键 + 参数），用于实例详情页。</param>
/// <param name="JavaRuntimeSummary">Java 运行时的简写（键 + 参数），用于仪表板那种概览位置。</param>
/// <param name="GameDirectory">游戏根目录。</param>
/// <param name="InstancePath">该实例的目录（隔离目录时为 versions 下的实例目录）。</param>
/// <param name="LastLaunched">上次启动时间文本（键 + 参数）。</param>
/// <param name="ResourceVerification">资源校验状态文本（键 + 参数）。</param>
/// <param name="Account">绑定的账户显示名（键 + 参数；未绑定时是 <c>未绑定</c>）。</param>
/// <param name="MemoryMb">内存分配（MB）。</param>
/// <param name="SizeOnDisk">占用空间文本。</param>
/// <param name="ModSummary">模组数量摘要（键 + 参数），如 <c>87 个 (已启用 84)</c>。</param>
/// <param name="PlayTime">累计游玩时长文本（键 + 参数）。</param>
/// <param name="LaunchCount">启动次数。</param>
/// <param name="LastExitCode">最后退出码，0 表示正常。</param>
/// <param name="WindowMode">游戏窗口文本（键 + 参数），如 <c>1920 × 1080 窗口化</c>。</param>
/// <param name="IsolatedDirectory">是否启用隔离目录。</param>
public sealed record InstanceDetail(
    string Name,
    TextTemplate GameVersion,
    TextTemplate LoaderDisplay,
    TextTemplate JavaRuntime,
    TextTemplate JavaRuntimeSummary,
    string GameDirectory,
    string InstancePath,
    TextTemplate LastLaunched,
    TextTemplate ResourceVerification,
    TextTemplate Account,
    int MemoryMb,
    string SizeOnDisk,
    TextTemplate ModSummary,
    TextTemplate PlayTime,
    int LaunchCount,
    int LastExitCode,
    TextTemplate WindowMode,
    bool IsolatedDirectory)
{
    /// <summary>内存分配的显示文本（纯数据键 <c>{0} MB</c>：数字 + 国际单位，两种语言逐字相同）。</summary>
    public TextTemplate MemoryText =>
        TextTemplate.Of("{0} MB", MemoryMb.ToString(CultureInfo.InvariantCulture));

    /// <summary>最后退出码的显示文本（<c>0 (正常退出)</c> / <c>1 (异常退出)</c>）。</summary>
    public TextTemplate LastExitCodeText => TextTemplate.Of(
        LastExitCode == 0 ? "{0} (正常退出)" : "{0} (异常退出)",
        LastExitCode.ToString(CultureInfo.InvariantCulture));

    /// <summary>最后退出码的色调。</summary>
    public Tone LastExitCodeTone => LastExitCode == 0 ? Tone.Ok : Tone.Error;

    /// <summary>隔离目录的显示文本（<c>已启用</c> / <c>未启用</c>）。</summary>
    public TextTemplate IsolatedDirectoryText => TextTemplate.Of(IsolatedDirectory ? "已启用" : "未启用");
}
