using System.Windows;

namespace EiTRVO.Core.App.Views;

/// <summary>帮助与支持中心（独立窗口）。业务状态在 <c>HelpViewModel</c>，这里只有 <see cref="InitializeComponent"/>。</summary>
public partial class HelpWindow : Window
{
    /// <summary>构造窗口。</summary>
    public HelpWindow() => InitializeComponent();
}
