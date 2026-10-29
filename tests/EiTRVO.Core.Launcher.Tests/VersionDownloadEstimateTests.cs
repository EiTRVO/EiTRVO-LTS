using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// B-2 / p04：向导第 2 步的"安装将下载约 X"必须按版本 JSON 估算，而不是写死一个数。
/// </summary>
[TestClass]
public sealed class VersionDownloadEstimateTests
{
    private const long Mb = 1024 * 1024;

    [TestMethod]
    public void ItSumsTheClientLibrariesAssetsAndWindowsNatives()
    {
        var json = new VersionJson
        {
            Downloads = new VersionDownloads
            {
                Client = new DownloadInfo { Size = 20 * Mb },
            },
            AssetIndex = new AssetIndexInfo { TotalSize = 100 * Mb },
            Libraries =
            [
                new Library
                {
                    Name = "com.example:plain:1.0",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadInfo { Size = 50 * Mb },
                    },
                },
                new Library
                {
                    Name = "org.lwjgl:lwjgl:2.9.1",
                    Natives = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["windows"] = "natives-windows",
                    },
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadInfo { Size = 10 * Mb },
                        Classifiers = new Dictionary<string, DownloadInfo>(StringComparer.Ordinal)
                        {
                            ["natives-windows"] = new DownloadInfo { Size = 5 * Mb },
                        },
                    },
                },
            ],
        };

        var estimate = VersionDownloadEstimator.Estimate(json);

        Assert.AreEqual(20 * Mb, estimate.ClientBytes);
        Assert.AreEqual(60 * Mb, estimate.LibraryBytes);
        Assert.AreEqual(100 * Mb, estimate.AssetBytes);
        Assert.AreEqual(5 * Mb, estimate.NativeBytes);
        Assert.AreEqual(185 * Mb, estimate.TotalBytes);
    }

    [TestMethod]
    public void LibrariesForOtherOperatingSystemsAreNotCounted()
    {
        // 规则表只保留 Windows 可用的库；否则会把 macOS/Linux 几十 MB 也算进去。
        var json = new VersionJson
        {
            Libraries =
            [
                new Library
                {
                    Name = "com.example:osx-only:1.0",
                    Rules =
                    [
                        new VersionRule("allow", new VersionRuleOs("osx", null, null), null),
                    ],
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadInfo { Size = 99 * Mb },
                    },
                },
                new Library
                {
                    Name = "com.example:windows:1.0",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadInfo { Size = 7 * Mb },
                    },
                },
            ],
        };

        var estimate = VersionDownloadEstimator.Estimate(json);

        Assert.AreEqual(7 * Mb, estimate.LibraryBytes);
    }

    [TestMethod]
    public void UnknownSizesCountAsZeroInsteadOfGuessing()
    {
        var json = new VersionJson
        {
            Libraries = [new Library { Name = "com.example:no-size:1.0" }],
        };

        var estimate = VersionDownloadEstimator.Estimate(json);

        Assert.AreEqual(0, estimate.TotalBytes);
    }
}
