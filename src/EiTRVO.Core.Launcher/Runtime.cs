using System.Globalization;
using System.Text.RegularExpressions;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>Java 运行时在检测结果里的状态。</summary>
public enum JavaRuntimeState
{
    /// <summary>已检测到且可用。</summary>
    Ready,

    /// <summary>已安装但当前不适用（如过旧）。</summary>
    Disabled,

    /// <summary>所需版本未检测到，需要获取。</summary>
    Missing,
}

/// <summary>检测到的 Java 运行时。</summary>
/// <param name="Version">版本号，如 <c>17.0.9</c>；未检测到时形如 <c>21</c>。</param>
/// <param name="Bits">位数文本，如 <c>64 位</c>；未知为 <c>—</c>。</param>
/// <param name="Vendor">供应商，如 <c>Eclipse Temurin</c>；未知为 <c>—</c>。</param>
/// <param name="Path">安装路径；未检测到为 <c>未检测到</c>。</param>
/// <param name="SupportedRange">适用游戏版本区间，如 <c>1.18–1.20.4</c>。</param>
/// <param name="State">状态。</param>
/// <param name="IsActive">是否被选中（表格高亮行）。</param>
public sealed record JavaRuntime(
    string Version,
    string Bits,
    string Vendor,
    string Path,
    string SupportedRange,
    JavaRuntimeState State,
    bool IsActive);

/// <summary>检测到的一个 Java 运行时（<b>事实</b>，不含任何界面判断）。</summary>
/// <param name="ExecutablePath">可执行文件路径（Windows 下优先 <c>javaw.exe</c>）。</param>
/// <param name="HomeDirectory">运行时根目录（<c>bin</c> 的上一级），也就是界面上「路径」那一列显示的值。</param>
/// <param name="VersionText">版本号原文，如 <c>17.0.9</c> / <c>1.8.0_421</c>。</param>
/// <param name="MajorVersion">主版本号，如 <c>17</c>。</param>
/// <param name="Vendor">供应商，如 <c>Eclipse Temurin</c>；识别不出时为 <c>—</c>。</param>
/// <param name="Bits">位数（64 / 32）；自报输出里看不出来时为 <c>0</c>。</param>
/// <remarks>
/// 与 <see cref="JavaRuntime"/> 的分工：这里是**检测出来的事实**（M4 启动要用它），
/// <see cref="JavaRuntime"/> 是「某个版本的当前上下文里这一行该显示成什么」——
/// 两者的差别就在"状态"与"适用区间"上，那两项取决于当前选中的游戏版本。
/// </remarks>
public sealed record JavaInstallation(
    string ExecutablePath,
    string HomeDirectory,
    string VersionText,
    int MajorVersion,
    string Vendor,
    int Bits)
{
    /// <summary>位数文本（键 + 参数：与设计稿一致，只写「{0} 位」，认不出写纯数据键「—」）。</summary>
    /// <remarks>
    /// **M6-e-3f**：位数从 `string` 改成「键 + 参数」——`位` 是**文案**（英文界面要写 `-bit`），
    /// 数字是**数据**。界面一律经 <c>JavaSettingsViewModel</c> 的行投影拼成句子。
    /// </remarks>
    public TextTemplate BitsText => Bits switch
    {
        64 => TextTemplate.Of("{0} 位", "64"),
        32 => TextTemplate.Of("{0} 位", "32"),
        _ => TextTemplate.Of("—"),
    };

    /// <summary>该运行时"最低要求就是它"的游戏版本区间文本，如 <c>1.18–1.20.4</c>。</summary>
    public string SupportedGameRange => JavaRequirement.SupportedGameRange(MajorVersion);
}

/// <summary>
/// Minecraft 版本对 Java 的要求（AGENTS.md §6 的规则表）。
/// <para>
/// <b>规则表只是兜底</b>：版本 JSON 里的 <c>javaVersion.majorVersion</c> 是 Mojang 自己写的答案，
/// 本地有那份 JSON 时以它为准（见 <c>FileVersionCatalog</c>）。这里回答的是
/// "还没有 JSON 时该显示什么、该不该判它缺 Java"。
/// </para>
/// <para>
/// 因此本类给的是**区间**而不是单值：<see cref="RequiredMajor"/> 是下限，
/// <see cref="MaxUsableMajor"/> 是上限 —— 1.12.2 及以前用 LWJGL 2.x，
/// Java 9 起删掉了它依赖的 API，装个 Java 17 反而起不来，所以那些版本的上限硬性封在 8。
/// 只判下限会让"本机只有 Java 17"被当成满足 1.12.2，启动时才崩。
/// </para>
/// </summary>
public static class JavaRequirement
{
    /// <summary>还能跑游戏的 Java 下限（Java 7 及以前一律不认）。</summary>
    public const int OldestUsableMajor = 8;

    /// <summary>无上限（新版本不再受 LWJGL 2 的限制）。</summary>
    public const int NoUpperBound = int.MaxValue;

    /// <summary>给定游戏版本所需的 Java 主版本号（下限）。</summary>
    /// <param name="gameVersion">游戏版本号，如 <c>1.20.4</c>。</param>
    /// <returns>所需 Java 主版本号；无法判断时返回 8。</returns>
    public static int RequiredMajor(string gameVersion) => Classify(gameVersion).Required;

    /// <summary>给定游戏版本能用的最高 Java 主版本号（上限；老版本封在 8）。</summary>
    /// <param name="gameVersion">游戏版本号。</param>
    /// <returns>上限主版本号；无法判断时为 <see cref="NoUpperBound"/>。</returns>
    public static int MaxUsableMajor(string gameVersion) => Classify(gameVersion).Maximum;

    /// <summary>某个 Java 主版本是否满足某个游戏版本（下限与上限都要满足）。</summary>
    /// <param name="javaMajor">Java 主版本号。</param>
    /// <param name="gameVersion">游戏版本号。</param>
    /// <returns>满足返回 <see langword="true"/>。</returns>
    public static bool IsSatisfiedBy(int javaMajor, string gameVersion)
    {
        var (required, maximum) = Classify(gameVersion);
        return javaMajor >= required && javaMajor <= maximum;
    }

    /// <summary>某个 Java 主版本是否落在给定的可用区间内（下限与上限都要满足）。</summary>
    /// <param name="javaMajor">Java 主版本号。</param>
    /// <param name="requiredMajor">下限。</param>
    /// <param name="maxUsableMajor">上限。</param>
    /// <returns>满足返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 与 <see cref="IsSatisfiedBy"/> 的分工：那个按**规则表**判断，这个按显式区间判断 ——
    /// 本地版本 JSON 声明的 <c>javaVersion.majorVersion</c> 优先于规则表（C-9），
    /// 这时区间来自声明值，规则表的"这个版本只需 Java 8"就不该再参与判断。
    /// </remarks>
    public static bool IsWithin(int javaMajor, int requiredMajor, int maxUsableMajor) =>
        javaMajor >= requiredMajor && javaMajor <= maxUsableMajor;

    /// <summary>
    /// 某个 Java 主版本"最低要求就是它"的游戏版本区间文本（p10 的「适用版本」列）。
    /// <para>
    /// 语义是**最低要求区间**，不是"这个 Java 能跑的全部版本"：Java 21 当然也能跑 1.16.5，
    /// 但那一格要回答的是"这一行是给哪个世代准备的"，与设计稿的三行（21 / 17 / 8）一致。
    /// </para>
    /// </summary>
    /// <param name="javaMajor">Java 主版本号。</param>
    /// <returns>区间文本；没有对应世代时为 <c>—</c>。</returns>
    public static string SupportedGameRange(int javaMajor) => javaMajor switch
    {
        >= 25 => "26.1+",
        21 => "1.20.5+",
        17 => "1.18–1.20.4",
        16 => "1.17",
        8 => "≤1.16.5",
        _ => "—",
    };

    /// <summary>把版本号分成（下限，上限）两件事。</summary>
    private static (int Required, int Maximum) Classify(string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return (8, NoUpperBound);
        }

        var text = gameVersion.Trim();

        // 年份式版本号（26.1 / 26.4-snapshot-2）：必须要求一个点号，
        // 否则 24w03a、25w14a 这类**周次快照**也会被当成"2 开头"，一律判成 Java 25（实机清单里 753 个快照都会显示 Java 25）。
        if (TryParseDotted(text, out var dottedMajor, out var dottedMinor))
        {
            if (dottedMajor >= 24)
            {
                return (25, NoUpperBound);
            }

            if (dottedMajor == 1)
            {
                return ReleaseByMinor(dottedMinor, PatchOf(text));
            }
        }

        // 周次快照（24w03a / 20w14infinite）：年份决定世代。
        if (TryParseSnapshotYear(text, out var year, out var week))
        {
            if (year >= 24)
            {
                return (21, NoUpperBound);
            }

            if (year >= 21)
            {
                return (17, NoUpperBound);
            }

            // 2020 年第 46 周起是 1.17 的快照（1.17 要求 Java 16）。
            return year == 20 && week >= 46 ? (16, NoUpperBound) : (8, 8);
        }

        // 认不出来的版本 id（如 "Combat Test 8c"、"rd-132211"）：只给下限，**不设上限** ——
        // 上限猜错会把"能跑"判成"缺 Java"，那是会拦住用户的假警报。
        return (8, NoUpperBound);
    }

    /// <summary>1.x 系列的规则（AGENTS.md §6：1.20.5+ 用 21，1.18–1.20.4 用 17）。</summary>
    /// <remarks>
    /// 上限**只保留一条**：1.12.2 及以前用 LWJGL 2，Java 9 删掉了它依赖的 API，那才是真的跑不起来。
    /// 1.13 起是 LWJGL 3，更新的 Java 都能跑 —— 曾经按 Neo 的写法给 1.13–1.20.4 封了个 21 的上限，
    /// 结果"本机只有 Java 25"会被判成"缺少 Java 17"：那是会**拦住能玩的用户**的假警报，
    /// 而"推荐用哪一代"是 p10 的表格要表达的事，不该做成启动前的硬门槛。
    /// </remarks>
    private static (int Required, int Maximum) ReleaseByMinor(int minor, int patch)
    {
        if (minor >= 21)
        {
            return (21, NoUpperBound);
        }

        if (minor == 20)
        {
            // 少了这一层判断就会把 1.20.5 也算成 17 —— 这是单元测试实际抓到的缺陷。
            return patch >= 5 ? (21, NoUpperBound) : (17, NoUpperBound);
        }

        if (minor == 17)
        {
            return (16, NoUpperBound);
        }

        if (minor >= 18)
        {
            return (17, NoUpperBound);
        }

        // 1.13 起换成 LWJGL 3；1.12.2 及以前是 LWJGL 2，硬性封在 8。
        return minor >= 13 ? (8, NoUpperBound) : (8, 8);
    }

    /// <summary>解析带点号的版本号（<c>1.20.4</c> / <c>26.1</c> / <c>26.4-snapshot-2</c>）。</summary>
    /// <remarks>
    /// 必须带点号：<c>24w03a</c> 这类**周次快照**不含点，不能被当成"2 开头的年份版本号"。
    /// 取的是每段开头连续的数字，这样 <c>26.4-snapshot-2</c> 与 <c>1.20.5-pre1</c>
    /// 这种带后缀的真实 id 也能读出来。
    /// </remarks>
    private static bool TryParseDotted(string text, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        var match = DottedPattern.Match(text);
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major)
               && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>取 <c>1.20.4</c> 的补丁号；没有则 0。</summary>
    private static int PatchOf(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return 0;
        }

        var match = LeadingDigits.Match(parts[2]);
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            ? patch
            : 0;
    }

    /// <summary>解析周次快照 id（<c>24w03a</c> → 24 / 3；<c>20w14infinite</c> → 20 / 14）。</summary>
    private static bool TryParseSnapshotYear(string text, out int year, out int week)
    {
        year = 0;
        week = 0;

        var match = SnapshotPattern.Match(text);
        return match.Success
               && int.TryParse(match.Groups[1].Value, out year)
               && int.TryParse(match.Groups[2].Value, out week);
    }

    private static readonly Regex SnapshotPattern = new(@"^(\d{2})w(\d{1,2})", RegexOptions.CultureInvariant);

    /// <summary>带点号的版本号：两段数字，允许后面跟后缀（<c>26.4-snapshot-2</c>）。</summary>
    private static readonly Regex DottedPattern = new(@"^(\d+)\.(\d+)", RegexOptions.CultureInvariant);

    /// <summary>一段文本开头的连续数字。</summary>
    private static readonly Regex LeadingDigits = new(@"^\d+", RegexOptions.CultureInvariant);
}
