using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// 「添加 Microsoft 账户」对话框（p15 的模态层）。
/// 对应 mockup/pages/p15-dialog-account-login.html。
/// <para>
/// M5-1 起它是**真的**：设备代码流由 <see cref="IMicrosoftSignIn"/> 走完（申请代码 → 轮询授权 →
/// Xbox Live / XSTS / Minecraft 服务 → 加密落盘），界面显示的所有值都来自内核。
/// 启动器 <b>不会</b> 也无法获取用户密码 —— 这一页只显示代码，没有任何输入密码的 UI
/// （AGENTS.md §6 明令禁止内嵌浏览器收密码）。
/// </para>
/// <para>
/// 流程由 <see cref="Refresh"/> 启动：外壳在页面被导航到时（也含语言切换时）会调它一次，
/// 而"打开这一页"就是发起登录的时刻。已经在跑就不重开 —— 否则切个语言就会把上一个会话丢掉。
/// </para>
/// </summary>
internal sealed partial class AddAccountDialogViewModel : ObservableObject, IPageDialog, IDisposable
{
    /// <summary>还没拿到代码时的占位会话（用户代码那一格显示 <c>—</c>，而不是空白）。</summary>
    private static readonly DeviceCodeSession Pending =
        new(string.Empty, "—", DeviceCodeState.Waiting, ExpiresInMinutes: 15);

    private readonly IMicrosoftSignIn _signIn;
    private readonly IDeviceCodeFlow _flow;
    private readonly IShellService _shell;
    private readonly INavigationService _navigation;

    private CancellationTokenSource? _session;
    private AuthStage? _stage;
    private string? _error;
    private bool _signingIn;

    /// <summary>构造添加账户对话框。</summary>
    /// <param name="signIn">完整的正版登录流程（设备代码流 + 刷新链 + 落盘）。</param>
    /// <param name="flow">设备代码流（取当前会话：链接与用户代码）。</param>
    /// <param name="shell">外壳服务（复制链接 / 代码、打开浏览器）。</param>
    /// <param name="navigation">导航服务（成功或取消之后离开本页）。</param>
    public AddAccountDialogViewModel(
        IMicrosoftSignIn signIn,
        IDeviceCodeFlow flow,
        IShellService shell,
        INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(navigation);

        _signIn = signIn;
        _flow = flow;
        _shell = shell;
        _navigation = navigation;
    }

    /// <summary>设备代码流会话（链接 + 用户代码）；还没拿到代码时是占位值。</summary>
    public DeviceCodeSession Session => _flow.Current ?? Pending;

    /// <summary>
    /// 界面上显示的链接文本（**去掉协议**的短形式）。
    /// </summary>
    /// <remarks>
    /// 设计稿里这一格写的就是短形式（<c>microsoft.com/link</c>，见 mockup/pages/p15-dialog-account-login.html）——
    /// 那不是省事：这一行还要放下「复制链接」与「用系统浏览器打开」两个按钮，带上 <c>https://</c>
    /// 就会把最后一个按钮挤出对话框（2026-10-06 的 Bug3.png 就是这个）。
    /// 复制到剪贴板与用浏览器打开用的都是**完整 URL**（<see cref="CopyLink"/> / <see cref="OpenLink"/>），
    /// 完整值也挂在 `ToolTip` 上。
    /// </remarks>
    public string LinkText => Session.VerificationUrl.Length == 0
        ? Session.VerificationUrl
        : Session.VerificationUrl
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>最下面那行状态 / 失败原因。</summary>
    /// <remarks>
    /// 设计稿在这里只有一句"等待你在浏览器中完成授权…"，而失败同样是这一行 ——
    /// 失败原因必须看得见，为它新增一个控件属于改设计（S9 的范畴）。
    /// <para>
    /// 文案**以会话状态为准**（它是同步可读的：设备代码流一拿到代码就写好），
    /// 进度上报只用来区分"已授权"之后的两个细分动作 —— 全部押在异步上报上会让界面出现
    /// "代码都已经显示出来了、提示还写着正在申请"这种自相矛盾的状态。
    /// </para>
    /// </remarks>
    public string WaitingText => _error ?? Session.State switch
    {
        DeviceCodeState.Waiting => HasCode
            ? Loc.T("等待你在浏览器中完成授权…（代码 {0} 分钟内有效）", Session.ExpiresInMinutes)
            : Loc.T("正在向 Microsoft 申请设备代码…"),
        DeviceCodeState.Authorized => _stage == AuthStage.CompletingSignIn
            ? Loc.T("正在完成登录（Xbox Live → Minecraft 服务）…")
            : Loc.T("已完成授权，正在换取令牌…"),
        DeviceCodeState.Expired => LocalizedText.OrNull(Session.Failure) ?? Loc.T("设备代码已过期，请返回后重新打开本页。"),
        _ => LocalizedText.OrNull(Session.Failure) ?? Loc.T("登录没有完成，请返回后重试。"),
    };

    /// <summary>链接与代码是否已经拿到（没拿到时两个复制按钮是灰的）。</summary>
    public bool HasCode => Session.UserCode.Length > 0 && Session.UserCode != "—";

    /// <inheritdoc />
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);

        if (SignInCommand.CanExecute(null))
        {
            SignInCommand.Execute(null);
        }
    }

    /// <summary>
    /// 释放正在进行的轮询（应用退出时由容器调用）。
    /// </summary>
    /// <remarks>
    /// 这个对话框会在用户等待授权期间一直持有一个 <see cref="CancellationTokenSource"/>，
    /// 它是可释放对象 —— 不实现 <see cref="IDisposable"/> 的话它就只是一个"谁也不知道什么时候该清"的字段。
    /// </remarks>
    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// 走完整个登录流程。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="Progress{T}"/> 而不是自己排线程：它把回调送回**创建它的那个同步上下文**
    /// （这里就是 UI 线程），因此下面几个字段可以被直接赋值 —— 这正是 D-1 说的
    /// "3 个 UI 回调 → IProgress&lt;AuthProgress&gt;"。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        _signingIn = true;
        _error = null;
        _stage = AuthStage.RequestingCode;
        SignInCommand.NotifyCanExecuteChanged();
        Notify();

        _session = new CancellationTokenSource();

        var progress = new Progress<AuthProgress>(report =>
        {
            // 流程已经结束（成功/失败/取消）之后到达的上报一律忽略：
            // 否则一句"正在换取令牌…"会把失败原因盖掉。
            if (!_signingIn)
            {
                return;
            }

            _stage = report.Stage;
            Notify();
        });

        try
        {
            await _signIn.SignInAsync(progress, _session.Token);

            _stage = AuthStage.Completed;

            // 成功之后回账户页：新账户已经在列表里（p11 被导航到时重读账户文件）。
            _navigation.Navigate(PageId.Accounts);
        }
        catch (OperationCanceledException)
        {
            // 用户点了取消（或关掉窗口）：什么都不说。
        }
        catch (AuthException ex)
        {
            // 内核给的是「键 + 参数」，句子在这一层拼（Q8）—— 直接显示 ex.Message 会露出键。
            _error = LocalizedText.Of(ex.Reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // 网络类失败与"登录被拒"分开说：前者再试一次就行。
            _error = Loc.T("网络不可用，请稍后重试。");
        }
        finally
        {
            _signingIn = false;
            _session?.Dispose();
            _session = null;
            SignInCommand.NotifyCanExecuteChanged();
            Notify();
        }
    }

    private bool CanSignIn() => !_signingIn;

    /// <summary>复制链接。</summary>
    [RelayCommand(CanExecute = nameof(HasCode))]
    private void CopyLink() => _shell.CopyToClipboard(Session.VerificationUrl);

    /// <summary>用系统默认浏览器打开授权页（绝不内嵌浏览器）。</summary>
    [RelayCommand(CanExecute = nameof(HasCode))]
    private void OpenLink() => _shell.OpenUrl(Session.VerificationUrl);

    /// <summary>复制用户代码。</summary>
    [RelayCommand(CanExecute = nameof(HasCode))]
    private void CopyCode() => _shell.CopyToClipboard(Session.UserCode);

    /// <summary>取消：中止轮询并回账户页。</summary>
    [RelayCommand]
    private void Cancel()
    {
        _session?.Cancel();
        _navigation.Navigate(PageId.Accounts);
    }

    /// <summary>把与状态相关的绑定一起通知一遍（会话对象、状态文本、按钮可用性都会变）。</summary>
    private void Notify()
    {
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(LinkText));
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(HasCode));

        CopyLinkCommand.NotifyCanExecuteChanged();
        OpenLinkCommand.NotifyCanExecuteChanged();
        CopyCodeCommand.NotifyCanExecuteChanged();
    }
}
