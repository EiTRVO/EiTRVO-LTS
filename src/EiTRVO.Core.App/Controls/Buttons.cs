using System.Windows;
using System.Windows.Controls;

namespace EiTRVO.Core.App.Controls;

/// <summary>
/// 「图标 + 文字」按钮。设计稿里几乎所有操作按钮都是这个形状
/// （例如 <c>⚙ 实例设置</c>），统一成一个控件可以保证 22px 高度、6px 间距、
/// 13px 图标尺寸在三处以上不会各写各的。
/// <para>
/// <see cref="Text"/> 为空时只渲染图标，用于筛选行那种 24×22 的小方钮。
/// </para>
/// </summary>
internal sealed class IconButton : Button
{
    /// <summary><see cref="Icon"/> 依赖属性。</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(IconButton),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="Text"/> 依赖属性。</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(IconButton),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="IconSize"/> 依赖属性。</summary>
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(IconButton),
            new FrameworkPropertyMetadata(13d));

    static IconButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(IconButton),
            new FrameworkPropertyMetadata(typeof(IconButton)));
    }

    /// <summary>Segoe MDL2 Assets 字形。</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>按钮文字。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>图标字号，默认 13。</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
}

/// <summary>
/// 深色页头右侧的全局工具按钮（队列 / 添加账户 / 设置 / 帮助）。
/// 比 <see cref="IconButton"/> 多两个槽：未完成任务数徽标、下拉三角。
/// 定义见 mockup/pages/p16-chrome-spec.html §2。
/// </summary>
internal sealed class HeaderTool : Button
{
    /// <summary><see cref="Icon"/> 依赖属性。</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(string),
            typeof(HeaderTool),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="Text"/> 依赖属性。</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(HeaderTool),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="Badge"/> 依赖属性。</summary>
    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(
            nameof(Badge),
            typeof(string),
            typeof(HeaderTool),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary><see cref="ShowCaret"/> 依赖属性。</summary>
    public static readonly DependencyProperty ShowCaretProperty =
        DependencyProperty.Register(
            nameof(ShowCaret),
            typeof(bool),
            typeof(HeaderTool),
            new FrameworkPropertyMetadata(false));

    static HeaderTool()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(HeaderTool),
            new FrameworkPropertyMetadata(typeof(HeaderTool)));
    }

    /// <summary>Segoe MDL2 Assets 字形。</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>按钮文字标签。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>徽标文本；为空表示不显示徽标（对应「未完成任务数为 0 时隐藏」）。</summary>
    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    /// <summary>是否显示下拉三角（只有队列按钮显示）。</summary>
    public bool ShowCaret
    {
        get => (bool)GetValue(ShowCaretProperty);
        set => SetValue(ShowCaretProperty, value);
    }
}

/// <summary>
/// 「任务 ▾」按钮。对应 shell.css 的 <c>.taskbtn</c>，
/// 出现在区块标题右侧与事件表表头。
/// </summary>
internal sealed class TaskButton : Button
{
    /// <summary><see cref="Text"/> 依赖属性。</summary>
    /// <remarks>
    /// 默认值是**空串**，不是 <c>Loc.T("任务")</c>。原因值得写在这里：
    /// 依赖属性的默认值只在类型初始化时求值一次，把 <c>Loc.T(...)</c> 写在这里，
    /// 等于把当时那一刻的语言**烙进控件**——用户在「Java 与设置」里切成 English 之后，
    /// 这些按钮会继续显示中文（截图时若以英文启动，类型初始化时语言已是 en，反而看不出问题）。
    /// 因此文案由每个使用点用 <c>Text="{loc:Text 任务}"</c> 提供：
    /// 那个标记扩展返回的是绑定，能跟着 <c>Item[]</c> 通知实时刷新。
    /// </remarks>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(TaskButton),
            new FrameworkPropertyMetadata(string.Empty));

    static TaskButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(TaskButton),
            new FrameworkPropertyMetadata(typeof(TaskButton)));
    }

    /// <summary>按钮文字。默认空串，文案由使用点的 <c>{loc:Text …}</c> 提供。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
