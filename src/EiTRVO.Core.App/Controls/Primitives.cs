using System.Windows;
using System.Windows.Controls;

namespace EiTRVO.Core.App.Controls;

/// <summary>
/// 下拉三角：9×9 小方块，内部是纯三角形。
/// 对应 shell.css 的 <c>.caret</c>；深色底与浅色底两种形态由样式区分
/// （<c>CaretGlyph</c> = 黑底白三角，<c>CaretGlyph.Dark</c> = 透明底深色三角）。
/// </summary>
internal sealed class CaretGlyph : Control
{
    static CaretGlyph()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(CaretGlyph),
            new FrameworkPropertyMetadata(typeof(CaretGlyph)));
    }
}


/// <summary>
/// 内联迷你进度条（10px 描边条 + 蓝色填充 + 右侧百分比）。
/// 对应 shell.css 的 <c>.mini-progress</c>。
/// </summary>
internal sealed class MiniProgressBar : Control
{
    /// <summary><see cref="Value"/> 依赖属性。</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(MiniProgressBar),
            new FrameworkPropertyMetadata(0d));

    /// <summary><see cref="BarWidth"/> 依赖属性。</summary>
    public static readonly DependencyProperty BarWidthProperty =
        DependencyProperty.Register(
            nameof(BarWidth),
            typeof(double),
            typeof(MiniProgressBar),
            new FrameworkPropertyMetadata(150d));

    /// <summary><see cref="ShowPercent"/> 依赖属性。</summary>
    public static readonly DependencyProperty ShowPercentProperty =
        DependencyProperty.Register(
            nameof(ShowPercent),
            typeof(bool),
            typeof(MiniProgressBar),
            new FrameworkPropertyMetadata(true));

    /// <summary><see cref="IsIndeterminate"/> 依赖属性。</summary>
    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(
            nameof(IsIndeterminate),
            typeof(bool),
            typeof(MiniProgressBar),
            new FrameworkPropertyMetadata(false));

    static MiniProgressBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MiniProgressBar),
            new FrameworkPropertyMetadata(typeof(MiniProgressBar)));
    }

    /// <summary>完成百分比（0–100）。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>进度条本身宽度，默认 150（设计稿值）。</summary>
    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    /// <summary>是否在右侧显示百分比文本。</summary>
    public bool ShowPercent
    {
        get => (bool)GetValue(ShowPercentProperty);
        set => SetValue(ShowPercentProperty, value);
    }

    /// <summary>是否为不确定进度（不知道总量时显示循环动画，而不是停在 0%）。</summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }
}
