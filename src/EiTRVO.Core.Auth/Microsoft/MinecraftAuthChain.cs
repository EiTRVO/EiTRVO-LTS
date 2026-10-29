using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>令牌刷新链（D-2）：refresh → Xbox Live → XSTS → Minecraft 服务 → profile。</summary>
public interface IMinecraftAuthChain
{
    /// <summary>从 Microsoft 令牌一路走到玩家档案。</summary>
    /// <param name="microsoft">Microsoft 令牌（刚换来的，或刷新得到的新一套）。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>登录结果（档案 + 两个访问令牌）。</returns>
    /// <exception cref="AuthException">任何一步失败（消息已经是可以给用户看的一句话）。</exception>
    Task<MicrosoftLoginResult> CompleteAsync(
        MicrosoftTokens microsoft,
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IMinecraftAuthChain"/> 的真实实现。
/// <para>
/// 四步全部成功才返回：Xbox Live 用户认证 → XSTS 授权 → Minecraft 服务登录 → 玩家档案。
/// 任何一步失败都抛 <see cref="AuthException"/>，调用方因此**不会**落盘一个半成品账户
/// （"全链成功才落盘"这条好设计来自 Neo，保留）。
/// </para>
/// <para>
/// 五个值得一提的地方：
/// </para>
/// <list type="number">
/// <item><b>XSTS 的 <c>XErr</c> 必须被翻译</b>：401 的正文里带着一个数字（<c>2148916233</c> 等），
/// 官方文档之外几乎没人知道它们的意思，而它们恰好是"没有 Xbox 账户 / 地区不支持 / 未成年账户"三类
/// **用户自己能处理**的情况 —— 只说"401 Unauthorized"等于把问题丢回给用户。</item>
/// <item><b><c>RpsTicket</c> 要带 <c>d=</c> 前缀</b>：官方要求把 MSA 访问令牌标成委托票（delegation），
/// 少了这两个字符的表现是 XBL 返回 400，而错误信息里不会提到它。</item>
/// <item><b>Minecraft 登录用的 <c>identityToken</c> 格式是 <c>XBL3.0 x=&lt;uhs&gt;;&lt;xsts&gt;</c></b>：
/// 中间那个分号与 <c>x=</c> 都不能少。</item>
/// <item><b>档案里的 UUID 是 32 位无连字符</b>，而启动命令行要的是标准带连字符形式 —— 在**这里**换好，
/// 让下游只认一种形状（M4 的红脱敏也按带连字符那种形式清扫）。</item>
/// <item><b>皮肤地址官方给的是 <c>http://</c></b>（<c>textures.minecraft.net</c>）。这里只把它**存下来**
/// 并把主机过一遍出站白名单；真要下载纹理时得升到 https，而那个主机支持 https。</item>
/// </list>
/// </summary>
public sealed class MinecraftAuthChain : IMinecraftAuthChain
{
    /// <summary>Xbox Live 的 <c>x-xbl-contract-version</c>（少了它 XSTS 会拒）。</summary>
    private const string ContractVersion = "1";

    /// <summary>认不出的 XErr 也要有话说，但不必把数字当秘密。</summary>
    private static readonly Dictionary<long, TextTemplate> XstsErrors = new()
    {
        [2148916233] = TextTemplate.Of("这个 Microsoft 账户还没有 Xbox 档案。请先在 Xbox 官网（或任意 Xbox 应用）里创建一个档案，再回来登录。"),
        [2148916235] = TextTemplate.Of("Xbox Live 在当前地区不可用（或该账户已被封禁），无法登录。"),
        [2148916236] = TextTemplate.Of("这个账户需要先在 Xbox 官网上完成一次成人验证。"),
        [2148916237] = TextTemplate.Of("这个账户需要先在 Xbox 官网上完成一次成人验证。"),
        [2148916238] = TextTemplate.Of("这是未成年账户，需要先加入一个家庭组（由成年人配置）才能用于 Minecraft。"),
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpHostPolicy _hostPolicy;
    private readonly IClock _clock;

    /// <summary>构造认证链。</summary>
    /// <param name="httpClientFactory">命名客户端工厂。</param>
    /// <param name="hostPolicy">出站域名策略。</param>
    /// <param name="clock">时间源（Minecraft 令牌到期时刻要可测）。</param>
    public MinecraftAuthChain(
        IHttpClientFactory httpClientFactory,
        HttpHostPolicy hostPolicy,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(hostPolicy);
        ArgumentNullException.ThrowIfNull(clock);

        _httpClientFactory = httpClientFactory;
        _hostPolicy = hostPolicy;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<MicrosoftLoginResult> CompleteAsync(
        MicrosoftTokens microsoft,
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(microsoft);

        progress?.Report(new AuthProgress(AuthStage.CompletingSignIn));

        var xbox = await AuthenticateXboxAsync(microsoft.AccessToken, cancellationToken).ConfigureAwait(false);

        progress?.Report(new AuthProgress(AuthStage.CompletingSignIn));

        var xsts = await AuthorizeXstsAsync(xbox.Token, cancellationToken).ConfigureAwait(false);

        progress?.Report(new AuthProgress(AuthStage.CompletingSignIn));

        var minecraft = await LoginToMinecraftAsync(xsts, cancellationToken).ConfigureAwait(false);

        progress?.Report(new AuthProgress(AuthStage.CompletingSignIn));

        var profile = await FetchProfileAsync(minecraft.AccessToken, cancellationToken).ConfigureAwait(false);

        return new MicrosoftLoginResult(microsoft, minecraft, profile);
    }

    /// <summary>Xbox Live 用户认证（第 1 步）。</summary>
    private async Task<XboxUserToken> AuthenticateXboxAsync(string microsoftAccessToken, CancellationToken cancellationToken)
    {
        var body = new XboxUserRequest
        {
            Properties = new XboxUserProperties
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                // 官方把 MSA 访问令牌标成"委托票"，前缀 d= 是格式的一部分。
                RpsTicket = "d=" + microsoftAccessToken,
            },
            RelyingParty = MicrosoftEndpoints.XboxRelyingParty,
            TokenType = "JWT",
        };

        using var response = await PostJsonAsync(
            MicrosoftEndpoints.XboxUserAuthUrl,
            body,
            contractVersion: null,
            cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(TextTemplate.Of(
                "登录 Xbox Live 失败（{0} {1}）。请稍后再试。",
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                response.ReasonPhrase ?? string.Empty));
        }

        var raw = Deserialize<TokenResponse>(text, "Xbox Live");

        var token = raw.Token;
        var userHash = FirstUserHash(raw.DisplayClaims);

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userHash))
        {
            throw new AuthException(TextTemplate.Of("Xbox Live 没有返回可用的令牌（缺少 Token 或 uhs）。"));
        }

        return new XboxUserToken(token, userHash);
    }

    /// <summary>XSTS 授权（第 2 步）。</summary>
    private async Task<XstsToken> AuthorizeXstsAsync(string xboxToken, CancellationToken cancellationToken)
    {
        var body = new XstsRequest
        {
            Properties = new XstsProperties
            {
                SandboxId = MicrosoftEndpoints.RetailSandbox,
                UserTokens = [xboxToken],
            },
            RelyingParty = MicrosoftEndpoints.XstsRelyingParty,
            TokenType = "JWT",
        };

        using var response = await PostJsonAsync(
            MicrosoftEndpoints.XstsAuthorizeUrl,
            body,
            ContractVersion,
            cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(DescribeXstsFailure(response, text));
        }

        var raw = Deserialize<TokenResponse>(text, "XSTS");
        var userHash = FirstUserHash(raw.DisplayClaims);

        if (string.IsNullOrWhiteSpace(raw.Token) || string.IsNullOrWhiteSpace(userHash))
        {
            throw new AuthException(TextTemplate.Of("XSTS 没有返回可用的令牌（缺少 Token 或 uhs）。"));
        }

        return new XstsToken(raw.Token, userHash);
    }

    /// <summary>用 XSTS 令牌换 Minecraft 访问令牌（第 3 步）。</summary>
    private async Task<MinecraftToken> LoginToMinecraftAsync(XstsToken xsts, CancellationToken cancellationToken)
    {
        var body = new MinecraftLoginRequest
        {
            IdentityToken = $"XBL3.0 x={xsts.UserHash};{xsts.Token}",
        };

        using var response = await PostJsonAsync(
            MicrosoftEndpoints.MinecraftLoginUrl,
            body,
            contractVersion: null,
            cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(TextTemplate.Of(
                "登录 Minecraft 服务失败（{0} {1}）。这通常意味着这个账户没有购买 Minecraft Java 版。",
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                response.ReasonPhrase ?? string.Empty));
        }

        // 传给 Deserialize 的那个名字会**原样进句子**（它是参数，不是键），所以只能是
        // 两种语言里都成立的专有名词：写"Minecraft 服务"会在英文界面里露出三个汉字。
        var raw = Deserialize<MinecraftLoginResponse>(text, "Minecraft");

        if (string.IsNullOrWhiteSpace(raw.AccessToken))
        {
            throw new AuthException(TextTemplate.Of("Minecraft 服务没有返回可用的访问令牌。"));
        }

        var lifetime = raw.ExpiresIn > 0 ? raw.ExpiresIn : 86400;

        return new MinecraftToken(raw.AccessToken, _clock.Now.AddSeconds(lifetime));
    }

    /// <summary>取玩家档案（第 4 步）。</summary>
    private async Task<MinecraftProfile> FetchProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var uri = Allowed(MicrosoftEndpoints.MinecraftProfileUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        using var response = await Client().SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 官方对"没有 Java 版档案"用的就是 404 + 一段 JSON。
            throw new AuthException(TextTemplate.Of("这个 Microsoft 账户没有 Minecraft Java 版档案（尚未购买或尚未创建）。"));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(TextTemplate.Of(
                "获取玩家档案失败（{0} {1}）。",
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                response.ReasonPhrase ?? string.Empty));
        }

        // 读法（UUID 换形、皮肤地址升 https 并过白名单、取 ACTIVE 那一条）与 D-9 的皮肤接口共用同一个实现。
        return MinecraftProfileJson.Parse(text, _hostPolicy);
    }

    /// <summary>取显示声明里的 <c>uhs</c>（官方一定给，但它是远端数据，形状不对时如实返回空）。</summary>
    private static string? FirstUserHash(DisplayClaims? claims) =>
        claims?.Xui is { Count: > 0 } xui ? xui[0].UserHash : null;

    private async Task<HttpResponseMessage> PostJsonAsync<TBody>(
        string url,
        TBody body,
        string? contractVersion,
        CancellationToken cancellationToken)
    {
        var uri = Allowed(url);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body, options: BodyJsonOptions),
        };

        if (contractVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", contractVersion);
        }

        return await Client().SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(HttpClientNames.Core);

    private Uri Allowed(string url)
    {
        var uri = new Uri(url);
        _hostPolicy.EnsureAllowed(uri);
        return uri;
    }

    /// <summary>XSTS 的 401 要翻译 <c>XErr</c>；官方文档之外几乎没人知道那几个数字。</summary>
    private static TextTemplate DescribeXstsFailure(HttpResponseMessage response, string body)
    {
        try
        {
            var error = LauncherJson.Deserialize<XstsError>(body, LauncherJson.Indented);

            if (error?.XErr is { } code && XstsErrors.TryGetValue(code, out var message))
            {
                return message;
            }

            if (error?.XErr is { } unknown)
            {
                return TextTemplate.Of(
                    "Xbox Live 拒绝了这次授权（XErr={0}）。请确认这个账户已创建 Xbox 档案、且所在地区支持 Xbox Live。",
                    unknown.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (JsonException)
        {
            // 不是 JSON：退到状态码那一条。
        }

        return TextTemplate.Of(
            "Xbox Live 拒绝了这次授权（{0} {1}）。",
            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
            response.ReasonPhrase ?? string.Empty);
    }

    private static T Deserialize<T>(string body, string what) where T : class
    {
        T? value;

        try
        {
            value = LauncherJson.Deserialize<T>(body, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new AuthException(TextTemplate.Of("{0} 返回的不是合法 JSON。", what), ex);
        }

        return value ?? throw new AuthException(TextTemplate.Of("{0} 的响应是空的。", what));
    }

    /// <summary>请求体的 JSON 选项：与全仓库一致（只额外要求忽略 null 字段）。</summary>
    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        // Xbox Live / XSTS 的请求体是 **PascalCase**（Properties / RelyingParty / TokenType /
        // AuthMethod / SiteName / RpsTicket / SandboxId / UserTokens）。原来这里配了 CamelCase，
        // 真机登录时 Xbox Live 直接回 400 —— 单测也照着错的大小写断言，所以一直没发现。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------- 请求体 ----------

    /// <summary>Xbox Live 用户认证请求。</summary>
    private sealed record XboxUserRequest
    {
        public XboxUserProperties? Properties { get; init; }

        public string? RelyingParty { get; init; }

        public string? TokenType { get; init; }
    }

    /// <summary>Xbox Live 用户认证的 Properties。</summary>
    private sealed record XboxUserProperties
    {
        public string? AuthMethod { get; init; }

        public string? SiteName { get; init; }

        public string? RpsTicket { get; init; }
    }

    /// <summary>XSTS 授权请求。</summary>
    private sealed record XstsRequest
    {
        public XstsProperties? Properties { get; init; }

        public string? RelyingParty { get; init; }

        public string? TokenType { get; init; }
    }

    /// <summary>XSTS 授权的 Properties。</summary>
    private sealed record XstsProperties
    {
        public string? SandboxId { get; init; }

        public IReadOnlyList<string>? UserTokens { get; init; }
    }

    /// <summary>Minecraft 服务登录请求（驼峰 <c>identityToken</c>，与官方一致）。</summary>
    private sealed record MinecraftLoginRequest
    {
        // Minecraft 服务的字段是 camelCase；Xbox 那两步是 PascalCase，不能共用一套命名策略。
        [JsonPropertyName("identityToken")]
        public string? IdentityToken { get; init; }
    }

    // ---------- 响应体 ----------

    /// <summary>Xbox Live / XSTS 的成功响应。</summary>
    private sealed record TokenResponse
    {
        public string? Token { get; init; }

        public DisplayClaims? DisplayClaims { get; init; }
    }

    /// <summary>显示声明（<c>DisplayClaims.xui[0].uhs</c>）。</summary>
    private sealed record DisplayClaims
    {
        public IReadOnlyList<XuiClaim>? Xui { get; init; }
    }

    /// <summary>一条 <c>xui</c> 声明。</summary>
    private sealed record XuiClaim
    {
        [JsonPropertyName("uhs")]
        public string? UserHash { get; init; }
    }

    /// <summary>XSTS 失败时的正文（<c>XErr</c> 是数字，必须按数字读）。</summary>
    private sealed record XstsError
    {
        [JsonPropertyName("XErr")]
        public long? XErr { get; init; }

        [JsonPropertyName("Message")]
        public string? Message { get; init; }
    }

    /// <summary>Minecraft 服务登录响应。</summary>
    private sealed record MinecraftLoginResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
