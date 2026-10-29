using EiTRVO.Core.Launcher.Security;

namespace EiTRVO.Core.Launcher.Content.Modpack;

/// <summary>
/// 整合包里的相对路径（<c>files[].path</c> 与 zip 条目名）的校验与归一化（M7-e）。
/// </summary>
/// <remarks>
/// <para>
/// 这是<strong>规范原文点名的攻击面</strong>：<c>files[].path</c> 是"相对实例目录的路径"，
/// 而一个写着 <c>../../Windows/System32/x.dll</c> 或 <c>C:\x.dll</c> 的清单能让启动器
/// 把文件写到实例目录之外。规范给的判据是：不能含 <c>..</c>、不能以盘符或分隔符开头。
/// </para>
/// <para>
/// 本仓库把它做成**两件事一起**：先按上述判据归一化（<c>\</c> 一律当分隔符），
/// 再逐段走 <see cref="PathSafety.IsSafePathSegment"/>（单段名、非法字符、保留设备名、
/// 首尾空格与结尾点）。最后真正落地时还要过一次 <see cref="PathSafety.CombineWithin"/>
/// —— 那一次会额外检查路径上有没有 NTFS 重解析点（junction），字符串检查是看不见它的。
/// </para>
/// <para>
/// 与"单段名"的校验刻意分开：这里的输入是**多段相对路径**（<c>mods/foo.jar</c>），
/// 拿单段规则去套会把所有合法路径都拒掉。
/// </para>
/// </remarks>
public static class ModpackFilePaths
{
    /// <summary>单段的最大长度。</summary>
    public const int MaxSegmentLength = 128;

    /// <summary>路径最多几段（与压缩包扫描器的深度上限一致）。</summary>
    public const int MaxSegments = 10;

    /// <summary>校验并归一化一条相对路径。</summary>
    /// <param name="relativePath">清单或 zip 条目里的路径。</param>
    /// <param name="normalized">归一化后的路径（<c>/</c> 分隔）；失败时为空串。</param>
    /// <param name="segments">路径分段；失败时为空列表。</param>
    /// <returns>可用时返回 <see langword="true"/>。</returns>
    public static bool TryNormalize(
        string? relativePath,
        out string normalized,
        out IReadOnlyList<string> segments)
    {
        normalized = string.Empty;
        segments = [];

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // 反斜杠按分隔符处理（Windows 上打出来的包会用它）；归一化之后只有 `/` 一种分隔符。
        var candidate = relativePath.Trim().Replace('\\', '/');

        // 绝对路径 / 盘符 / UNC：一票否决。
        if (candidate.StartsWith('/') || candidate.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (Path.IsPathRooted(candidate))
        {
            return false;
        }

        var parts = candidate.Split('/', StringSplitOptions.None);

        if (parts.Length == 0 || parts.Length > MaxSegments)
        {
            return false;
        }

        List<string> safe = [];

        foreach (var part in parts)
        {
            // 空段 = `a//b` 或结尾的 `/`（目录条目）；`.` / `..` 由 IsSafePathSegment 拦下。
            if (part.Length == 0)
            {
                return false;
            }

            if (!PathSafety.IsSafePathSegment(part, MaxSegmentLength))
            {
                return false;
            }

            safe.Add(part);
        }

        segments = safe;
        normalized = string.Join('/', safe);
        return true;
    }
}
