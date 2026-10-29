using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>版本类型。</summary>
public enum VersionKind
{
    /// <summary>正式版。</summary>
    Release,

    /// <summary>快照。</summary>
    Snapshot,
}

/// <summary>版本在本地的状态。</summary>
public enum VersionLocalState
{
    /// <summary>已安装，可直接用于创建实例。</summary>
    Installed,

    /// <summary>版本已存在但缺所需 Java。</summary>
    MissingJava,

    /// <summary>完全未安装。</summary>
    NotInstalled,
}

/// <summary>版本清单中的一行（对应 <c>version_manifest_v2.json</c> 的条目）。</summary>
/// <param name="Id">版本号，如 <c>1.20.4</c>。</param>
/// <param name="Kind">版本类型。</param>
/// <param name="ReleaseDate">发布日期文本。</param>
/// <param name="RequiredJava">所需 Java 文本，如 <c>Java 21</c>。</param>
/// <param name="LocalState">本地状态。</param>
/// <param name="LocalStateText">本地状态显示文本（键 + 参数：<c>已安装</c> / <c>未安装</c> / <c>缺少 Java {0}</c>）。</param>
/// <param name="IsActive">是否被选中（表格高亮行）。</param>
/// <remarks>
/// **M6-e-3e**：`LocalStateText` 从 `string` 改成「键 + 参数」——`已安装` 是**文案**，
/// 而 `缺少 Java 25` 里的版本号是**数据**（AGENTS.md §5.3 第 ⑩ 条）。
/// 界面不能直接绑这一格，一律经 <c>VersionRowViewModel</c> 投影（第 ⑨ 条）。
/// </remarks>
public sealed record MinecraftVersion(
    string Id,
    VersionKind Kind,
    string ReleaseDate,
    string RequiredJava,
    VersionLocalState LocalState,
    TextTemplate LocalStateText,
    bool IsActive)
{
    /// <summary>已安装的版本可以直接用来建实例，否则只能先下载。</summary>
    public bool CanCreateInstance => LocalState == VersionLocalState.Installed;
}

/// <summary>加载器在向导里的可选性。</summary>
public enum LoaderAvailability
{
    /// <summary>默认推荐（图中为 Forge）。</summary>
    Recommended,

    /// <summary>可用。</summary>
    Available,

    /// <summary>该游戏版本没有对应版本，不可用。</summary>
    Unavailable,

    /// <summary>可用但不推荐（原版装整合包等）。</summary>
    Discouraged,

    /// <summary>可用，但需要人工点击官方安装器界面。</summary>
    NeedsManualStep,

    /// <summary>这次没取到版本列表（网络/端点失败），可以再点一次重试；不等于"该游戏版本没有版本"。</summary>
    Failed,
}

/// <summary>新建实例向导第 2 步的加载器选项。</summary>
/// <param name="Kind">加载器种类（**身份**：界面按它回选，不再拿显示名反查）。</param>
/// <param name="Display">显示名（键 + 参数）。</param>
/// <param name="Detail">说明文本（键 + 参数）。</param>
/// <param name="Availability">可选性。</param>
/// <param name="IsActive">是否已选择。</param>
/// <param name="Versions">可选的加载器版本列表（**纯版本号**，不带「（推荐）」这类装饰）。</param>
/// <param name="RecommendedVersion"><paramref name="Versions"/> 里加载器自己推荐的那一个；没有则为 <see langword="null"/>。</param>
/// <remarks>
/// **M6-e-3d**：`Display` / `Detail` 从 `string` 改成「键 + 参数」——`原版` 与那些说明词都是**文案**，
/// 英文界面上曾经原样露出中文（AGENTS.md §5.3 第 ⑩ 条）。
/// 版本列表则反过来：它里面的字符串会**原样**变成安装请求里的版本段，
/// 所以「（推荐）」这种标记不再进列表，改用 <paramref name="RecommendedVersion"/> 表达，
/// 由 App 层拼显示文本（<c>LoaderOptionViewModel</c>）。
/// </remarks>
public sealed record LoaderOption(
    LoaderKind Kind,
    TextTemplate Display,
    TextTemplate Detail,
    LoaderAvailability Availability,
    bool IsActive,
    IReadOnlyList<string> Versions,
    string? RecommendedVersion = null);

/// <summary>向导步骤状态。</summary>
public enum WizardStepState
{
    /// <summary>已完成。</summary>
    Done,

    /// <summary>当前步骤。</summary>
    Current,

    /// <summary>未开始。</summary>
    Pending,
}

/// <summary>向导步骤条的一项。</summary>
/// <param name="Number">步骤序号，从 1 开始。</param>
/// <param name="Title">步骤名（键 + 参数：`游戏版本` / `模组加载器` / `名称与目录` / `确认并安装`）。</param>
/// <param name="State">状态。</param>
/// <remarks>
/// **M6-e-3e**：`Title` 从 `string` 改成「键 + 参数」。它是**文案**，
/// 而页头副标题（`第 2 步，共 4 步 · 模组加载器`）与步骤条都要读它 ——
/// 界面一律经 <c>WizardStepViewModel</c> 投影（AGENTS.md §5.3 第 ⑨ 条）。
/// </remarks>
public sealed record WizardStep(int Number, TextTemplate Title, WizardStepState State);
