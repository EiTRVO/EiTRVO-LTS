using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>走完一次完整的正版登录（设备代码流 + 刷新链 + 落盘）。</summary>
public interface IMicrosoftSignIn
{
    /// <summary>登录一个 Microsoft 账户，成功时它已经落盘并成为默认账户。</summary>
    /// <param name="progress">进度接收者（D-1 的三个用户可见时刻都由它上报）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>落盘后的账户行。</returns>
    /// <exception cref="AuthException">任何一步失败；<see cref="AuthException.Reason"/> 是给用户看的原因（键 + 参数）。</exception>
    Task<Account> SignInAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IMicrosoftSignIn"/> 的真实实现：把四件事按顺序串起来 ——
/// 申请设备代码 → 等用户授权 → 走刷新链拿档案 → **全链成功才落盘**。
/// <para>
/// 刻意做得很薄：每一段的能力都在它自己的类型里（<see cref="DeviceCodeFlow"/> 管轮询与钓鱼防护，
/// <see cref="MinecraftAuthChain"/> 管四个端点，<c>IAccountCatalog</c> 管加密落盘），
/// 这里只负责顺序与"哪一步失败该说哪句话"。合成一个方法的代价是它自己没法被单测
/// —— 而"登录失败时用户看到什么"恰恰是最需要被钉住的部分。
/// </para>
/// </summary>
public sealed class MicrosoftSignIn : IMicrosoftSignIn
{
    private readonly IDeviceCodeFlow _flow;
    private readonly IMinecraftAuthChain _chain;
    private readonly IAccountCatalog _accounts;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造登录服务。</summary>
    /// <param name="flow">设备代码流。</param>
    /// <param name="chain">令牌刷新链。</param>
    /// <param name="accounts">账户目录（落盘）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public MicrosoftSignIn(
        IDeviceCodeFlow flow,
        IMinecraftAuthChain chain,
        IAccountCatalog accounts,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _flow = flow;
        _chain = chain;
        _accounts = accounts;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public async Task<Account> SignInAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _flow.StartAsync(progress, cancellationToken).ConfigureAwait(false);

        var outcome = await _flow.WaitAsync(progress, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            // 过期/被拒**不是异常**：它们是设备代码流正常的两种结局，原因已经在会话里了。
            throw new AuthException(
                outcome.Failure ?? TextTemplate.Of("登录没有完成，请重新发起。"));
        }

        var login = await _chain
            .CompleteAsync(outcome.Tokens!, progress, cancellationToken)
            .ConfigureAwait(false);

        var account = _accounts.AddMicrosoft(login);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Auth,
            "正版账户 {0} 登录成功并已加密落盘",
            [account.PlayerName]);

        progress?.Report(new AuthProgress(AuthStage.Completed));

        return account;
    }
}
