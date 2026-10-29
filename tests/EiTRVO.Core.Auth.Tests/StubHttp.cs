using System.Net;
using System.Text;

namespace EiTRVO.Core.Auth.Tests.Fakes;

/// <summary>
/// 可编排的 HTTP 桩：按地址片段匹配响应（与请求顺序无关），并记录每一次请求的**方法、地址与请求体**。
/// <para>
/// 认证链的验收（规划 §4 第 3 条）是"每一步请求的 URL、方法、查询/请求体被逐一断言"，
/// 所以记录请求体与记录地址同样重要：<c>RpsTicket</c> 少了 <c>d=</c> 前缀、<c>identityToken</c>
/// 少了分号这类错误在状态码上是看不出来的（XBL 会给 400，但错误信息不会提到格式）。
/// </para>
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly List<(string Fragment, Func<HttpRequestMessage, HttpResponseMessage> Factory)> _rules = [];
    private readonly List<CapturedRequest> _requests = [];
    private readonly object _gate = new();

    /// <summary>
    /// 收到的请求（按顺序）。
    /// </summary>
    /// <remarks>
    /// 返回**快照**：并发路径（令牌刷新与重试可能同时在跑）下多个线程会同时记录，
    /// 而 <see cref="List{T}"/> 不是线程安全的 —— 直接 <c>Add</c> 会丢条目，
    /// 于是"发了几次请求"这类断言随机失败，报出来却像是产品代码的问题。
    /// </remarks>
    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>按地址片段登记一个响应；后登记的优先匹配（覆盖前面的）。</summary>
    /// <param name="urlFragment">URL 里应包含的片段。</param>
    /// <param name="statusCode">状态码。</param>
    /// <param name="body">响应体。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler RespondTo(string urlFragment, HttpStatusCode statusCode, string? body = null) =>
        RespondWith(urlFragment, _ => new HttpResponseMessage(statusCode)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        });

    /// <summary>
    /// 按地址片段登记一个**按请求**决定响应的规则（后登记的优先匹配）。
    /// <para>
    /// 需要它是因为有些验收点看的是"同一个地址被问了两次、两次答案不同"——
    /// 例如皮肤接口的 401 → 刷新令牌 → 重试：第一跳必须失败、第二跳必须成功，
    /// 而"按地址固定一个响应"的 <see cref="RespondTo"/> 表达不了这件事。
    /// </para>
    /// </summary>
    /// <param name="urlFragment">URL 里应包含的片段。</param>
    /// <param name="factory">响应工厂（可以抛异常，用来模拟网络失败）。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler RespondWith(string urlFragment, Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _rules.Insert(0, (urlFragment, factory));
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var captured = new CapturedRequest(
            request.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            body,
            request.Headers.TryGetValues("x-xbl-contract-version", out var versions)
                ? string.Join(',', versions)
                : string.Empty,
            request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(',', auth) : string.Empty,
            request.Content?.Headers.ContentType?.MediaType ?? string.Empty);

        var url = request.RequestUri?.ToString() ?? string.Empty;

        lock (_gate)
        {
            _requests.Add(captured);

            foreach (var (fragment, factory) in _rules)
            {
                if (url.Contains(fragment, StringComparison.Ordinal))
                {
                    return factory(request);
                }
            }

            throw new InvalidOperationException(
                $"桩没有为第 {_requests.Count} 次请求准备响应（{request.Method} {request.RequestUri}）。");
        }
    }
}

/// <summary>一次被记录下来的请求。</summary>
/// <param name="Method">方法。</param>
/// <param name="Url">完整地址。</param>
/// <param name="Body">请求体（表单或 JSON 原文）。</param>
/// <param name="ContractVersion"><c>x-xbl-contract-version</c> 请求头。</param>
/// <param name="Authorization"><c>Authorization</c> 请求头。</param>
/// <param name="ContentType">请求体的媒体类型（D-9 要断言文件字段是 <c>image/png</c>）。</param>
internal sealed record CapturedRequest(
    HttpMethod Method,
    string Url,
    string Body,
    string ContractVersion,
    string Authorization,
    string ContentType);

/// <summary>返回固定桩的 <see cref="IHttpClientFactory"/>。</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly List<string> _requestedNames = [];
    private readonly object _gate = new();

    /// <summary>构造工厂。</summary>
    /// <param name="handler">底层处理器。</param>
    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    /// <summary>创建过的客户端名字（断言"用的是命名客户端"）—— 快照，并发下不会丢。</summary>
    public IReadOnlyList<string> RequestedNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _requestedNames];
            }
        }
    }

    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        lock (_gate)
        {
            _requestedNames.Add(name);
        }

        return new HttpClient(_handler, disposeHandler: false);
    }
}
