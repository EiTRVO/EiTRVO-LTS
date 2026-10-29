using System.Windows;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.Views;

namespace EiTRVO.Core.App.Services;

/// <summary><see cref="ILicenseWindowService"/> 的 WPF 实现。</summary>
internal sealed class LicenseWindowService : ILicenseWindowService
{
    private readonly ThirdPartyLicensesViewModel _viewModel;
    private Window? _window;

    /// <summary>构造服务。</summary>
    /// <param name="viewModel">窗口内容 ViewModel（组合根注入）。</param>
    public LicenseWindowService(ThirdPartyLicensesViewModel viewModel)
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

        var window = new ThirdPartyLicensesWindow
        {
            DataContext = _viewModel,
        };

        // Owner 只能指向已经显示的窗口：与帮助窗口同一条启动期保护。
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
