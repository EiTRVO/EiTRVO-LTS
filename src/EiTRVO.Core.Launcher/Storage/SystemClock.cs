using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.Launcher.Storage;

/// <summary>
/// <see cref="IClock"/> 的系统实现。
/// <para>
/// 只此一个实现，单测里换成测试时钟（<c>FixedClock</c>），所以这里不需要单例、
/// 也不需要静态成员 —— 一律由组合根注入。
/// </para>
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;
}
