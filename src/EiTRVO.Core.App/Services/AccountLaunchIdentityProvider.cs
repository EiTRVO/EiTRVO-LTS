using System.IO;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 把账户目录里的凭据交给内核的启动编排。
/// <para>
/// 存在的理由只有一条：内核**不引用认证层**（依赖方向是 Auth → Launcher），
/// 所以"用哪个账户、令牌从哪来"必须由界面层提供。这里把 <c>LaunchCredential</c> 映射成
/// <see cref="LaunchIdentity"/> —— 纯数据搬运，但失败的几种原因要翻成几句不同的人话
/// （"还没有账户""这个号要重新登录""网络断了，再点一次"），而不是一律"启动失败"。
/// </para>
/// <para>
/// 正版账户的令牌刷新发生在 <c>IAccountCatalog.GetCredentialAsync</c> 里面（它有账户与加密存储），
/// 这里只负责把结果或失败原因翻译给用户 —— 界面层不该知道刷新链有几步。
/// </para>
/// <para>
/// 刻意不叫 <c>LaunchService</c> 也不额外包一层"启动服务"：ViewModel 直接用内核的
/// <see cref="IGameLauncher"/>（快照 + 命令），多一层转发只会多一处要跟着改的地方。
/// </para>
/// </summary>
internal sealed class AccountLaunchIdentityProvider : ILaunchIdentityProvider
{
    private readonly IAccountCatalog _accounts;
    private readonly IInstanceCatalog _instances;
    private readonly IClock _clock;

    /// <summary>构造凭据来源。</summary>
    /// <param name="accounts">账户目录。</param>
    /// <param name="instances">实例目录（读实例绑定的账户）。</param>
    /// <param name="clock">时间源（记录账户「上次使用」）。</param>
    public AccountLaunchIdentityProvider(
        IAccountCatalog accounts,
        IInstanceCatalog instances,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(clock);

        _accounts = accounts;
        _instances = instances;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<LaunchIdentity> GetIdentityAsync(
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        string? bound;

        try
        {
            bound = _instances.GetLaunchInfo(instanceName).Account;
        }
        catch (KeyNotFoundException)
        {
            // 实例不存在：交给内核的启动流程去报（它才知道该说什么）。
            throw new LaunchIdentityException(TextTemplate.Of("找不到实例「{0}」。", instanceName));
        }

        try
        {
            var credential = await _accounts.GetCredentialAsync(bound, cancellationToken).ConfigureAwait(false);

            // 2026-10-25 用户要求：一个正版账号都没登录过时，不允许用离线账户启动游戏。
            // 「登录过」看账户库里有没有 Microsoft 账户（令牌过期也算登录过，重新登录是账户页的事）；
            // 只要曾经登录过一个正版账号，离线账户就仍可启动单机。
            if (string.Equals(credential.UserType, "mojang", StringComparison.OrdinalIgnoreCase)
                && !_accounts.GetAccounts().Any(static account => account.Kind == AccountKind.Microsoft))
            {
                throw new LaunchIdentityException(TextTemplate.Of(
                    "还没有登录过 Microsoft 正版账号，离线账户不能启动游戏。请先到「账户」页登录正版账号。"));
            }

            // 「上次使用」在拿到凭据、准备启动时落盘：下次打开页头账户下拉就能看到真实时间。
            // 只更新这一条记录，不改令牌与默认标记；写失败不影响这次启动（账户文件坏了应该在启动前就报过）。
            try
            {
                _accounts.RecordUse(credential.PlayerName, _clock.Now);
            }
            catch (Exception exception) when (exception is AccountStoreException
                                                or IOException
                                                or UnauthorizedAccessException)
            {
                // 账户文件读不出来时启动本身仍会失败（GetCredentialAsync 已先抛）；这里不重复报。
                // 仅在更新「上次使用」时写盘失败，也不该拦住这次启动。
            }

            return new LaunchIdentity(
                PlayerName: credential.PlayerName,
                Uuid: credential.Uuid,
                AccessToken: credential.AccessToken,
                UserType: credential.UserType);
        }
        catch (CredentialException exception)
        {
            throw new LaunchIdentityException(Describe(exception), exception);
        }
    }

    /// <summary>把凭据失败原因翻成「键 + 参数」（句子由 <c>LocalizedText</c> 在界面层拼）。</summary>
    private static TextTemplate Describe(CredentialException exception) => exception.Failure switch
    {
        CredentialFailure.NoSuchAccount =>
            TextTemplate.Of("还没有可用的账户。请先到「账户」页登录 Microsoft 正版账号；没有登录过正版账号时，离线账户也不能启动游戏。"),
        CredentialFailure.SessionExpired =>
            TextTemplate.Of("这个正版账户的登录已失效，需要到「账户」页重新登录一次。"),
        CredentialFailure.RefreshFailed when exception.Reason is not null =>
            // 内层也是「键 + 参数」：在这里先把它拼成当前语言，再嵌进外层句子 ——
            // 直接把内层的**键**当参数，英文界面里会露出中文。
            TextTemplate.Of(
                "刷新正版登录令牌失败（{0}）。网络正常时再启动一次即可。",
                LocalizedText.Of(exception.Reason)),
        CredentialFailure.RefreshFailed =>
            TextTemplate.Of("刷新正版登录令牌失败。网络正常时再启动一次即可。"),
        _ => TextTemplate.Of("这个账户当前不可用，无法启动游戏。"),
    };
}
