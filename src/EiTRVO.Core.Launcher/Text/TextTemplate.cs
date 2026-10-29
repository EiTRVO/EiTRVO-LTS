namespace EiTRVO.Core.Launcher.Text;

/// <summary>
/// 一句"要显示给用户的话"的**内核形状**：模板键 + 参数。
/// </summary>
/// <remarks>
/// Q8 定的规矩是「内核给键 + 参数，App 层用 <c>.resx</c> 拼句子」：内核是 <c>net8.0</c>，
/// 不参与本地化，**拼句子只发生在 App 层**（<c>Loc.T</c>）。
/// <para>
/// <b>键就是这句话的简体中文原文</b>（与事件键同一条约定），因此：
/// 有参数时键里必须写成 <c>{0}</c>、<c>{1}</c>… 并与 <see cref="Parameters"/> 一一对应 ——
/// 少一个占位符会让格式化抛异常，多一个占位符会把参数**静默丢掉**（M6-a/M6-b 踩过的那条）。
/// 这条由 <c>UserTextKeyTests</c> 机械对账（键在不在资源里、占位符个数 == 参数个数）。
/// </para>
/// <para>
/// 刻意**不提供**隐式转换：构造点必须显式写成 <see cref="Of(string)"/>，扫描门禁才看得见
/// "这个键是要给用户看的"（隐式转换会让键混在普通字符串里，门禁扫不到就等于没有门禁）。
/// </para>
/// </remarks>
/// <param name="Key">模板键（简体中文原文，同时也是资源键）。</param>
/// <param name="Parameters">参数（按占位符顺序）。</param>
public sealed record TextTemplate(string Key, IReadOnlyList<string> Parameters)
{
    /// <summary>
    /// 跟在后面的一句话（**拼接两句话的唯一合法方式**）。
    /// </summary>
    /// <remarks>
    /// 内核拼不了句子（Q8），但有些地方确实要说两句：例如"安装失败的原因"后面再补一句
    /// "已回滚这次安装写入的 3 项"。把第二句渲染成字符串再拼进第一句的**参数**是错的
    /// （那句中文在英文界面里会露出来）；拼成一个新键也不行（第一句是变长的）。
    /// 所以第二句单独给一个模板，由 App 层在拼完第一句后按语言习惯接上。
    /// </remarks>
    public TextTemplate? Note { get; init; }

    /// <summary>没有参数的一句话。</summary>
    /// <param name="key">模板键（简体中文原文）。</param>
    /// <returns>模板。</returns>
    public static TextTemplate Of(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new TextTemplate(key, []);
    }

    /// <summary>带参数的一句话（键里的 <c>{0}</c>… 个数必须与参数个数一致）。</summary>
    /// <param name="key">模板键（简体中文原文）。</param>
    /// <param name="parameters">参数。</param>
    /// <returns>模板。</returns>
    public static TextTemplate Of(string key, params string[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(parameters);

        return new TextTemplate(key, parameters);
    }

    /// <summary>日志与异常里显示键本身（中文原文，读得懂；参数不拼进来 —— 拼句子是 App 的事）。</summary>
    /// <returns>键。</returns>
    public override string ToString() => Key;

    /// <summary>
    /// 值相等：键、参数（逐个比）与 <see cref="Note"/> 都一样才算相等。
    /// </summary>
    /// <param name="other">另一个模板。</param>
    /// <returns>是否相等。</returns>
    /// <remarks>
    /// **必须自己写**：`record` 自动生成的相等性对 <see cref="IReadOnlyList{T}"/> 用的是引用相等，
    /// 于是 `Of("等待中") == Of("等待中")` 会是 <see langword="false"/> —— 那种"看起来结构化、
    /// 其实永远不相等"的相等性是给后来人挖坑（比对快照、去重、测试断言都会中招）。
    /// </remarks>
    public bool Equals(TextTemplate? other) =>
        other is not null
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && Parameters.Count == other.Parameters.Count
        && Parameters.SequenceEqual(other.Parameters, StringComparer.Ordinal)
        && Equals(Note, other.Note);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Parameters.Count, Note);
}
