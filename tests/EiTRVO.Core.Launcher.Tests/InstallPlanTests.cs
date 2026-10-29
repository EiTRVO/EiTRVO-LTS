using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2 / B-4：安装计划的三个纯逻辑件 —— Maven 路径、库规则筛选、计划生成。
/// <para>
/// 这三样都**不碰网络也不碰磁盘**，所以可以逐条钉住：装错一个库、漏一个 native、
/// 把文件落到游戏目录之外，在真机上往往只表现为一句含糊的 <c>UnsatisfiedLinkError</c> 或
/// "资源没声音"，靠人眼是查不出来的。
/// </para>
/// </summary>
[TestClass]
public sealed class InstallPlanTests
{
    private const string GameDirectory = @"C:\Games\.minecraft";

    // ==================== Maven 路径 ====================

    [TestMethod]
    public void AMavenCoordinateBecomesARepositoryPath()
    {
        // 迁移自 Neo 的 ModLoaderServiceTests.MavenNameToPath。
        Assert.AreEqual(
            "org/apache/commons/commons-lang3/3.3.2/commons-lang3-3.3.2.jar",
            MavenPath.FromName("org.apache.commons:commons-lang3:3.3.2"));
    }

    [TestMethod]
    public void AClassifierEndsUpInTheFileName()
    {
        Assert.AreEqual(
            "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
            MavenPath.FromName("org.lwjgl:lwjgl:3.3.1:natives-windows"));
    }

    [TestMethod]
    public void AnExtensionOverrideIsHonoured()
    {
        // 真实清单里出现过 group:artifact:version@zip（如某些 natives 打包成 zip）。
        Assert.AreEqual(
            "com/example/pack/1.0/pack-1.0.zip",
            MavenPath.FromName("com.example:pack:1.0@zip"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("only:two")]
    [DataRow("a:b:c:d:e")]
    [DataRow("a::1.0")]
    [DataRow("a:b:1.0@")]
    public void MalformedCoordinatesAreRejectedInsteadOfGuessed(string name)
    {
        Assert.IsFalse(MavenPath.TryFromName(name, out var path));
        Assert.AreEqual(string.Empty, path);
        Assert.ThrowsException<ArgumentException>(() => MavenPath.FromName(name));
    }

    // ==================== 库规则 ====================

    [TestMethod]
    public void WithoutRulesALibraryApplies()
    {
        Assert.IsTrue(LibraryFilter.IsAllowed(null, GamePlatform.WindowsX64));
        Assert.IsTrue(LibraryFilter.IsAllowed([], GamePlatform.WindowsX64));
    }

    [TestMethod]
    public void AWindowsOnlyLibraryIsKeptAndAMacOnlyOneIsDropped()
    {
        VersionRule[] windowsOnly = [new("allow", new VersionRuleOs("windows", null, null), null)];
        VersionRule[] macOnly = [new("allow", new VersionRuleOs("osx", null, null), null)];

        Assert.IsTrue(LibraryFilter.IsAllowed(windowsOnly, GamePlatform.WindowsX64));
        Assert.IsFalse(LibraryFilter.IsAllowed(macOnly, GamePlatform.WindowsX64));
    }

    [TestMethod]
    public void TheLastMatchingRuleDecides()
    {
        // 官方语义：从前往后逐条判断，最后一条匹配的说了算。
        VersionRule[] rules =
        [
            new("allow", null, null),
            new("disallow", new VersionRuleOs("windows", null, null), null),
        ];

        Assert.IsFalse(LibraryFilter.IsAllowed(rules, GamePlatform.WindowsX64));
    }

    [TestMethod]
    public void ARuleThatMatchesNothingMeansNotAllowed()
    {
        VersionRule[] rules = [new("disallow", new VersionRuleOs("linux", null, null), null)];

        Assert.IsFalse(LibraryFilter.IsAllowed(rules, GamePlatform.WindowsX64));
    }

    [TestMethod]
    public void AFeatureRuleOnlyMatchesWhenThatFeatureIsEnabled()
    {
        // Neo 完全忽略 features，于是"只为演示模式准备的库"会被装进普通环境。
        VersionRule[] demoOnly = [new("allow", null, new Dictionary<string, bool>(StringComparer.Ordinal) { ["is_demo_user"] = true })];
        VersionRule[] withCustomResolution =
        [
            new("allow", null, new Dictionary<string, bool>(StringComparer.Ordinal) { ["has_custom_resolution"] = true }),
        ];

        Assert.IsFalse(LibraryFilter.IsAllowed(demoOnly, GamePlatform.WindowsX64), "安装时没有任何特性被启用。");

        var withFeature = GamePlatform.WindowsX64 with
        {
            Features = new Dictionary<string, bool>(StringComparer.Ordinal) { ["is_demo_user"] = true },
        };

        Assert.IsTrue(LibraryFilter.IsAllowed(demoOnly, withFeature));
        Assert.IsFalse(LibraryFilter.IsAllowed(withCustomResolution, withFeature));
    }

    [TestMethod]
    public void AnArchitectureRuleIsEvaluated()
    {
        VersionRule[] x86Only = [new("allow", new VersionRuleOs(null, "x86", null), null)];
        VersionRule[] armOnly = [new("allow", new VersionRuleOs(null, "arm64", null), null)];

        Assert.IsFalse(LibraryFilter.IsAllowed(x86Only, GamePlatform.WindowsX64));
        Assert.IsTrue(LibraryFilter.IsAllowed(armOnly, GamePlatform.WindowsX64 with { OsArch = "arm64" }));
    }

    [TestMethod]
    public void AnOsVersionRuleIsEvaluatedWithATimeout()
    {
        VersionRule[] windows10 = [new("allow", new VersionRuleOs("windows", null, "^10\\."), null)];

        Assert.IsTrue(LibraryFilter.IsAllowed(windows10, GamePlatform.WindowsX64));
        Assert.IsFalse(LibraryFilter.IsAllowed(windows10, GamePlatform.WindowsX64 with { OsVersion = "6.1" }));
    }

    [TestMethod]
    public void ABrokenOrCatastrophicVersionRegexCountsAsNotMatching()
    {
        VersionRule[] broken = [new("allow", new VersionRuleOs("windows", null, "("), null)];

        // 表达式来自远端清单：非法或回溯爆炸都按"不匹配"处理（宁可少装一个库，也不要卡住）。
        Assert.IsFalse(LibraryFilter.IsAllowed(broken, GamePlatform.WindowsX64));
        Assert.IsFalse(LibraryFilter.IsAllowed(
            [new("allow", new VersionRuleOs("windows", null, "(a+)+$"), null)],
            GamePlatform.WindowsX64 with { OsVersion = new string('a', 40) + "b" }));
    }

    // ==================== 安装计划 ====================

    [TestMethod]
    public void TheClientJarAndVersionJsonLandInTheVersionDirectory()
    {
        var plan = Plan(VanillaVersion());

        Assert.AreEqual(@"C:\Games\.minecraft\versions\1.20.4\1.20.4.json", plan.VersionJsonPath);
        Assert.AreEqual(@"C:\Games\.minecraft\versions\1.20.4\1.20.4.jar", plan.ClientJarPath);
        Assert.AreEqual(@"C:\Games\.minecraft\versions\1.20.4\natives", plan.NativesDirectory);

        var client = plan.Files.Single(f => f.Kind == Downloads.DownloadKind.ClientJar);
        Assert.AreEqual("https://piston-data.mojang.com/v1/objects/cccc/client.jar", client.Url);
        Assert.AreEqual(@"C:\Games\.minecraft\versions\1.20.4\1.20.4.jar", client.TargetPath);
    }

    [TestMethod]
    public void LibrariesAndTheAssetIndexAndTheLogConfigAreAllPlanned()
    {
        var plan = Plan(VanillaVersion());
        var kinds = plan.Files.Select(f => f.Kind).ToList();

        CollectionAssert.Contains(kinds, Downloads.DownloadKind.Library);
        CollectionAssert.Contains(kinds, Downloads.DownloadKind.AssetIndex);
        CollectionAssert.Contains(kinds, Downloads.DownloadKind.Logging);

        var authlib = plan.Files.Single(f => f.DisplayName == "authlib-6.0.54.jar");
        Assert.AreEqual(@"C:\Games\.minecraft\libraries\com\mojang\authlib\6.0.54\authlib-6.0.54.jar", authlib.TargetPath);

        var index = plan.Files.Single(f => f.Kind == Downloads.DownloadKind.AssetIndex);
        Assert.AreEqual(@"C:\Games\.minecraft\assets\indexes\12.json", index.TargetPath);
        Assert.AreEqual("12", plan.AssetIndexId);

        var logConfig = plan.Files.Single(f => f.Kind == Downloads.DownloadKind.Logging);
        Assert.AreEqual(@"C:\Games\.minecraft\assets\log_configs\client-1.12.xml", logConfig.TargetPath);
    }

    [TestMethod]
    public void LibrariesThatDoNotApplyToThisPlatformAreLeftOut()
    {
        var version = VanillaVersion() with
        {
            Libraries =
            [
                Library("com.example:windows-only:1.0", [new("allow", new VersionRuleOs("windows", null, null), null)]),
                Library("com.example:mac-only:1.0", [new("allow", new VersionRuleOs("osx", null, null), null)]),
            ],
        };

        var plan = Plan(version);

        Assert.IsTrue(plan.Files.Any(f => f.DisplayName == "windows-only-1.0.jar"));
        Assert.IsFalse(plan.Files.Any(f => f.DisplayName == "mac-only-1.0.jar"));
    }

    [TestMethod]
    public void ALegacyLibraryWithOnlyARepositoryBaseUrlIsPlannedWithoutAChecksum()
    {
        // 旧版 Forge（≤1.12.2）：没有 downloads，只有库对象自身上的仓库地址，也没有 SHA-1。
        var version = VanillaVersion() with
        {
            Libraries = [new Library { Name = "net.minecraftforge:forge:1.12.2-14.23.5.2860", Url = "https://maven.minecraftforge.net/" }],
        };

        var plan = Plan(version);
        var forge = plan.Files.Single(f => f.DisplayName.StartsWith("forge-", StringComparison.Ordinal));

        Assert.AreEqual(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.12.2-14.23.5.2860/forge-1.12.2-14.23.5.2860.jar",
            forge.Url);
        Assert.AreEqual(string.Empty, forge.Sha1);
        Assert.IsFalse(forge.IsVerifiable, "没有校验值就如实标成不可校验，而不是假装校验过。");
    }

    [TestMethod]
    public void NativesAreBothDownloadedAndQueuedForExtraction()
    {
        var plan = Plan(VanillaVersion());

        Assert.AreEqual(1, plan.Natives.Count);

        var extraction = plan.Natives[0];
        Assert.AreEqual(@"C:\Games\.minecraft\libraries\org\lwjgl\lwjgl\3.3.1\lwjgl-3.3.1-natives-windows.jar", extraction.ArchivePath);
        Assert.AreEqual(@"C:\Games\.minecraft\versions\1.20.4\natives", extraction.TargetDirectory);
        CollectionAssert.AreEqual(new[] { "META-INF/" }, extraction.Exclude.ToArray());

        // natives 归档本身也要下载，而且落在 libraries 下（与官方启动器一致）。
        Assert.IsTrue(plan.Files.Any(f => f.TargetPath == extraction.ArchivePath));
    }

    [TestMethod]
    public void ANativeForAnotherOperatingSystemIsNotDownloaded()
    {
        var version = VanillaVersion() with
        {
            Libraries =
            [
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.3.1",
                    Natives = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["osx"] = "natives-macos",
                    },
                    Downloads = new LibraryDownloads
                    {
                        Classifiers = new Dictionary<string, DownloadInfo>(StringComparer.Ordinal)
                        {
                            ["natives-macos"] = new() { Path = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-macos.jar", Url = "https://libraries.minecraft.net/macos.jar", Sha1 = "dddd", Size = 10 },
                        },
                    },
                },
            ],
        };

        var plan = Plan(version);

        Assert.AreEqual(0, plan.Natives.Count);
        Assert.IsFalse(plan.Files.Any(f => f.DisplayName.Contains("macos", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void APathThatEscapesTheGameDirectoryIsRejectedLoudly()
    {
        // 库路径来自远端清单。含 .. 时**抛异常而不是跳过**：静默跳过会装出一个"看起来装完了、
        // 其实少一半"的游戏，而带穿越路径的清单只能是被改过的。
        var version = VanillaVersion() with
        {
            Libraries =
            [
                new Library
                {
                    Name = "com.example:evil:1.0",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadInfo { Path = "../../evil.jar", Url = "https://libraries.minecraft.net/evil.jar" },
                    },
                },
            ],
        };

        Assert.ThrowsException<InvalidDataException>(() => Plan(version));
    }

    [TestMethod]
    public void AVersionsAssetObjectsAreExpandedAndDeduplicatedByHash()
    {
        var index = new AssetIndexContents
        {
            Objects = new Dictionary<string, AssetObjectInfo>(StringComparer.Ordinal)
            {
                ["minecraft/sounds/a.ogg"] = new("aa" + new string('0', 38), 100),
                ["minecraft/sounds/b.ogg"] = new("aa" + new string('0', 38), 100),
                ["minecraft/lang/zh_cn.json"] = new("bb" + new string('1', 38), 200),
            },
        };

        var items = InstallPlanner.PlanAssetObjects(index, GameDirectory, "1.20.4");

        Assert.AreEqual(2, items.Count, "两个资源名共用同一个哈希时只下一次。");
        Assert.AreEqual(300, Downloads.DiskSpaceChecker.TotalBytes(items));

        var first = items[0];
        StringAssert.StartsWith(first.Url, "https://resources.download.minecraft.net/aa/");
        Assert.AreEqual(@"C:\Games\.minecraft\assets\objects\aa\aa" + new string('0', 38), first.TargetPath);
    }

    [TestMethod]
    public void OldVersionsGetTheirVirtualAndResourcesLayoutsPlanned()
    {
        var index = new AssetIndexContents
        {
            Virtual = true,
            MapToResources = true,
            Objects = new Dictionary<string, AssetObjectInfo>(StringComparer.Ordinal)
            {
                ["minecraft/sounds/a.ogg"] = new("aa" + new string('0', 38), 100),
            },
        };

        var links = InstallPlanner.PlanAssetLayout(index, GameDirectory, "legacy");

        Assert.AreEqual(2, links.Count);
        Assert.AreEqual(@"C:\Games\.minecraft\assets\virtual\legacy\minecraft\sounds\a.ogg", links[0].TargetPath);
        Assert.AreEqual(@"C:\Games\.minecraft\resources\minecraft\sounds\a.ogg", links[1].TargetPath);
        Assert.IsTrue(links.All(l => l.SourcePath.EndsWith("aa" + new string('0', 38), StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ModernVersionsNeedNoExtraLayoutCopies()
    {
        var index = new AssetIndexContents
        {
            Objects = new Dictionary<string, AssetObjectInfo>(StringComparer.Ordinal)
            {
                ["minecraft/sounds/a.ogg"] = new("aa" + new string('0', 38), 100),
            },
        };

        Assert.AreEqual(0, InstallPlanner.PlanAssetLayout(index, GameDirectory, "12").Count);
    }

    [TestMethod]
    public void ThePlanTotalIsTheSumOfEverythingToDownload()
    {
        var plan = Plan(VanillaVersion());

        Assert.AreEqual(Downloads.DiskSpaceChecker.TotalBytes(plan.Files), plan.TotalBytes);
        Assert.IsTrue(plan.TotalBytes > 0);
    }

    // ==================== 辅助 ====================

    private static InstallPlan Plan(VersionJson json) =>
        InstallPlanner.Plan(
            new ResolvedVersion(json, json.Id ?? "1.20.4", [json.Id ?? "1.20.4"]),
            GameDirectory,
            GamePlatform.WindowsX64);

    /// <summary>造一个带主构件的库（只有名字与规则不同）。</summary>
    private static Library Library(string name, IReadOnlyList<VersionRule> rules)
    {
        var parts = name.Split(':');
        var fileName = parts[1] + "-" + parts[2] + ".jar";
        var path = parts[0].Replace('.', '/') + "/" + parts[1] + "/" + parts[2] + "/" + fileName;

        return new Library
        {
            Name = name,
            Rules = rules,
            Downloads = new LibraryDownloads
            {
                Artifact = new DownloadInfo
                {
                    Path = path,
                    Url = "https://libraries.minecraft.net/" + path,
                    Sha1 = "aabb",
                    Size = 1024,
                },
            },
        };
    }

    /// <summary>一份贴近真实的 1.20.4 版本 JSON（裁剪到安装需要的字段）。</summary>
    private static VersionJson VanillaVersion() => new()
    {
        Id = "1.20.4",
        Assets = "12",
        AssetIndex = new AssetIndexInfo
        {
            Id = "12",
            Url = "https://piston-meta.mojang.com/v1/packages/dddd/12.json",
            Sha1 = "dddd",
            Size = 4096,
        },
        Downloads = new VersionDownloads
        {
            Client = new DownloadInfo
            {
                Url = "https://piston-data.mojang.com/v1/objects/cccc/client.jar",
                Sha1 = "cccc",
                Size = 25_000_000,
            },
        },
        Logging = new VersionLogging
        {
            Client = new VersionLoggingClient
            {
                Argument = "-Dlog4j.configurationFile=${path}",
                Type = "log4j2-xml",
                File = new DownloadInfo
                {
                    Id = "client-1.12.xml",
                    Url = "https://piston-data.mojang.com/v1/objects/eeee/client-1.12.xml",
                    Sha1 = "eeee",
                    Size = 1024,
                },
            },
        },
        Libraries =
        [
            new Library
            {
                Name = "com.mojang:authlib:6.0.54",
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadInfo
                    {
                        Path = "com/mojang/authlib/6.0.54/authlib-6.0.54.jar",
                        Url = "https://libraries.minecraft.net/com/mojang/authlib/6.0.54/authlib-6.0.54.jar",
                        Sha1 = "aaaa",
                        Size = 700_000,
                    },
                },
            },
            new Library
            {
                Name = "org.lwjgl:lwjgl:3.3.1",
                Natives = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["windows"] = "natives-windows",
                },
                Extract = new LibraryExtract { Exclude = ["META-INF/"] },
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadInfo
                    {
                        Path = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar",
                        Url = "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar",
                        Sha1 = "bbbb",
                        Size = 900_000,
                    },
                    Classifiers = new Dictionary<string, DownloadInfo>(StringComparer.Ordinal)
                    {
                        ["natives-windows"] = new()
                        {
                            Path = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
                            Url = "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
                            Sha1 = "ffff",
                            Size = 120_000,
                        },
                    },
                },
            },
        ],
    };
}
