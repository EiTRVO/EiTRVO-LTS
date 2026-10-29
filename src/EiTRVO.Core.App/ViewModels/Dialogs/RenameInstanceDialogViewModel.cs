using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>p02 的「重命名实例」对话框：只改实例目录名，不碰实例内容。</summary>
internal sealed partial class RenameInstanceDialogViewModel : ObservableObject, IPageDialog
{
    private readonly IInstanceCatalog _instances;

    /// <summary>原实例名（打开对话框时由页面传入）。</summary>
    [ObservableProperty]
    private string _oldName = string.Empty;

    /// <summary>新实例名（提交前 Trim）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _newName = string.Empty;

    /// <summary>校验 / 移动失败的原因（空串 = 没有错误）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>构造重命名对话框。</summary>
    /// <param name="instances">实例目录（移动目录并重新读取详情都经它）。</param>
    public RenameInstanceDialogViewModel(IInstanceCatalog instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        _instances = instances;
    }

    /// <summary>对话框关闭；<see cref="RenameInstanceClosedEventArgs.NewName"/> 非空表示真的改了。</summary>
    public event EventHandler<RenameInstanceClosedEventArgs>? Closed;

    /// <summary>新名字合法且与原名字不同。</summary>
    public bool CanConfirm =>
        LauncherPaths.IsValidInstanceName(NewName.Trim())
        && !string.Equals(NewName.Trim(), OldName, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否有错误要显示。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>每次打开都从原名字开始，方便用户只改一段。</summary>
    /// <param name="oldName">原实例名。</param>
    public void Reset(string oldName)
    {
        OldName = oldName;
        NewName = oldName;
        Error = string.Empty;
    }

    /// <inheritdoc />
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>执行目录重命名。</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var target = NewName.Trim();

        try
        {
            _instances.RenameInstance(OldName, target);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or KeyNotFoundException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            Error = exception.Message;
            return;
        }

        Error = string.Empty;
        Closed?.Invoke(this, new RenameInstanceClosedEventArgs(target));
    }

    /// <summary>取消：什么都不做。</summary>
    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, new RenameInstanceClosedEventArgs(null));

    /// <summary>名字一变就重新校验，错误提示随输入消失 / 出现。</summary>
    /// <param name="value">新名字。</param>
    partial void OnNewNameChanged(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            Error = string.Empty;
            return;
        }

        if (!LauncherPaths.IsValidInstanceName(trimmed))
        {
            Error = Loc.T("实例名只能包含字母、数字、空格、下划线、短横线与点，且不能是保留名。");
            return;
        }

        Error = string.Equals(trimmed, OldName, StringComparison.OrdinalIgnoreCase)
            ? Loc.T("新名字不能与原名字相同。")
            : string.Empty;
    }
}

/// <summary>重命名对话框关闭事件：取消时为 <see langword="null"/>。</summary>
/// <param name="newName">新实例名；取消时为 <see langword="null"/>。</param>
internal sealed class RenameInstanceClosedEventArgs(string? newName) : EventArgs
{
    /// <summary>新实例名；取消时为 <see langword="null"/>。</summary>
    public string? NewName { get; } = newName;
}
