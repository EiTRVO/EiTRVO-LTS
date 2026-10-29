using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-e：把设置里的内存按本机物理内存的上限收敛一次。
/// <para>
/// 这里钉住的是**关系**（实际采用 ≤ 设置值、上限来自本机、读不出来时不动用户的设置）
/// 以及几个手算过的具体数字 —— 关系用例能挡住逻辑写反，具体数字能挡住比例被悄悄改动。
/// </para>
/// </summary>
[TestClass]
public sealed class MemoryAllocationTests
{
    [TestMethod]
    public void AValueAboveTheMachineLimitIsCappedAndSaysWhatTheLimitIs()
    {
        // 8 GB 机器：可分配上限 = min(0.70 × 8192, 8192 − 512) = 5734 MB。
        var memory = MemoryRecommendations.For(8192);

        var allocation = MemoryAllocation.Resolve(16384, memory);

        Assert.AreEqual(16384, allocation.RequestedMb, "设置里的原值要保留 —— 界面靠它说清「我填的是 16384」。");
        Assert.AreEqual(5734, allocation.EffectiveMb);
        Assert.AreEqual(5734, allocation.LimitMb);
        Assert.IsTrue(allocation.WasCapped);
    }

    [TestMethod]
    public void AValueWithinTheLimitIsUsedAsIs()
    {
        // 32 GB 机器：0.70 × 32768 = 22937 MB，用户填的 8192 完全在范围内。
        var allocation = MemoryAllocation.Resolve(8192, MemoryRecommendations.For(32768));

        Assert.AreEqual(8192, allocation.EffectiveMb);
        Assert.AreEqual(22937, allocation.LimitMb);
        Assert.IsFalse(allocation.WasCapped, "没超上限就不该改用户的值。");
    }

    [TestMethod]
    public void ATinyMachineCannotGetMoreThanItHasEvenIfTheDefaultAsksForIt()
    {
        // 2 GB 机器 + 出厂默认 4096：这正是"照着默认值填、游戏必然起不来"的那种机器。
        var allocation = MemoryAllocation.Resolve(4096, MemoryRecommendations.For(2048));

        Assert.AreEqual(1433, allocation.EffectiveMb, "实际分配必须落在这台机器装得下的范围内。");
        Assert.IsTrue(allocation.WasCapped);
    }

    [TestMethod]
    public void UnreadablePhysicalMemoryLeavesTheUsersValueAlone()
    {
        // 读不出来（TotalMb = 0）就没有依据：不猜一个上限去改用户的设置（同 M3 的「没有检测结果不下断言」）。
        var allocation = MemoryAllocation.Resolve(12288, MemoryRecommendations.Unknown);

        Assert.AreEqual(12288, allocation.EffectiveMb);
        Assert.IsNull(allocation.LimitMb, "没有依据时 LimitMb 必须是 null —— 界面靠它区分「没超」和「不知道」。");
        Assert.IsFalse(allocation.WasCapped);
    }

    [TestMethod]
    public void TheLimitNeverGoesBelowWhatMinecraftNeedsToLoad()
    {
        // 防御性的一条：本机信息给来一个比下限还小的上限时，按下限走 ——
        // 把分配压到 512 MB 以下，游戏连加载都过不去，那比"用得多一点"更糟。
        var bogus = new MemoryRecommendation(
            TotalMb: 4096,
            DefaultMb: 1024,
            SafeZoneMb: 1024,
            WarnZoneMb: 1024,
            MaxMb: 64);

        var allocation = MemoryAllocation.Resolve(4096, bogus);

        Assert.AreEqual(MemoryRecommendations.MinimumMb, allocation.EffectiveMb);
        Assert.AreEqual(MemoryRecommendations.MinimumMb, allocation.LimitMb);
    }

    [TestMethod]
    public void EveryTotalKeepsTheInvariants()
    {
        // 关系用例覆盖一大片机器：无论多少内存，都不得出现"实际比设置大"或"低于下限"。
        foreach (var totalMb in new long[] { 0, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 262144 })
        {
            var memory = MemoryRecommendations.For(totalMb);

            foreach (var requested in new[] { 512, 1024, 4096, 16384, 65536 })
            {
                var allocation = MemoryAllocation.Resolve(requested, memory);

                Assert.IsTrue(allocation.EffectiveMb <= requested, $"{totalMb} MB 机器：实际分配不能超过设置值。");
                Assert.IsTrue(
                    allocation.EffectiveMb >= MemoryRecommendations.MinimumMb,
                    $"{totalMb} MB 机器：实际分配不能低于能加载的下限。");
                Assert.AreEqual(allocation.WasCapped, allocation.EffectiveMb < requested);

                if (totalMb <= 0)
                {
                    Assert.IsNull(allocation.LimitMb);
                    Assert.AreEqual(requested, allocation.EffectiveMb);
                }
                else
                {
                    Assert.IsNotNull(allocation.LimitMb);
                }
            }
        }
    }

    [TestMethod]
    public void ANonPositiveRequestIsRejectedInsteadOfBecomingAStrangeCommandLine()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => MemoryAllocation.Resolve(0, MemoryRecommendations.Unknown));

        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => MemoryAllocation.Resolve(-1, MemoryRecommendations.For(32768)));
    }
}
