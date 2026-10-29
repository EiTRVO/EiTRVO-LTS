using System.Net;
using System.Text;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// 可编排的 HTTP 桩：按顺序给出响应（或抛异常），并记录每一次请求。
/// <para>
/// 存在的理由是审计 §4.4 的教训 —— Neo 的 HTTP 测试从不断言请求 URL 与查询参数，
/// 于是"请求发错了地址"这类缺陷没有任何回归保护。这里的记录器让
/// <see cref="HttpAssert"/> 能把"第 N 次请求到底打到了哪个 URL"断言到具体字符串。
/// </para>
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps = new();
    private readonly List<(string Fragment, Func<HttpRequestMessage, HttpResponseMessage> Factory)> _rules = [];
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string> _bodies = [];
    private readonly object _gate = new();

    /// <summary>
    /// 按顺序收到的请求。
    /// </summary>
    /// <remarks>
    /// 返回的是**快照**（每次读都复制一份）：并发下载的用例里多个下载线程会同时发请求，
    /// 而 <see cref="List{T}"/> 不是线程安全的 —— 直接往里面 <c>Add</c> 会让记录**丢条目**
    /// （实测：8 个文件的用例偶发只记下 7 次请求，于是断言"发了 8 次"随机失败，
    /// 报出来的却是"下载器多发/少发了请求"）。记录本身必须是原子的，检查才谈得上可信。
    /// </remarks>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// 收到的 <c>application/json</c> 请求体（按请求顺序；只记这一种内容类型）。
    /// </summary>
    /// <remarks>
    /// 存在的理由与 URL 记录一样：Modrinth 的哈希对账是 **POST**，请求形状（<c>hashes</c> /
    /// <c>algorithm</c> / <c>loaders</c> / <c>game_versions</c>）只能从请求体上看出来。
    /// 只记 JSON 是刻意的 —— 下载用例的内容是几十 MB 的字节，读成字符串没有意义。
    /// </remarks>
    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_gate)
            {
                return [.. _bodies];
            }
        }
    }

    /// <summary>入队一个响应。</summary>    /// <param name="statusCode">状态码。</param>
    /// <param name="body">响应体（可空）。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler Respond(HttpStatusCode statusCode, string? body = null)
    {
        _steps.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = body is null ? null : new StringContent(body),
        });
        return this;
    }

    /// <summary>
    /// 按地址匹配的响应：请求 URL 里含该片段就用它，**与请求顺序无关**。
    /// <para>
    /// 并发下载的用例离不开它：一次安装要下客户端 JAR、库、natives、资源索引、几百个资源对象，
    /// 用队列桩就得假设"哪个先跑"，而并发下这个顺序本来就不确定 —— 那样的用例是随机失败的用例。
    /// </para>
    /// </summary>
    /// <param name="urlFragment">URL 里应包含的片段。</param>
    /// <param name="statusCode">状态码。</param>
    /// <param name="body">响应体。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler RespondTo(string urlFragment, HttpStatusCode statusCode, string? body = null) =>
        RespondBytesTo(urlFragment, body is null ? null : Encoding.UTF8.GetBytes(body), statusCode);

    /// <summary>按地址匹配的**二进制**响应（natives 归档这类内容必须是真字节）。</summary>
    /// <param name="urlFragment">URL 里应包含的片段。</param>
    /// <param name="content">响应体字节。</param>
    /// <param name="statusCode">状态码。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler RespondBytesTo(
        string urlFragment,
        byte[]? content,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _rules.Add((urlFragment, _ => new HttpResponseMessage(statusCode)
        {
            Content = content is null ? null : new ByteArrayContent(content),
        }));

        return this;
    }

    /// <summary>按地址匹配一个异常（如连接被重置）。</summary>
    /// <param name="urlFragment">URL 里应包含的片段。</param>
    /// <param name="exception">要抛出的异常。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler ThrowTo(string urlFragment, Exception exception)
    {
        _rules.Add((urlFragment, _ => throw exception));
        return this;
    }

    /// <summary>入队一个异常。</summary>
    /// <param name="exception">要抛出的异常。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler Throw(Exception exception)
    {
        _steps.Enqueue(_ => throw exception);
        return this;
    }

    /// <summary>
    /// 入队一个**重定向**响应（带 <c>Location</c>）。
    /// <para>
    /// 重定向要能测：自动重定向发生在底层处理器里，所以"白名单主机回一张 302 把我们带去别处"
    /// 这条路径只有在桩能造出 <c>Location</c> 时才测得到。
    /// </para>
    /// </summary>
    /// <param name="location">目标地址（可以是相对地址）。</param>
    /// <param name="statusCode">状态码，默认 302。</param>
    /// <returns>自身，便于链式编排。</returns>
    public StubHandler RedirectTo(string location, HttpStatusCode statusCode = HttpStatusCode.Found)
    {
        _steps.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        });

        return this;
    }

    /// <summary>第 <paramref name="index"/> 次请求的地址。</summary>
    /// <param name="index">从 0 开始的序号。</param>
    /// <returns>请求地址。</returns>
    public Uri UriAt(int index) => Requests[index].RequestUri!;

    /// <summary>第 <paramref name="index"/> 次请求的查询参数值。</summary>
    /// <param name="index">从 0 开始的序号。</param>
    /// <param name="key">参数名。</param>
    /// <returns>参数值；不存在时为 <see langword="null"/>。</returns>
    public string? QueryAt(int index, string key)
    {
        var query = Requests[index].RequestUri!.Query;
        if (query.Length <= 1)
        {
            return null;
        }

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(pair[..separator]);
            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // M7-g：可选的"进站闸门"。用例需要在一个任务**确实正在跑**的时候去动队列
        // （取消 / 重试 / 清空），而"跑得够慢"不能靠 sleep 猜 —— 这里让用例把第一个请求卡住，
        // 拿到 <see cref="Gate"/> 的信号之后再放行。
        var gate = Gate;

        if (gate is not null)
        {
            await gate(request, cancellationToken).ConfigureAwait(false);
        }

        return Respond(request);
    }

    /// <summary>
    /// 可选的进站闸门（默认 <see langword="null"/> = 不挡）。
    /// </summary>
    /// <remarks>
    /// 加了它是为了**确定性地**把任务卡在"正在执行"：测试里任何"等 200 毫秒它应该跑起来了吧"
    /// 都是随机失败的来源。闸门必须是并发安全的（下载是几十路并发的），用例自己用
    /// <see cref="TaskCompletionSource"/> 实现。
    /// </remarks>
    public Func<HttpRequestMessage, CancellationToken, Task>? Gate { get; set; }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        // 记录与"取哪个响应"都在锁里：并发下载时多个线程同时进来，
        // 队列桩的 Dequeue 同样不是线程安全的（RespondTo 的按地址匹配不受顺序影响，但记录会丢）。
        lock (_gate)
        {
            _requests.Add(request);

            if (request.Content is not null
                && string.Equals(
                    request.Content.Headers.ContentType?.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase))
            {
                _bodies.Add(request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());
            }

            var url = request.RequestUri?.ToString() ?? string.Empty;

            foreach (var (fragment, factory) in _rules)
            {
                if (url.Contains(fragment, StringComparison.Ordinal))
                {
                    return factory(request);
                }
            }

            if (_steps.Count == 0)
            {
                throw new InvalidOperationException(
                    $"桩没有为第 {_requests.Count} 次请求准备响应（{request.Method} {request.RequestUri}）。");
            }

            return _steps.Dequeue()(request);
        }
    }
}

/// <summary>
/// 返回固定桩的 <see cref="IHttpClientFactory"/>，供"构造注入工厂"的服务做单测。
/// </summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly List<string> _requestedNames = [];
    private readonly object _gate = new();

    /// <summary>构造工厂。</summary>
    /// <param name="handler">底层处理器（通常是 <see cref="StubHandler"/>）。</param>
    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    /// <summary>创建过的客户端名字，便于断言"服务用的是哪个命名客户端"（快照，并发下不会丢）。</summary>
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

/// <summary>
/// 请求断言工具。**所有涉及 HTTP 的测试都应当经它断言 URL 与查询参数**（审计 §4.4）。
/// </summary>
internal static class HttpAssert
{
    /// <summary>断言请求次数。</summary>
    /// <param name="handler">桩。</param>
    /// <param name="expected">期望次数。</param>
    public static void RequestCount(StubHandler handler, int expected)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Assert.AreEqual(
            expected,
            handler.Requests.Count,
            "实际请求：" + string.Join(" → ", handler.Requests.Select(static r => r.RequestUri?.ToString())));
    }

    /// <summary>断言第 N 次请求的完整 URL（含查询串）。</summary>
    /// <param name="handler">桩。</param>
    /// <param name="index">从 0 开始的序号。</param>
    /// <param name="expected">期望的完整 URL。</param>
    public static void Url(StubHandler handler, int index, string expected)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Assert.AreEqual(expected, handler.UriAt(index).ToString());
    }

    /// <summary>断言第 N 次请求的方法。</summary>
    /// <param name="handler">桩。</param>
    /// <param name="index">从 0 开始的序号。</param>
    /// <param name="expected">期望的方法。</param>
    public static void Method(StubHandler handler, int index, HttpMethod expected)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Assert.AreEqual(expected, handler.Requests[index].Method);
    }

    /// <summary>断言第 N 次请求的某个查询参数值。</summary>
    /// <param name="handler">桩。</param>
    /// <param name="index">从 0 开始的序号。</param>
    /// <param name="key">参数名。</param>
    /// <param name="expected">期望值。</param>
    public static void Query(StubHandler handler, int index, string key, string? expected)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Assert.AreEqual(expected, handler.QueryAt(index, key), $"查询参数 {key} 不符。");
    }
}
