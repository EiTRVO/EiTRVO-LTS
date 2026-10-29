using System.Windows;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.Views;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// <see cref="IHelpWindowService"/> 的 WPF 实现：创建（或前置）帮助与支持中心窗口。
/// </summary>
/// <remarks>
/// ViewModel 从容器解析（帮助窗口不是"页面"，不经过 <c>INavigationService</c>），
/// 窗口本身只做 <c>DataContext</c> 绑定 —— 与模态框走同一条"ViewModel-first"的路。
/// </remarks>
internal sealed class HelpWindowService : IHelpWindowService
{
    private readonly HelpViewModel _viewModel;
    private Window? _window;

    /// <summary>构造实现。</summary>
    /// <param name="viewModel">帮助中心的 ViewModel（由组合根注入，避免在服务里做服务定位）。</param>
    public HelpWindowService(HelpViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
    }

    /// <inheritdoc />
    public void Show()
    {
        if (_window is { IsVisible: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new HelpWindow
        {
            DataContext = _viewModel,
        };

        // Owner 只能在主窗口**已经显示**之后设置：启动瞬间（例如截图脚本在构造外壳时就打开帮助）
        // 设 Owner 会抛 InvalidOperationException（"无法将 Owner 属性设置为之前未显示的 Window"）。
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }

        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
    }
}
