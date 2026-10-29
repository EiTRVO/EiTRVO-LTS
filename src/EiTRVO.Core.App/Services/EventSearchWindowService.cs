using System.Windows;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.Views;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// <see cref="IEventSearchWindowService"/> 的 WPF 实现。
/// </summary>
/// <remarks>
/// 与帮助窗口一样，窗口只负责把 ViewModel 绑到 DataContext；事件快照由调用方传入，
/// 窗口不反查当前页面。
/// </remarks>
internal sealed class EventSearchWindowService : IEventSearchWindowService
{
    private readonly EventSearchViewModel _viewModel;
    private Window? _window;

    /// <summary>构造服务。</summary>
    /// <param name="viewModel">搜索窗口 ViewModel（组合根注入）。</param>
    public EventSearchWindowService(EventSearchViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
    }

    /// <inheritdoc />
    public void Show(IReadOnlyList<EventEntry> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        _viewModel.Open(events);

        if (_window is { IsVisible: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new EventSearchWindow
        {
            DataContext = _viewModel,
        };

        // 与帮助窗口相同的启动期保护：Owner 只能指向已经显示的窗口。
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }

        void OnCloseRequested(object? sender, EventArgs e) => window.Close();

        _viewModel.CloseRequested += OnCloseRequested;
        window.Closed += (_, _) =>
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _window = null;
        };

        _window = window;
        window.Show();
    }
}
