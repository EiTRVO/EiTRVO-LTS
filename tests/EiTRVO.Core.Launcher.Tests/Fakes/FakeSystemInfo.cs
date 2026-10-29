using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// 本机信息的替身：只给物理内存总量，其余文本与真实机器无关。
/// <para>
/// 内存推荐值刻意走**真实规则**（<see cref="MemoryRecommendations.For"/>）而不是写死几个数字：
/// M4-e 的收敛逻辑要验的正是"设置值 × 本机上限"这条关系，
/// 把推荐值也替身掉就等于自己给自己出题。
/// </para>
/// </summary>
internal sealed class FakeSystemInfoProvider : ISystemInfoProvider
{
    /// <summary>物理内存总量（MB）；<c>0</c> 表示读不出来。</summary>
    public long TotalPhysicalMemoryMb { get; set; } = 32768;

    /// <inheritdoc />
    public string OperatingSystem => "Windows 11";

    /// <inheritdoc />
    public string Processor => "Fake CPU";

    /// <inheritdoc />
    public string InstalledMemory => "32.0 GB";

    /// <inheritdoc />
    public string TotalMemory => TotalPhysicalMemoryMb <= 0
        ? "未知"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{TotalPhysicalMemoryMb / 1024.0:0.0} GB");

    /// <inheritdoc />
    public MemoryRecommendation Memory => MemoryRecommendations.For(TotalPhysicalMemoryMb);

    /// <inheritdoc />
    public string GraphicsCard => "Fake GPU";
}
