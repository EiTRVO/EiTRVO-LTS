using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M3：本机信息（<see cref="ISystemInfoProvider"/> 的真实实现）。
/// <para>
/// 原始数据来自注册表与 kernel32 —— 在这台机器上能读到什么不该进断言（换台机器就飘），
/// 所以这里测的是**拼装规则**：读不出来时显示「—」而不编数字、
/// Windows 11 的 <c>ProductName</c> 谎报要修正、内存推荐值随实际内存走。
/// </para>
/// </summary>
[TestClass]
public sealed class SystemInfoProviderTests
{
    private sealed class FakeSource : ISystemInfoSource
    {
        public long TotalPhysicalMemoryMb { get; set; } = 32768;

        public long AvailablePhysicalMemoryMb { get; set; } = 19046;

        public string OperatingSystemProductName { get; set; } = "Windows 10 Pro";

        public string OperatingSystemDisplayVersion { get; set; } = "24H2";

        public string OperatingSystemBuild { get; set; } = "26100";

        public string ProcessorName { get; set; } = "12th Gen Intel Core i7-12700";

        public string GraphicsAdapterName { get; set; } = "NVIDIA GeForce RTX 4060";
    }

    [TestMethod]
    public void MemoryTextsCarryBothTotalAndAvailable()
    {
        var provider = new SystemInfoProvider(new FakeSource());

        Assert.AreEqual("32.0 GB (可用 18.6 GB)", provider.InstalledMemory);
        Assert.AreEqual("32.0 GB", provider.TotalMemory);
        Assert.AreEqual("12th Gen Intel Core i7-12700", provider.Processor);
        Assert.AreEqual("NVIDIA GeForce RTX 4060", provider.GraphicsCard);
    }

    [TestMethod]
    public void AMachineWithLessThanAGigabyteIsShownInMegabytes()
    {
        var provider = new SystemInfoProvider(new FakeSource
        {
            TotalPhysicalMemoryMb = 512,
            AvailablePhysicalMemoryMb = 320,
        });

        Assert.AreEqual("512 MB (可用 320 MB)", provider.InstalledMemory);
        Assert.AreEqual("512 MB", provider.TotalMemory);
    }

    [TestMethod]
    public void UnreadableValuesBecomeADashInsteadOfMadeUpNumbers()
    {
        // 旧版 Neo 的兜底是"假设 8 GB 总内存、4 GB 可用" ——
        // 那会让 4 GB 的机器在设置页上看到 8 GB，并按错误的量分配内存。
        var provider = new SystemInfoProvider(new FakeSource
        {
            TotalPhysicalMemoryMb = 0,
            AvailablePhysicalMemoryMb = 0,
            ProcessorName = "   ",
            GraphicsAdapterName = string.Empty,
            OperatingSystemProductName = string.Empty,
            OperatingSystemDisplayVersion = string.Empty,
            OperatingSystemBuild = string.Empty,
        });

        Assert.AreEqual("—", provider.InstalledMemory);
        Assert.AreEqual("—", provider.TotalMemory);
        Assert.AreEqual("—", provider.Processor);
        Assert.AreEqual("—", provider.GraphicsCard);
        Assert.AreEqual("—", provider.OperatingSystem);
    }

    [TestMethod]
    public void WindowsElevenIsNotReportedAsWindowsTen()
    {
        // 众所周知的注册表谎报：Windows 11 的 ProductName 至今写着 "Windows 10 Pro"，
        // 只能靠内部版本号（>= 22000）分辨。照抄的话界面会在 11 上显示 10。
        var windows11 = new SystemInfoProvider(new FakeSource());
        var windows10 = new SystemInfoProvider(new FakeSource { OperatingSystemBuild = "19045" });

        Assert.AreEqual("Windows 11 Pro 24H2 (26100)", windows11.OperatingSystem);
        Assert.AreEqual("Windows 10 Pro 24H2 (19045)", windows10.OperatingSystem);
    }

    [TestMethod]
    public void TheOperatingSystemLineOmitsWhateverIsMissing()
    {
        var provider = new SystemInfoProvider(new FakeSource { OperatingSystemDisplayVersion = string.Empty });

        Assert.AreEqual("Windows 11 Pro (26100)", provider.OperatingSystem);
    }

    [TestMethod]
    public void MemoryRecommendationsFollowTheRealTotal()
    {
        var big = new SystemInfoProvider(new FakeSource { TotalPhysicalMemoryMb = 65536 });
        var small = new SystemInfoProvider(new FakeSource { TotalPhysicalMemoryMb = 2048 });

        // 推荐默认值有 8 GB 的上限（再大对 Minecraft 没有意义）。
        Assert.AreEqual(8192, big.Memory.DefaultMb);
        Assert.AreEqual(65536, big.Memory.TotalMb);

        // 2 GB 的机器收敛到 1 GB，而不是"推荐 2 GB 以上"。
        Assert.AreEqual(1024, small.Memory.DefaultMb);
        Assert.AreEqual(2048, small.Memory.TotalMb);
    }

    [TestMethod]
    public void UnreadableMemoryMeansTheConservativeRecommendation()
    {
        var provider = new SystemInfoProvider(new FakeSource { TotalPhysicalMemoryMb = 0 });

        Assert.AreEqual(MemoryRecommendations.Unknown, provider.Memory);
    }
}
