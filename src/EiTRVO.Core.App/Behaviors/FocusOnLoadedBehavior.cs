using System.Windows;
using Microsoft.Xaml.Behaviors;

namespace EiTRVO.Core.App.Behaviors;

/// <summary>控件加载后自动获得键盘焦点（用于搜索窗口的输入框）。</summary>
internal sealed class FocusOnLoadedBehavior : Behavior<FrameworkElement>
{
    /// <inheritdoc />
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Loaded += OnLoaded;
    }

    /// <inheritdoc />
    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= OnLoaded;
        base.OnDetaching();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AssociatedObject.Focus();
}
