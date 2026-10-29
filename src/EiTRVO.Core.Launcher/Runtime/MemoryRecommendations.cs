namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>内存推荐值（M3 的 C-11）。单位一律 MB。</summary>
/// <param name="TotalMb">计算依据：物理内存总量；读不出来时为 <c>0</c>。</param>
/// <param name="DefaultMb">推荐默认值（用户没设过时用它）。</param>
/// <param name="SafeZoneMb">安全区上界（≤ 它算绿区）。</param>
/// <param name="WarnZoneMb">警告区上界（&gt; 它算红区）。</param>
/// <param name="MaxMb">可分配上限（再高就是手改错了，或把自己饿死）。</param>
public sealed record MemoryRecommendation(
    long TotalMb,
    int DefaultMb,
    int SafeZoneMb,
    int WarnZoneMb,
    int MaxMb);

/// <summary>
/// 由物理内存推内存推荐值（Neo 的 <c>SystemMemoryInfo</c> 里那套百分比，去掉静态构造后搬过来重写）。
/// <para>
/// 与 Neo 的区别只有一条，但是关键的一条：<b>低于 2 GB 的机器要向下收敛</b>。
/// Neo 的写法是 <c>Clamp(total * 0.20, 2048, 8192)</c> —— 下限写死 2048，
/// 于是 1 GB 的机器会得到"推荐 2048 MB"，比整机物理内存还大；
/// 用户照着填，游戏必然起不来，而且看起来像是启动器的错。这里改成
/// **先按百分比算，再用"给系统留 512 MB"的上界收一次**，因此
/// <c>1 GB → 512 MB</c>、<c>2 GB → 1024 MB</c>，且始终满足
/// <c>MinimumMb ≤ DefaultMb ≤ SafeZoneMb ≤ WarnZoneMb ≤ MaxMb</c>。
/// </para>
/// </summary>
public static class MemoryRecommendations
{
    /// <summary>能跑 Minecraft 的最低分配（MB）。再低连加载都过不去。</summary>
    public const int MinimumMb = 512;

    /// <summary>给操作系统留出的余量（MB）：不留的话分配成功那一刻就开始换页。</summary>
    private const int ReservedForSystemMb = 512;

    /// <summary>推荐默认值的比例与上下限。</summary>
    private const double DefaultRatio = 0.25;
    private const int DefaultFloorMb = 1024;
    private const int DefaultCeilingMb = 8192;

    private const double SafeZoneRatio = 0.50;
    private const int SafeZoneFloorMb = 1024;
    private const int SafeZoneCeilingMb = 16384;

    private const double WarnZoneRatio = 0.65;
    private const int WarnZoneFloorMb = 1024;
    private const int WarnZoneCeilingMb = 24576;

    private const double MaxRatio = 0.70;
    private const int MaxFloorMb = 1024;
    private const int MaxCeilingMb = 32768;

    /// <summary>物理内存读不出来时用的保守推荐（8 GB 机器的档位）。</summary>
    public static MemoryRecommendation Unknown { get; } = new(
        TotalMb: 0,
        DefaultMb: 2048,
        SafeZoneMb: 4096,
        WarnZoneMb: 6144,
        MaxMb: 8192);

    /// <summary>按物理内存总量算推荐值。</summary>
    /// <param name="totalPhysicalMb">物理内存总量（MB）；<c>0</c> 表示读不出来。</param>
    /// <returns>推荐值。</returns>
    public static MemoryRecommendation For(long totalPhysicalMb)
    {
        if (totalPhysicalMb <= 0)
        {
            return Unknown;
        }

        // 分配上限不能超过"物理内存 - 给系统留的余量"；连余量都留不出来时退到下限，
        // 于是极小内存的机器上四档会收敛到同一个数 —— 这正是这个函数存在的意义。
        var ceiling = Math.Max(MinimumMb, totalPhysicalMb - ReservedForSystemMb);

        var max = Math.Min(Ceiling(totalPhysicalMb * MaxRatio, MaxFloorMb, MaxCeilingMb), ceiling);
        var warn = Math.Min(Ceiling(totalPhysicalMb * WarnZoneRatio, WarnZoneFloorMb, WarnZoneCeilingMb), max);
        var safe = Math.Min(Ceiling(totalPhysicalMb * SafeZoneRatio, SafeZoneFloorMb, SafeZoneCeilingMb), warn);
        var recommended = Math.Min(Ceiling(totalPhysicalMb * DefaultRatio, DefaultFloorMb, DefaultCeilingMb), safe);

        return new MemoryRecommendation(
            TotalMb: totalPhysicalMb,
            DefaultMb: (int)recommended,
            SafeZoneMb: (int)safe,
            WarnZoneMb: (int)warn,
            MaxMb: (int)max);
    }

    private static long Ceiling(double value, int floor, int ceiling) =>
        Math.Clamp((long)value, floor, ceiling);
}
