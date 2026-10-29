using System.Windows;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// <see cref="IWindowService"/> 的 WPF 实现。
/// </summary>
internal sealed class WindowService : IWindowService
{
    /// <inheritdoc />
    public bool IsMainWindowVisible => MainWindow?.IsVisible == true;

    /// <inheritdoc />
    public void Minimize()
    {
        if (MainWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    /// <inheritdoc />
    public void Restore()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        window.WindowState = WindowState.Normal;

        // 只是把状态改回 Normal 不够：从任务栏恢复时窗口可能仍在后面。
        window.Show();
        window.Activate();
    }

    /// <inheritdoc />
    public void Close() => MainWindow?.Close();

    private static Window? MainWindow => Application.Current?.MainWindow;
}
