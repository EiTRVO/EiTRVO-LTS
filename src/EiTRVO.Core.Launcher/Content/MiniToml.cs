using System.Text;

namespace EiTRVO.Core.Launcher.Content;

/// <summary>TOML 里的一个表（<c>[a.b]</c> 或 <c>[[a]]</c>）与它的标量键值。</summary>
/// <param name="Header">表头名字（根表为空串）。</param>
/// <param name="Values">标量键值（重复的表头会出现多次，顺序保留）。</param>
public sealed record TomlSection(string Header, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// 极简 TOML 读取器 —— 只为 <c>META-INF/mods.toml</c> 与 <c>neoforge.mods.toml</c> 服务。
/// </summary>
/// <remarks>
/// <b>刻意不实现完整 TOML</b>（也不引库：AGENTS.md §3.2）。我们只读五个**标量**键
/// （<c>modId</c> / <c>version</c> / <c>displayName</c> / <c>mandatory</c> / <c>versionRange</c>），
/// 而 Forge 的元数据文件里其余内容（描述、作者、数组、多行字符串）都不需要解释。
/// <para>
/// 因此这里的取舍是：**认得的就解析，认不得的跳过**，任何一处看不懂都不影响其它键 ——
/// 一份元数据读不完整最坏的后果是"这一行显示文件名"，而抛异常会让整页模组列表消失。
/// 多行字符串会整体读掉（不解析内容），否则它内部的 <c>=</c> 会被当成新的键值对。
/// </para>
/// </remarks>
public static class MiniToml
{
    /// <summary>单个值的长度上限（多行描述可能很长，读进来只为跳过它）。</summary>
    private const int MaxValueLength = 64 * 1024;

    /// <summary>解析文本。</summary>
    /// <param name="text">TOML 文本。</param>
    /// <returns>按出现顺序排列的表。</returns>
    public static IReadOnlyList<TomlSection> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sections = new List<TomlSection>();
        var currentHeader = string.Empty;
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r').Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                sections.Add(new TomlSection(currentHeader, current));
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                currentHeader = HeaderOf(line);
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            current[key] = ReadValue(line[(separator + 1)..].TrimStart(), lines, ref index);
        }

        sections.Add(new TomlSection(currentHeader, current));
        return sections;
    }

    /// <summary>取第一个指定表头下某个键的值。</summary>
    /// <param name="sections">表。</param>
    /// <param name="header">表头名字。</param>
    /// <param name="key">键名。</param>
    /// <returns>值；不存在时为 <see langword="null"/>。</returns>
    public static string? Value(IReadOnlyList<TomlSection> sections, string header, string key)
    {
        ArgumentNullException.ThrowIfNull(sections);

        foreach (var section in sections)
        {
            if (string.Equals(section.Header, header, StringComparison.Ordinal)
                && section.Values.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string HeaderOf(string line)
    {
        var text = line.Trim();
        var start = text.StartsWith("[[", StringComparison.Ordinal) ? 2 : 1;
        var end = text.EndsWith("]]", StringComparison.Ordinal) ? text.Length - 2
            : text.EndsWith(']') ? text.Length - 1
            : text.Length;

        return end <= start ? string.Empty : text[start..end].Trim();
    }

    private static string ReadValue(string rest, string[] lines, ref int index)
    {
        if (rest.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            return ReadMultiline(rest, lines, ref index, "\"\"\"", unescape: true);
        }

        if (rest.StartsWith("'''", StringComparison.Ordinal))
        {
            return ReadMultiline(rest, lines, ref index, "'''", unescape: false);
        }

        if (rest.StartsWith('"'))
        {
            var body = InsideQuotes(rest, '"');
            return body is null ? rest : Unescape(body);
        }

        if (rest.StartsWith('\''))
        {
            var body = InsideQuotes(rest, '\'');
            return body ?? rest;
        }

        // 裸值（true / 123 / 1.0）：行内注释从这里开始。
        var comment = rest.IndexOf('#', StringComparison.Ordinal);
        return (comment >= 0 ? rest[..comment] : rest).Trim();
    }

    private static string ReadMultiline(string rest, string[] lines, ref int index, string delimiter, bool unescape)
    {
        var builder = new StringBuilder();
        builder.Append(rest, delimiter.Length, rest.Length - delimiter.Length);

        while (true)
        {
            if (TryTake(builder, delimiter, out var text))
            {
                // 开分隔符之后紧跟的那个换行按 TOML 规范要被吃掉（多行字符串不以此开头）。
                return unescape ? Unescape(text).TrimStart('\n') : text.TrimStart('\n');
            }

            // 文件被截断（没有结束分隔符）：当成"这个值没读完"，不把半截内容当成值。
            if (index + 1 >= lines.Length || builder.Length > MaxValueLength)
            {
                return string.Empty;
            }

            index++;
            builder.Append('\n');
            builder.Append(lines[index].TrimEnd('\r'));
        }
    }

    /// <summary>整段里已经有结束分隔符时取它之前的内容。</summary>
    private static bool TryTake(StringBuilder builder, string delimiter, out string text)
    {
        var content = builder.ToString();
        var end = content.IndexOf(delimiter, StringComparison.Ordinal);
        if (end < 0)
        {
            text = string.Empty;
            return false;
        }

        text = content[..end];
        return true;
    }

    /// <summary>取出 <paramref name="quote"/> 里的内容（不算转义）；没有结束引号时返回 <see langword="null"/>。</summary>
    private static string? InsideQuotes(string text, char quote)
    {
        var builder = new StringBuilder();

        for (var index = 1; index < text.Length; index++)
        {
            var current = text[index];

            if (quote == '"' && current == '\\' && index + 1 < text.Length)
            {
                builder.Append(current).Append(text[index + 1]);
                index++;
                continue;
            }

            if (current == quote)
            {
                return builder.ToString();
            }

            builder.Append(current);
        }

        return null;
    }

    /// <summary>还原基本字符串里的转义（只处理 TOML 规定的那几种）。</summary>
    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current != '\\' || index + 1 >= text.Length)
            {
                builder.Append(current);
                continue;
            }

            index++;
            builder.Append(text[index] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                _ => text[index],
            });
        }

        return builder.ToString();
    }
}
