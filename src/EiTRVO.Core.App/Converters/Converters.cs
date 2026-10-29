using System.Globalization;
using System.Windows;
using System.Windows.Data;
using EiTRVO.Core.Launcher;

namespace EiTRVO.Core.App.Converters;

/// <summary>
/// 把一个长度按固定比例换算成另一个长度（用于纯装饰的水平滚动条条带宽度）。
/// <para>
/// 之所以做成转换器而不是在控件里算：这是纯粹的「显示比例」，没有状态、没有业务含义，
/// 放在 View 层最薄的地方最合适。
/// </para>
/// </summary>
internal sealed class RatioConverter : IValueConverter
{
    /// <summary>目标 = 源 × <see cref="Ratio"/> + <see cref="Offset"/>。</summary>
    public double Ratio { get; set; } = 1d;

    /// <summary>换算后的像素修正量（用于扣掉边框）。</summary>
    public double Offset { get; set; }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double source || double.IsNaN(source))
        {
            return 0d;
        }

        var result = (source * Ratio) + Offset;
        return result > 0d ? result : 0d;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 字符串为空 → 折叠（<see cref="VisibleWhenEmpty"/> = true 时反过来：为空才显示）。
/// <para>
/// 为什么用转换器而不是 <c>DataTrigger Value=""</c>：
/// 在 <c>ControlTemplate.Triggers</c> 里，用
/// <c>{Binding X, RelativeSource={RelativeSource TemplatedParent}}</c> 作数据触发器的
/// 绑定源是**不会生效**的（触发器看起来被求值了，但 Setter 从不应用）。
/// 实测：深色页头的「设置 / 帮助」按钮会一直显示那个本该被隐藏的蓝色徽标空框。
/// 绑定到元素自身的 Visibility 则没有这个坑。
/// </para>
/// </summary>
internal sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    /// <summary>为 true 时「空 → 显示、非空 → 折叠」。</summary>
    public bool VisibleWhenEmpty { get; set; }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is not string text || text.Length == 0;
        var visible = VisibleWhenEmpty ? isEmpty : !isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>bool → Visibility（true = Visible；<see cref="Invert"/> 为 true 时相反）。</summary>
internal sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>是否取反。用于「向导走到最后一步时换一个动作按钮」这类互斥显示。</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        return visible != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 页头下拉的定位：源 = 该按钮**右侧那一组工具**的实际宽度，参数 = 浮层顶边距，返回 <c>Margin(0, 参数, 源 + 12, 0)</c>。
/// <para>
/// 用来把窗口内浮层的右边缘对齐按钮右边缘：页头右侧那一组工具是右对齐的，
/// 所以「按钮右边还有多宽」就等于「它右边那组工具的实际宽度 + 页头右内边距 12」。
/// 按钮宽度会随语言与玩家名变化，不能在 XAML 里写死一个数字。
/// </para>
/// </summary>
internal sealed class MenuRightAlignConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var top = 54d;
        if (parameter is string text)
        {
            _ = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out top);
        }

        var right = value is double width && !double.IsNaN(width) ? width + 12d : 12d;
        return new Thickness(0d, top, right, 0d);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 把 Launcher 层的 <see cref="Tone"/> 映射成主题画刷。
/// <para>
/// 色调（好 / 坏 / 待确认）是 Model 的表达方式，具体颜色是 View 的表达方式；
/// 这个转换器就是这两者之间唯一的桥。颜色本身仍然只在 Theme.xaml 里声明，
/// 这里只做 key 的选择，不写死色值。
/// </para>
/// </summary>
internal sealed class ToneBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Tone.Ok => "Brush.StateOk",
            Tone.Warn => "Brush.StateWarn",
            Tone.Error => "Brush.StateErr",
            Tone.Muted => "Brush.StateOff",
            Tone.Info => "Brush.StateInfo",
            _ => "Brush.Text",
        };

        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
