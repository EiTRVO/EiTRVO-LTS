using System.Text;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 启动命令行的脱敏（C-15）：<c>--accessToken</c> / <c>--uuid</c> 之类的值不得进日志。
/// <para>
/// 为什么必须有这一层，而不是"记得别打印就行"：命令行是**排查启动失败时第一个想看的东西**
/// （p03/p14 的事件表、崩溃报告、用户贴到 issue 里的日志），而它里面正好有访问令牌。
/// 一次复制粘贴就等于把账户交出去；令牌还有效期内可被直接用来登录。
/// 所以这里把"能打印的形状"做成**唯一**的出口：<see cref="LaunchCommandLine"/> 刻意不提供
/// 未脱敏的文本访问器，要文本就只能拿 <c>RedactedCommandText</c>。
/// </para>
/// <para>
/// 三层处理，每一层都对应 Neo 的一个具体缺口：
/// </para>
/// <list type="number">
/// <item><b>按位置脱敏</b>：<c>--accessToken &lt;值&gt;</c> 与 <c>--accessToken=&lt;值&gt;</c> 两种写法都认
/// （Neo 只认前者 —— 一份用合并写法的自定义版本 JSON 会把令牌原样写进日志）。
/// 大小写不敏感：多抹一点没有代价，漏抹一次就是泄漏。</item>
/// <item><b>值清扫</b>：同一个令牌如果被**别处复制**（自定义/整合包版本 JSON 完全可以把
/// <c>${auth_access_token}</c> 放在别的参数里），按位置脱敏抓不到那一份。所以把收集到的敏感值
/// 在整个列表里再扫一遍。清扫只处理**足够长**的值：离线令牌是 <c>0</c>，
/// 全局替换一个字符 <c>0</c> 会把 <c>--width 1280</c> 之类毁掉。</item>
/// <item><b>问题文本清扫</b>：<see cref="LaunchArgumentBuilder"/> 在发现"参数被拒绝"时会记下那条参数
/// —— 而一条恶意参数完全可以是 <c>-javaagent:${auth_access_token}</c>：它会被安全策略拒掉，
/// 然后**带着令牌**出现在事件日志里。这是"让启动器自己把令牌写出来"的攻击，所以在源头抹掉。</item>
/// </list>
/// <para>
/// 刻意**不**脱敏的东西，以及理由：<c>--username</c>（排查"进不去服务器"时必须知道是谁）、
/// <c>--clientId</c>（微软应用注册的公开标识，本来就写在每个请求里）。两者都不是秘密。
/// </para>
/// </summary>
public static class LaunchRedaction
{
    /// <summary>脱敏后的占位文本。</summary>
    public const string Mask = "***";

    /// <summary>参与"值清扫"的最短长度：短于此的值不做全局替换（离线令牌 <c>0</c> 会毁掉整条命令）。</summary>
    public const int MinimumSecretLength = 8;

    /// <summary>需要按位置脱敏的选项（大小写不敏感）。</summary>
    /// <remarks>
    /// <c>--xuid</c> 与 <c>--uuid</c> 同类：都是"这个账户是谁"的标识，而排查启动问题并不需要它。
    /// C-15 只点名了前两个，第三个是顺手按同一标准处理的。
    /// </remarks>
    private static readonly string[] SecretOptions = ["--accessToken", "--uuid", "--xuid"];

    /// <summary>把实参表里的敏感值换成 <see cref="Mask"/>。</summary>
    /// <param name="arguments">实参表（原表不会被修改）。</param>
    /// <returns>脱敏后的实参表（长度与顺序不变）。</returns>
    public static IReadOnlyList<string> Redact(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        List<string> masked = [.. arguments];
        List<string> secrets = [];

        for (var index = 0; index < masked.Count; index++)
        {
            var argument = masked[index];

            // 合并写法：--accessToken=<值>
            if (TrySplitJoinedValue(argument, out var name, out var joined) && IsSecretOption(name))
            {
                secrets.Add(joined);
                masked[index] = name + "=" + Mask;
                continue;
            }

            // 分开写法：--accessToken <值>
            if (!IsSecretOption(argument) || index + 1 >= masked.Count)
            {
                continue;
            }

            var value = masked[index + 1];

            // 下一个词是另一个选项 → 这个选项根本没带值。此时把它当值抹掉，
            // 会让日志少掉一个真实参数（表现是"日志里明明有 --username，界面上却说没有"）。
            if (value.StartsWith('-'))
            {
                continue;
            }

            secrets.Add(value);
            masked[index + 1] = Mask;
            index++;
        }

        return Sweep(masked, secrets);
    }

    /// <summary>脱敏并拼成一条可读的命令行文本（含空白的实参加双引号）。</summary>
    /// <param name="arguments">实参表。</param>
    /// <returns>用于日志/事件的文本。</returns>
    public static string Describe(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var redacted = Redact(arguments);
        var builder = new StringBuilder();

        foreach (var argument in redacted)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            AppendQuoted(builder, argument);
        }

        return builder.ToString();
    }

    /// <summary>把已知的敏感值从任意文本里抹掉（用于问题说明、诊断文本）。</summary>
    /// <param name="text">文本。</param>
    /// <param name="secrets">已知的敏感值（长度不足 <see cref="MinimumSecretLength"/> 的会被忽略）。</param>
    /// <returns>抹掉之后的文本。</returns>
    public static string RedactText(string text, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secrets);

        foreach (var secret in Significant(secrets))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>值清扫：把收集到的敏感值在整张表里再抹一遍。</summary>
    private static List<string> Sweep(List<string> arguments, IEnumerable<string> secrets)
    {
        var significant = Significant(secrets);
        if (significant.Count == 0)
        {
            return arguments;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            foreach (var secret in significant)
            {
                if (argument.Contains(secret, StringComparison.Ordinal))
                {
                    argument = argument.Replace(secret, Mask, StringComparison.Ordinal);
                }
            }

            arguments[index] = argument;
        }

        return arguments;
    }

    /// <summary>够长、去重、并按长度从长到短排好序的敏感值（先抹长的，避免短值把长值切碎）。</summary>
    private static List<string> Significant(IEnumerable<string> secrets) =>
        [.. secrets
            .Where(secret => !string.IsNullOrEmpty(secret) && secret.Length >= MinimumSecretLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)];

    private static bool IsSecretOption(string argument) =>
        SecretOptions.Contains(argument, StringComparer.OrdinalIgnoreCase);

    private static bool TrySplitJoinedValue(string argument, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;

        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        var separator = argument.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 2)
        {
            return false;
        }

        name = argument[..separator];
        value = argument[(separator + 1)..];
        return true;
    }

    private static void AppendQuoted(StringBuilder builder, string argument)
    {
        if (argument.Length == 0)
        {
            builder.Append("\"\"");
            return;
        }

        if (!argument.Contains(' ') && !argument.Contains('"'))
        {
            builder.Append(argument);
            return;
        }

        // 只是为了日志可读：不是 shell 转义（我们从来不把命令行拼成字符串交给 shell）。
        builder.Append('"').Append(argument.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
    }
}
