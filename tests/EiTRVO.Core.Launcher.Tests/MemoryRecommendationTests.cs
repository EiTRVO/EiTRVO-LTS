using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M3 的 C-11：由物理内存推内存推荐值。
/// <para>
/// 设计稿的「物理内存 32.0 GB · 推荐 8–16 GB · 安全区上限 16384 MB」正是 32 GB 机器
/// 按这套规则算出来的结果，所以那个用例是在逐字复现设计稿；
/// 剩下的用例守的是 Neo 那个版本真正的缺陷：**下限写死 2048**，于是 1 GB 的机器
/// 会得到"推荐 2048 MB"（比整机内存还大），用户照着填必然起不来。
/// </para>
/// </summary>
[TestClass]
public sealed class MemoryRecommendationTests
{
    [TestMethod]
    public void ThirtyTwoGigabytesReproducesTheDesignTextVerbatim()
    {
        // 设计稿那一行是「物理内存 32.0 GB · 推荐 8–16 GB · 安全区上限 16384 MB」，
        // 也就是这四个数字（推荐区间 = 默认值 ~ 安全区）。
        var memory = MemoryRecommendations.For(32768);

        Assert.AreEqual(32768, memory.TotalMb);
        Assert.AreEqual(8192, memory.DefaultMb);
        Assert.AreEqual(16384, memory.SafeZoneMb);
        Assert.AreEqual(21299, memory.WarnZoneMb);
        Assert.AreEqual(22937, memory.MaxMb);
    }

    [TestMethod]
    public void TheRecommendationAlwaysFitsInsideTheMachine()
    {
        // 这是 Neo 的写法翻车的地方：Clamp(total * 0.20, 2048, 8192) 在 1 GB 机器上给出 2048。
        foreach (var total in new long[] { 512, 1024, 1536, 2048, 3072, 4096, 8192, 16384, 32768, 65536 })
        {
            var memory = MemoryRecommendations.For(total);

            Assert.IsTrue(
                memory.DefaultMb <= total,
                $"{total} MB 的机器上推荐了 {memory.DefaultMb} MB 默认内存，超过整机物理内存。");
            Assert.IsTrue(
                memory.MaxMb <= Math.Max(MemoryRecommendations.MinimumMb, total - 512),
                $"{total} MB 的机器上允许分配到 {memory.MaxMb} MB，没给系统留余量。");
            Assert.IsTrue(
                memory.DefaultMb >= MemoryRecommendations.MinimumMb,
                $"{total} MB 的机器上推荐了 {memory.DefaultMb} MB，低于能跑起来的下限。");
        }
    }

    [TestMethod]
    [DataRow(1024, 512)]
    [DataRow(2048, 1024)]
    [DataRow(4096, 1024)]
    [DataRow(8192, 2048)]
    [DataRow(16384, 4096)]
    [DataRow(32768, 8192)]
    public void SmallMachinesConvergeInsteadOfGettingAnImpossibleDefault(int totalMb, int expectedDefaultMb)
    {
        Assert.AreEqual(expectedDefaultMb, MemoryRecommendations.For(totalMb).DefaultMb);
    }

    [TestMethod]
    public void TheFourNumbersAreAlwaysOrdered()
    {
        // 界面用它们画"推荐区间"与"安全区上限"，顺序错了会显示出 "推荐 16–8 GB" 这种文本。
        foreach (var total in new long[] { 512, 1024, 2048, 4096, 6144, 12288, 32768, 262144 })
        {
            var memory = MemoryRecommendations.For(total);

            Assert.IsTrue(memory.DefaultMb <= memory.SafeZoneMb, $"{total}: 推荐默认值大于安全区。");
            Assert.IsTrue(memory.SafeZoneMb <= memory.WarnZoneMb, $"{total}: 安全区大于警告区。");
            Assert.IsTrue(memory.WarnZoneMb <= memory.MaxMb, $"{total}: 警告区大于可分配上限。");
        }
    }

    [TestMethod]
    public void AnUnreadableTotalFallsBackToConservativeNumbersRatherThanZero()
    {
        var memory = MemoryRecommendations.For(0);

        Assert.AreEqual(0, memory.TotalMb, "读不出来就是读不出来，不编一个总量。");
        Assert.IsTrue(memory.DefaultMb >= MemoryRecommendations.MinimumMb);
        Assert.IsTrue(memory.MaxMb >= memory.DefaultMb);
        Assert.AreEqual(MemoryRecommendations.Unknown, memory);
    }
}
