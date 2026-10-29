using System.Windows;
using System.Windows.Controls;

namespace EiTRVO.Core.App.Controls;

/// <summary>提示条的语气。</summary>
internal enum BannerTone
{
    /// <summary>普通信息（灰边 + 浅灰底）。</summary>
    Info,

    /// <summary>警告（橙边 + 浅橙底）。</summary>
    Warning,

    /// <summary>错误（红边 + 浅红底）。</summary>
    Error,
}

/// <summary>
/// 区块标题：左侧 14×14 语义图标 + 标题（20px 半粗，二级用 14px）+ 副标题 + 右侧工具区。
/// <para>
/// 原图这里是「20px 粗体 + 左侧 14px 灰色方块」。Core 让这个 14px 方块承载
/// 「该区块是什么」的语义图标，**位置与尺寸不变**；未指定图标时回退为原来的灰色方块，
/// 因此不会出现空白（见 AGENTS.md §5.2 与 shell.css 的注释）。
/// </para>
/// <para>
/// 继承 <see cref="ItemsControl"/> 而不是 <see cref="ContentControl"/>：右侧工具区在
/// 设计稿里经常不止一个控件（例如「新建实例」+「任务 ▾」），
/// 后者只接受一个子级，会逼着每个调用点都套一层 StackPanel。
/// </para>
/// </summary>
internal sealed class SectionHeader : ItemsControl
{
    /// <summary><see cref="Icon"/> 依赖属性。</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="Title"/> 依赖属性。</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="Subtitle"/> 依赖属性。</summary>
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(
            nameof(Subtitle),
            typeof(string),
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="TitleSize"/> 依赖属性。</summary>
    public static readonly DependencyProperty TitleSizeProperty =
        DependencyProperty.Register(
            nameof(TitleSize),
            typeof(double),
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(20d));

    /// <summary><see cref="IconSize"/> 依赖属性。</summary>
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(16d));

    static SectionHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(typeof(SectionHeader)));
    }

    /// <summary>语义图标字形（Segoe MDL2 Assets）；为空时显示原图的灰色方块。</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>区块标题。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>副标题（灰色小字），为空时不占位。</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>标题字号，默认 20（一级区块）；二级区块用 14。</summary>
    public double TitleSize
    {
        get => (double)GetValue(TitleSizeProperty);
        set => SetValue(TitleSizeProperty, value);
    }

    /// <summary>图标字号，默认 16。</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
}

/// <summary>
/// 提示条。对应 shell.css 的 <c>.banner</c>：1px 细线框 + 浅色底 + 左侧图标，
/// 可选右侧一个操作按钮（例如模组页的「修复依赖」）。
/// </summary>
internal sealed class Banner : ContentControl
{
    /// <summary><see cref="Icon"/> 依赖属性。</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(Banner),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="Tone"/> 依赖属性。</summary>
    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(
            nameof(Tone),
            typeof(BannerTone),
            typeof(Banner),
            new FrameworkPropertyMetadata(BannerTone.Info));

    /// <summary><see cref="Extra"/> 依赖属性。</summary>
    public static readonly DependencyProperty ExtraProperty =
        DependencyProperty.Register(
            nameof(Extra),
            typeof(object),
            typeof(Banner),
            new FrameworkPropertyMetadata(null));

    static Banner()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(Banner),
            new FrameworkPropertyMetadata(typeof(Banner)));
    }

    /// <summary>Segoe MDL2 Assets 字形。</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>语气，决定边框与底色。</summary>
    public BannerTone Tone
    {
        get => (BannerTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>右侧附加内容（通常是修复类按钮）。</summary>
    public object? Extra
    {
        get => GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }
}
