using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>设备代码流与令牌刷新的**端点客户端**（只管 HTTP 与响应形状，不管轮询节奏）。</summary>
public interface IDeviceCodeClient
{
    /// <summary>申请一个设备代码。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>挑战（设备代码、用户代码、验证地址、有效期与间隔）。</returns>
    /// <exception cref="AuthException">状态码异常、响应不是合法 JSON、或缺少必需字段。</exception>
    Task<DeviceCodeChallenge> RequestCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>轮询一次授权结果。</summary>
    /// <param name="deviceCode">设备代码。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结论（待授权 / 轮太快 / 过期 / 被拒 / 成功）。</returns>
    /// <exception cref="AuthException">网络或状态码异常（<c>400</c> 的 OAuth 错误按结论返回，不抛）。</exception>
    Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken cancellationToken = default);

    /// <summary>用刷新令牌换一套新令牌（D-2 的第一步；刷新令牌是**轮换**的，必须把新的存下来）。</summary>
    /// <param name="refreshToken">上次的刷新令牌。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>新令牌。</returns>
    /// <exception cref="AuthException">刷新令牌已失效（需要重新登录）或其他失败。</exception>
    Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IDeviceCodeClient"/> 的真实实现。
/// <para>
/// 三条刻意的设计：
/// </para>
/// <list type="number">
/// <item><b>只用命名客户端</b>（<c>core</c>）：域名白名单与重试挂在管道上；每个地址再显式过一遍策略，
/// 让"这个地址不被允许"表现为一句明确的话，而不是来自管道深处的异常。</item>
/// <item><b>OAuth 的 <c>400</c> 不是异常</b>：设备代码流**靠 400 工作**（<c>authorization_pending</c> 就是 400）。
/// 把它当异常处理会让"正在等你授权"变成一条失败日志。</item>
/// <item><b>OAuth 的字段是蛇形</b>（<c>device_code</c> / <c>access_token</c> / <c>expires_in</c>），
/// 而全仓库的 JSON 选项是驼峰策略 —— 必须逐个显式标注，否则这些字段**永远不会被绑定**，
/// 表现是"拿到了响应但所有字段都是空的"（M2-f 的 <c>map_to_resources</c> 正是被这个坑了一整天）。</item>
/// </list>
/// </summary>
public sealed class DeviceCodeClient : IDeviceCodeClient
{
    private const int DefaultExpiresInSeconds = 900;
    private const int DefaultIntervalSeconds = 5;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpHostPolicy _hostPolicy;
    private readonly AuthOptions _options;
    private readonly IClock _clock;

    /// <summary>构造设备代码客户端。</summary>
    /// <param name="httpClientFactory">命名客户端工厂。</param>
    /// <param name="hostPolicy">出站域名策略。</param>
    /// <param name="options">认证配置（Client ID 来自配置文件，禁止硬编码）。</param>
    /// <param name="clock">时间源（令牌到期时刻要可测）。</param>
    public DeviceCodeClient(
        IHttpClientFactory httpClientFactory,
        HttpHostPolicy hostPolicy,
        AuthOptions options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(hostPolicy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _httpClientFactory = httpClientFactory;
        _hostPolicy = hostPolicy;
        _options = options;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<DeviceCodeChallenge> RequestCodeAsync(CancellationToken cancellationToken = default)
    {
        var uri = Allowed(MicrosoftEndpoints.DeviceCodeUrl);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["scope"] = MicrosoftEndpoints.Scope,
        });

        using var response = await Client()
            .PostAsync(uri, content, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(DescribeFailure(TokenAction.RequestCode, response, body));
        }

        var raw = Deserialize<RawDeviceCode>(body)
                  ?? throw new AuthException(TextTemplate.Of("Microsoft 返回的设备代码响应是空的。"));

        if (string.IsNullOrWhiteSpace(raw.DeviceCode) || string.IsNullOrWhiteSpace(raw.UserCode))
        {
            throw new AuthException(TextTemplate.Of("Microsoft 的设备代码响应缺少 device_code 或 user_code。"));
        }

        return new DeviceCodeChallenge(
            DeviceCode: raw.DeviceCode,
            UserCode: raw.UserCode,
            VerificationUri: raw.VerificationUri ?? "https://microsoft.com/link",
            ExpiresInSeconds: raw.ExpiresIn > 0 ? raw.ExpiresIn : DefaultExpiresInSeconds,
            // 官方要求不得比 interval 更快地轮询；响应没给时按 5 秒（官方的默认值）。
            IntervalSeconds: raw.Interval > 0 ? raw.Interval : DefaultIntervalSeconds);
    }

    /// <inheritdoc />
    public async Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);

        var uri = Allowed(MicrosoftEndpoints.TokenUrl);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = MicrosoftEndpoints.DeviceCodeGrantType,
            ["client_id"] = _options.ClientId,
            ["device_code"] = deviceCode,
        });

        return await PostTokenAsync(uri, content, TokenAction.Poll, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var uri = Allowed(MicrosoftEndpoints.TokenUrl);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = MicrosoftEndpoints.Scope,
        });

        var poll = await PostTokenAsync(uri, content, TokenAction.Refresh, cancellationToken).ConfigureAwait(false);

        if (poll.Kind == DeviceCodePollKind.Tokens && poll.Tokens is not null)
        {
            return poll.Tokens;
        }

        // 这里最有用的判断是"刷新令牌已经不能用了"（invalid_grant）—— 它意味着必须重新登录，
        // 而不是"网络抖了一下，等会儿再试"。两者在界面上是两句不同的话。
        throw new AuthException(
            RefreshFailure(poll),
            innerException: null,
            requiresSignIn: poll.Error is "invalid_grant" or "invalid_request");
    }

    /// <summary>刷新失败的那句话（官方给了原因就用它，没给就说"没给"）。</summary>
    /// <param name="poll">轮询结论。</param>
    /// <returns>失败原因。</returns>
    private static TextTemplate RefreshFailure(DeviceCodePoll poll)
    {
        if (poll.Error is "invalid_grant" or "invalid_request")
        {
            return TextTemplate.Of("这个账户的登录已过期，需要重新登录一次。");
        }

        var reason = poll.Description ?? poll.Error;

        // 官方没给原因时**不能**把"未知原因"塞进参数里当字面量：参数是要原样显示的，
        // 英文界面会露出那三个汉字（UserTextKeyTests 会拦这种事）—— 所以另给一个键。
        return reason is null
            ? TextTemplate.Of("刷新令牌失败，官方没有给出原因。")
            : TextTemplate.Of("刷新令牌失败：{0}。", reason);
    }

    /// <summary>令牌端点的公共部分：<c>400</c> 也读 body（设备代码流正常就是这么工作的）。</summary>
    private async Task<DeviceCodePoll> PostTokenAsync(
        Uri uri,
        HttpContent content,
        TokenAction action,
        CancellationToken cancellationToken)
    {
        using var response = await Client()
            .PostAsync(uri, content, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var granted = Deserialize<RawToken>(body)
                          ?? throw new AuthException(EmptyResponse(action));

            if (string.IsNullOrWhiteSpace(granted.AccessToken))
            {
                throw new AuthException(MissingAccessToken(action));
            }

            var lifetime = granted.ExpiresIn > 0 ? granted.ExpiresIn : 3600;

            return new DeviceCodePoll(
                DeviceCodePollKind.Tokens,
                new MicrosoftTokens(
                    granted.AccessToken,
                    granted.RefreshToken,
                    _clock.Now.AddSeconds(lifetime)));
        }

        RawError? error;

        try
        {
            error = Deserialize<RawError>(body);
        }
        catch (AuthException)
        {
            throw new AuthException(DescribeFailure(action, response, body));
        }

        var code = error?.Error ?? string.Empty;

        return code switch
        {
            "authorization_pending" => new DeviceCodePoll(DeviceCodePollKind.Pending, Error: code),
            "slow_down" => new DeviceCodePoll(DeviceCodePollKind.SlowDown, Error: code),
            "expired_token" => new DeviceCodePoll(DeviceCodePollKind.Expired, Error: code, Description: error?.ErrorDescription),
            "access_denied" or "bad_verification_code" or "invalid_grant" =>
                new DeviceCodePoll(DeviceCodePollKind.Denied, Error: code, Description: error?.ErrorDescription),

            // 认不出的错误：如实报出来，而不是当成"还在等"（后者会让用户永远等下去）。
            _ => throw new AuthException(DescribeFailure(action, response, body)),
        };
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(HttpClientNames.Core);

    /// <summary>把地址过一遍出站策略：不允许时在这里就停住（一个请求都不发）。</summary>
    private Uri Allowed(string url)
    {
        var uri = new Uri(url);
        _hostPolicy.EnsureAllowed(uri);
        return uri;
    }

    private static T? Deserialize<T>(string body) where T : class
    {
        try
        {
            return LauncherJson.Deserialize<T>(body, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new AuthException(TextTemplate.Of("认证服务返回的不是合法 JSON。"), ex);
        }
    }

    /// <summary>
    /// 把一次失败拼成一句可读的话（状态码 + OAuth 的 <c>error_description</c>）。
    /// </summary>
    /// <remarks>
    /// 动作（申请代码 / 轮询 / 刷新）**写进键里**，不当作参数传：参数是要原样显示的，
    /// 传一段中文"申请设备代码"进英文句子就会露出中文。三个动作 × 两种形状 = 六个键，
    /// 多写几个键换来"两种语言都读得通"。
    /// </remarks>
    /// <param name="action">失败发生在哪一步。</param>
    /// <param name="response">响应。</param>
    /// <param name="body">响应体。</param>
    /// <returns>失败原因。</returns>
    private static TextTemplate DescribeFailure(TokenAction action, HttpResponseMessage response, string body)
    {
        string detail = string.Empty;

        try
        {
            var error = LauncherJson.Deserialize<RawError>(body, LauncherJson.Indented);
            detail = error?.ErrorDescription ?? error?.Error ?? string.Empty;
        }
        catch (JsonException)
        {
            // 响应不是 JSON（错误页、限流页）：状态码本身就是最有用的一条信息。
        }

        var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var phrase = response.ReasonPhrase ?? string.Empty;

        if (detail.Length > 0)
        {
            return action switch
            {
                TokenAction.RequestCode =>
                    TextTemplate.Of("申请设备代码失败：{0}（{1}）：{2}", phrase, status, detail),
                TokenAction.Poll =>
                    TextTemplate.Of("轮询授权结果失败：{0}（{1}）：{2}", phrase, status, detail),
                _ => TextTemplate.Of("刷新令牌失败：{0}（{1}）：{2}", phrase, status, detail),
            };
        }

        return action switch
        {
            TokenAction.RequestCode =>
                TextTemplate.Of("申请设备代码失败：{0}（{1}）。", phrase, status),
            TokenAction.Poll =>
                TextTemplate.Of("轮询授权结果失败：{0}（{1}）。", phrase, status),
            _ => TextTemplate.Of("刷新令牌失败：{0}（{1}）。", phrase, status),
        };
    }

    /// <summary>成功状态码下响应为空体时的那句话（按动作分开，理由同 <see cref="DescribeFailure"/>）。</summary>
    /// <param name="action">哪一步。</param>
    /// <returns>失败原因。</returns>
    private static TextTemplate EmptyResponse(TokenAction action) => action switch
    {
        TokenAction.Poll => TextTemplate.Of("轮询授权结果时，Microsoft 返回了空响应。"),
        _ => TextTemplate.Of("刷新令牌时，Microsoft 返回了空响应。"),
    };

    /// <summary>成功状态码下响应里没有 <c>access_token</c> 时的那句话。</summary>
    /// <param name="action">哪一步。</param>
    /// <returns>失败原因。</returns>
    private static TextTemplate MissingAccessToken(TokenAction action) => action switch
    {
        TokenAction.Poll => TextTemplate.Of("轮询授权结果的响应里没有 access_token。"),
        _ => TextTemplate.Of("刷新令牌的响应里没有 access_token。"),
    };

    /// <summary>令牌端点上的三个动作（决定键的写法，见 <see cref="DescribeFailure"/>）。</summary>
    private enum TokenAction
    {
        /// <summary>申请设备代码。</summary>
        RequestCode,

        /// <summary>轮询授权结果。</summary>
        Poll,

        /// <summary>用刷新令牌换新令牌。</summary>
        Refresh,
    }

    /// <summary>设备代码端点 / 令牌端点的响应（蛇形字段必须显式标注）。</summary>
    private sealed record RawDeviceCode
    {
        [JsonPropertyName("device_code")]
        public string? DeviceCode { get; init; }

        [JsonPropertyName("user_code")]
        public string? UserCode { get; init; }

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("interval")]
        public int Interval { get; init; }
    }

    /// <summary>令牌端点的成功响应。</summary>
    private sealed record RawToken
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }

    /// <summary>令牌端点的错误响应（OAuth 标准字段）。</summary>
    private sealed record RawError
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}
