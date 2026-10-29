using System.Text.RegularExpressions;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 求值规则时用的「当前平台」。
/// <para>
/// 刻意做成**数据**而不是直接调 <c>OperatingSystem.IsWindows()</c>（Neo 的做法）：
/// 目标平台只有 Windows 没错，但"这条 macOS 专属的库该不该下载"是一个必须能被断言的判断 ——
/// 直接读进程所在系统的话，除非真在 mac 上跑，否则永远测不到"排除 osx 库"这条分支。
/// </para>
/// </summary>
/// <param name="OsName">系统名（<c>windows</c> / <c>osx</c> / <c>linux</c>）。</param>
/// <param name="OsArch">架构（<c>x86</c> / <c>x86_64</c> / <c>arm64</c>）。</param>
/// <param name="OsVersion">系统版本号（用于规则里的 <c>os.version</c> 正则）。</param>
/// <param name="Features">已启用的特性（安装时为空；启动时由 M4 按用户选择填）。</param>
public sealed record GamePlatform(
    string OsName,
    string OsArch,
    string OsVersion,
    IReadOnlyDictionary<string, bool> Features)
{
    /// <summary>规则的 <c>os.version</c> 正则匹配超时。</summary>
    /// <remarks>
    /// 正则来自**远端清单**，所以必须给超时：一个灾难性回溯的表达式足以让安装卡死。
    /// 超时按"这条规则不匹配"处理（宁可少装一个库，也不要卡住）。
    /// </remarks>
    public static readonly TimeSpan VersionRegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>当前进程所在平台（安装时用；特性为空）。</summary>
    public static GamePlatform Current { get; } = new(
        OsName: OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        OsArch: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x86_64",
        },
        OsVersion: Environment.OSVersion.Version.ToString(),
        Features: new Dictionary<string, bool>(StringComparer.Ordinal));

    /// <summary>Windows x64（本产品的目标平台；测试与安装流程用它，不依赖跑测试的机器）。</summary>
    public static GamePlatform WindowsX64 { get; } = new(
        "windows",
        "x86_64",
        "10.0",
        new Dictionary<string, bool>(StringComparer.Ordinal));

    /// <summary>该操作系统名是否与规则里的名字相符。</summary>
    /// <param name="ruleOsName">规则里的系统名。</param>
    /// <returns>相符返回 <see langword="true"/>。</returns>
    public bool MatchesOsName(string? ruleOsName) =>
        string.IsNullOrEmpty(ruleOsName)
        || string.Equals(ruleOsName, OsName, StringComparison.OrdinalIgnoreCase);

    /// <summary>该架构是否与规则里的架构相符。</summary>
    /// <param name="ruleArch">规则里的架构。</param>
    /// <returns>相符返回 <see langword="true"/>。</returns>
    public bool MatchesArch(string? ruleArch)
    {
        if (string.IsNullOrEmpty(ruleArch))
        {
            return true;
        }

        // 规则里出现过 x86 / x86_64 / arm64 / arm32 四种写法；x86 的含义是"32 位"。
        return ruleArch.ToLowerInvariant() switch
        {
            "x86" => string.Equals(OsArch, "x86", StringComparison.OrdinalIgnoreCase),
            "x86_64" or "amd64" => string.Equals(OsArch, "x86_64", StringComparison.OrdinalIgnoreCase),
            "arm64" or "aarch64" => string.Equals(OsArch, "arm64", StringComparison.OrdinalIgnoreCase),
            "arm32" => string.Equals(OsArch, "arm32", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    /// <summary>系统版本是否匹配规则里的正则。</summary>
    /// <param name="pattern">正则（可为空）。</param>
    /// <returns>匹配返回 <see langword="true"/>；表达式非法或超时按不匹配处理。</returns>
    public bool MatchesOsVersion(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        try
        {
            return Regex.IsMatch(OsVersion, pattern, RegexOptions.None, VersionRegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
