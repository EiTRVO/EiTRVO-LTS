using System.Globalization;
using System.Text.RegularExpressions;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary><c>java -version</c> 解析出来的版本信息。</summary>
/// <param name="VersionText">版本号原文，如 <c>17.0.9</c> / <c>1.8.0_421</c>。</param>
/// <param name="MajorVersion">主版本号，如 <c>17</c>。</param>
/// <param name="Vendor">供应商，如 <c>Eclipse Temurin</c>；识别不出时为 <c>—</c>。</param>
/// <param name="Bits">位数（64 / 32）；输出里看不出来时为 <c>0</c>。</param>
public sealed record JavaVersionInfo(string VersionText, int MajorVersion, string Vendor, int Bits);

/// <summary>
/// 解析 <c>java -version</c> 的输出（迁移评估 C-7 版本检测 + **C-8 供应商/位数识别**）。
/// <para>
/// 三件事刻意写在这里而不是散在调用处：
/// </para>
/// <list type="number">
/// <item><b>读 stderr</b>：<c>java -version</c> 把版本写进标准错误（Neo 的实现已经是这样，
/// 迁移评估要求照做 —— 读 stdout 会得到空串，而且看起来像"这个 Java 坏了"）。</item>
/// <item><b>供应商要按"具体优先"匹配</b>：Temurin 的输出里同时含 <c>Temurin</c> 与 <c>OpenJDK</c>，
/// 先匹配 <c>OpenJDK</c> 会把 Eclipse Temurin 全说成 OpenJDK（Neo 缺这个字段，正是 D3 要补的）。</item>
/// <item><b>位数从 <c>Server VM</c> 那行读</b>：只有它是 <c>64-Bit</c> / <c>32-Bit</c> 的可靠来源；
/// 认不出就写 <c>0</c>（界面显示「—」），**不猜**。</item>
/// </list>
/// </summary>
public static class JavaVersionParser
{
    /// <summary>供应商识别表：顺序就是优先级，先匹配到的说了算。</summary>
    private static readonly (string Token, string Vendor)[] Vendors =
    [
        ("temurin", "Eclipse Temurin"),
        ("adoptium", "Eclipse Temurin"),
        ("microsoft", "Microsoft"),
        ("zulu", "Azul Zulu"),
        ("azul", "Azul Zulu"),
        ("corretto", "Amazon Corretto"),
        ("amazon", "Amazon Corretto"),
        ("liberica", "BellSoft Liberica"),
        ("bellsoft", "BellSoft Liberica"),
        ("semeru", "IBM Semeru"),
        ("graalvm", "GraalVM"),
        ("sapmachine", "SAP SapMachine"),
        ("dragonwell", "Alibaba Dragonwell"),
        // Oracle 的发行版会同时出现 "Java(TM) SE Runtime Environment" 与 "Java HotSpot(TM)"，
        // 两条都认；放在最后是因为前面的厂商也可能在输出里提到 HotSpot。
        ("java(tm)", "Oracle"),
        ("hotspot", "Oracle"),
        ("oracle", "Oracle"),
        ("openjdk", "OpenJDK"),
    ];

    /// <summary>版本号：优先取引号里的那段（<c>openjdk version "17.0.9"</c>）。</summary>
    private static readonly Regex QuotedVersion = new(
        @"version\s+""(?<version>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>没有引号时的兜底（个别发行版会去掉引号）。</summary>
    private static readonly Regex BareVersion = new(
        @"version\s+(?<version>\d[^\s,]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex VersionDigits = new(@"^\d+", RegexOptions.CultureInvariant);

    /// <summary>解析输出。</summary>
    /// <param name="output">命令行输出（<c>java -version</c> 写在 stderr 上的那段）。</param>
    /// <returns>版本信息；不是 Java 的版本输出时为 <see langword="null"/>。</returns>
    public static JavaVersionInfo? TryParse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var version = ExtractVersion(output);
        if (version is null || !TryMajor(version, out var major) || major <= 0)
        {
            return null;
        }

        return new JavaVersionInfo(version, major, ExtractVendor(output), ExtractBits(output));
    }

    /// <summary>版本号原文。</summary>
    private static string? ExtractVersion(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = QuotedVersion.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["version"].Value.Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        var bare = BareVersion.Match(output);
        return bare.Success ? bare.Groups["version"].Value.Trim() : null;
    }

    /// <summary>从版本号文本取主版本：<c>1.8.0_421</c> → 8，<c>17.0.9</c> → 17，<c>8u421</c> → 8。</summary>
    private static bool TryMajor(string versionText, out int major)
    {
        major = 0;

        var components = versionText.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            return false;
        }

        // 1.x 形态：主版本是第二段（1.8 → 8，1.7 → 7）。
        if (components[0] == "1" && components.Length >= 2)
        {
            return TryLeadingDigits(components[1], out major);
        }

        return TryLeadingDigits(components[0], out major);
    }

    /// <summary>取字符串开头连续的数字（<c>8u421</c> → 8、<c>21</c> → 21）。</summary>
    private static bool TryLeadingDigits(string text, out int value)
    {
        value = 0;

        var match = VersionDigits.Match(text);
        return match.Success
               && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>供应商：整段输出里按优先级找第一个能认出来的标记。</summary>
    private static string ExtractVendor(string output)
    {
        foreach (var (token, vendor) in Vendors)
        {
            if (output.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return vendor;
            }
        }

        return "—";
    }

    /// <summary>
    /// 位数：先认自报的 <c>64-Bit</c> / <c>32-Bit</c>，再认 <c>Client VM</c>。
    /// </summary>
    /// <remarks>
    /// Java 8 的 32 位发行版**不写** <c>32-Bit</c>，它的 VM 那行是
    /// <c>OpenJDK Client VM (Temurin)(build 25.402-b06, mixed mode)</c> ——
    /// 而 Client VM 就是 32 位的客户端 JVM（64 位一律是 Server VM）。
    /// 少了这一条，32 位 Java 会显示成「—」，而它其实根本跑不了现代版本。
    /// 两个标记都没有时才写 <c>0</c>（界面显示「—」），不猜。
    /// </remarks>
    private static int ExtractBits(string output)
    {
        if (output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase))
        {
            return 64;
        }

        if (output.Contains("32-Bit", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Client VM", StringComparison.OrdinalIgnoreCase))
        {
            return 32;
        }

        return 0;
    }
}
