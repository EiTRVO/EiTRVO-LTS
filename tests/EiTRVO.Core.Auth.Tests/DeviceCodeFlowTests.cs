using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Tests.Fakes;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// 设备代码流的轮询状态机（D-1）与钓鱼防护（D-3）。
/// <para>
/// "等待"被注入成 <c>(delay, token) =&gt; Task.CompletedTask</c>，因此每一条路径（继续等 / 慢下来 /
/// 过期 / 被拒 / 成功）都能在毫秒级内被断言 —— 真等 5 秒一次的间隔会让这一组用例没人愿意跑。
/// </para>
/// </summary>
[TestClass]
public sealed class DeviceCodeFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheHappyPathReportsTheThreeUserVisibleStages()
    {
        var client = new ScriptedDeviceCodeClient()
            .ReturnCode("DEVICE-CODE", "K7QP-4M2X", "microsoft.com/link", expiresIn: 900, interval: 1)
            .Poll(DeviceCodePollKind.Pending)
            .Poll(DeviceCodePollKind.Pending)
            .Poll(DeviceCodePollKind.Tokens);

        var (flow, stages, _) = NewFlow(client);

        var session = await flow.StartAsync(stages);

        Assert.AreEqual("https://microsoft.com/link", session.VerificationUrl, "验证地址被规范成绝对 https 地址。");
        Assert.AreEqual("K7QP-4M2X", session.UserCode);
        Assert.AreEqual(DeviceCodeState.Waiting, session.State);
        Assert.AreEqual(15, session.ExpiresInMinutes);

        var outcome = await flow.WaitAsync(stages);

        Assert.IsTrue(outcome.Succeeded);
        Assert.AreEqual("ms-access", outcome.Tokens!.AccessToken);
        Assert.AreEqual(DeviceCodeState.Authorized, flow.Current!.State);
        Assert.AreEqual(3, client.Polls);

        // 三个用户看得见的时刻：① 申请代码 ② 等授权 ③ 已授权。
        CollectionAssert.AreEqual(
            new[] { AuthStage.RequestingCode, AuthStage.WaitingForUser, AuthStage.Authorized },
            stages.Values.Select(static s => s.Stage).ToArray());
    }

    [TestMethod]
    public async Task SlowDownMakesTheNextWaitLonger()
    {
        var client = new ScriptedDeviceCodeClient()
            .ReturnCode("D", "C", "microsoft.com/link", expiresIn: 900, interval: 1)
            .Poll(DeviceCodePollKind.SlowDown)
            .Poll(DeviceCodePollKind.Tokens);

        var (flow, _, delays) = NewFlow(client);

        await flow.StartAsync();
        await flow.WaitAsync();

        // 官方的规定是每次 +5 秒；不当回事地继续按原间隔轮询会被限流，登录就卡在这里。
        CollectionAssert.AreEqual(
            new[] { 1d, 6d },
            delays.Select(static d => d.TotalSeconds).ToArray());
    }

    [TestMethod]
    public async Task AnExpiredCodeEndsTheFlowByTheClockNotByThePollCount()
    {
        var clock = new MutableClock(Now);

        var client = new ScriptedDeviceCodeClient()
            .ReturnCode("D", "C", "microsoft.com/link", expiresIn: 60, interval: 5)
            .Poll(DeviceCodePollKind.Pending)
            .Poll(DeviceCodePollKind.Pending);

        var flow = new DeviceCodeFlow(
            client,
            clock,
            new RecordingDiagnosticsLog(),
            (delay, _) =>
            {
                // 每次"等待"都把时钟往前推：时间旅行代替真的等 60 秒。
                clock.Now = clock.Now.Add(delay);
                return Task.CompletedTask;
            });

        await flow.StartAsync();

        var outcome = await flow.WaitAsync();

        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(DeviceCodeState.Expired, flow.Current!.State);
        Assert.IsNotNull(outcome.Failure);
        Assert.AreEqual("设备代码已过期，请重新发起登录。", outcome.Failure.Key, "过期那句是**它自己的键**（App 层再拼句子）。");

        // 60 秒有效期 ÷ 5 秒间隔 = 第 12 次等待时跨过截止时刻，因此只轮询了 11 次。
        // 断言这个数就是在钉"到点就停"：多轮一次是被官方限流的开始，少轮一次则可能漏掉授权。
        Assert.AreEqual(11, client.Polls, "过期之后不该继续轮询。");
    }

    [TestMethod]
    public async Task ADeniedAuthorizationEndsTheFlow()
    {
        var client = new ScriptedDeviceCodeClient()
            .ReturnCode("D", "C", "microsoft.com/link", expiresIn: 900, interval: 1)
            .Poll(DeviceCodePollKind.Denied);

        var (flow, _, _) = NewFlow(client);

        await flow.StartAsync();
        var outcome = await flow.WaitAsync();

        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(DeviceCodeState.Failed, flow.Current!.State);
        Assert.AreEqual("授权被拒绝或代码无效，请重新发起登录。", outcome.Failure!.Key);
    }

    [TestMethod]
    public async Task AHostileVerificationUrlIsRefusedBeforeItReachesTheUi()
    {
        // D-3 的关键一条：校验在**展示之前**。可疑地址不会被交给界面，
        // 所以钓鱼页不会被显示成"请到这里输入你的代码"。
        var client = new ScriptedDeviceCodeClient()
            .ReturnCode("D", "C", "microsoft-login.evil.example/link", expiresIn: 900, interval: 1);

        var (flow, _, _) = NewFlow(client);

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(() => flow.StartAsync());

        Assert.AreEqual(
            "Microsoft 返回的验证地址不在允许的登录域名内，已中止这次登录。",
            exception.Reason.Key,
            "失败原因也是「键 + 参数」：App 层才拼句子。");
        Assert.IsNotNull(flow.Current);
        Assert.AreEqual(DeviceCodeState.Failed, flow.Current.State);
        Assert.AreEqual(string.Empty, flow.Current.VerificationUrl, "可疑地址不进会话。");
        Assert.IsFalse(
            flow.Current.Failure!.Key.Contains("evil.example", StringComparison.Ordinal),
            "连原因里都不该出现它 —— 那句话的作用是让人别去，而不是告诉他去哪儿。");
    }

    [TestMethod]
    public async Task WaitingWithoutStartingFirstIsARefusal()
    {
        var (flow, _, _) = NewFlow(new ScriptedDeviceCodeClient());

        await Assert.ThrowsExceptionAsync<AuthException>(() => flow.WaitAsync());
    }

    private static (DeviceCodeFlow Flow, CollectingProgress<AuthProgress> Stages, List<TimeSpan> Delays) NewFlow(
        ScriptedDeviceCodeClient client)
    {
        var stages = new CollectingProgress<AuthProgress>();
        var delays = new List<TimeSpan>();

        var flow = new DeviceCodeFlow(
            client,
            new FixedClock(Now),
            new RecordingDiagnosticsLog(),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        return (flow, stages, delays);
    }

    /// <summary>脚本化的设备代码客户端：按预置顺序回答轮询结论。</summary>
    private sealed class ScriptedDeviceCodeClient : IDeviceCodeClient
    {
        private readonly Queue<DeviceCodePollKind> _polls = new();

        private string _code = "D";
        private string _userCode = "C";
        private string _verificationUri = "microsoft.com/link";
        private int _expiresIn = 900;
        private int _interval = 1;
        private bool _configured;

        /// <summary>被轮询的次数。</summary>
        public int Polls { get; private set; }

        public ScriptedDeviceCodeClient ReturnCode(
            string code,
            string userCode,
            string verificationUri,
            int expiresIn,
            int interval)
        {
            _code = code;
            _userCode = userCode;
            _verificationUri = verificationUri;
            _expiresIn = expiresIn;
            _interval = interval;
            _configured = true;
            return this;
        }

        public ScriptedDeviceCodeClient Poll(DeviceCodePollKind kind)
        {
            _polls.Enqueue(kind);
            return this;
        }

        public Task<DeviceCodeChallenge> RequestCodeAsync(CancellationToken cancellationToken = default)
        {
            if (!_configured)
            {
                throw new InvalidOperationException("用例没有为这次设备代码申请准备响应。");
            }

            return Task.FromResult(new DeviceCodeChallenge(_code, _userCode, _verificationUri, _expiresIn, _interval));
        }

        public Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken cancellationToken = default)
        {
            Polls++;
            var kind = _polls.Count > 0 ? _polls.Dequeue() : DeviceCodePollKind.Pending;

            return Task.FromResult(kind == DeviceCodePollKind.Tokens
                ? new DeviceCodePoll(kind, new MicrosoftTokens("ms-access", "refresh", Now.AddHours(1)))
                : new DeviceCodePoll(kind));
        }

        public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("这一组用例不涉及刷新。");
    }
}
