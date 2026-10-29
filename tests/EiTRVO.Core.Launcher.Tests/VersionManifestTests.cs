using System.Net;
using System.Text;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M2 / B-1…B-3：版本清单获取、版本 JSON 解析与 <c>inheritsFrom</c> 继承链合并。
/// <para>
/// 合并那一组用例由 Neo 的 <c>ModLoaderServiceTests</c> 迁移而来（5 条），
/// 另外补上 Neo 那版**测不到**的行为：库的顺序（决定 classpath 优先级）、
/// 参数的顺序（父在前）、多层链（Neo 只合并一层）、成环与缺父版本、
/// 以及"子版本自己声明了 assets/downloads 时该听谁的"。
/// </para>
/// </summary>
[TestClass]
public sealed class VersionManifestTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8));

    /// <summary>一份贴近真实的清单片段：一个正式版、一个快照、一个老版本（不该出现在界面上）。</summary>
    private const string ManifestJson = """
        {
          "latest": { "release": "1.20.4", "snapshot": "24w03a" },
          "versions": [
            {
              "id": "24w03a",
              "type": "snapshot",
              "url": "https://piston-meta.mojang.com/v1/packages/aaaa/24w03a.json",
              "time": "2024-01-17T15:04:29+00:00",
              "releaseTime": "2024-01-17T15:00:00+00:00",
              "sha1": "aaaa",
              "complianceLevel": 1
            },
            {
              "id": "1.20.4",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/bbbb/1.20.4.json",
              "time": "2023-12-07T12:00:00+00:00",
              "releaseTime": "2023-12-07T11:00:00+00:00",
              "sha1": "bbbb",
              "complianceLevel": 1
            },
            {
              "id": "a1.2.6",
              "type": "old_alpha",
              "url": "https://piston-meta.mojang.com/v1/packages/cccc/a1.2.6.json",
              "time": "2013-09-20T12:00:00+00:00",
              "releaseTime": "2010-11-30T12:00:00+00:00",
              "sha1": "cccc",
              "complianceLevel": 0
            }
          ]
        }
        """;

    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-versions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(Now);
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private VersionManifestClient NewClient(StubHandler handler) =>
        new(new FakeHttpClientFactory(handler), _paths, _clock, _diagnostics);

    // ------------------------------------------------------- 清单解析 ---

    [TestMethod]
    public void TheManifestIsParsedIntoRowsWithTheirTimestamps()
    {
        var manifest = LauncherJson.Deserialize<VersionManifest>(ManifestJson, LauncherJson.Indented)!;

        Assert.AreEqual("1.20.4", manifest.Latest?.Release);
        Assert.AreEqual("24w03a", manifest.Latest?.Snapshot);
        Assert.AreEqual(3, manifest.Versions.Count);

        var release = manifest.Versions.Single(v => v.Id == "1.20.4");
        Assert.AreEqual("release", release.Type);
        Assert.AreEqual("https://piston-meta.mojang.com/v1/packages/bbbb/1.20.4.json", release.Url);
        Assert.AreEqual("bbbb", release.Sha1);
        Assert.AreEqual(new DateTimeOffset(2023, 12, 7, 11, 0, 0, TimeSpan.Zero), release.ReleaseTime);
    }

    [TestMethod]
    [DataRow("release", VersionKind.Release)]
    [DataRow("snapshot", VersionKind.Snapshot)]
    public void TypesMapToTheTwoKindsTheDesignShows(string type, VersionKind expected) =>
        Assert.AreEqual(expected, VersionTypes.ToKind(type));

    [TestMethod]
    public void OldAlphaAndBetaVersionsAreNotShownInTheList()
    {
        // 设计稿的「共 917 个」正好是 305 正式版 + 612 快照，老版本不在界面范围内。
        Assert.IsFalse(VersionTypes.IsDisplayed("old_alpha"));
        Assert.IsFalse(VersionTypes.IsDisplayed("old_beta"));
        Assert.IsTrue(VersionTypes.IsDisplayed("release"));
        Assert.IsTrue(VersionTypes.IsDisplayed("snapshot"));
    }

    // ------------------------------------------------- 清单获取与缓存 ---

    [TestMethod]
    public async Task FetchingHitsThePistonMetaEndpointOnceThroughTheNamedClient()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ManifestJson);
        var factory = new FakeHttpClientFactory(handler);
        var client = new VersionManifestClient(factory, _paths, _clock, _diagnostics);

        var manifest = await client.FetchAsync();

        HttpAssert.RequestCount(handler, 1);
        HttpAssert.Url(handler, 0, "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        HttpAssert.Method(handler, 0, HttpMethod.Get);
        Assert.IsTrue(factory.RequestedNames.Contains(HttpClientNames.Core), "必须走命名客户端（白名单与重试都挂在它上面）。");

        Assert.AreEqual(3, manifest.Versions.Count);
    }

    [TestMethod]
    public async Task TheResponseIsCachedVerbatim()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, ManifestJson);
        var client = NewClient(handler);

        await client.FetchAsync();

        Assert.IsTrue(client.HasCache);
        Assert.AreEqual(ManifestJson, File.ReadAllText(client.CachePath, Encoding.UTF8), "缓存必须是远端原文。");
        Assert.IsNotNull(client.CacheWrittenAt);
    }

    [TestMethod]
    public async Task FetchingIsRecordedWithTheVersionCount()
    {
        var client = NewClient(new StubHandler().Respond(HttpStatusCode.OK, ManifestJson));

        await client.FetchAsync();

        var entry = _diagnostics.Entries.Single(e => e.MessageKey == "已更新版本清单（{0} 个版本，最新正式版 {1}，{2}）");
        Assert.AreEqual("3", entry.Parameters[0]);
        Assert.AreEqual("1.20.4", entry.Parameters[1]);
    }

    [TestMethod]
    public async Task AnErrorStatusKeepsThePreviousCache()
    {
        var client = NewClient(new StubHandler().Respond(HttpStatusCode.OK, ManifestJson));
        await client.FetchAsync();
        var good = File.ReadAllText(client.CachePath, Encoding.UTF8);

        // 第二次获取遇到限流。
        var failing = new VersionManifestClient(
            new FakeHttpClientFactory(new StubHandler().Respond(HttpStatusCode.TooManyRequests, "slow down")),
            _paths,
            _clock,
            _diagnostics);

        await Assert.ThrowsExceptionAsync<VersionDataException>(() => failing.FetchAsync());

        Assert.AreEqual(good, File.ReadAllText(client.CachePath, Encoding.UTF8), "失败不该动缓存。");
        Assert.AreEqual(LauncherEventLevel.Warning, _diagnostics.Entries[^1].Level);
    }

    [TestMethod]
    public async Task AnEmptyManifestIsRejectedAndDoesNotOverwriteTheCache()
    {
        var good = NewClient(new StubHandler().Respond(HttpStatusCode.OK, ManifestJson));
        await good.FetchAsync();

        var empty = NewClient(new StubHandler().Respond(HttpStatusCode.OK, """{"latest":{},"versions":[]}"""));

        var exception = await Assert.ThrowsExceptionAsync<VersionDataException>(() => empty.FetchAsync());

        StringAssert.Contains(exception.Message, "保留");
        Assert.AreEqual(ManifestJson, File.ReadAllText(good.CachePath, Encoding.UTF8));
    }

    [TestMethod]
    public async Task AManifestWithoutTheVersionsFieldIsRejected()
    {
        var client = NewClient(new StubHandler().Respond(HttpStatusCode.OK, """{"latest":{"release":"1.20.4"}}"""));

        await Assert.ThrowsExceptionAsync<VersionDataException>(() => client.FetchAsync());
    }

    [TestMethod]
    public async Task ABodyThatIsNotJsonIsRejected()
    {
        var client = NewClient(new StubHandler().Respond(HttpStatusCode.OK, "<html>nope</html>"));

        await Assert.ThrowsExceptionAsync<VersionDataException>(() => client.FetchAsync());
    }

    // --------------------------------------------------------- 离线读 ---

    [TestMethod]
    public async Task TheCacheIsReadableWithoutAnyNetwork()
    {
        var client = NewClient(new StubHandler().Respond(HttpStatusCode.OK, ManifestJson));
        await client.FetchAsync();

        // 换一个客户端实例、不准备任何响应：只读缓存，不该发请求。
        var offline = new VersionManifestClient(
            new FakeHttpClientFactory(new StubHandler()),
            _paths,
            _clock,
            _diagnostics);

        var manifest = offline.TryReadCache();

        Assert.IsNotNull(manifest);
        Assert.AreEqual(3, manifest.Versions.Count);
    }

    [TestMethod]
    public void ReadingWithoutACacheReturnsNull()
    {
        Assert.IsNull(NewClient(new StubHandler()).TryReadCache());
    }

    [TestMethod]
    public void ACorruptCacheIsTreatedAsMissingAndKeptOnDisk()
    {
        var client = NewClient(new StubHandler());
        AtomicFile.WriteAllText(client.CachePath, "{\"versions\": [");

        Assert.IsNull(client.TryReadCache());
        Assert.AreEqual("{\"versions\": [", File.ReadAllText(client.CachePath, Encoding.UTF8));
        Assert.AreEqual(LauncherEventLevel.Warning, _diagnostics.Entries.Single().Level);

        // 同一个坏文件只报一次。
        client.TryReadCache();
        Assert.AreEqual(1, _diagnostics.Entries.Count);
    }

    // ------------------------------------------------- 参数结构（1.13+）---

    [TestMethod]
    public void APlainArgumentIsASingleValueWithoutRules()
    {
        var argument = ReadArgument("\"-XX:+UnlockExperimentalVMOptions\"");

        CollectionAssert.AreEqual(new[] { "-XX:+UnlockExperimentalVMOptions" }, (System.Collections.ICollection)argument.Values);
        Assert.IsFalse(argument.IsConditional);
    }

    [TestMethod]
    public void AConditionalArgumentCarriesItsRulesAndValueArray()
    {
        var argument = ReadArgument(
            """
            {"rules":[{"action":"allow","os":{"name":"osx"}}],"value":["-XstartOnFirstThread"]}
            """);

        Assert.IsTrue(argument.IsConditional);
        CollectionAssert.AreEqual(new[] { "-XstartOnFirstThread" }, (System.Collections.ICollection)argument.Values);
        Assert.AreEqual("allow", argument.Rules![0].Action);
        Assert.IsTrue(argument.Rules[0].IsAllow);
        Assert.AreEqual("osx", argument.Rules[0].Os?.Name);
    }

    [TestMethod]
    public void AFeaturesRuleIsPreserved()
    {
        var argument = ReadArgument(
            """
            {"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}"]}
            """);

        Assert.IsTrue(argument.Rules![0].Features!["has_custom_resolution"]);
        CollectionAssert.AreEqual(
            new[] { "--width", "${resolution_width}" },
            (System.Collections.ICollection)argument.Values);
    }

    [TestMethod]
    public void UnknownFieldsInsideAnArgumentAreIgnored()
    {
        var argument = ReadArgument("""{"value":"-Dfoo=1","futureField":{"nested":true}}""");

        CollectionAssert.AreEqual(new[] { "-Dfoo=1" }, (System.Collections.ICollection)argument.Values);
        Assert.IsNull(argument.Rules);
    }

    [TestMethod]
    public void ArgumentsAreReadThroughTheVersionJsonModel()
    {
        var version = ReadVersion(
            """
            {
              "id": "1.20.4",
              "mainClass": "net.minecraft.client.main.Main",
              "arguments": {
                "game": ["--username", "${auth_player_name}", {"rules":[{"action":"allow","os":{"name":"windows"}}],"value":["--demo"]}],
                "jvm": [{"rules":[{"action":"allow","os":{"arch":"x86"}}],"value":"-Xss1M"}]
              }
            }
            """);

        Assert.AreEqual("net.minecraft.client.main.Main", version.MainClass);
        Assert.AreEqual(3, version.Arguments!.Game!.Count);
        Assert.AreEqual("--demo", version.Arguments.Game[2].Values[0]);
        Assert.AreEqual("windows", version.Arguments.Game[2].Rules![0].Os?.Name);
        Assert.IsTrue(version.Arguments.Jvm![0].IsConditional);
    }

    [TestMethod]
    public void ALibraryKeepsItsNativesRulesAndLegacyUrl()
    {
        var version = ReadVersion(
            """
            {
              "id": "1.20.4",
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:3.3.1",
                  "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar", "sha1": "abc", "size": 10, "url": "https://libraries.minecraft.net/x.jar" } },
                  "natives": { "windows": "natives-windows" },
                  "rules": [{ "action": "allow" }, { "action": "disallow", "os": { "name": "osx" } }]
                },
                { "name": "net.minecraftforge:forge:1.12.2", "url": "https://maven.minecraftforge.net/" }
              ]
            }
            """);

        var lwjgl = version.Libraries![0];
        Assert.AreEqual("org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar", lwjgl.Downloads?.Artifact?.Path);
        Assert.AreEqual("natives-windows", lwjgl.Natives!["windows"]);
        Assert.AreEqual(2, lwjgl.Rules!.Count);
        Assert.AreEqual("https://maven.minecraftforge.net/", version.Libraries[1].Url);
    }

    [TestMethod]
    public void TheDeclaredJavaVersionIsKept()
    {
        var version = ReadVersion("""{"id":"1.20.4","javaVersion":{"component":"java-runtime-gamma","majorVersion":17}}""");

        Assert.AreEqual(17, version.JavaVersion?.MajorVersion);
        Assert.AreEqual("java-runtime-gamma", version.JavaVersion?.Component);
    }

    // ------------------------------------------------- 继承链合并 ---

    [TestMethod]
    public void MergeReplacesTheMainClass()
    {
        var merged = VersionInheritanceResolver.Merge(
            new VersionJson { Id = "forge", MainClass = "net.minecraft.launchwrapper.Launch" },
            new VersionJson { Id = "vanilla", MainClass = "net.minecraft.client.main.Main" });

        Assert.AreEqual("net.minecraft.launchwrapper.Launch", merged.MainClass);
    }

    [TestMethod]
    public void MergeAppendsLibraries()
    {
        var merged = VersionInheritanceResolver.Merge(
            new VersionJson { Id = "forge", Libraries = [new Library { Name = "net.minecraftforge:forge:1.8.9" }] },
            new VersionJson { Id = "1.8.9", Libraries = [new Library { Name = "com.mojang:authlib:1.5.21" }] });

        Assert.AreEqual(2, merged.Libraries!.Count);
        Assert.IsTrue(merged.Libraries.Any(l => l.Name == "net.minecraftforge:forge:1.8.9"));
        Assert.IsTrue(merged.Libraries.Any(l => l.Name == "com.mojang:authlib:1.5.21"));
    }

    [TestMethod]
    public void MergeDeduplicatesLibrariesKeepingTheChildVersion()
    {
        var merged = VersionInheritanceResolver.Merge(
            new VersionJson { Id = "forge", Libraries = [new Library { Name = "com.mojang:authlib:1.5.21", Url = "child" }] },
            new VersionJson { Id = "1.8.9", Libraries = [new Library { Name = "com.mojang:authlib:1.5.21", Url = "parent" }] });

        Assert.AreEqual(1, merged.Libraries!.Count);
        Assert.AreEqual("child", merged.Libraries[0].Url);
    }

    [TestMethod]
    public void OldStyleDuplicateLibrariesKeepTheirNativesWhenMerged()
    {
        // 1.13–1.14 的官方清单把同一个 Maven 坐标写两遍：一条只有主构件，另一条带 natives 分类器。
        // 第一版按名字去重只留了前一条，加载器安装出来的版本 JSON 因此一个 natives 都没有 ——
        // 1.14.4 + Fabric 启动时报 `UnsatisfiedLinkError: Failed to locate library: lwjgl.dll`。
        var plain = new Library
        {
            Name = "org.lwjgl:lwjgl:3.2.2",
            Downloads = new LibraryDownloads
            {
                Artifact = new DownloadInfo
                {
                    Path = "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar",
                    Sha1 = "artifact-sha1",
                    Size = 321900,
                    Url = "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar",
                },
            },
            Rules = [new("allow", null, null)],
        };

        var withNatives = new Library
        {
            Name = "org.lwjgl:lwjgl:3.2.2",
            Downloads = new LibraryDownloads
            {
                Artifact = plain.Downloads.Artifact,
                Classifiers = new Dictionary<string, DownloadInfo>
                {
                    ["natives-windows"] = new()
                    {
                        Path = "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2-natives-windows.jar",
                        Sha1 = "natives-sha1",
                        Size = 279593,
                        Url = "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2-natives-windows.jar",
                    },
                },
            },
            Natives = new Dictionary<string, string> { ["windows"] = "natives-windows" },
            Rules = [new("allow", null, null)],
        };

        var merged = VersionInheritanceResolver.MergeLibraries([plain, withNatives], null);

        Assert.AreEqual(1, merged.Count, "同一个 Maven 坐标仍然只占一条（顺序即 classpath 优先级，不能重复）。");
        Assert.AreEqual("artifact-sha1", merged[0].Downloads!.Artifact!.Sha1, "主构件以先出现的为准。");
        Assert.AreEqual("natives-windows", merged[0].Natives!["windows"], "natives 映射必须补上。");
        Assert.AreEqual(
            "natives-sha1",
            merged[0].Downloads!.Classifiers!["natives-windows"].Sha1,
            "natives 分类器必须一起补上，否则安装器既不下它也不解压它。");
    }

    [TestMethod]
    public void ResolvingALoaderAgainstAnOldStyleParentKeepsTheParentNatives()
    {
        var oldStyleNatives = new Library
        {
            Name = "org.lwjgl:lwjgl:3.2.2",
            Downloads = new LibraryDownloads
            {
                Artifact = new DownloadInfo { Url = "https://libraries.minecraft.net/lwjgl-3.2.2.jar", Sha1 = "artifact-sha1" },
                Classifiers = new Dictionary<string, DownloadInfo>
                {
                    ["natives-windows"] = new()
                    {
                        Url = "https://libraries.minecraft.net/lwjgl-3.2.2-natives-windows.jar",
                        Sha1 = "natives-sha1",
                    },
                },
            },
            Natives = new Dictionary<string, string> { ["windows"] = "natives-windows" },
            Rules = [new("allow", null, null)],
        };

        var versions = new Dictionary<string, VersionJson>(StringComparer.Ordinal)
        {
            ["fabric-loader-1"] = new() { Id = "fabric-loader-1", InheritsFrom = "1.14.4" },
            ["1.14.4"] = new()
            {
                Id = "1.14.4",
                MainClass = "net.minecraft.client.main.Main",
                Libraries = [new Library { Name = "org.lwjgl:lwjgl:3.2.2" }, oldStyleNatives],
            },
        };

        var resolved = VersionInheritanceResolver.Resolve(
            versions["fabric-loader-1"],
            id => versions.GetValueOrDefault(id));

        var lwjgl = resolved.Merged.Libraries!.Single(library => library.Name == "org.lwjgl:lwjgl:3.2.2");
        Assert.AreEqual("natives-windows", lwjgl.Natives!["windows"]);
        Assert.AreEqual("natives-sha1", lwjgl.Downloads!.Classifiers!["natives-windows"].Sha1);
    }

    [TestMethod]
    public void ChildLibrariesComeFirstBecauseOrderIsClasspathPriority()
    {
        var merged = VersionInheritanceResolver.Merge(
            new VersionJson { Id = "child", Libraries = [new Library { Name = "child:lib:1" }] },
            new VersionJson { Id = "parent", Libraries = [new Library { Name = "parent:lib:1" }] });

        CollectionAssert.AreEqual(
            new[] { "child:lib:1", "parent:lib:1" },
            merged.Libraries!.Select(l => l.Name!).ToArray());
    }

    [TestMethod]
    public void ParentArgumentsComeBeforeChildArguments()
    {
        var merged = VersionInheritanceResolver.Merge(
            new VersionJson
            {
                Id = "child",
                Arguments = new VersionArguments { Game = [VersionArgument.Plain("--child")] },
            },
            new VersionJson
            {
                Id = "parent",
                Arguments = new VersionArguments { Game = [VersionArgument.Plain("--parent")] },
            });

        CollectionAssert.AreEqual(
            new[] { "--parent", "--child" },
            merged.Arguments!.Game!.Select(a => a.Values[0]).ToArray());
    }

    [TestMethod]
    public void MergeClearsInheritsFromAndKeepsTheChildId()
    {
        var merged = VersionInheritanceResolver.Merge(
            new VersionJson { Id = "forge", InheritsFrom = "1.8.9" },
            new VersionJson { Id = "1.8.9" });

        Assert.AreEqual("forge", merged.Id);
        Assert.IsNull(merged.InheritsFrom);
    }

    [TestMethod]
    public void LegacyMinecraftArgumentsAreInheritedWhenTheChildHasNone()
    {
        var merged = VersionInheritanceResolver.Merge(
            new VersionJson { Id = "forge" },
            new VersionJson { Id = "1.8.9", MinecraftArguments = "--username ${auth_player_name}" });

        Assert.AreEqual("--username ${auth_player_name}", merged.MinecraftArguments);
    }

    [TestMethod]
    public void ScalarFieldsAreInheritedOnlyWhenTheChildIsMissingThem()
    {
        var parent = new VersionJson
        {
            Id = "1.20.4",
            Assets = "5",
            AssetIndex = new AssetIndexInfo { Id = "5", Url = "https://piston-meta.mojang.com/5.json" },
            Downloads = new VersionDownloads { Client = new DownloadInfo { Url = "https://piston-data.mojang.com/client.jar" } },
            JavaVersion = new JavaVersionRequirement { MajorVersion = 17 },
            Logging = new VersionLogging { Client = new VersionLoggingClient { Argument = "-Dlog4j.configurationFile=${path}", File = new DownloadInfo { Url = "https://piston-data.mojang.com/log4j.xml" } } },
        };

        // 子版本什么都没声明 → 全部继承。
        var inherited = VersionInheritanceResolver.Merge(new VersionJson { Id = "forge" }, parent);
        Assert.AreEqual("5", inherited.Assets);
        Assert.AreEqual("https://piston-meta.mojang.com/5.json", inherited.AssetIndex?.Url);
        Assert.AreEqual("https://piston-data.mojang.com/client.jar", inherited.Downloads?.Client?.Url);
        Assert.AreEqual(17, inherited.JavaVersion?.MajorVersion);
        Assert.IsNotNull(inherited.Logging);

        // 子版本自己声明了 → 听子的（这一步 Neo 会强制用父版本的，见类型注释里的差异说明）。
        var overridden = VersionInheritanceResolver.Merge(
            new VersionJson
            {
                Id = "modpack",
                Assets = "custom",
                AssetIndex = new AssetIndexInfo { Id = "custom", Url = "https://piston-meta.mojang.com/custom.json" },
                Downloads = new VersionDownloads { Client = new DownloadInfo { Url = "https://piston-data.mojang.com/custom.jar" } },
                JavaVersion = new JavaVersionRequirement { MajorVersion = 21 },
            },
            parent);

        Assert.AreEqual("custom", overridden.Assets);
        Assert.AreEqual("https://piston-meta.mojang.com/custom.json", overridden.AssetIndex?.Url);
        Assert.AreEqual("https://piston-data.mojang.com/custom.jar", overridden.Downloads?.Client?.Url);
        Assert.AreEqual(21, overridden.JavaVersion?.MajorVersion);
    }

    [TestMethod]
    public void AVanillaVersionResolvesToItself()
    {
        var resolved = VersionInheritanceResolver.Resolve(
            new VersionJson { Id = "1.20.4", MainClass = "net.minecraft.client.main.Main" },
            _ => null);

        Assert.AreEqual("1.20.4", resolved.BaseVersionId);
        CollectionAssert.AreEqual(new[] { "1.20.4" }, resolved.Chain.ToArray());
        Assert.AreEqual("net.minecraft.client.main.Main", resolved.Merged.MainClass);
    }

    [TestMethod]
    public void TheWholeChainIsMergedNotJustOneLevel()
    {
        // Quilt → Fabric → 原版 这种链在真实环境里存在；Neo 只合并一层，链上剩下的部分就丢了。
        var versions = new Dictionary<string, VersionJson>(StringComparer.Ordinal)
        {
            ["quilt"] = new()
            {
                Id = "quilt",
                InheritsFrom = "fabric",
                Libraries = [new Library { Name = "quilt:1" }],
                Arguments = new VersionArguments { Game = [VersionArgument.Plain("--quilt")] },
            },
            ["fabric"] = new()
            {
                Id = "fabric",
                InheritsFrom = "1.20.4",
                Libraries = [new Library { Name = "fabric:1" }],
                Arguments = new VersionArguments { Game = [VersionArgument.Plain("--fabric")] },
            },
            ["1.20.4"] = new()
            {
                Id = "1.20.4",
                MainClass = "net.minecraft.client.main.Main",
                Libraries = [new Library { Name = "vanilla:1" }],
                Arguments = new VersionArguments { Game = [VersionArgument.Plain("--vanilla")] },
                Downloads = new VersionDownloads { Client = new DownloadInfo { Url = "https://piston-data.mojang.com/client.jar" } },
            },
        };

        var resolved = VersionInheritanceResolver.Resolve(versions["quilt"], id => versions.GetValueOrDefault(id));

        CollectionAssert.AreEqual(new[] { "quilt", "fabric", "1.20.4" }, resolved.Chain.ToArray());
        Assert.AreEqual("1.20.4", resolved.BaseVersionId);
        CollectionAssert.AreEqual(
            new[] { "quilt:1", "fabric:1", "vanilla:1" },
            resolved.Merged.Libraries!.Select(l => l.Name!).ToArray());
        CollectionAssert.AreEqual(
            new[] { "--vanilla", "--fabric", "--quilt" },
            resolved.Merged.Arguments!.Game!.Select(a => a.Values[0]).ToArray());
        Assert.AreEqual("https://piston-data.mojang.com/client.jar", resolved.Merged.Downloads?.Client?.Url);
        Assert.IsNull(resolved.Merged.InheritsFrom);
    }

    [TestMethod]
    public void AMissingParentIsReportedWithTheChainSoFar()
    {
        var exception = Assert.ThrowsException<VersionResolutionException>(() =>
            VersionInheritanceResolver.Resolve(
                new VersionJson { Id = "forge", InheritsFrom = "1.20.4" },
                _ => null));

        Assert.AreEqual("1.20.4", exception.MissingVersionId);
        CollectionAssert.AreEqual(new[] { "forge" }, exception.Chain.ToArray());
        Assert.AreEqual("缺少父版本「{0}」：{1} 声明了 inheritsFrom，但本地没有它的版本 JSON。", exception.Reason.Key);
        CollectionAssert.AreEqual(new[] { "1.20.4", "forge" }, exception.Reason.Parameters.ToArray());
    }

    [TestMethod]
    public void ACycleIsReportedInsteadOfLoopingForever()
    {
        var versions = new Dictionary<string, VersionJson>(StringComparer.Ordinal)
        {
            ["a"] = new() { Id = "a", InheritsFrom = "b" },
            ["b"] = new() { Id = "b", InheritsFrom = "a" },
        };

        var exception = Assert.ThrowsException<VersionResolutionException>(() =>
            VersionInheritanceResolver.Resolve(versions["a"], id => versions.GetValueOrDefault(id)));

        Assert.AreEqual("inheritsFrom 成环：{0} 已经出现过（链：{1} → {2}）。", exception.Reason.Key);
        CollectionAssert.AreEqual(new[] { "a", "a → b", "a" }, exception.Reason.Parameters.ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, exception.Chain.ToArray());
    }

    [TestMethod]
    public void AnOverlyDeepChainIsRejectedInsteadOfBeingFollowed()
    {
        // 100 层的链：真实数据里不存在，跟下去只会把界面拖死。
        var versions = new Dictionary<string, VersionJson>(StringComparer.Ordinal);
        for (var i = 0; i < 20; i++)
        {
            versions[$"v{i}"] = new VersionJson { Id = $"v{i}", InheritsFrom = $"v{i + 1}" };
        }

        versions["v20"] = new VersionJson { Id = "v20" };

        var exception = Assert.ThrowsException<VersionResolutionException>(() =>
            VersionInheritanceResolver.Resolve(versions["v0"], id => versions.GetValueOrDefault(id)));

        Assert.AreEqual("inheritsFrom 链超过 {0} 层，已停止解析（链：{1}）。", exception.Reason.Key);
        Assert.AreEqual(
            VersionInheritanceResolver.MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exception.Reason.Parameters[0]);
    }

    /// <summary>从一段 JSON 里读出一个参数项。</summary>
    private static VersionArgument ReadArgument(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<VersionArgument>(json, LauncherJson.Compact)!;

    private static VersionJson ReadVersion(string json) =>
        LauncherJson.Deserialize<VersionJson>(json, LauncherJson.Indented)!;
}
