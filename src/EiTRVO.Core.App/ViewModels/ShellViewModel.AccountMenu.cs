using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Auth.Storage;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 页头「账户」下拉菜单（2026-10-18，S10-b）：账户列表 + 切换默认账户 + 添加/管理入口。
/// <para>
/// 设计稿 p19 的边界：下拉里**只做切换默认账户与添加**；改名 / 移除 / 重新登录 / 皮肤仍在 p11。
/// 破坏性操作不进页头 —— 一个"看起来能删东西"的入口出现在全局工具区，
/// 误点的代价比在账户页里大得多。
/// </para>
/// </summary>
internal sealed partial class ShellViewModel
{
    private bool _isAccountMenuOpen;
    private IReadOnlyList<ShellAccountRowViewModel> _accountRows = [];
    private string _accountMenuError = string.Empty;
    private IRelayCommand? _toggleAccountMenuCommand;
    private IRelayCommand? _selectAccountCommand;
    private IRelayCommand? _addMicrosoftAccountCommand;
    private IRelayCommand? _addOfflineAccountCommand;
    private IRelayCommand? _manageAccountsCommand;

    /// <summary>账户下拉是否展开（View 的 <c>Popup.IsOpen</c> 双向绑定它）。</summary>
    public bool IsAccountMenuOpen
    {
        get => _isAccountMenuOpen;
        set
        {
            if (!SetProperty(ref _isAccountMenuOpen, value))
            {
                return;
            }

            if (value)
            {
                CloseQueueMenuSilently();
            }

            OnPropertyChanged(nameof(IsAnyMenuOpen));
        }
    }

    /// <summary>下拉里的账户行（默认账户带「默认」标记）。</summary>
    public IReadOnlyList<ShellAccountRowViewModel> AccountRows
    {
        get => _accountRows;
        private set => SetProperty(ref _accountRows, value);
    }

    /// <summary>下拉里的错误（切换默认账户失败、账户文件读不出来）；空串 = 没有错误。</summary>
    public string AccountMenuError
    {
        get => _accountMenuError;
        private set
        {
            if (SetProperty(ref _accountMenuError, value))
            {
                OnPropertyChanged(nameof(HasAccountMenuError));
            }
        }
    }

    /// <summary>是否有错误要显示。</summary>
    public bool HasAccountMenuError => AccountMenuError.Length > 0;

    /// <summary>下拉头行的计数文本。</summary>
    public string AccountMenuSummary => Loc.T("已登录 {0} 个", AccountRows.Count);

    /// <summary>是否至少有一个账户（决定显示列表还是空状态）。</summary>
    public bool HasAccounts => AccountRows.Count > 0;

    /// <summary>零账户（空状态：不画假行）。</summary>
    public bool HasNoAccounts => !HasAccounts;

    /// <summary>打开 / 收起账户下拉。</summary>
    public IRelayCommand ToggleAccountMenuCommand =>
        _toggleAccountMenuCommand ??= new RelayCommand(ToggleAccountMenu);

    /// <summary>点一行账户 = 设为默认账户（启动游戏时使用它）。</summary>
    public IRelayCommand SelectAccountCommand =>
        _selectAccountCommand ??= new RelayCommand<ShellAccountRowViewModel>(SelectAccount);

    /// <summary>添加微软账户：进 p15（设备代码流页面，打开即发起登录）。</summary>
    public IRelayCommand AddMicrosoftAccountCommand =>
        _addMicrosoftAccountCommand ??= new RelayCommand(AddMicrosoftAccount);

    /// <summary>添加离线账户：进 p11 账户页的离线账户入口。</summary>
    /// <remarks>
    /// 设计稿 p19 写的是"直接打开离线账户对话框"。当前这一步先把入口落到账户页的同一个动作
    /// （对话框由账户页承载）；把那个模态框搬到外壳层是后续小步 —— 记在规划 §12.14 的残留里，
    /// 不假装这里已经做到了。
    /// </remarks>
    public IRelayCommand AddOfflineAccountCommand =>
        _addOfflineAccountCommand ??= new RelayCommand(AddOfflineAccount);

    /// <summary>管理账户：进 p11（改名 / 移除 / 重新登录都在那里）。</summary>
    public IRelayCommand ManageAccountsCommand =>
        _manageAccountsCommand ??= new RelayCommand(ManageAccounts);

    /// <summary>重新读账户列表（展开时、切换默认账户后、语言切换时）。</summary>
    private void RebuildAccountMenu()
    {
        AccountRows = [.. _accounts.GetAccounts().Select(ShellAccountRowViewModel.From)];

        // 账户文件读不出来时 GetAccounts 返回空列表（内核的读路径纪律）：这里不能显示成
        // "还没有登录账户" —— 那是把失败说成没有数据（AGENTS.md §6）。用 StoreStatus 区分。
        AccountMenuError = _accounts.StoreStatus is null
            ? string.Empty
            : Loc.T("账户文件无法读取，请到「账户」页查看原因（原文件未改动）。");

        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(AccountTooltip));
        OnPropertyChanged(nameof(AccountTarget));
    }

    private void ToggleAccountMenu()
    {
        if (IsAccountMenuOpen)
        {
            IsAccountMenuOpen = false;
            return;
        }

        RebuildAccountMenu();
        IsAccountMenuOpen = true;
    }

    private void SelectAccount(ShellAccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsDefault)
        {
            // 已经是默认账户：不闪一下、不弹提示、不写文件。再点一次只是收起菜单。
            IsAccountMenuOpen = false;
            return;
        }

        try
        {
            _accounts.SetDefault(row.PlayerName);
        }
        catch (AccountStoreException)
        {
            // 写入路径读不出账户文件时必须拒绝继续（内核已保证原文件未改动）。
            // 先重建列表、再写错误：RebuildAccountMenu 会把错误清成 StoreStatus 那一条。
            RebuildAccountMenu();
            AccountMenuError = Loc.T("切换默认账户失败：账户文件无法读写，原文件未改动。");
            return;
        }
        catch (KeyNotFoundException)
        {
            // 菜单里的行是打开那一刻的快照：账户在这期间被移除 / 改名了。
            RebuildAccountMenu();
            AccountMenuError = Loc.T("账户列表已经变化，请重新打开菜单。");
            return;
        }

        IsAccountMenuOpen = false;
        RebuildAccountMenu();
    }

    private void AddMicrosoftAccount()
    {
        IsAccountMenuOpen = false;
        _navigation.Navigate(PageId.AccountLogin);
    }

    private void AddOfflineAccount()
    {
        IsAccountMenuOpen = false;
        _navigation.Navigate(PageId.Accounts);
    }

    private void ManageAccounts()
    {
        IsAccountMenuOpen = false;
        _navigation.Navigate(PageId.Accounts);
    }

    /// <summary>关掉队列下拉（被账户下拉的展开调用）；不触发“关另一个”的连锁。</summary>
    private void CloseQueueMenuSilently()
    {
        if (!_isQueueMenuOpen)
        {
            return;
        }

        _isQueueMenuOpen = false;
        OnPropertyChanged(nameof(IsQueueMenuOpen));
        OnPropertyChanged(nameof(IsAnyMenuOpen));
    }

    /// <summary>关掉账户下拉（被队列下拉的展开调用）；不触发“关另一个”的连锁。</summary>
    private void CloseAccountMenuSilently()
    {
        if (!_isAccountMenuOpen)
        {
            return;
        }

        _isAccountMenuOpen = false;
        OnPropertyChanged(nameof(IsAccountMenuOpen));
        OnPropertyChanged(nameof(IsAnyMenuOpen));
    }
}
