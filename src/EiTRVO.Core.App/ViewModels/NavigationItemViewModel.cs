using CommunityToolkit.Mvvm.ComponentModel;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 左导航的一项。
/// <para>
/// <see cref="Icon"/> 是 Segoe MDL2 Assets 的字形字符串。它是 <see cref="string"/>，
/// 不是任何 WPF 类型 —— AGENTS.md §2.1/§7 禁止 ViewModel 引用 UI <b>类型</b>，
/// 而「导航项配哪个字形」属于导航结构本身，与「选中哪一项」同源，故一并放在这里。
/// 具体颜色、字号仍在 View 层（Chrome.xaml 的 NavItemButton）。
/// </para>
/// </summary>
internal sealed partial class NavigationItemViewModel : ObservableObject
{
    /// <summary>构造导航项。</summary>
    /// <param name="key">导航项标识，用于判断高亮。</param>
    /// <param name="target">点击后跳转的页面。</param>
    /// <param name="icon">Segoe MDL2 Assets 字形。</param>
    /// <param name="textKey">导航项文字的资源键（简体中文原文）。</param>
    public NavigationItemViewModel(NavKey key, PageId target, string icon, string textKey)
    {
        Key = key;
        Target = target;
        Icon = icon;
        _textKey = textKey;
    }

    private readonly string _textKey;

    /// <summary>导航项标识。</summary>
    public NavKey Key { get; }

    /// <summary>跳转目标。</summary>
    public PageId Target { get; }

    /// <summary>Segoe MDL2 Assets 字形。</summary>
    public string Icon { get; }

    /// <summary>导航项文字（当前语言，现算 —— 这样切语言时左导航会跟着变）。</summary>
    public string Text => Loc.T(_textKey);

    /// <summary>语言切换后由外壳调用，让绑定重新读一次 <see cref="Text"/>。</summary>
    public void RefreshText() => OnPropertyChanged(nameof(Text));

    /// <summary>是否为当前页所在的分域（决定 4px 主色竖条 + 高亮底 + 加粗）。</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// 左导航的一个分组。第一组没有标题（对应设计稿里顶部不分组的那四项）。
/// </summary>
internal sealed class NavigationGroupViewModel : ObservableObject
{
    /// <summary>构造分组。</summary>
    /// <param name="titleKey">分组标题的资源键；<see langword="null"/> 表示该组没有标题行。</param>
    /// <param name="items">组内导航项。</param>
    public NavigationGroupViewModel(string? titleKey, IReadOnlyList<NavigationItemViewModel> items)
    {
        _titleKey = titleKey;
        Items = items;
    }

    private readonly string? _titleKey;

    /// <summary>分组标题（当前语言，现算）。</summary>
    public string? Title => _titleKey is null ? null : Loc.T(_titleKey);

    /// <summary>是否有标题行。</summary>
    public bool HasTitle => _titleKey is not null;

    /// <summary>组内导航项。</summary>
    public IReadOnlyList<NavigationItemViewModel> Items { get; }

    /// <summary>语言切换后由外壳调用，让绑定重新读一次 <see cref="Title"/>。</summary>
    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}
