using System.Globalization;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Instances;

/// <summary>
/// 加载器与实例名的文本规则。
/// <para>
/// **M6-e-3d 修正了这里原来的一条判断**（M1 时写的是"这里的文本是**数据**、不纳入 `.resx`"）：
/// `原版` 是**文案**（英文界面要显示 Vanilla），`Forge` / `NeoForge` 这些是专有名词（纯数据键）——
/// 两者都走「键 + 参数」，判据同 AGENTS.md §5.3 第 ⑩ 条。
/// </para>
/// <para>
/// 真正属于**数据**的只有两样：**实例名**（目录名，见 <see cref="SuggestUniqueName"/>）与
/// **日志 / 事件里的加载器名**（<see cref="Token"/> / <see cref="DataDisplay"/>）——
/// 后者必须两种语言一致，否则同一个事件在中英两种界面下读起来是两回事。
/// </para>
/// </summary>
public static class InstanceNaming
{
    /// <summary>实例名里版本与加载器之间的分隔符（设计稿里的实例名形如 <c>1.20.4-Forge</c>）。</summary>
    public const char NameSeparator = '-';

    /// <summary>加载器的短名（用于拼实例名 / 目录名 / 日志与事件参数）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>短名（枚举名，如 <c>Forge</c> / <c>Vanilla</c>）。</returns>
    public static string Token(LoaderKind kind) => kind.ToString();

    /// <summary>加载器的显示名（键 + 参数）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns><c>原版</c> 那个键，或纯数据键 <c>{0}</c>（专有名词原样显示）。</returns>
    public static TextTemplate Display(LoaderKind kind) =>
        kind == LoaderKind.Vanilla
            ? TextTemplate.Of("原版")
            : TextTemplate.Of("{0}", kind.ToString());

    /// <summary>带版本的加载器显示名（键 + 参数），如 <c>Forge 49.0.30</c>。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="version">加载器版本；空表示只给显示名。</param>
    /// <returns>键 + 参数。</returns>
    /// <remarks>
    /// 版本号是**数据**、专有名词也是数据，所以这里没有可翻译的词 —— 用**纯数据键** <c>{0} {1}</c>，
    /// 两种语言逐字相同；也就不会出现"把中文塞进参数"（第 ③ 条）。
    /// </remarks>
    public static TextTemplate DisplayWithVersion(LoaderKind kind, string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? Display(kind)
            : TextTemplate.Of("{0} {1}", kind.ToString(), version.Trim());

    /// <summary>
    /// 日志与事件参数里的加载器名（**纯数据**：ASCII、两种语言一致）。
    /// </summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="version">加载器版本；空表示只给短名。</param>
    /// <returns>如 <c>Vanilla</c> / <c>Forge 49.0.30</c>。</returns>
    /// <remarks>
    /// 与 <see cref="Display"/> 是**刻意分开的两件事**：显示名是文案（`原版` → `Vanilla`），
    /// 而事件参数、失败原因里的那一份是排障数据 —— 它必须稳定、与语言无关。
    /// 拿它当参数也正好满足第 ③ 条（参数原样显示，不许有中文字面量）。
    /// </remarks>
    public static string DataDisplay(LoaderKind kind, string? version = null) =>
        string.IsNullOrWhiteSpace(version) ? Token(kind) : $"{Token(kind)} {version.Trim()}";

    /// <summary>
    /// 由「游戏版本 + 加载器」推出一个**不与现有实例重名**的实例名。
    /// <para>
    /// 向导的第 3 步（名称与目录）在设计稿里没有画输入控件，因此名称由版本与加载器推导；
    /// 设计稿的实例名正好就是这个形状（<c>1.20.4-Forge</c>）。重名时补 <c>-2</c>、<c>-3</c>。
    /// </para>
    /// </summary>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loader">加载器种类。</param>
    /// <param name="existingNames">已有实例名。</param>
    /// <returns>建议的实例名。</returns>
    public static string SuggestUniqueName(
        string gameVersion,
        LoaderKind loader,
        IEnumerable<string>? existingNames = null)
    {
        var version = string.IsNullOrWhiteSpace(gameVersion) ? "未指定版本" : gameVersion.Trim();
        var stem = $"{version}{NameSeparator}{Token(loader)}";

        return Unique(stem, existingNames);
    }

    /// <summary>
    /// 由**整合包名**推出一个不与现有实例重名的实例名（M7-e-2）。
    /// </summary>
    /// <param name="packName">清单里的整合包名（远端数据，不可信）。</param>
    /// <param name="gameVersion">清单里声明的游戏版本。</param>
    /// <param name="loader">清单里声明的加载器。</param>
    /// <param name="existingNames">已有实例名。</param>
    /// <returns>建议的实例名。</returns>
    /// <remarks>
    /// 用包名而不是"版本-加载器"：导入出来的实例在列表里叫 <c>Fabulously Optimized</c> 才有意义，
    /// 叫 <c>1.20.4-Fabric</c> 的话用户根本认不出它是哪个包。
    /// <para>
    /// 但包名是**远端来的字符串**，不能直接当目录名：过长、含 <c>:</c> / <c>/</c>、首尾空格、
    /// 保留设备名（<c>CON</c>）都必须被挡下 —— 规则交给
    /// <see cref="Storage.LauncherPaths.IsValidInstanceName"/>（与 <c>CreateInstance</c> 用的是同一条），
    /// 不合法就退回 <see cref="SuggestUniqueName"/> 的"版本-加载器"形状。
    /// </para>
    /// </remarks>
    public static string SuggestUniqueNameForPack(
        string? packName,
        string gameVersion,
        LoaderKind loader,
        IEnumerable<string>? existingNames = null)
    {
        var trimmed = (packName ?? string.Empty).Trim();

        // 长度上限与 CreateInstance 一致（超出就退回，而不是截断：截断出来的名字是**另一个**包）。
        return Storage.LauncherPaths.IsValidInstanceName(trimmed)
            ? Unique(trimmed, existingNames)
            : SuggestUniqueName(gameVersion, loader, existingNames);
    }

    /// <summary>重名时补 <c>-2</c>、<c>-3</c>。</summary>
    /// <param name="stem">基础名。</param>
    /// <param name="existingNames">已有实例名。</param>
    /// <returns>可用的名字。</returns>
    private static string Unique(string stem, IEnumerable<string>? existingNames)
    {
        var taken = new HashSet<string>(existingNames ?? [], StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem))
        {
            return stem;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{stem}{NameSeparator}{index.ToString(CultureInfo.InvariantCulture)}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 清掉加载器版本串里的附加说明（如 <c>49.0.30（推荐）</c> → <c>49.0.30</c>）。
    /// <para>
    /// 界面上的版本列表会带「（推荐）」这类标记，但元数据里存的必须是纯版本号 ——
    /// 否则之后拿它去拼加载器安装地址、或与版本清单比较时全都不匹配。
    /// M6-e-3d 之后内核自己已经不再往版本串上贴标记（改成 <c>LoaderOption.RecommendedVersion</c>，
    /// 由 App 层拼显示文本），这里保留有两个用处：**空串 → <see langword="null"/>**，
    /// 以及将来再出现装饰时的兜底。
    /// </para>
    /// </summary>
    /// <param name="version">版本串。</param>
    /// <returns>纯版本号；输入为空时返回 <see langword="null"/>。</returns>
    public static string? CleanLoaderVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var cleaned = version.Trim();
        var marker = cleaned.IndexOf('（', StringComparison.Ordinal);
        if (marker < 0)
        {
            marker = cleaned.IndexOf('(', StringComparison.Ordinal);
        }

        if (marker >= 0)
        {
            cleaned = cleaned[..marker].Trim();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }
}
