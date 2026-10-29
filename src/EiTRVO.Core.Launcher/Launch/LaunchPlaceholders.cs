using System.Text;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 启动参数里的占位符替换与旧版参数串切分。
/// <para>
/// 占位符表本身由 <see cref="LaunchArgumentBuilder"/> 按请求填（值来自版本 JSON、账户与设置），
/// 这里只负责"怎么替换"，因为这两条规则各有各的坑：
/// </para>
/// <list type="number">
/// <item><b>结构化参数不做空格切分</b>。1.13 起 <c>arguments.game</c> 的每一项就是**一个**实参，
/// 值里含空格（比如装在 <c>C:\Program Files\</c> 下的游戏目录）完全合法。
/// Neo 对每一项都跑了一遍"按空格切分"，于是它不得不给含空格的路径补引号
/// （<c>QuoteIfHasSpace</c>）—— 而补引号是在替一个本来就不该发生的切分打补丁。
/// 我们用 <c>ProcessStartInfo.ArgumentList</c> 起进程，实参逐个传递，不需要也不该有这一层。</item>
/// <item><b>旧版参数串必须切分</b>（1.12 及以前的 <c>minecraftArguments</c> 是一整串），
/// 但切分时要**先认占位符、再认空格**：否则一个含空格的路径会被切碎。
/// 实现是逐字符走一遍模板，遇到 <c>${...}</c> 就把替换值整段塞进当前 token ——
/// 值里的空格因此天然不会被当成分隔符，不需要引号技巧。</item>
/// </list>
/// </summary>
public static class LaunchPlaceholders
{
    /// <summary>用户属性占位符名（C-2 的必需项兜底要按它判断"版本是否声明过这个参数"）。</summary>
    public const string UserProperties = "user_properties";

    /// <summary>日志配置路径占位符名（只出现在 <c>logging.client.argument</c> 里，不是通用占位符）。</summary>
    public const string LoggingPath = "path";

    /// <summary>替换模板里的占位符。</summary>
    /// <param name="template">模板（可能含 <c>${name}</c>）。</param>
    /// <param name="values">占位符表。</param>
    /// <param name="unresolved">表里没有的占位符名（按出现顺序，可能重复）；调用方据此记问题。</param>
    /// <returns>替换结果。**未知占位符原样保留** —— 悄悄替换成空串会让一个坏参数看起来正常。</returns>
    public static string Substitute(
        string template,
        IReadOnlyDictionary<string, string> values,
        out IReadOnlyList<string> unresolved)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        List<string> missing = [];
        var builder = new StringBuilder(template.Length + 32);
        var position = 0;

        while (position < template.Length)
        {
            var open = template.IndexOf("${", position, StringComparison.Ordinal);
            if (open < 0)
            {
                builder.Append(template, position, template.Length - position);
                break;
            }

            var close = template.IndexOf('}', open + 2);
            if (close < 0)
            {
                // 没有闭合的 ${：整段原样保留（坏数据，但不吞掉它）。
                builder.Append(template, position, template.Length - position);
                break;
            }

            builder.Append(template, position, open - position);

            var name = template[(open + 2)..close];
            if (values.TryGetValue(name, out var value))
            {
                builder.Append(value);
            }
            else
            {
                builder.Append(template, open, close - open + 1);
                missing.Add(name);
            }

            position = close + 1;
        }

        unresolved = missing;
        return builder.ToString();
    }

    /// <summary>
    /// 切分旧版参数串（<c>minecraftArguments</c> 与设置里的附加参数都用它）。
    /// </summary>
    /// <param name="template">整串参数（可能含引号与占位符）。</param>
    /// <param name="values">占位符表。</param>
    /// <param name="unresolved">表里没有的占位符名。</param>
    /// <returns>逐个实参。引号只用于分组，不进入结果。</returns>
    public static IReadOnlyList<string> SplitLegacy(
        string template,
        IReadOnlyDictionary<string, string> values,
        out IReadOnlyList<string> unresolved)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        List<string> tokens = [];
        List<string> missing = [];
        var current = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];

            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (character == '$' && index + 1 < template.Length && template[index + 1] == '{')
            {
                var close = template.IndexOf('}', index + 2);
                if (close > 0)
                {
                    var name = template[(index + 2)..close];
                    if (values.TryGetValue(name, out var value))
                    {
                        current.Append(value);
                    }
                    else
                    {
                        current.Append(template, index, close - index + 1);
                        missing.Add(name);
                    }

                    index = close;
                    continue;
                }
            }

            if ((character == ' ' || character == '\t') && !inQuotes)
            {
                Flush(current, tokens);
                continue;
            }

            current.Append(character);
        }

        Flush(current, tokens);

        unresolved = missing;
        return tokens;
    }

    /// <summary>模板里是否引用了某个占位符。</summary>
    /// <param name="template">模板（可为 <see langword="null"/>）。</param>
    /// <param name="placeholderName">占位符名（不含 <c>${}</c>）。</param>
    /// <returns>引用返回 <see langword="true"/>。</returns>
    public static bool References(string? template, string placeholderName)
    {
        ArgumentNullException.ThrowIfNull(placeholderName);

        return !string.IsNullOrEmpty(template)
            && template.Contains("${" + placeholderName + "}", StringComparison.Ordinal);
    }

    private static void Flush(StringBuilder current, List<string> tokens)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }
}
