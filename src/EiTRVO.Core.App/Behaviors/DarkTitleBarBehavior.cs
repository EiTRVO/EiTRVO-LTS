using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Xaml.Behaviors;

namespace EiTRVO.Core.App.Behaviors;

/// <summary>
/// 把窗口标题栏切成深色（对应设计稿最顶部那条 #2b2b2b 的标题栏）。
/// <para>
/// 实现方式遵循 AGENTS.md §5.2：用 <c>DwmSetWindowAttribute</c> 让 <b>系统原生</b> 标题栏变深，
/// 而 <b>不是</b> 用 <c>WindowChrome</c> 全自绘 —— 自绘会丢掉系统阴影、贴靠布局与 Alt+Tab 快照，
/// 还会让最小化/最大化/关闭按钮变成自己画的图标。
/// </para>
/// <para>
/// 因此设计稿里那三个「− □ ✕」仍旧是系统的三个按钮，位置与行为都由 Windows 定义
/// （p16 §3 也正是这个结论）。
/// </para>
/// </summary>
internal sealed class DarkTitleBarBehavior : Behavior<Window>
{
    // Windows 10 1809–1909 用 19，Windows 10 2004+ / 11 用 20。先试 20，失败再退回 19。
    private const int DwmwaUseImmersiveDarkModePre20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

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

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            // 老版本 Windows：换成 1809 时期的属性号重试一次。
            // 两者都失败（Windows 10 1809 之前）时系统标题栏保持浅色，内容不受影响。
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModePre20H1, ref enabled, sizeof(int));
        }
    }
}
