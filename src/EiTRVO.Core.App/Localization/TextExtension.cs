using System.Windows.Data;
using System.Windows.Markup;

namespace EiTRVO.Core.App.Localization;

/// <summary>
/// XAML 里的界面文案：<c>Text="{loc:Text 本地实例}"</c>。
/// <para>
/// 它返回的不是字符串而是一个指向 <see cref="ILocalizationService"/> 索引器的
/// <see cref="Binding"/>，所以切换语言时这些文字会**自动**跟着变
/// （服务对 <c>"Item[]"</c> 发了变更通知），不需要重建窗口或资源字典。
/// </para>
/// <para>
/// 参数写中文原文而不是「键」：原文即键，见 <see cref="LocalizationService"/> 的类型注释。
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TextExtension : MarkupExtension
{
    /// <summary>构造空的标记扩展（XAML 用具名参数形式时使用）。</summary>
    public TextExtension()
    {
    }

    /// <summary>按原文构造。</summary>
    /// <param name="text">简体中文原文（资源键）。</param>
    public TextExtension(string text) => Text = text;

    /// <summary>简体中文原文（资源键）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Text}]")
        {
            Source = Loc.Service,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}