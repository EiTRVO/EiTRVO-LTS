using System.Net;

namespace EiTRVO.Core.Launcher.Net;

/// <summary>
/// 重试策略：重试几次、什么情况该重试、每次等多久。
/// <para>
/// 刻意做成**纯逻辑**（不碰网络、不等待），因为"等多久"必须能被断言到具体值 ——
/// 这正是审计 §4.4 的教训：测试只断言"没抛异常"的代码，缺陷不会被发现。
/// 真正的等待由 <see cref="RetryingHandler"/> 执行，并且它可以注入假的延迟函数。
/// </para>
/// <para>
/// 不做随机抖动：退避序列是 1s → 2s → 4s（带上限），配合 3 次尝试与域名白名单，
/// 不存在惊群问题；而确定性让"第 N 次重试等 X 秒"可以被测试钉死。
/// </para>
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>默认：3 次尝试，退避 1s / 2s / 4s，上限 8s。</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>构造策略。</summary>
    /// <param name="maxAttempts">总尝试次数（含首次），至少 1。</param>
    /// <param name="baseDelay">首次重试前的等待，必须为正。</param>
    /// <param name="maxDelay">单次等待上限，必须不小于 <paramref name="baseDelay"/>。</param>
    /// <param name="idempotentOnly">是否只对幂等方法（GET / HEAD）重试。</param>
    public RetryPolicy(
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        bool idempotentOnly = true)
    {
        var resolvedBase = baseDelay ?? TimeSpan.FromSeconds(1);
        var resolvedMax = maxDelay ?? TimeSpan.FromSeconds(8);

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "尝试次数至少为 1。");
        }

        if (resolvedBase <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), resolvedBase, "首次退避必须为正。");
        }

        if (resolvedMax < resolvedBase)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), resolvedMax, "退避上限不能小于首次退避。");
        }

        MaxAttempts = maxAttempts;
        BaseDelay = resolvedBase;
        MaxDelay = resolvedMax;
        IdempotentOnly = idempotentOnly;
    }

    /// <summary>总尝试次数（含首次）。</summary>
    public int MaxAttempts { get; }

    /// <summary>首次重试前的等待。</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>单次等待上限。</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// 是否只对幂等方法重试。
    /// <para>
    /// 默认 <see langword="true"/>：<see cref="HttpRequestMessage"/> 发出后不能再发第二次
    /// （内容会被标记为已发送），因此对 POST 做透明重试需要重建请求体。
    /// 认证链里的 POST 由调用方自己用新请求重试 —— 这里宁可少做，也不做半对的重试。
    /// </para>
    /// </summary>
    public bool IdempotentOnly { get; }

    /// <summary>该 HTTP 方法是否允许重试。</summary>
    /// <param name="method">请求方法。</param>
    /// <returns>允许返回 <see langword="true"/>。</returns>
    public bool IsRetryableMethod(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return !IdempotentOnly
            || HttpMethod.Get.Equals(method)
            || HttpMethod.Head.Equals(method);
    }

    /// <summary>该状态码是否值得重试（限流、超时、服务端临时故障）。</summary>
    /// <param name="statusCode">响应状态码。</param>
    /// <returns>值得重试返回 <see langword="true"/>。</returns>
    public static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>该异常是否值得重试。</summary>
    /// <param name="exception">捕获到的异常。</param>
    /// <returns>值得重试返回 <see langword="true"/>。</returns>
    public static bool IsRetryableException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 网络层错误（连接被重置、DNS 抖动）与读超时值得重试；
        // 策略拒绝（HttpPolicyException）不在其列 —— 再试一百次也不会放行。
        return exception is HttpRequestException
            or IOException
            or TaskCanceledException;
    }

    /// <summary>第 <paramref name="failedAttempt"/> 次尝试失败后应等待多久。</summary>
    /// <param name="failedAttempt">刚失败的尝试序号（从 1 开始）。</param>
    /// <returns>等待时长。</returns>
    public TimeSpan GetDelay(int failedAttempt)
    {
        if (failedAttempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAttempt), failedAttempt, "尝试序号从 1 开始。");
        }

        // base * 2^(n-1)，指数会迅速超过上限，所以先夹住指数再乘，避免移位溢出。
        var exponent = Math.Min(failedAttempt - 1, 16);
        var ticks = BaseDelay.Ticks * (1L << exponent);
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks(ticks);
    }
}
