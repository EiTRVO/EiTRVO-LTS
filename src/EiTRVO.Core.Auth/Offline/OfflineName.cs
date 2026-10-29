namespace EiTRVO.Core.Auth.Offline;

/// <summary>玩家名不合规的原因。</summary>
/// <remarks>
/// 用枚举而不是直接给一句提示文本：内核报出的是**事实**（哪条规则不满足），
/// 具体怎么说由界面按当前语言决定（AGENTS.md §5.3：非 UI 层的文本属于数据，不纳入本地化）。
/// </remarks>
public enum OfflineNameIssue
{
    /// <summary>合规。</summary>
    None,

    /// <summary>空或只有空白。</summary>
    Empty,

    /// <summary>太短。</summary>
    TooShort,

    /// <summary>太长。</summary>
    TooLong,

    /// <summary>含有字母、数字、下划线之外的字符。</summary>
    IllegalCharacter,
}

/// <summary>
/// 离线玩家名的规则（D-6）。
/// <para>
/// 规则与 Minecraft 的正版用户名一致：<b>3–16 位，只允许字母、数字与下划线</b>。
/// 离线模式本来没有服务端来校验，但这个名字会：
/// </para>
/// <list type="number">
/// <item>原样进 <c>--username</c>（带空格或引号会让命令行参数错位）；</item>
/// <item>参与 UUID 派生（名字一变，存档里的玩家数据就对不上了）；</item>
/// <item>被服务器在离线模式下与它自己算出的名字比对（超长名字会被踢）。</item>
/// </list>
/// </summary>
public static class OfflineName
{
    /// <summary>最短长度。</summary>
    public const int MinLength = 3;

    /// <summary>最长长度（与正版用户名一致）。</summary>
    public const int MaxLength = 16;

    /// <summary>校验玩家名。</summary>
    /// <param name="playerName">玩家名。</param>
    /// <returns>不合规的原因；合规时为 <see cref="OfflineNameIssue.None"/>。</returns>
    public static OfflineNameIssue Validate(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return OfflineNameIssue.Empty;
        }

        // 首尾空白**不算**输入错误的一种"宽容情形"：名字会进命令行与 UUID 派生，
        // 静默 trim 会得到"看起来一样、UUID 不一样"的两个账号。调用方先 Trim 再校验。
        var name = playerName;
        if (name.Length < MinLength)
        {
            return OfflineNameIssue.TooShort;
        }

        if (name.Length > MaxLength)
        {
            return OfflineNameIssue.TooLong;
        }

        foreach (var c in name)
        {
            var legal = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';
            if (!legal)
            {
                return OfflineNameIssue.IllegalCharacter;
            }
        }

        return OfflineNameIssue.None;
    }

    /// <summary>玩家名是否合规。</summary>
    /// <param name="playerName">玩家名。</param>
    /// <returns>合规返回 <see langword="true"/>。</returns>
    public static bool IsValid(string? playerName) => Validate(playerName) == OfflineNameIssue.None;
}
