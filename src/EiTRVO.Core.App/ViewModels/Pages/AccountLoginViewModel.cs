using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Dialogs;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p15 添加 Microsoft 账户。对应 mockup/pages/p15-dialog-account-login.html。
/// <para>
/// 页面内容只有「已有账户 + 离线登录」，中间那个设备代码流对话框由
/// <see cref="Dialog"/> 提供、由外壳画在遮罩之上（这样遮罩能盖住整个窗口）。
/// </para>
/// </summary>
internal sealed partial class AccountLoginViewModel : PageViewModelBase
{
    private readonly IAccountCatalog _accounts;
    private readonly INavigationService _navigation;

    /// <summary>构造添加账户页面。</summary>
    /// <param name="accounts">账户目录（列出已有账户；离线账户也落在这里）。</param>
    /// <param name="dialog">该页面自带的对话框。</param>
    /// <param name="navigation">导航服务（离线账户建好之后回账户页）。</param>
    public AccountLoginViewModel(
        IAccountCatalog accounts,
        AddAccountDialogViewModel dialog,
        INavigationService navigation)
        : base(PageId.AccountLogin, "添加 Microsoft 账户")
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(navigation);

        _accounts = accounts;
        _navigation = navigation;

        Accounts = [.. accounts.GetAccounts().Select(AccountRowViewModel.From)];
        Dialog = dialog;
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Accounts;

    // 页面名（面包屑末级）是「添加 Microsoft 账户」，但内容区那一块的标题是「账户」，
    // 副标题才写「添加 Microsoft 账户」—— 与设计稿 p15 一致。
    /// <inheritdoc />
    public override string SectionTitle => Loc.T("账户");

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T("添加 Microsoft 账户");

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb =>
        NestedCrumb(Loc.T("账户"), PageId.Accounts);

    /// <inheritdoc />
    public override bool HasDialog => true;

    /// <inheritdoc />
    public override object? Dialog { get; }

    /// <summary>
    /// 已有账户。
    /// </summary>
    /// <remarks>
    /// 是**投影**而不是内核的 <c>IReadOnlyList&lt;Account&gt;</c>：那一行的摘要是「键 + 参数」，
    /// 直接绑内核记录会在界面上显示**键**（AGENTS.md §5.3 第 ⑨ 条）。属性名不变，XAML 一字未改。
    /// </remarks>
    public IReadOnlyList<AccountRowViewModel> Accounts { get; }

    /// <summary>离线登录用的玩家名（设计稿把它的占位文案画成了输入框里的值）。</summary>
    public string OfflinePlayerName { get; set; } = Loc.T("玩家名称");

    /// <summary>离线登录失败的原因（空串 = 没有错误）。</summary>
    [ObservableProperty]
    private string _offlineError = string.Empty;

    /// <summary>
    /// 「以离线模式登录」：名字 → 派生 UUID → 落盘 → 回账户页（那边会选中新账户）。
    /// </summary>
    /// <remarks>
    /// 2026-10-06 之前这个按钮**只导航回账户页、什么都不做** —— 设计稿画的是"以离线模式登录"，
    /// 而它连账户都没建，属于"看起来可点但没有行为"。现在它走的是与 p11「添加离线账户」
    /// 完全同一个 <see cref="OfflineAccountCreator"/>（规则只有一份）。
    /// </remarks>
    [RelayCommand]
    private void AddOffline()
    {
        var created = OfflineAccountCreator.TryCreate(_accounts, OfflinePlayerName, out var error);
        if (created is null)
        {
            // 失败时**不清空**输入（用户要能改了重试），原因显示在输入框旁边。
            OfflineError = error;
            return;
        }

        OfflineError = string.Empty;
        _navigation.Navigate(PageId.Accounts);
    }
}
