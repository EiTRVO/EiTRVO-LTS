using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// 固定时钟：让「事件时间戳」「上次启动时间」这类断言的期望值是一个**具体值**，
/// 而不是「差不多是现在」。
/// </summary>
internal sealed class FixedClock : IClock
{
    /// <summary>构造固定时钟。</summary>
    /// <param name="now">要返回的时间。</param>
    public FixedClock(DateTimeOffset now) => Now = now;

    /// <inheritdoc />
    public DateTimeOffset Now { get; set; }
}
