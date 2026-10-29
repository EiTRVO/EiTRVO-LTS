using System.Globalization;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// 测试侧的「键 + 参数 → 句子」渲染器。
/// </summary>
/// <remarks>
/// 内核给的是 <see cref="TextTemplate"/>（键 + 参数），句子本该由 App 层用资源表拼（Q8）。
/// 内核测试里没有资源表，但**键就是简体中文原文**，所以按不变文化格式化就能得到与中文界面一致的那句话。
/// <para>
/// 用它只有两个场景：**给断言当消息**（<c>Assert.IsTrue(x, Text.Of(failure))</c>）与
/// **包含片段**的断言。要钉"到底是哪一个键、参数是什么"的用例一律直接断言
/// <c>.Key</c> / <c>.Parameters</c> —— 那才是这一步（M6-e）真正要保护的东西。
/// </para>
/// </remarks>
internal static class TestText
{
    /// <summary>把模板渲染成中文句子；模板为空时为 <see cref="string.Empty"/>。</summary>
    /// <param name="text">模板。</param>
    /// <returns>句子。</returns>
    public static string Of(TextTemplate? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var sentence = text.Parameters.Count == 0
            ? text.Key
            : string.Format(CultureInfo.InvariantCulture, text.Key, [.. text.Parameters.Cast<object?>()]);

        // Note 是"跟在后面的一句话"（App 层也是这么拼的，见 LocalizedText.Of）——
        // 测试里漏掉它，"失败原因里写了回滚"这类断言就会假红。
        return text.Note is null ? sentence : sentence + " " + Of(text.Note);
    }

    /// <summary>同上；渲染结果为空时用 <paramref name="fallback"/>。</summary>
    /// <param name="text">模板。</param>
    /// <param name="fallback">兜底文本。</param>
    /// <returns>句子。</returns>
    public static string Or(TextTemplate? text, string fallback)
    {
        var rendered = Of(text);

        return rendered.Length == 0 ? fallback : rendered;
    }
}
