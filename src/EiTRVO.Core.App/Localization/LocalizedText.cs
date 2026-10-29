using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Localization;

/// <summary>
/// 把内核给的「模板键 + 参数」拼成**当前语言**的一句话（Q8 的落点）。
/// </summary>
/// <remarks>
/// 内核（<c>net8.0</c>）不参与本地化：它给的 <see cref="TextTemplate"/> 只是键与参数，
/// 句子只能在这一层拼。三处调用点必须都走这里，否则同一个键会在不同页面显示成不同语言：
/// 事件表（<c>FileEventLog</c>）、登录/账户失败提示、以及启动与安装的失败原因。
/// </remarks>
internal static class LocalizedText
{
    /// <summary>按键与参数拼出当前语言的句子。</summary>
    /// <param name="key">模板键（简体中文原文）。</param>
    /// <param name="parameters">参数。</param>
    /// <returns>当前语言的句子。</returns>
    public static string Of(string key, IReadOnlyList<string> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(parameters);

        return parameters.Count == 0
            ? Loc.T(key)
            : Loc.T(key, [.. parameters.Cast<object?>()]);
    }

    /// <summary>按键与参数拼出当前语言的句子；有 <see cref="TextTemplate.Note"/> 时接在它后面。</summary>
    /// <param name="text">内核给的模板。</param>
    /// <returns>当前语言的句子。</returns>
    public static string Of(TextTemplate text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sentence = Of(text.Key, text.Parameters);

        return text.Note is null ? sentence : sentence + " " + Of(text.Note);
    }

    /// <summary>
    /// 把一句已经拼好的话，与内核给的「后面那一句」接起来（拼法与 <see cref="Of(TextTemplate)"/> 里的 Note 一致）。
    /// </summary>
    /// <param name="sentence">已经拼好的句子（例如它自己就是把某个内层模板渲染出来的结果）。</param>
    /// <param name="note">跟在后面的那句话；为空时原样返回 <paramref name="sentence"/>。</param>
    /// <returns>接好的句子。</returns>
    /// <remarks>
    /// 存在的理由（AGENTS.md §5.3 第 ⑤ / ⑦ 条）：内核**渲染不了**自己的模板，所以当一句外壳话里
    /// 要嵌一个内层模板（如"&lt;加载器&gt; · N 个模组 · 上次 &lt;时间&gt;"）时，
    /// 只能由 App 层先把内层拼成当前语言、再把它当参数塞进外壳那句 —— 这个动作就是本方法。
    /// </remarks>
    public static string Concat(string sentence, TextTemplate? note)
    {
        ArgumentNullException.ThrowIfNull(sentence);

        return note is null ? sentence : sentence + " " + Of(note);
    }

    /// <summary>同上；模板为空时返回 <see langword="null"/>（"没有失败原因"这一态要保留）。</summary>
    /// <param name="text">内核给的模板；可以为 <see langword="null"/>。</param>
    /// <returns>当前语言的句子；模板为空时为 <see langword="null"/>。</returns>
    public static string? OrNull(TextTemplate? text) => text is null ? null : Of(text);
}
