using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Xaml.Behaviors;

namespace EiTRVO.Core.App.Behaviors;

/// <summary>
/// 把主窗口的**系统原生**标题栏切成白色（2026-10-24 用户要求）。
/// <para>
/// 仍然走 <c>DwmSetWindowAttribute</c>，不做 <c>WindowChrome</c> 全自绘：
/// 自绘会丢系统阴影、贴靠布局与 Alt+Tab 快照，也会把最小化 / 最大化 / 关闭按钮变成我们自己画的。
/// </para>
/// <para>
/// 三个属性：关闭暗色模式（20 / 旧系统 19）、标题栏底色（35）、标题文字颜色（36）。
/// Windows 10 不支持 35 / 36 时调用会失败，但关闭暗色模式后系统会回到浅色标题栏；
/// 所以这里对失败一律静默，不拿“白得不够白”去阻塞窗口启动。
/// </para>
/// </summary>
internal sealed class LightTitleBarBehavior : Behavior<Window>
{
    private const int DwmwaUseImmersiveDarkModePre20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>白色（COLORREF：0x00BBGGRR）。</summary>
    private const int White = 0x00FFFFFF;

    /// <summary>黑色标题文字（COLORREF：0x00BBGGRR）。</summary>
    private const int Black = 0x00000000;

    /// <inheritdoc />
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.SourceInitialized += OnSourceInitialized;
    }

    /// <inheritdoc />
    protected override void OnDetaching()
    {
        AssociatedObject.SourceInitialized -= OnSourceInitialized;
        base.OnDetaching();
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(AssociatedObject).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var disabled = 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref disabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModePre20H1, ref disabled, sizeof(int));
        }

        SetColor(handle, DwmwaCaptionColor, White);
        SetColor(handle, DwmwaTextColor, Black);
        SetColor(handle, DwmwaBorderColor, White);
    }

    private static void SetColor(IntPtr handle, int attribute, int color) =>
        _ = DwmSetWindowAttribute(handle, attribute, ref color, sizeof(int));
}
