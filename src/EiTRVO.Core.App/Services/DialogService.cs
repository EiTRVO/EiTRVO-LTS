using Microsoft.Win32;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// <see cref="IDialogService"/> 的 WPF 实现。
/// <para>
/// 三个对话框全部是 WPF 自带类型（<c>Microsoft.Win32.*</c>），没有引入任何第三方库；
/// <c>OpenFolderDialog</c> 需要 .NET 8 —— 本项目正是 net8.0-windows。
/// </para>
/// </summary>
internal sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickSaveFile(string title, string filter, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName ?? string.Empty,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
