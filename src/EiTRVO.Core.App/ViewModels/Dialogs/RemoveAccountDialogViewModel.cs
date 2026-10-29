using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Storage;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// 「移除账户」确认框（p11 的模态层）。
/// </summary>
/// <remarks>
/// 2026-10-06 用户反馈："点击移除账户按钮会跳转到移除实例页面，无法删除账户" ——
/// 之前那个按钮绑的是导航到 <c>PageId.DeleteInstance</c>（p13 删的是**实例**），
/// 于是它既不删账户、还把人带到另一个页面去。现在它打开这个确认框，
/// 确认后走 <see cref="IAccountCatalog.Remove"/> 真的把本机那一份记录删掉。
/// <para>
/// 文案必须说清"删的是什么"：只删本机这一份（含令牌），官方账户本身不受影响 ——
/// 写成"删除账号"就是在骗人，用户会以为账号被注销了。
/// </para>
/// </remarks>
internal sealed partial class RemoveAccountDialogViewModel : ObservableObject, IPageDialog
{
    private readonly IAccountCatalog _accounts;

    /// <summary>要移除的玩家名（打开时由页面给定；空串 = 还没选定目标）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Question))]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    private string _playerName = string.Empty;

    /// <summary>失败原因（空串 = 没有错误）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>构造移除账户确认框。</summary>
    /// <param name="accounts">账户目录（真的删它）。</param>
    public RemoveAccountDialogViewModel(IAccountCatalog accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _accounts = accounts;
    }

    /// <summary>对话框关掉了；<see cref="Removed"/> 为真表示真的删掉了（页面据此重读列表）。</summary>
    public event EventHandler? Closed;

    /// <summary>这次关闭是否伴随着一次成功的移除。</summary>
    public bool Removed { get; private set; }

    /// <summary>提问句。</summary>
    public string Question => Loc.T("确定要移除账户「{0}」吗？", PlayerName);

    /// <summary>有没有选定的目标（没有时确认按钮是灰的）。</summary>
    public bool HasTarget => PlayerName.Length > 0;

    /// <summary>是否有错误要显示。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>打开对话框：清掉上一次的错误，记下这一次的目标账户。</summary>
    /// <param name="playerName">要移除的玩家名。</param>
    public void Target(string playerName)
    {
        Removed = false;
        Error = string.Empty;
        PlayerName = playerName ?? string.Empty;
    }

    /// <inheritdoc />
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>取消：什么都不做，直接关掉。</summary>
    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 确认移除：删掉本机那一份记录，然后关掉对话框。
    /// </summary>
    /// <remarks>
    /// 失败时**不关**对话框：用户需要看到原因（账户文件读不出来时内核拒绝写入，
    /// 那份"解不开但可能还有救"的文件必须原样留着），而不是看着框消失、账户还在。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasTarget))]
    private void Confirm()
    {
        try
        {
            _accounts.Remove(PlayerName);
        }
        catch (KeyNotFoundException)
        {
            // 列表与磁盘不同步（例如别的地方刚删过）：如实说，别假装成功。
            Error = Loc.T("这个账户已经不在列表里了，请关闭后重试。");
            return;
        }
        catch (AccountStoreException)
        {
            Error = Loc.T("账户文件读不出来，已取消这次移除（原文件未改动）。");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.T("账户文件写入失败，请检查磁盘与权限。");
            return;
        }

        Removed = true;
        Error = string.Empty;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>目标变了：重新判断确认按钮是否可用。</summary>
    /// <param name="value">新的玩家名。</param>
    partial void OnPlayerNameChanged(string value) => ConfirmCommand.NotifyCanExecuteChanged();
}
