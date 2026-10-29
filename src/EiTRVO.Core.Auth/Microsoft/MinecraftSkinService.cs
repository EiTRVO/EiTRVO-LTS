using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>
/// <see cref="ISkinService"/> 的真实实现（D-9）：官方皮肤接口 + 令牌 + 本地冷却。
/// <para>
/// 五个值得写下来的决定：
/// </para>
/// <list type="number">
/// <item><b>令牌用 Minecraft 访问令牌</b>（不是 MSA 令牌），而且**复用 M5-1 的取用路径**
/// （<see cref="IAccountCatalog.GetCredentialAsync"/>）：它会按到期时刻决定要不要先刷新一次。
/// 这里再实现一遍"过期判断"最典型的后果是两边判得不一样（一边说还有效、一边说该刷新）。</item>
/// <item><b>401/403 只刷新一次、只重试一次</b>，仍失败就是"需要重新登录"。
/// 循环重试会让一个已经作废的令牌把账户推到限流里去。</item>
/// <item><b>429 绝不重试</b>，而且**同时把本地冷却拉起来** —— 否则用户会继续点，
/// 点一次就是一次 429，官方对这种情况给的是临时封禁。</item>
/// <item><b>响应里就是改完之后的档案</b>，所以成功之后直接用它的四格刷新界面，不再多发一次请求
/// （<c>DELETE</c> 例外：官方可能回 204，那就重新取一次档案 —— 那一次 GET 是**为了知道当前状态**，
/// 不是"再确认一遍"。）。</item>
/// <item><b>本地 PNG 校验在发请求之前</b>：官方对不合规的图片只回 400、不说哪儿不合规，
/// 而 64×64 / 64×32 是完全确定的规则。</item>
/// </list>
/// <para>
/// <b>未验事项</b>（官方没有公开这些接口的文档，以下都是社区口径或我们的探针，别当成契约）：
/// 最大文件大小、alpha 是否必须、400 的 <c>errorType</c>、429 是否带 <c>Retry-After</c>、
/// <c>variant</c> 是否接受大写、真实的冷却秒数。
/// </para>
/// </summary>
public sealed class MinecraftSkinService : ISkinService
{
    /// <summary>
    /// 成功之后多久内不允许再改皮肤。
    /// <para>
    /// 官方没有公布这个数；社区一致口径约 1 次/分钟，而连续失败会导致账户被临时封禁，
    /// 所以宁可保守。这是**我们的策略**，不是官方契约。
    /// </para>
    /// </summary>
    public static readonly TimeSpan UploadCooldown = TimeSpan.FromSeconds(60);

    /// <summary>请求体的 JSON 选项（驼峰；与代码库里其它请求体一致）。</summary>
    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpHostPolicy _hostPolicy;
    private readonly IAccountCatalog _accounts;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>每个账户最近一次改皮肤的时刻（本地冷却；进程内，不落盘 —— 重启后官方那边早就过去了）。</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastChanges = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    /// <summary>构造皮肤服务。</summary>
    /// <param name="httpClientFactory">命名客户端工厂。</param>
    /// <param name="hostPolicy">出站域名策略。</param>
    /// <param name="accounts">账户目录（取令牌、读档案、写回档案）。</param>
    /// <param name="clock">时间源（冷却窗口要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public MinecraftSkinService(
        IHttpClientFactory httpClientFactory,
        HttpHostPolicy hostPolicy,
        IAccountCatalog accounts,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(hostPolicy);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _hostPolicy = hostPolicy;
        _accounts = accounts;
        _clock = clock;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public TimeSpan CooldownRemaining(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return TimeSpan.Zero;
        }

        lock (_gate)
        {
            if (!_lastChanges.TryGetValue(playerName, out var last))
            {
                return TimeSpan.Zero;
            }

            var remaining = last + UploadCooldown - _clock.Now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <inheritdoc />
    public async Task<SkinProfile> UploadAsync(
        string playerName,
        string pngFilePath,
        SkinModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pngFilePath);

        var account = RequireMicrosoftAccount(playerName);
        RequireCooldownPassed(account.PlayerName);

        var name = account.PlayerName;
        var bytes = await ReadImageAsync(name, pngFilePath, cancellationToken).ConfigureAwait(false);

        var issue = SkinImageValidator.Validate(bytes, out var width, out var height);
        if (issue != SkinImageIssue.None)
        {
            throw Fail(
                name,
                SkinChangeFailure.InvalidImage,
                SkinImageValidator.Describe(issue, bytes.Length, width, height)
                    ?? TextTemplate.Of("这张图片不可用。"));
        }

        var variant = VariantText(model);
        var fileName = Path.GetFileName(pngFilePath);

        var body = await SendAsync(
            HttpMethod.Post,
            MicrosoftEndpoints.MinecraftSkinsUrl,
            () => CreateUploadContent(variant, bytes, string.IsNullOrEmpty(fileName) ? "skin.png" : fileName),
            name,
            cancellationToken).ConfigureAwait(false);

        return Finish(name, ReadProfile(name, body), "已上传皮肤（{0}，{1}）", [name, variant]);
    }

    /// <inheritdoc />
    public async Task<SkinProfile> SetModelAsync(
        string playerName,
        SkinModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var account = RequireMicrosoftAccount(playerName);
        RequireCooldownPassed(account.PlayerName);

        var name = account.PlayerName;
        var existing = _accounts.GetProfile(name);

        // JSON 形态要带上档案里**已有的**纹理地址 —— 没有地址就说明当前是官方默认皮肤，
        // 那时"换模型"这件事无从谈起（要先有一张自定义皮肤）。
        if (existing?.SkinUrl is not { Length: > 0 } url)
        {
            throw Fail(
                name,
                SkinChangeFailure.NoCustomSkin,
                TextTemplate.Of("这个账户当前是官方默认皮肤，先上传一张自定义皮肤再改模型。"));
        }

        var variant = VariantText(model);

        var body = await SendAsync(
            HttpMethod.Post,
            MicrosoftEndpoints.MinecraftSkinsUrl,
            () => JsonContent.Create(new SkinRequest { Variant = variant, Url = url }, options: BodyJsonOptions),
            name,
            cancellationToken).ConfigureAwait(false);

        return Finish(name, ReadProfile(name, body), "已更新皮肤模型（{0}，{1}）", [name, variant]);
    }

    /// <inheritdoc />
    public async Task<SkinProfile> ResetAsync(string playerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var account = RequireMicrosoftAccount(playerName);
        RequireCooldownPassed(account.PlayerName);

        var name = account.PlayerName;

        var body = await SendAsync(
            HttpMethod.Delete,
            MicrosoftEndpoints.MinecraftActiveSkinUrl,
            contentFactory: null,
            name,
            cancellationToken).ConfigureAwait(false);

        if (body.Length == 0)
        {
            // DELETE 成功是 204（没有正文）：改完之后的档案只能重新取一次。
            body = await SendAsync(
                HttpMethod.Get,
                MicrosoftEndpoints.MinecraftProfileUrl,
                contentFactory: null,
                name,
                cancellationToken).ConfigureAwait(false);
        }

        if (body.Length == 0)
        {
            throw Fail(
                name,
                SkinChangeFailure.ServerError,
                TextTemplate.Of("官方没有返回档案，无法确认恢复的结果，请稍后再试。"));
        }

        return Finish(name, ReadProfile(name, body), "已恢复默认皮肤（{0}）", [name]);
    }

    /// <summary>取令牌 → 发一次请求 →（401/403 时刷新一次再试一次）→ 状态码分派。</summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="url">目标地址（逐跳白名单由管道负责，这里也先过一次）。</param>
    /// <param name="contentFactory">请求体工厂：**每次尝试都要一个新的**（同一个 HttpContent 不能可靠地重发）。</param>
    /// <param name="playerName">玩家名（事件与错误消息里用）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>成功时的响应正文。</returns>
    private async Task<string> SendAsync(
        HttpMethod method,
        string url,
        Func<HttpContent>? contentFactory,
        string playerName,
        CancellationToken cancellationToken)
    {
        var accessToken = await AccessTokenAsync(playerName, cancellationToken).ConfigureAwait(false);

        var attempt = await AttemptAsync(method, url, contentFactory, accessToken, playerName, cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 401/403 有可能是"这个访问令牌刚过期"（我们按到期时刻判断，而官方那边可能已经先失效）：
            // 刷新一次、重试一次；再失败就是真的需要重新登录。
            var refreshed = await RefreshTokenAsync(playerName, cancellationToken).ConfigureAwait(false);

            attempt = await AttemptAsync(method, url, contentFactory, refreshed, playerName, cancellationToken)
                .ConfigureAwait(false);
        }

        if (attempt.IsSuccess)
        {
            return attempt.Body;
        }

        if (attempt.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 刷新之后**还是** 401/403：这个账户的会话真的没了，只能重新登录一次。
            // 这一条必须与"网络抖了一下"分开说 —— 否则用户会一直点重试。
            throw Fail(
                playerName,
                SkinChangeFailure.NotSignedIn,
                TextTemplate.Of("这个账户需要重新登录一次（刷新令牌已被作废）。"));
        }

        if (attempt.Status == HttpStatusCode.TooManyRequests)
        {
            // 429：不许循环重试。顺手把本地冷却拉起来，界面上的按钮会真的灰掉。
            MarkChanged(playerName);

            throw Fail(
                playerName,
                SkinChangeFailure.Cooldown,
                TextTemplate.Of("上传太频繁，官方要求冷却（429）。请等一分钟左右再试 —— 连续重试可能导致账户被临时限制。"));
        }

        throw Fail(playerName, FailureOf(attempt.Status), Describe(attempt));
    }

    /// <summary>发一次请求并把状态码/正文原样拿回来（不在这里做分派）。</summary>
    private async Task<SkinAttempt> AttemptAsync(
        HttpMethod method,
        string url,
        Func<HttpContent>? contentFactory,
        string accessToken,
        string playerName,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        _hostPolicy.EnsureAllowed(uri);

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

        if (contentFactory is not null)
        {
            request.Content = contentFactory();
        }

        HttpResponseMessage response;

        try
        {
            response = await Client().SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // 用户没取消，那就是超时（连接被挂住）：归到"网络"那一句，不要让它变成一条没有解释的取消。
            throw Fail(playerName, SkinChangeFailure.Network, TextTemplate.Of("请求超时，请检查网络后重试。"), ex);
        }
        catch (HttpRequestException ex)
        {
            throw Fail(playerName, SkinChangeFailure.Network, TextTemplate.Of("网络请求失败，请检查网络后重试。"), ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new SkinAttempt(response.StatusCode, response.ReasonPhrase ?? string.Empty, body);
        }
    }

    /// <summary>取这个账户可用的 Minecraft 访问令牌（走 M5-1 的同一条路径）。</summary>
    private async Task<string> AccessTokenAsync(string playerName, CancellationToken cancellationToken)
    {
        try
        {
            var credential = await _accounts.GetCredentialAsync(playerName, cancellationToken).ConfigureAwait(false);

            return credential.AccessToken;
        }
        catch (CredentialException ex)
        {
            throw Fail(
                playerName,
                ex.Failure switch
                {
                    CredentialFailure.NoSuchAccount => SkinChangeFailure.NoSuchAccount,
                    CredentialFailure.SessionExpired => SkinChangeFailure.NotSignedIn,
                    _ => SkinChangeFailure.Network,
                },
                // 凭据层的 Reason 可能为空（那条路径没给原因）：宁可给一句笼统的话，也不要一句空话。
                ex.Reason ?? TextTemplate.Of("取不到这个账户的登录凭据，无法继续。"),
                ex);
        }
        catch (AccountStoreException ex)
        {
            throw Fail(
                playerName,
                SkinChangeFailure.StoreUnavailable,
                TextTemplate.Of("账户文件读不出来，已取消这次皮肤操作（原文件未改动）。"),
                ex);
        }
    }

    /// <summary>强制刷新一次令牌，并返回刷新后的访问令牌。</summary>
    private async Task<string> RefreshTokenAsync(string playerName, CancellationToken cancellationToken)
    {
        try
        {
            await _accounts.RefreshMicrosoftAsync(playerName, cancellationToken).ConfigureAwait(false);
            var credential = await _accounts.GetCredentialAsync(playerName, cancellationToken).ConfigureAwait(false);

            return credential.AccessToken;
        }
        catch (AuthException ex)
        {
            throw Fail(
                playerName,
                ex.RequiresSignIn ? SkinChangeFailure.NotSignedIn : SkinChangeFailure.Network,
                ex.RequiresSignIn ? TextTemplate.Of("这个账户需要重新登录一次（刷新令牌已被作废）。") : ex.Reason,
                ex);
        }
        catch (AccountStoreException ex)
        {
            throw Fail(
                playerName,
                SkinChangeFailure.StoreUnavailable,
                TextTemplate.Of("账户文件读不出来，已取消这次皮肤操作（原文件未改动）。"),
                ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw Fail(playerName, SkinChangeFailure.NoSuchAccount, TextTemplate.Of("库里没有玩家「{0}」。", playerName), ex);
        }
    }

    /// <summary>确认是库里的一条正版账户；读不出来时如实说是"读不出来"而不是"没有账户"。</summary>
    private Account RequireMicrosoftAccount(string playerName)
    {
        if (_accounts.StoreStatus is { } status)
        {
            throw Fail(
                playerName,
                SkinChangeFailure.StoreUnavailable,
                TextTemplate.Of(
                    "账户文件读不出来（{0}），无法确认这个账户的状态；原文件未改动。",
                    status.Failure.ToString()));
        }

        var account = _accounts
            .GetAccounts()
            .FirstOrDefault(a => string.Equals(a.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            throw Fail(playerName, SkinChangeFailure.NoSuchAccount, TextTemplate.Of("库里没有玩家「{0}」，请先在账户页登录。", playerName));
        }

        if (account.Kind != AccountKind.Microsoft)
        {
            throw Fail(
                playerName,
                SkinChangeFailure.OfflineAccount,
                TextTemplate.Of("离线账户没有官方档案，不能上传皮肤（皮肤只属于正版账户）。"));
        }

        return account;
    }

    /// <summary>冷却窗口内直接拒绝 —— 连一次请求都不发。</summary>
    private void RequireCooldownPassed(string playerName)
    {
        var remaining = CooldownRemaining(playerName);

        if (remaining > TimeSpan.Zero)
        {
            throw Fail(
                playerName,
                SkinChangeFailure.Cooldown,
                TextTemplate.Of(
                    "刚刚才改过皮肤，请等 {0} 秒再试（官方约 1 次/分钟）。",
                    ((int)Math.Ceiling(remaining.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>读文件；读不出来时说"读不出来"，而不是"图片不合规"。</summary>
    private async Task<byte[]> ReadImageAsync(string playerName, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException
                                      or System.Security.SecurityException)
        {
            throw Fail(
                playerName,
                SkinChangeFailure.ImageUnreadable,
                TextTemplate.Of("选中的图片读不出来（可能已被移动、删除，或没有读取权限）。"),
                ex);
        }
    }

    /// <summary>读响应里的档案；读不出来是"官方返回的东西我们看不懂"，不是"用户做错了什么"。</summary>
    private MinecraftProfile ReadProfile(string playerName, string body)
    {
        try
        {
            return MinecraftProfileJson.Parse(body, _hostPolicy);
        }
        catch (AuthException ex)
        {
            throw Fail(
                playerName,
                SkinChangeFailure.ServerError,
                // 内层也是一句话（键 + 参数）：用 Note 接在后面，而不是把键塞进参数（见 AGENTS.md §5.3 第 ⑤⑦ 条）。
                TextTemplate.Of("官方返回的档案读不出来。") with { Note = ex.Reason },
                ex);
        }
    }

    /// <summary>把改完之后的档案写回账户，拉起冷却，写一条事件，并返回界面要的四格。</summary>
    private SkinProfile Finish(
        string playerName,
        MinecraftProfile profile,
        string eventKey,
        IReadOnlyList<string> parameters)
    {
        try
        {
            _accounts.UpdateProfile(playerName, profile);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or AccountStoreException
                                      or KeyNotFoundException)
        {
            // 官方那边**已经改好了**：这句话必须说清楚，否则用户会以为没生效、然后再点一次。
            throw Fail(
                playerName,
                SkinChangeFailure.StoreUnavailable,
                TextTemplate.Of("皮肤已经改好了，但账户文件写不进去，界面上的皮肤信息要等下次取档才会更新（原文件未改动）。"),
                ex);
        }

        MarkChanged(playerName);
        _diagnostics.Write(LauncherEventLevel.Information, DiagnosticsSources.Auth, eventKey, parameters);

        return _accounts.GetSkin(playerName);
    }

    /// <summary>记下这次改动的时刻（本地冷却的起点）。</summary>
    private void MarkChanged(string playerName)
    {
        lock (_gate)
        {
            _lastChanges[playerName] = _clock.Now;
        }
    }

    /// <summary>multipart 请求体：<c>variant</c> 字段 + <c>file</c> 文件字段。</summary>
    /// <remarks>
    /// 文件字段的媒体类型必须是 <c>image/png</c>。写错（例如 <c>application/png</c>）时官方的回应是
    /// <c>415</c>，而那个状态码看起来像"服务器不支持"，其实是我们的请求头写错了 —— 见
    /// <see cref="SkinChangeFailure.UnsupportedMedia"/>。
    /// </remarks>
    private static MultipartFormDataContent CreateUploadContent(string variant, byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(variant), "variant");

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);

        return content;
    }

    /// <summary>官方的 <c>variant</c> 用小写（档案里回的是大写，读取时大小写不敏感）。</summary>
    private static string VariantText(SkinModel model) => model == SkinModel.Slim ? "slim" : "classic";

    /// <summary>把状态码翻成失败原因（每一种在界面上是一句不同的话）。</summary>
    private static SkinChangeFailure FailureOf(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => SkinChangeFailure.ImageRejected,
        HttpStatusCode.NotFound => SkinChangeFailure.NoJavaProfile,
        HttpStatusCode.UnsupportedMediaType => SkinChangeFailure.UnsupportedMedia,
        _ => SkinChangeFailure.ServerError,
    };

    /// <summary>把状态码翻成一句给用户看的话（键 + 参数）。</summary>
    private static TextTemplate Describe(SkinAttempt attempt) => attempt.Status switch
    {
        HttpStatusCode.BadRequest =>
            TextTemplate.Of("官方接口拒收了这个请求（400）。请确认这是一张 64×64 或 64×32 的 PNG 皮肤图。"),
        HttpStatusCode.NotFound =>
            TextTemplate.Of("这个 Microsoft 账户没有 Minecraft Java 版档案（尚未购买或尚未创建）。"),
        HttpStatusCode.UnsupportedMediaType =>
            TextTemplate.Of("官方接口不接受上传的媒体类型（415）。这是启动器的缺陷（multipart 的 Content-Type 写错了），请反馈这条信息。"),
        _ => TextTemplate.Of(
            "官方服务器返回了 {0} {1}，请稍后再试。",
            ((int)attempt.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
            attempt.Reason),
    };

    /// <summary>造一个失败（**所有失败都从这里出去**：事件只写一处，就不会漏写）。</summary>
    private SkinChangeException Fail(
        string playerName,
        SkinChangeFailure failure,
        TextTemplate reason,
        Exception? innerException = null)
    {
        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.Auth,
            "皮肤操作未成功（{0}）：{1}",
            [failure.ToString(), playerName]);

        return new SkinChangeException(reason, failure, innerException);
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(HttpClientNames.Core);

    /// <summary>换模型时的 JSON 请求体。</summary>
    private sealed record SkinRequest
    {
        public string? Variant { get; init; }

        public string? Url { get; init; }
    }

    /// <summary>一次请求的结果（状态码 + 原因短语 + 正文）。</summary>
    private sealed record SkinAttempt(HttpStatusCode Status, string Reason, string Body)
    {
        /// <summary>成功（<c>200</c> 有档案，<c>204</c> 无正文）。</summary>
        public bool IsSuccess => Status is HttpStatusCode.OK or HttpStatusCode.NoContent;
    }
}
