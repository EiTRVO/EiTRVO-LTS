using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Dialogs;

using EiTRVO.Core.Launcher.Text;
namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p11 账户。对应 mockup/pages/p11-accounts.html。
/// <para>
/// 这一页有四个有实际状态变化的行为，因此它不是一个死界面：
/// </para>
/// <list type="number">
/// <item><b>令牌刷新</b>：刷新后账户行的令牌状态会真的变绿；</item>
/// <item><b>离线登录</b>（M5 的 D-6）：填玩家名 → 派生 UUID → 落盘 → 出现在列表里并被选中。
/// 派生算法必须与 Java 版一致（字节序），因为存档与服务端是按 UUID 认人的（见 <see cref="Auth.Offline.OfflineUuid"/>）。</item>
/// <item><b>上传皮肤 / 恢复默认皮肤</b>（M5 的 D-9）：选一张 PNG → 本地校验 → 官方接口 → 用响应里的档案刷新四格；</item>
/// <item><b>换皮肤模型</b>（D-9）：拿档案里已有的纹理地址换个 <c>variant</c> 重发（下拉框从此是真的）。</item>
/// </list>
/// </summary>
internal sealed partial class AccountsViewModel : PageViewModelBase
{
    private readonly IAccountCatalog _accounts;
    private readonly ISkinService _skins;
    private readonly IDialogService _dialogs;
    private readonly OfflineAccountDialogViewModel _addOffline;
    private readonly RemoveAccountDialogViewModel _removeAccount;

    /// <summary>
    /// "现在这次 <see cref="SkinModel"/> 的写入是代码干的，不是用户选的"。
    /// </summary>
    /// <remarks>
    /// 换选中项、换语言、上传成功后回填，都会用代码写 <see cref="SkinModel"/>；而它一变
    /// （<c>OnSkinModelChanged</c>）就会去调官方接口换模型 —— 少了这个标记，
    /// **仅仅切到另一个账户就会给那个账户改一次皮肤模型**。
    /// </remarks>
    private bool _applyingSkin;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshTokensCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadSkinCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeSkinModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSkinCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRemoveAccountCommand))]
    [NotifyPropertyChangedFor(nameof(CanSetDefault))]
    [NotifyPropertyChangedFor(nameof(CanRemoveAccount))]
    [NotifyPropertyChangedFor(nameof(CanChangeSkinModel))]
    private AccountRowViewModel? _selectedAccount;

    private SkinProfile _skin;

    [ObservableProperty]
    private SkinModel _skinModel;

    /// <summary>离线登录输入框里的玩家名。</summary>
    /// <remarks>
    /// 2026-10-06 起这个字段**搬进了 <see cref="OfflineAccountDialogViewModel"/>**：
    /// 用户指出账户列表底下那条「离线登录」不属于本页（设计稿的 p11 没有它），
    /// 离线账户要有**自己独立的增加入口**。本页只留下"打开那个对话框"与"列表"两件事。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    [NotifyPropertyChangedFor(nameof(Dialog))]
    private bool _isAddOfflineOpen;

    /// <summary>「移除账户」确认框是否打开。</summary>
    /// <remarks>
    /// 2026-10-06 用户反馈："点击移除账户按钮会跳转到移除实例页面，无法删除账户" ——
    /// 那个按钮以前绑的是导航到 p13（删**实例**的页面），所以现在它打开自己的确认框。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    [NotifyPropertyChangedFor(nameof(Dialog))]
    private bool _isRemoveAccountOpen;

    /// <summary>
    /// 本页最近一次操作的错误提示（空串 = 没有错误）。
    /// </summary>
    /// <remarks>
    /// M5-1 起刷新令牌真的会失败（网络断了、令牌被作废），失败必须看得见 ——
    /// 这条提示位从"离线登录输入框旁边"挪到了同一位置的一行独立提示（只在有错误时占高度），
    /// 因为那个输入框本身已经搬进对话框了。
    /// </remarks>
    [ObservableProperty]
    private string _actionError = string.Empty;

    /// <summary>构造账户 ViewModel。</summary>
    /// <param name="accounts">账户目录。</param>
    /// <param name="skins">皮肤服务（D-9：上传 / 换模型 / 恢复默认）。</param>
    /// <param name="dialogs">文件选择（皮肤是一张本地 PNG，WPF 没有非交互式的替代品）。</param>
    /// <param name="addOffline">「添加离线账户」那个独立对话框（本页的模态层内容之一）。</param>
    /// <param name="removeAccount">「移除账户」确认框（本页的另一个模态层内容）。</param>
    public AccountsViewModel(
        IAccountCatalog accounts,
        ISkinService skins,
        IDialogService dialogs,
        OfflineAccountDialogViewModel addOffline,
        RemoveAccountDialogViewModel removeAccount)
        : base(PageId.Accounts, "账户")
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(skins);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(addOffline);
        ArgumentNullException.ThrowIfNull(removeAccount);

        _accounts = accounts;
        _skins = skins;
        _dialogs = dialogs;
        _addOffline = addOffline;
        _addOffline.Closed += OnAddOfflineClosed;
        _removeAccount = removeAccount;
        _removeAccount.Closed += OnRemoveAccountClosed;

        Accounts = [];
        _skin = new SkinProfile(
            string.Empty,
            TextTemplate.Of("未获取"),
            HasCape: false,
            SkinModel.Classic,
            TextTemplate.Of("从未更新"));
        Reload();
    }

    /// <summary>本页是否有打开的模态层（离线账户对话框 / 移除账户确认框）。</summary>
    public override bool HasDialog => IsAddOfflineOpen || IsRemoveAccountOpen;

    /// <summary>模态层内容。两个框不会同时开，所以挑当前开着的那个。</summary>
    public override object? Dialog => IsRemoveAccountOpen ? _removeAccount : _addOffline;

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Accounts;

    /// <inheritdoc />
    /// <remarks>
    /// 账户文件读不出来时（D-5），副标题直接改成失败原因 —— 这是"显式提示"的落点：
    /// 不新增任何控件、不动 §5.2 的几何，但用户一眼能看到"你的账户文件没坏，只是解不开"。
    /// 若把它显示成「0 个已登录账户」，用户很可能以为号被删了，然后去重建、覆盖掉还能救的文件。
    /// </remarks>
    public override string SectionSubtitle => _accounts.StoreStatus is { } status
        ? Loc.T("账户文件无法读取：{0}", FailureText(status.Failure))
        : Loc.T("{0} 个已登录账户", Accounts.Count);

    private static string FailureText(AccountStoreFailure failure) => failure switch
    {
        AccountStoreFailure.DecryptionFailed => Loc.T("换了 Windows 用户或换了机器（密文绑当前用户），请重新登录；原文件未改动"),
        AccountStoreFailure.UnsupportedPlatform => Loc.T("当前平台不支持账户加密存储"),
        _ => Loc.T("文件已损坏，请重新登录；原文件未改动"),
    };

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>账户列表。</summary>
    public ObservableCollection<AccountRowViewModel> Accounts { get; }

    /// <summary>当前账户的皮肤信息。</summary>
    public SkinProfile Skin
    {
        get => _skin;
        private set => SetProperty(ref _skin, value);
    }

    /// <summary>皮肤区块的「当前皮肤」那一格（键 + 参数 → 当前语言）。</summary>
    /// <remarks>
    /// 内核那份 <see cref="SkinProfile.CurrentSkin"/> 是「键 + 参数」，
    /// **不能直接绑**（字符串内插只调 ToString()，拿到的是键）—— 见 AGENTS.md §5.3 第 ⑨ 条。
    /// </remarks>
    public string SkinCurrentSkinText => LocalizedText.Of(Skin.CurrentSkin);

    /// <summary>皮肤区块的「最近更新」那一格（键 + 参数 → 当前语言）。</summary>
    public string SkinLastUpdatedText => LocalizedText.Of(Skin.LastUpdated);

    /// <summary>皮肤模型选项。</summary>
    public IReadOnlyList<ChoiceOption<SkinModel>> SkinModelOptions { get; } =
    [
        new(Loc.T("经典 (Classic)"), SkinModel.Classic),
        new(Loc.T("纤细 (Slim)"), SkinModel.Slim),
    ];

    /// <summary>「设为默认」是否可用（当前选中的不是默认账户时可用）。</summary>
    public bool CanSetDefault => SelectedAccount is { IsDefault: false };

    /// <summary>「移除账户」是否可用（有选中账户时可用）。</summary>
    public bool CanRemoveAccount => SelectedAccount is not null;

    /// <summary>只有微软账户有 Java 版皮肤档案；离线账户的「皮肤模型」下拉要如实置灰。</summary>
    public bool CanChangeSkinModel => SelectedAccount is { Kind: AccountKind.Microsoft };

    /// <summary>
    /// 打开「添加离线账户」对话框（本页独立的离线入口）。
    /// </summary>
    /// <remarks>
    /// 入口放在「账户列表」区块标题的右侧工具区：视图是列表，加一项的最自然位置就在列表标题旁边。
    /// 对话框每次打开都清空（<see cref="OfflineAccountDialogViewModel.Reset"/>），
    /// 否则上一次的名字与错误会跟着带进来。
    /// </remarks>
    [RelayCommand]
    private void OpenAddOffline()
    {
        ActionError = string.Empty;
        _addOffline.Reset();
        IsAddOfflineOpen = true;
    }

    /// <summary>对话框关掉了：加成功了就重读列表并选中新账户，取消就只是关掉。</summary>
    private void OnAddOfflineClosed(object? sender, OfflineAccountClosedEventArgs e)
    {
        IsAddOfflineOpen = false;

        if (e.AddedPlayerName is { } name)
        {
            ActionError = string.Empty;
            Reload(selectPlayerName: name);
        }
    }

    /// <summary>
    /// 打开「移除账户」确认框（移除的是**当前选中**的那个账户）。
    /// </summary>
    /// <remarks>
    /// 以前这个按钮绑的是导航到 <c>PageId.DeleteInstance</c> —— 那是删**实例**的页面，
    /// 所以它既不删账户，还把人带到别处（2026-10-06 用户反馈）。现在它开自己的确认框。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveAccount))]
    private void OpenRemoveAccount()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        ActionError = string.Empty;
        _removeAccount.Target(account.PlayerName);
        IsRemoveAccountOpen = true;
    }

    /// <summary>确认框关掉了：真的删掉过就重读列表（被删的账户不该继续显示）。</summary>
    private void OnRemoveAccountClosed(object? sender, EventArgs e)
    {
        IsRemoveAccountOpen = false;

        if (_removeAccount.Removed)
        {
            ActionError = string.Empty;
            Reload();
        }
    }

    /// <summary>
    /// 刷新所选账户的令牌（M5-1 起是真的：用存储里的刷新令牌重跑整条链，并把新的令牌写回去）。
    /// </summary>
    /// <remarks>
    /// 刷新令牌是**轮换**的，所以失败时必须让用户看得见 —— 一个"点了没反应"的按钮会让人以为
    /// 令牌还是好的，而下次启动会当场失败。失败原因分两类（内核报的），这里原样转述：
    /// "需要重新登录"与"网络不可用，再试一次"。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefreshTokens))]
    private async Task RefreshTokensAsync()
    {
        if (SelectedAccount is not { Kind: AccountKind.Microsoft } account)
        {
            return;
        }

        ActionError = string.Empty;

        try
        {
            await _accounts.RefreshMicrosoftAsync(account.PlayerName);
        }
        catch (AuthException ex)
        {
            // 内核给的是「键 + 参数」，句子在这一层拼（Q8）。
            ActionError = LocalizedText.Of(ex.Reason);
        }
        catch (KeyNotFoundException)
        {
            // 列表与磁盘不一致（别的进程改过账户文件）：重读一遍列表，用户会看到真实情况。
            Reload();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AccountStoreException)
        {
            ActionError = Loc.T("账户文件写入失败，请检查磁盘与权限。");
        }

        Reload(selectPlayerName: account.PlayerName);
    }

    private bool CanRefreshTokens() => SelectedAccount is { Kind: AccountKind.Microsoft };

    /// <summary>
    /// 上传一张 PNG 作为当前账户的皮肤（M5 的 D-9）。
    /// </summary>
    /// <remarks>
    /// 三件事都交给内核（<see cref="ISkinService"/>）：本地 PNG 校验、令牌（含刷新一次再重试）、
    /// 以及"成功之后 60 秒内不许再传"。界面这边只负责**选文件**与**把失败原因说出来** ——
    /// 尤其是"401/403 要重新登录""404 没有 Java 版档案""400 图片有问题""429 太频繁"
    /// 这四种必须各说各的（内核已经把它们分开了，这里一个字都不合并）。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanChangeSkin))]
    private async Task UploadSkinAsync()
    {
        if (SelectedAccount is not { Kind: AccountKind.Microsoft } account)
        {
            return;
        }

        var path = _dialogs.PickFile(Loc.T("选择皮肤文件"), Loc.T("PNG 图片 (*.png)|*.png"));

        // 用户取消选文件**不是**错误：什么都不说、什么也不记。
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ActionError = string.Empty;

        try
        {
            await _skins.UploadAsync(account.PlayerName, path, SkinModel);
        }
        catch (SkinChangeException ex)
        {
            ActionError = LocalizedText.Of(ex.Reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionError = Loc.T("账户文件写入失败，请检查磁盘与权限。");
        }

        ApplySkinChange(account.PlayerName);
    }

    /// <summary>换皮肤模型（D-9）：拿档案里已有的纹理地址换个 <c>variant</c> 重发，不必再选一次文件。</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSkin))]
    private async Task ChangeSkinModelAsync()
    {
        if (SelectedAccount is not { Kind: AccountKind.Microsoft } account)
        {
            return;
        }

        ActionError = string.Empty;

        try
        {
            await _skins.SetModelAsync(account.PlayerName, SkinModel);
        }
        catch (SkinChangeException ex)
        {
            ActionError = LocalizedText.Of(ex.Reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionError = Loc.T("账户文件写入失败，请检查磁盘与权限。");
        }

        ApplySkinChange(account.PlayerName);
    }

    /// <summary>恢复官方默认皮肤（D-9：<c>DELETE …/skins/active</c>）。</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSkin))]
    private async Task ResetSkinAsync()
    {
        if (SelectedAccount is not { Kind: AccountKind.Microsoft } account)
        {
            return;
        }

        ActionError = string.Empty;

        try
        {
            await _skins.ResetAsync(account.PlayerName);
        }
        catch (SkinChangeException ex)
        {
            ActionError = LocalizedText.Of(ex.Reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionError = Loc.T("账户文件写入失败，请检查磁盘与权限。");
        }

        ApplySkinChange(account.PlayerName);
    }

    /// <summary>三个皮肤动作共用一条可执行条件：正版账户 + 不在冷却窗口里。</summary>
    /// <remarks>
    /// 冷却由内核按**每个账户**记（<see cref="ISkinService.CooldownRemaining"/>），
    /// 所以按钮在 60 秒里是真的灰的 —— 而不是让用户点下去、再被 429 拒一次。
    /// </remarks>
    private bool CanChangeSkin() =>
        SelectedAccount is { Kind: AccountKind.Microsoft } account
        && _skins.CooldownRemaining(account.PlayerName) <= TimeSpan.Zero;

    /// <summary>皮肤动作之后：重读四格（成功）或把下拉框拉回真实值（失败），并刷新可执行状态。</summary>
    /// <param name="playerName">玩家名。</param>
    private void ApplySkinChange(string playerName)
    {
        _applyingSkin = true;
        Skin = _accounts.GetSkin(playerName);
        SkinModel = Skin.Model;
        _applyingSkin = false;

        NotifySkinCommands();
        BeginCooldownRelease(playerName);
    }

    /// <summary>冷却过去之后把三个按钮重新点亮（内核按时间判，这里只是去问一次）。</summary>
    /// <param name="playerName">玩家名。</param>
    private void BeginCooldownRelease(string playerName)
    {
        var remaining = _skins.CooldownRemaining(playerName);

        if (remaining > TimeSpan.Zero)
        {
            _ = ReleaseCooldownAsync(playerName, remaining);
        }
    }

    /// <summary>等到冷却结束再通知一次按钮（不加定时器、不碰任何 UI 类型）。</summary>
    private async Task ReleaseCooldownAsync(string playerName, TimeSpan remaining)
    {
        // 多等 250 毫秒：Task.Delay 醒来时可能还差一点点（时钟精度），
        // 而"差 3 毫秒"的表现是按钮再也不亮 —— 因为这里只调度一次。
        await Task.Delay(remaining + TimeSpan.FromMilliseconds(250));

        if (CooldownStillRelevant(playerName))
        {
            NotifySkinCommands();
        }
    }

    private bool CooldownStillRelevant(string playerName) =>
        string.Equals(SelectedAccount?.PlayerName, playerName, StringComparison.OrdinalIgnoreCase);

    private void NotifySkinCommands()
    {
        UploadSkinCommand.NotifyCanExecuteChanged();
        ChangeSkinModelCommand.NotifyCanExecuteChanged();
        ResetSkinCommand.NotifyCanExecuteChanged();
    }

    /// <summary>把当前选中的账户设为默认账户。</summary>
    [RelayCommand(CanExecute = nameof(CanSetDefault))]
    private void SetDefault()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        try
        {
            _accounts.SetDefault(account.PlayerName);
        }
        catch (AccountStoreException)
        {
            ActionError = Loc.T("账户文件读不出来，已取消这次写入（原文件未改动）。");
            return;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IOException or UnauthorizedAccessException)
        {
            ActionError = Loc.T("账户文件写入失败，请检查磁盘与权限。");
            return;
        }
        Reload(selectPlayerName: account.PlayerName);
    }

    /// <summary>
    /// 页面被导航到时重读账户列表。
    /// </summary>
    /// <remarks>
    /// M5-1 起这条是**必需**的：p15 的登录流程在成功后跳回本页，而账户是刚写进磁盘的
    /// （本页实例是单例，构造时读的那一份已经过时）。新出现的账户会被选中 ——
    /// 用户刚刚登录它，就是要用它。
    /// </remarks>
    public override void OnActivated()
    {
        var before = Accounts.Select(static account => account.PlayerName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        base.OnActivated();

        var after = _accounts.GetAccounts();
        var added = after.FirstOrDefault(account => !before.Contains(account.PlayerName));

        Reload(selectPlayerName: added?.PlayerName);
    }

    /// <summary>选中项变了：皮肤区块与「设为默认」跟着走（与实例页"选中即切换"同一条规矩）。</summary>
    /// <param name="value">新的选中账户。</param>
    /// <remarks>
    /// <see cref="_applyingSkin"/> 在这里是**必需**的：填 <see cref="SkinModel"/> 会触发
    /// <c>OnSkinModelChanged</c>，而那个方法会去调官方接口换模型 ——
    /// 少了这个标记，"切到另一个账户"就等于"给那个账户改一次皮肤模型"。
    /// </remarks>
    partial void OnSelectedAccountChanged(AccountRowViewModel? value)
    {
        _applyingSkin = true;
        Skin = _accounts.GetSkin(value?.PlayerName ?? string.Empty);
        SkinModel = Skin.Model;
        _applyingSkin = false;

        NotifySkinCommands();
    }

    /// <summary>
    /// 用户在下拉框里换了模型 → 真的去换（D-9）。
    /// </summary>
    /// <param name="value">新模型。</param>
    partial void OnSkinModelChanged(SkinModel value)
    {
        if (_applyingSkin || SelectedAccount is not { Kind: AccountKind.Microsoft })
        {
            return;
        }

        if (ChangeSkinModelCommand.CanExecute(null))
        {
            ChangeSkinModelCommand.Execute(null);
        }
    }

    /// <summary>重新读账户列表（新建 / 改默认之后必须重读：界面显示的必须是磁盘上的那一份）。</summary>
    /// <param name="selectPlayerName">要选中的玩家名；为空时尽量保住当前选中项。</param>
    private void Reload(string? selectPlayerName = null)
    {
        var wanted = selectPlayerName ?? SelectedAccount?.PlayerName;
        var rows = _accounts.GetAccounts();

        // 行摘要（`Microsoft 正版 · UUID · 上次使用 …`）由投影拼：内核给的是「键 + 参数」。
        var projected = rows.Select(AccountRowViewModel.From).ToList();

        Accounts.Clear();
        foreach (var account in projected)
        {
            Accounts.Add(account);
        }

        SelectedAccount = wanted is null
            ? projected.FirstOrDefault(static account => account.IsDefault) ?? (projected.Count > 0 ? projected[0] : null)
            : projected.FirstOrDefault(account => string.Equals(account.PlayerName, wanted, StringComparison.OrdinalIgnoreCase))
              ?? projected.FirstOrDefault(static account => account.IsDefault)
              ?? (projected.Count > 0 ? projected[0] : null);

        OnPropertyChanged(nameof(SectionSubtitle));
        OnPropertyChanged(nameof(CanSetDefault));
        SetDefaultCommand.NotifyCanExecuteChanged();
    }
}
