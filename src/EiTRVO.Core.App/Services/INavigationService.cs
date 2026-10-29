using EiTRVO.Core.App.ViewModels;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 页面导航。ViewModel-first：调用方只说「去哪个页面」，由组合根决定用哪个 ViewModel、
/// View 由 <c>DataTemplate</c> 映射（AGENTS.md §2.2）。
/// </summary>
internal interface INavigationService
{
    /// <summary>当前页面 ViewModel。</summary>
    PageViewModelBase Current { get; }

    /// <summary>页面切换后触发。</summary>
    event EventHandler<PageViewModelBase>? Navigated;

    /// <summary>导航到指定页面。目标就是当前页时不做任何事。</summary>
    /// <param name="page">页面标识。</param>
    void Navigate(PageId page);
}
