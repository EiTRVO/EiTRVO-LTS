using System.Net;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Tests.Fakes;
using EiTRVO.Core.Launcher.Net;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// 端点客户端：请求参数、响应解析与"OAuth 的 400 不是异常"。
/// <para>
/// 一对容易搞混的字段形状必须被钉死：请求是**表单**（<c>client_id</c> / <c>scope</c>），
/// 响应是**蛇形 JSON**（<c>device_code</c> / <c>access_token</c> / <c>expires_in</c>），
/// 而全仓库的 JSON 选项是驼峰策略 —— 不显式标注的话这些字段永远不会被绑定，
/// 表现是"拿到了 200 但每个字段都是空的"。
/// </para>
/// </summary>
[TestClass]
public sealed class DeviceCodeClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheDeviceCodeRequestCarriesTheClientIdAndScopeAsAForm()
    {
        var handler = new StubHandler().RespondTo(
            "/devicecode",
            HttpStatusCode.OK,
            """
            {
              "user_code": "K7QP-4M2X",
              "device_code": "DEVICE-CODE",
              "verification_uri": "https://microsoft.com/link",
              "expires_in": 900,
              "interval": 5,
              "message": "To sign in, use a web browser to open ..."
            }
            """);

        var challenge = await NewClient(handler).RequestCodeAsync();

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", request.Url);
        StringAssert.Contains(request.Body, "client_id=11111111-2222-3333-4444-555555555555");
        StringAssert.Contains(request.Body, "scope=XboxLive.signin+offline_access");

        Assert.AreEqual("K7QP-4M2X", challenge.UserCode);
        Assert.AreEqual("DEVICE-CODE", challenge.DeviceCode);
        Assert.AreEqual("https://microsoft.com/link", challenge.VerificationUri);
        Assert.AreEqual(900, challenge.ExpiresInSeconds);
        Assert.AreEqual(5, challenge.IntervalSeconds);
    }

    [TestMethod]
    public async Task MissingIntervalFallsBackToTheOfficialFiveSeconds()
    {
        var handler = new StubHandler().RespondTo(
            "/devicecode",
            HttpStatusCode.OK,
            "{\"device_code\":\"D\",\"user_code\":\"C\",\"verification_uri\":\"https://microsoft.com/link\"}");

        var challenge = await NewClient(handler).RequestCodeAsync();

        Assert.AreEqual(5, challenge.IntervalSeconds, "响应没给 interval 时按官方的 5 秒。");
        Assert.AreEqual(900, challenge.ExpiresInSeconds);
    }

    [DataTestMethod]
    [DataRow("authorization_pending", DeviceCodePollKind.Pending)]
    [DataRow("slow_down", DeviceCodePollKind.SlowDown)]
    [DataRow("expired_token", DeviceCodePollKind.Expired)]
    [DataRow("access_denied", DeviceCodePollKind.Denied)]
    [DataRow("bad_verification_code", DeviceCodePollKind.Denied)]
    public async Task OAuthErrorsBecomePollResultsInsteadOfExceptions(string error, DeviceCodePollKind expected)
    {
        // 设备代码流**靠 400 工作**：把它当异常处理会让"正在等你授权"变成一条失败日志。
        var handler = new StubHandler().RespondTo(
            "/token",
            HttpStatusCode.BadRequest,
            "{\"error\":\"" + error + "\",\"error_description\":\"...\"}");

        var poll = await NewClient(handler).PollAsync("DEVICE-CODE");

        Assert.AreEqual(expected, poll.Kind);
        Assert.AreEqual(error, poll.Error);
    }

    [TestMethod]
    public async Task AnUnknownErrorIsNotSilentlyTreatedAsStillWaiting()
    {
        // 认不出的错误当成"还在等"会让用户永远等下去。
        var handler = new StubHandler().RespondTo(
            "/token",
            HttpStatusCode.BadRequest,
            "{\"error\":\"interaction_required\",\"error_description\":\"need user action\"}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => NewClient(handler).PollAsync("D"));

        Assert.AreEqual("轮询授权结果失败：{0}（{1}）：{2}", exception.Reason.Key);
        CollectionAssert.AreEqual(
            new[] { "Bad Request", "400", "need user action" },
            exception.Reason.Parameters.ToArray(),
            "状态码与 OAuth 的 error_description 都是**参数**，不是拼进键里的字面量。");
    }

    [TestMethod]
    public async Task ASuccessfulPollReturnsTokensWithTheExpiryFromTheClock()
    {
        var handler = new StubHandler().RespondTo(
            "/token",
            HttpStatusCode.OK,
            "{\"access_token\":\"ms-access\",\"refresh_token\":\"ms-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");

        var poll = await NewClient(handler).PollAsync("D");

        Assert.AreEqual(DeviceCodePollKind.Tokens, poll.Kind);
        Assert.AreEqual("ms-access", poll.Tokens!.AccessToken);
        Assert.AreEqual("ms-refresh", poll.Tokens.RefreshToken);
        Assert.AreEqual(Now.AddHours(1), poll.Tokens.ExpiresAt);
    }

    [TestMethod]
    public async Task TheRefreshGrantKeepsTheRotatedRefreshToken()
    {
        var handler = new StubHandler().RespondTo(
            "/token",
            HttpStatusCode.OK,
            "{\"access_token\":\"ms-access-2\",\"refresh_token\":\"ms-refresh-2\",\"expires_in\":3600}");

        var tokens = await NewClient(handler).RefreshAsync("ms-refresh-1");

        var request = handler.Requests.Single();
        StringAssert.Contains(request.Body, "grant_type=refresh_token");
        StringAssert.Contains(request.Body, "refresh_token=ms-refresh-1");

        Assert.AreEqual("ms-refresh-2", tokens.RefreshToken, "刷新令牌是轮换的，新的那一份必须被带回去。");
    }

    [TestMethod]
    public async Task AnInvalidGrantMeansSignInRequiredNotJustANetworkError()
    {
        // 两者的界面文案不同：一个要用户重新登录，另一个只要他再点一次。
        var handler = new StubHandler().RespondTo(
            "/token",
            HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70043: The refresh token has expired\"}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => NewClient(handler).RefreshAsync("stale"));

        Assert.IsTrue(exception.RequiresSignIn);
        Assert.AreEqual("这个账户的登录已过期，需要重新登录一次。", exception.Reason.Key);
    }

    [TestMethod]
    public async Task ANetworkFailureWhileRefreshingDoesNotRequireSigningIn()
    {
        var handler = new StubHandler().RespondTo("/token", HttpStatusCode.InternalServerError, "{}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => NewClient(handler).RefreshAsync("refresh"));

        Assert.IsFalse(exception.RequiresSignIn, "服务端 5xx 是「等会儿再试」，不是「重新登录」。");
    }

    [TestMethod]
    public async Task AResponseThatIsNotJsonIsReportedWithTheStatus()
    {
        var handler = new StubHandler().RespondTo("/devicecode", HttpStatusCode.OK, "<html>rate limited</html>");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => NewClient(handler).RequestCodeAsync());

        Assert.AreEqual("认证服务返回的不是合法 JSON。", exception.Reason.Key);
    }

    private static DeviceCodeClient NewClient(StubHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            HttpHostPolicy.Default,
            new AuthOptions { ClientId = "11111111-2222-3333-4444-555555555555" },
            new FixedClock(Now));
}
