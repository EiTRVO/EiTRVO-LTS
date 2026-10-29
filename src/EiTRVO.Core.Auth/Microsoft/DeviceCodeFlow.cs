using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>
/// <see cref="Abstractions.IDeviceCodeFlow"/> 的真实实现：设备代码流的**轮询状态机**。
/// <para>
/// 它是纯逻辑 + 一个注入的"等待"函数，因此可以对 400/<c>authorization_pending</c>/<c>slow_down</c>/
/// <c>expired_token</c> 的每一条路径断言（规划 §4 验收第 2 条），而不用真的等 5 秒。
/// </para>
/// <para>
/// 三条刻意的行为：
/// </para>
/// <list type="number">
/// <item><b>先校验再展示</b>（D-3）：端点返回的验证地址必须落在 Microsoft 登录域白名单内。
/// 校验失败时**不把地址交给界面**，只把域名写进诊断事件（钓鱼页的特征就是一个像模像样的登录页，
/// 而"请到这里输入代码"这句话本身就是把用户送过去的邀请）。</item>
/// <item><b><c>slow_down</c> 要真的慢下来</b>：官方要求每次加 5 秒；不当回事地继续按原间隔轮询，
/// 后果是被限流，登录就卡在这里了。</item>
/// <item><b>过期由时钟判定，不由轮询次数判定</b>：<c>expires_in</c> 是官方给的秒数，
/// 而"已经等了多久"必须来自 <see cref="IClock"/> —— 否则测试要么真等 15 分钟，要么把过期逻辑删掉。</item>
/// </list>
/// </summary>
public sealed class DeviceCodeFlow : Abstractions.IDeviceCodeFlow
{
    /// <summary><c>slow_down</c> 时每次追加的间隔（官方的规定值）。</summary>
    public static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    private readonly IDeviceCodeClient _client;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    private DeviceCodeChallenge? _challenge;

    /// <summary>构建设备代码流。</summary>
    /// <param name="client">端点客户端。</param>
    /// <param name="clock">时间源（有效期判定要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    /// <param name="delayAsync">等待实现；缺省用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。</param>
    public DeviceCodeFlow(
        IDeviceCodeClient client,
        IClock clock,
        IDiagnosticsLog diagnostics,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _client = client;
        _clock = clock;
        _diagnostics = diagnostics;
        _delayAsync = delayAsync ?? (static (delay, token) => Task.Delay(delay, token));
    }

    /// <inheritdoc />
    public DeviceCodeSession? Current { get; private set; }

    /// <inheritdoc />
    public async Task<DeviceCodeSession> StartAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new AuthProgress(AuthStage.RequestingCode));

        var challenge = await _client.RequestCodeAsync(cancellationToken).ConfigureAwait(false);

        var uri = SignInUrlPolicy.Normalize(challenge.VerificationUri);

        if (uri is null || !SignInUrlPolicy.IsAllowed(uri))
        {
            // 只把域名写进诊断日志（排障需要它），不写进给用户看的那句话 ——
            // 那句话的作用是"告诉用户别去"，而不是"告诉用户去哪儿"。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Auth,
                "设备代码流的验证地址不在登录域名白名单内（{0}），已中止登录",
                [uri?.Host ?? "（不是合法 URL）"]);

            var rejected = new DeviceCodeSession(
                string.Empty,
                string.Empty,
                DeviceCodeState.Failed,
                ExpiresInMinutes: 0,
                Failure: TextTemplate.Of("Microsoft 返回的验证地址不在允许的登录域名内，已中止这次登录。"));

            Current = rejected;

            throw new AuthException(rejected.Failure!);
        }

        _challenge = challenge with { VerificationUri = uri.ToString() };

        // 有效期只按分钟展示（设计稿的文案就是"（代码 N 分钟内有效）"）。
        var minutes = Math.Max(1, (int)Math.Ceiling(challenge.ExpiresInSeconds / 60.0));

        Current = new DeviceCodeSession(
            VerificationUrl: uri.ToString(),
            UserCode: challenge.UserCode,
            State: DeviceCodeState.Waiting,
            ExpiresInMinutes: minutes);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Auth,
            "已申请设备代码（{0}），等待用户在浏览器完成授权（代码 {1} 分钟内有效）",
            [uri.Host, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        progress?.Report(new AuthProgress(AuthStage.WaitingForUser));

        return Current;
    }

    /// <inheritdoc />
    public async Task<DeviceCodeOutcome> WaitAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var challenge = _challenge
                        ?? throw new AuthException(
                            TextTemplate.Of("还没有申请设备代码，无法等待授权（请先调用 StartAsync）。"));

        var interval = TimeSpan.FromSeconds(challenge.IntervalSeconds);
        var deadline = _clock.Now.AddSeconds(challenge.ExpiresInSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 官方的节奏是"先等一个间隔，再问一次"；第一次就立刻问会被判成 slow_down。
            await _delayAsync(interval, cancellationToken).ConfigureAwait(false);

            if (_clock.Now >= deadline)
            {
                return Finish(
                    DeviceCodeState.Expired,
                    TextTemplate.Of("设备代码已过期，请重新发起登录。"),
                    progress);
            }

            var poll = await _client.PollAsync(challenge.DeviceCode, cancellationToken).ConfigureAwait(false);

            switch (poll.Kind)
            {
                case DeviceCodePollKind.Pending:
                    continue;

                case DeviceCodePollKind.SlowDown:
                    interval += SlowDownIncrement;
                    continue;

                case DeviceCodePollKind.Expired:
                    return Finish(
                        DeviceCodeState.Expired,
                        TextTemplate.Of("设备代码已过期，请重新发起登录。"),
                        progress);

                case DeviceCodePollKind.Denied:
                    return Finish(
                        DeviceCodeState.Failed,
                        TextTemplate.Of("授权被拒绝或代码无效，请重新发起登录。"),
                        progress);

                case DeviceCodePollKind.Tokens:
                {
                    _diagnostics.Write(
                        LauncherEventLevel.Information,
                        DiagnosticsSources.Auth,
                        "用户已完成授权",
                        []);

                    Current = Current! with { State = DeviceCodeState.Authorized };
                    progress?.Report(new AuthProgress(AuthStage.Authorized));

                    return new DeviceCodeOutcome(Current, poll.Tokens, Failure: null);
                }

                default:
                    throw new AuthException(
                        TextTemplate.Of("设备代码流收到了不认识的轮询结论：{0}。", poll.Kind.ToString()));
            }
        }
    }

    /// <summary>把会话推进到一个终态并如实上报。</summary>
    /// <param name="state">终态。</param>
    /// <param name="reason">失败原因（模板键 + 参数）。</param>
    /// <param name="progress">进度接收者。</param>
    private DeviceCodeOutcome Finish(
        DeviceCodeState state,
        TextTemplate reason,
        IProgress<AuthProgress>? progress)
    {
        Current = Current! with { State = state, Failure = reason };

        // 事件与用户看到的是**同一句话**（键 + 参数），所以中英两种界面读这段日志都对得上；
        // 参数表直接引用原因自己的 Parameters —— 那个键的占位符个数由 UserTextKeyTests 对账。
        _diagnostics.Write(
            state == DeviceCodeState.Expired ? LauncherEventLevel.Warning : LauncherEventLevel.Error,
            DiagnosticsSources.Auth,
            reason.Key,
            reason.Parameters);

        progress?.Report(new AuthProgress(AuthStage.Failed));

        return new DeviceCodeOutcome(Current, Tokens: null, Failure: reason);
    }
}
