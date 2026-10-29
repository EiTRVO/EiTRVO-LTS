using System.ComponentModel;
using System.Globalization;

namespace EiTRVO.Core.App.Localization;

/// <summary>语言条目。名字用该语言自己书写，不参与翻译。</summary>
/// <param name="Tag">BCP-47 标签（<c>zh-Hans</c> / <c>en</c>），同时是设置里存的值。</param>
/// <param name="DisplayName">下拉里显示的名字。</param>
public sealed record LanguageInfo(string Tag, string DisplayName);

/// <summary>
/// 界面文案查找服务。
/// <para>
/// ViewModel 通过 <see cref="Loc"/> 静态门面使用它；XAML 通过 <c>{loc:Text 原文}</c> 标记扩展
/// 绑到 <see cref="this[string]"/> 索引器。两者都指向同一个实例，所以切换语言是**全局实时**的。
/// </para>
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>当前语言标签。</summary>
    string CurrentLanguage { get; }

    /// <summary>当前区域。</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>可选语言。</summary>
    IReadOnlyList<LanguageInfo> Languages { get; }

    /// <summary>按原文取当前语言的文案（供 XAML 索引器绑定使用）。</summary>
    /// <param name="text">简体中文原文，同时也是资源键。</param>
    /// <returns>当前语言的文案；查不到则返回原文。</returns>
    string this[string text] { get; }

    /// <summary>切换语言。</summary>
    /// <param name="language">语言标签；无法识别时回落到简体中文。</param>
    void SetLanguage(string language);

    /// <summary>按原文取文案。</summary>
    /// <param name="text">简体中文原文（资源键）。</param>
    /// <returns>当前语言的文案。</returns>
    string Translate(string text);

    /// <summary>按原文取带占位符的文案并格式化。</summary>
    /// <param name="format">含 <c>{0}</c> 占位符的中文原文（资源键）。</param>
    /// <param name="arguments">占位符实参。</param>
    /// <returns>当前语言的文案。</returns>
    string Translate(string format, params object?[] arguments);
}