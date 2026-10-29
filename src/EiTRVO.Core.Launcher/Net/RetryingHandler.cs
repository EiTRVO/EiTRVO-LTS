using Microsoft.Extensions.Logging;

namespace EiTRVO.Core.Launcher.Net;

/// <summary>
/// 按 <see cref="RetryPolicy"/> 重试出站请求。
/// <para>
/// 等待通过构造参数注入（<c>delayAsync</c>），默认是 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。
/// 这样单测可以断言"重试了几次、每次等多久"，而不是真的睡几秒 —— 也才能断言到具体值。
/// </para>
/// <para>
/// 只重试幂等方法（见 <see cref="RetryPolicy.IdempotentOnly"/>）：<see cref="HttpRequestMessage"/>
/// 发出后无法再次发送，对 POST 做透明重试需要重建请求体，那是调用方的事。
/// </para>
/// </summary>
public sealed partial class RetryingHandler : DelegatingHandler
{
    private readonly RetryPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ILogger<RetryingHandler> _logger;

    /// <summary>构造处理器。</summary>
    /// <param name="policy">重试策略。</param>
    /// <param name="logger">日志（单测里传 <c>NullLogger</c>）。</param>
    /// <param name="delayAsync">等待实现；缺省用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。</param>
    public RetryingHandler(
        RetryPolicy policy,
        ILogger<RetryingHandler> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _policy = policy;
        _logger = logger;
        _delayAsync = delayAsync ?? (static (delay, token) => Task.Delay(delay, token));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_policy.IsRetryableMethod(request.Method))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isLastAttempt = attempt >= _policy.MaxAttempts;

            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (isLastAttempt || !RetryPolicy.IsRetryableStatus(response.StatusCode))
                {
                    return response;
                }

                // 要重试就必须释放这次响应，否则连接会被占住到 GC。
                var statusCode = response.StatusCode;
                response.Dispose();
                LogRetryingStatus(attempt, (int)statusCode, request.RequestUri);
            }
            catch (Exception ex) when (!isLastAttempt && RetryPolicy.IsRetryableException(ex))
            {
                LogRetryingException(ex, attempt, request.RequestUri);
            }

            await DelayBeforeNextAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task DelayBeforeNextAttemptAsync(int failedAttempt, CancellationToken cancellationToken)
    {
        var delay = _policy.GetDelay(failedAttempt);
        return delay <= TimeSpan.Zero ? Task.CompletedTask : _delayAsync(delay, cancellationToken);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "第 {Attempt} 次请求返回 {StatusCode}，将重试：{Uri}")]
    private partial void LogRetryingStatus(int attempt, int statusCode, Uri? uri);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "第 {Attempt} 次请求失败，将重试：{Uri}")]
    private partial void LogRetryingException(Exception exception, int attempt, Uri? uri);
}
