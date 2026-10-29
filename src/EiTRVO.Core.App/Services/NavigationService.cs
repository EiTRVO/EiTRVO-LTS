using EiTRVO.Core.App.ViewModels;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 导航服务实现。
/// <para>
/// 它不自己 <c>new</c> 页面 ViewModel，也不持有 <c>IServiceProvider</c>
/// （那等于把容器当服务定位器用）。解析函数由组合根在 <c>App.OnStartup</c> 里用
/// 显式 switch 构造后注入 —— 谁依赖谁，一眼能看完。
/// </para>
/// </summary>
internal sealed class NavigationService : INavigationService
{
    private readonly Func<PageId, PageViewModelBase> _resolve;
    private PageViewModelBase? _current;

    /// <summary>构造导航服务。</summary>
    /// <param name="resolve">页面解析函数，由组合根提供。</param>
    public NavigationService(Func<PageId, PageViewModelBase> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <inheritdoc />
    public PageViewModelBase Current =>
        _current ?? throw new InvalidOperationException("导航服务尚未完成首次导航。");

    /// <inheritdoc />
    public event EventHandler<PageViewModelBase>? Navigated;

    /// <inheritdoc />
    public void Navigate(PageId page)
    {
        var target = _resolve(page);
        if (ReferenceEquals(target, _current))
        {
            return;
        }

        _current = target;
        Navigated?.Invoke(this, target);
    }
}
