namespace EiTRVO.Core.Launcher.Abstractions;

/// <summary>
/// 时间源。
/// <para>
/// 存在的唯一理由：**让涉及时间的逻辑可测** —— 事件时间戳的格式化、游玩时长统计、
/// 缓存 TTL 判定、下载限速窗口。这些如果直接读 <see cref="DateTimeOffset.Now"/>，
/// 测试就只能断言"看起来差不多"，无法断言具体值。
/// </para>
/// <para>
/// 刻意只有 <see cref="Now"/> 一个成员：时长用 <see cref="System.Diagnostics.Stopwatch"/> 量，
/// 不需要时钟参与；再加第二个成员只会引入两个时间源不一致的风险。
/// </para>
/// </summary>
public interface IClock
{
    /// <summary>当前时间（本地时区偏移）。</summary>
    DateTimeOffset Now { get; }
}
