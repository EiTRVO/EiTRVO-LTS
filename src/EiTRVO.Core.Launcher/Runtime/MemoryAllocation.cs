namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// 启动时**实际采用**的内存分配（M4-e）：把设置里的数字按本机物理内存的上限收敛一次后的结果。
/// <para>
/// 为什么不直接把设置里的数字写进 <c>-Xmx</c>：用户在设置里填 16384、而机器只有 8 GB 时，
/// JVM 会在**分配成功那一刻**开始换页（整个系统一起卡），或者干脆起不来，报出来的却是
/// "游戏崩溃了"——看起来像启动器的错。这里把上限收敛掉，并把"收敛过"这件事如实带到界面上：
/// 三个字段就是为这件事留的，<c>-Xmx</c> 用的是 <see cref="EffectiveMb"/>，
/// 而界面能同时说出"设置里填的是多少、为什么变成了这个数"。
/// </para>
/// <para>
/// 物理内存读不出来时**不下断言**（与 M3「没有检测结果时不得声称缺少 Java」同一条纪律）：
/// 此时 <see cref="LimitMb"/> 为 <see langword="null"/>，用户的设置原样采用 ——
/// 拿一个猜出来的上限去改用户的设置，是另一种形式的"看起来很确定"。
/// </para>
/// </summary>
/// <param name="RequestedMb">设置里的值（实例覆盖项，或全局默认值）。</param>
/// <param name="EffectiveMb">真正写进命令行 <c>-Xmx</c> 的值。</param>
/// <param name="LimitMb">收敛依据的本机上限；<see langword="null"/> 表示物理内存读不出来、没有依据。</param>
public sealed record MemoryAllocation(int RequestedMb, int EffectiveMb, int? LimitMb)
{
    /// <summary>是否发生了收敛（实际采用的值比设置里小）。</summary>
    public bool WasCapped => EffectiveMb < RequestedMb;

    /// <summary>按本机内存推荐值把用户设置收敛一次。</summary>
    /// <param name="requestedMb">设置里的最大内存（MB）。</param>
    /// <param name="memory">本机内存推荐值（<c>ISystemInfoProvider.Memory</c>）。</param>
    /// <returns>实际采用的内存分配。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestedMb"/> 不是正数。</exception>
    public static MemoryAllocation Resolve(int requestedMb, MemoryRecommendation memory)
    {
        ArgumentNullException.ThrowIfNull(memory);

        // 与 LaunchArgumentBuilder 同一条前置条件：非正数在这里就断掉，别让它变成一条怪命令行。
        if (requestedMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMb), requestedMb, "内存分配必须是正数。");
        }

        if (memory.TotalMb <= 0)
        {
            return new MemoryAllocation(requestedMb, requestedMb, LimitMb: null);
        }

        // 上限不会低到"连加载都过不去"：本机信息给来一个更小的值时按下限走。
        // （MemoryRecommendations 保证 MaxMb ≥ MinimumMb，这里是防御性的第二道。）
        var limit = Math.Max(memory.MaxMb, MemoryRecommendations.MinimumMb);

        return new MemoryAllocation(requestedMb, Math.Min(requestedMb, limit), limit);
    }
}
