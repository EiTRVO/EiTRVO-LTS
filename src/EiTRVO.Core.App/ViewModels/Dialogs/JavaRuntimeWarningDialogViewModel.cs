using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>手动添加 Java 时的安全提示确认框。</summary>
internal sealed partial class JavaRuntimeWarningDialogViewModel : ObservableObject, IPageDialog
{
    /// <summary>关闭事件；<see langword="true"/> 表示用户坚持添加。</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>被检查的文件路径。</summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>风险说明。</summary>
    [ObservableProperty]
    private string _riskText = string.Empty;

    /// <summary>这个提示是否允许用户继续添加（无效文件只能关闭）。</summary>
    [ObservableProperty]
    private bool _canAdd;

    /// <summary>打开提示框。</summary>
    /// <param name="filePath">用户选择的文件。</param>
    /// <param name="riskText">要展示的风险/错误说明。</param>
    /// <param name="canAdd">是否允许用户仍然添加。</param>
    public void Show(string filePath, string riskText, bool canAdd)
    {
        FilePath = filePath;
        RiskText = riskText;
        CanAdd = canAdd;
    }

    /// <inheritdoc />
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>用户坚持添加。</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Accept() => Closed?.Invoke(this, true);

    /// <summary>取消。</summary>
    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    /// <summary>CanAdd 变化时刷新确认按钮。</summary>
    partial void OnCanAddChanged(bool value) => AcceptCommand.NotifyCanExecuteChanged();
}
