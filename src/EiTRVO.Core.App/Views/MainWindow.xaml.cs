namespace EiTRVO.Core.App.Views;

/// <summary>
/// 主窗口。code-behind 只有 <c>InitializeComponent()</c>（AGENTS.md §7）：
/// DataContext 由组合根在 <c>App.OnStartup</c> 里赋值，View 自己不去解析服务。
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    /// <summary>初始化 <see cref="MainWindow"/>。</summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
