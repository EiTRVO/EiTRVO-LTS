using System.Net;

namespace EiTRVO.Core.Launcher.Net;

/// <summary>
/// 在发送前校验目标地址是否在 <see cref="HttpHostPolicy"/> 白名单内，并且**逐跳**校验重定向。
/// <para>
/// 这是**纵深防御的第二层**：服务层本该只使用白名单内的地址，但版本清单里的 URL 来自远端，
/// 任何一处漏检都会变成"远端决定我们访问哪里"。挂在 HttpClient 管道上，
/// 就没有任何一条出站请求能绕过它。
/// </para>
/// <para>
/// <b>为什么自己跟重定向</b>：<c>HttpClient</c> 的自动重定向发生在**底层处理器**里，
/// 也就是在 <see cref="DelegatingHandler"/> 链**之后** —— 上面那句"没有任何一条出站请求能绕过它"
/// 在自动重定向打开时是**假的**：一个白名单主机回 <c>302 Location: https://evil.example/</c>，
/// 请求就跟着走了，本类一次都不会被调用。所以命名客户端把自动重定向关掉
/// （见组合根的 <c>ConfigurePrimaryHttpMessageHandler</c>），由这里自己跟：**每一跳都过策略**。
/// </para>
/// </summary>
public sealed class AllowedHostHandler : DelegatingHandler
{
    /// <summary>最多跟几跳。真实端点最多一跳；留几跳是给"HTTP → 同名 https"这类站点习惯用的。</summary>
    private const int MaxRedirects = 5;

    private readonly HttpHostPolicy _policy;

    /// <summary>构造处理器。</summary>
    /// <param name="policy">域名策略。</param>
    public AllowedHostHandler(HttpHostPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _policy.EnsureAllowed(request.RequestUri);

        var current = request;

        for (var hops = 0; ; hops++)
        {
            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);

            var target = RedirectTarget(response, current);
            if (target is null)
            {
                return response;
            }

            if (hops >= MaxRedirects)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpPolicyException(
                    $"重定向超过 {MaxRedirects} 跳（最后一跳 {status} → '{target}'），已停止跟随。");
            }

            // 带请求体的方法不跟：307/308 要求原样重发请求体，而请求体能否重放取决于它的类型，
            // 赌一把就是把"偶尔发出半个请求体"引进下载链。真实端点（OAuth / Xbox / Mojang）都不这么干，
            // 所以这里选择**响亮地失败**而不是静默降级。
            if (current.Method != HttpMethod.Get && current.Method != HttpMethod.Head)
            {
                var method = current.Method;
                response.Dispose();
                throw new HttpPolicyException(
                    $"'{method}' 请求收到 {response.StatusCode} 重定向到 '{target}'，但重定向不允许重放请求体。");
            }

            // 逐跳校验：这一行才是"远端决定不了我们访问哪里"。
            _policy.EnsureAllowed(target);

            var next = new HttpRequestMessage(
                response.StatusCode == HttpStatusCode.SeeOther ? HttpMethod.Get : current.Method,
                target);

            // 同源重定向保留原请求头；跨主机**不保留** —— Authorization 之类的身份头
            // 不该跟着一次重定向走到另一个主机上去。
            if (SameOrigin(target, current.RequestUri))
            {
                foreach (var header in current.Headers)
                {
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            response.Dispose();
            current = next;
        }
    }

    /// <summary>响应是否是一个要跟随的重定向；不是则返回 <see langword="null"/>。</summary>
    private static Uri? RedirectTarget(HttpResponseMessage response, HttpRequestMessage request)
    {
        var location = response.Headers.Location;
        if (location is null || !IsRedirect(response.StatusCode))
        {
            return null;
        }

        // Location 允许是相对地址（RFC 9110 §10.2.2），要对着当前地址解析。
        return location.IsAbsoluteUri || request.RequestUri is null
            ? location
            : new Uri(request.RequestUri, location);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool SameOrigin(Uri left, Uri? right) =>
        right is not null
        && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}
