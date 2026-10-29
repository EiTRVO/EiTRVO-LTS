namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 面包屑的一级。
/// <para>
/// 规则见 mockup/pages/p16-chrome-spec.html §6：
/// 不显示「EiTRVO Core」根节点；省略与页名重复的上一级；只有一级时不显示分隔符；
/// <b>整个面包屑统一细体，末级不加粗</b>。
/// </para>
/// </summary>
/// <param name="Text">显示文本。</param>
/// <param name="Target">点击后跳转的页面；<see langword="null"/> 表示这是当前页（不可点）。</param>
/// <param name="ShowSeparator">是否在本级前面画 <c>›</c> 分隔符（只有一级时不画）。</param>
internal sealed record BreadcrumbPartViewModel(string Text, PageId? Target, bool ShowSeparator)
{
    /// <summary>是否为当前页（末级）。</summary>
    public bool IsCurrent => Target is null;
}
