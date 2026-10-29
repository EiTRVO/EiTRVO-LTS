namespace EiTRVO.Core.App.Services;

/// <summary>
/// 与操作系统外壳打交道的动作（打开目录 / 打开浏览器 / 复制到剪贴板）。
/// <para>
/// 这些动作在 ViewModel 里必须只能通过接口调用：<c>Process.Start</c> 是 BCL，
/// 但 <c>System.Windows.Clipboard</c> 是 WPF 类型，一旦写进 ViewModel 就违反了
/// AGENTS.md §2.1。所以统一收敛到这一层，由 View 层提供实现。
/// </para>
/// </summary>
internal interface IShellService
{
    /// <summary>用资源管理器打开一个目录。</summary>
    /// <param name="path">目录路径。</param>
    /// <returns>是否成功发起。</returns>
    bool OpenFolder(string path);

    /// <summary>用系统默认浏览器打开一个链接。</summary>
    /// <param name="url">链接。</param>
    /// <returns>是否成功发起。</returns>
    bool OpenUrl(string url);

    /// <summary>把文本放进剪贴板。</summary>
    /// <param name="text">文本。</param>
    /// <returns>是否成功。</returns>
    bool CopyToClipboard(string text);
}
