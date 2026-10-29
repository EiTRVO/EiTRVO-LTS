namespace EiTRVO.Core.App.Services;

/// <summary>
/// 文件与目录选择。
/// <para>
/// <b>只做选择，不做确认框</b>：设计稿的确认是窗口内模态层（p13「删除实例」），
/// 不是系统 <c>MessageBox</c>；而且 <c>MessageBox</c> 属于 WPF 类型，ViewModel 不得引用。
/// 需要确认时用模态层，需要选文件时才用这里 —— 因为 WPF 没有非交互式的替代品。
/// </para>
/// <para>
/// 传入的过滤器一律用 Win32 习惯写法，例如
/// <c>"Modrinth 整合包 (*.mrpack)|*.mrpack|所有文件 (*.*)|*.*"</c>。
/// </para>
/// </summary>
internal interface IDialogService
{
    /// <summary>选择一个已存在的文件。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="filter">过滤器。</param>
    /// <returns>文件路径；用户取消时为 <see langword="null"/>。</returns>
    string? PickFile(string title, string filter);

    /// <summary>选择保存位置。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="filter">过滤器。</param>
    /// <param name="defaultFileName">默认文件名。</param>
    /// <returns>文件路径；用户取消时为 <see langword="null"/>。</returns>
    string? PickSaveFile(string title, string filter, string? defaultFileName = null);

    /// <summary>选择一个目录。</summary>
    /// <param name="title">对话框标题。</param>
    /// <returns>目录路径；用户取消时为 <see langword="null"/>。</returns>
    string? PickFolder(string title);
}
