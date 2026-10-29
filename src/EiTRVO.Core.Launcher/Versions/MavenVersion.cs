using System.Globalization;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// Maven 版本号比较（**够用就好**，不是 Maven 版本规范的完整实现）。
/// <para>
/// 只有两个用途：classpath 去重时判断哪个版本更高（C-4 的硬要求），
/// 以及诊断里把两个候选排个序。
/// </para>
/// <para>
/// 为什么要单独写一个比较器，而不用 <see cref="Version.TryParse(string, out Version)"/>：
/// Neo 的做法是"能解析成 <see cref="Version"/> 就按它比，否则退回**字符串序**"，
/// 而字符串序会把 <c>9.10.1</c> 判成比 <c>9.9</c> 小 —— asm 与 lwjgl 的真实版本号正好长这样，
/// 结果是"保最高版本"保住了低的那个。
/// </para>
/// <para>
/// 规则：按 <c>.</c> <c>-</c> <c>_</c> <c>+</c> 切成段逐段比较 ——
/// 两边都是整数按数值比；一边是数字、一边是文字限定符（<c>beta</c> / <c>Final</c> / <c>jre</c>）时**数字更大**；
/// 缺段时对方是数字就当 0（<c>1.0</c> 与 <c>1.0.0</c> 相同），对方是文字限定符时缺段表示**正式版**、更大。
/// </para>
/// </summary>
public static class MavenVersion
{
    /// <summary>版本号里的分隔符。</summary>
    private static readonly char[] Separators = ['.', '-', '_', '+'];

    /// <summary>比较两个版本号。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>小于 0 表示 <paramref name="left"/> 更低，0 表示相同，大于 0 表示更高。</returns>
    public static int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftParts = left.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < length; index++)
        {
            var comparison = ComparePart(
                index < leftParts.Length ? leftParts[index] : null,
                index < rightParts.Length ? rightParts[index] : null);

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int ComparePart(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        var leftIsNumber = TryParse(left, out var leftValue);
        var rightIsNumber = TryParse(right, out var rightValue);

        // 缺段 = 正式版：对方是数字时按 0 看，对方是文字限定符时更大。
        if (left is null)
        {
            return rightIsNumber ? 0L.CompareTo(rightValue) : 1;
        }

        if (right is null)
        {
            return leftIsNumber ? leftValue.CompareTo(0L) : -1;
        }

        if (leftIsNumber && rightIsNumber)
        {
            return leftValue.CompareTo(rightValue);
        }

        if (leftIsNumber)
        {
            return 1;
        }

        if (rightIsNumber)
        {
            return -1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParse(string? text, out long value)
    {
        value = 0;
        return text is not null
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
