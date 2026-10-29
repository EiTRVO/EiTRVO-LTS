using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Offline;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// 「离线账户」对话框：p11 账户列表右侧「添加离线账户」打开的那个独立入口。
/// </summary>
/// <remarks>
/// 2026-10-06 用户反馈（`Bug2.png`）：账户列表底下那条「离线登录 | 玩家名称」不属于本页
/// —— 它是在实现 p15 的页面区时**顺手搬到 p11** 的，设计稿的 p11 里没有它（设计稿的 p11
/// 动作条也只有四个按钮，没有「以离线模式登录」）。用户要的是**一个独立的离线账户增加方式**，
/// 所以这里把它做成一个自己的模态框：入口在「账户列表」区块标题右侧，字段、校验与错误都在框里。
/// <para>
/// 它同时是「离线账户也是可选的账户」那一半的入口：加完之后内核会把它设为默认账户
/// （见 <see cref="IAccountCatalog.AddOffline"/>），页面重读列表并选中它 ——
/// 全程**不经过 Xbox 服务器**，因为离线身份只有名字与派生 UUID。
/// </para>
/// </remarks>
internal sealed partial class OfflineAccountDialogViewModel : ObservableObject, IPageDialog
{
    private readonly IAccountCatalog _accounts;

    /// <summary>玩家名（提交前会 Trim）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _playerName = string.Empty;

    /// <summary>校验 / 落盘失败的原因（空串 = 没有错误）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>构造离线账户对话框。</summary>
    /// <param name="accounts">账户目录（校验重名与落盘都经它）。</param>
    public OfflineAccountDialogViewModel(IAccountCatalog accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _accounts = accounts;
    }

    /// <summary>对话框要关掉了；<see cref="OfflineAccountClosedEventArgs.AddedPlayerName"/> 非空表示真的加进去了。</summary>
    public event EventHandler<OfflineAccountClosedEventArgs>? Closed;

    /// <summary>「添加」是否可用：名字必须**当场**合规，不合规时按钮是灰的。</summary>
    public bool CanConfirm => OfflineName.IsValid(PlayerName.Trim());

    /// <summary>是否有错误要显示（决定提示位显示"规则"还是"原因"）。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>每次打开都是干净的：不留上一次的名字与错误。</summary>
    public void Reset()
    {
        PlayerName = string.Empty;
        Error = string.Empty;
    }

    /// <inheritdoc />
    /// <remarks>对话框里的文案都走 <see cref="Loc.T(string)"/>，切语言时重新求值一次就够（没有缓存值）。</remarks>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>
    /// 落盘一个新的离线账户。
    /// </summary>
    /// <remarks>
    /// 规则本身在 <see cref="OfflineAccountCreator"/>（p15 的「以离线模式登录」用的是同一份）。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var created = OfflineAccountCreator.TryCreate(_accounts, PlayerName, out var error);
        if (created is null)
        {
            Error = error;
            return;
        }

        PlayerName = string.Empty;
        Error = string.Empty;
        Closed?.Invoke(this, new OfflineAccountClosedEventArgs(created));
    }

    /// <summary>取消：什么都不做，直接关掉。</summary>
    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, new OfflineAccountClosedEventArgs(null));

    /// <summary>玩家名一变就重新校验：让用户在**按按钮之前**看到规则，而不是按完才被拒。</summary>
    /// <param name="value">新的玩家名。</param>
    partial void OnPlayerNameChanged(string value)
    {
        var issue = OfflineName.Validate(value.Trim());
        // 空输入不算错误（用户还没开始打字），合规的名字也不该留着上一条提示。
        Error = issue is OfflineNameIssue.None or OfflineNameIssue.Empty
            ? string.Empty
            : OfflineAccountCreator.IssueText(issue);
    }
}

/// <summary>对话框关闭事件：带上"加进去的玩家名"，取消时为 <see langword="null"/>。</summary>
/// <param name="addedPlayerName">新账户的玩家名；取消时为 <see langword="null"/>。</param>
internal sealed class OfflineAccountClosedEventArgs(string? addedPlayerName) : EventArgs
{
    /// <summary>新账户的玩家名；取消时为 <see langword="null"/>。</summary>
    public string? AddedPlayerName { get; } = addedPlayerName;
}
