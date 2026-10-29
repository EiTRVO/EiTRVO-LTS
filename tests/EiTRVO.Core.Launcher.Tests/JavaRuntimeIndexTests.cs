using System.Globalization;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// §12.7 的**纯函数**部分：官方运行时索引的解析与"该取哪一代"的选择规则。
/// <para>
/// 这一层刻意先做成纯函数再谈下载：挑选规则错的表现不是崩溃，而是"给 1.12.2 装了 Java 21
/// （那版封在 Java 8）"或"永远只装最新一代"，两种都只有在真机上启动时才暴露。
/// 索引 JSON 的形状取自实测的官方响应（平台 → 组件 → 可用版本数组）。
/// </para>
/// </summary>
[TestClass]
public sealed class JavaRuntimeIndexTests
{
    private const string Gamma = "java-runtime-gamma";
    private const string Beta = "java-runtime-beta";
    private const string Delta = "java-runtime-delta";
    private const string Epsilon = "java-runtime-epsilon";
    private const string Legacy = "jre-legacy";

    private static readonly DateTimeOffset Older = new(2025, 5, 19, 8, 11, 57, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2025, 5, 19, 8, 30, 12, TimeSpan.Zero);

    [TestMethod]
    public void ParseFlattensThePlatformComponentReleaseShape()
    {
        var release = JavaRuntimeFixture.Release(Gamma, "17.0.15");

        var index = JavaRuntimeIndex.Parse(JavaRuntimeFixture.IndexJson([release]));

        var candidate = index.Candidates.Single();

        Assert.AreEqual(JavaRuntimeFixture.Platform, candidate.Platform);
        Assert.AreEqual(Gamma, candidate.Component);
        Assert.AreEqual("17.0.15", candidate.VersionName);
        Assert.AreEqual(17, candidate.MajorVersion);
        Assert.AreEqual(release.ManifestSha1, candidate.ManifestSha1);
        Assert.AreEqual(release.ManifestSize, candidate.ManifestSize);
        Assert.AreEqual(release.ManifestUrl, candidate.ManifestUrl);
    }

    [TestMethod]
    public void MajorVersionComesFromTheLeadingDigitsOfTheVersionName()
    {
        // 官方清单里没有"这个组件是 Java 几"，只有版本名；四种真实写法都要能读出来。
        Assert.AreEqual(17, Candidate(Gamma, "17.0.15").MajorVersion);
        Assert.AreEqual(21, Candidate(Delta, "21.0.7").MajorVersion);
        Assert.AreEqual(25, Candidate(Epsilon, "25.0.1").MajorVersion);
        Assert.AreEqual(8, Candidate(Legacy, "8u51-cacert462b08").MajorVersion);

        // 读不出来的版本名是"未知"（0），不是 Java 0。
        Assert.AreEqual(0, Candidate(Gamma, string.Empty).MajorVersion);
    }

    [TestMethod]
    public void DeclaredComponentWins()
    {
        var index = RealIndex();

        var selected = index.Select(JavaRuntimeFixture.Platform, requiredMajor: 17, componentHint: Beta);

        // 声明的是 beta（虽然 gamma 更新），声明的优先级更高 —— 那是 Mojang 自己写的答案。
        Assert.AreEqual(Beta, selected!.Component, "版本 JSON 声明的组件优先。");
    }

    [TestMethod]
    public void AStaleDeclaredComponentDoesNotBeatTheRequiredMajor()
    {
        var index = RealIndex();

        // 游戏已经升到要 Java 21，版本 JSON 里的声明还写着 gamma（17）：照抄会下一个仍然跑不起来的运行时。
        var selected = index.Select(JavaRuntimeFixture.Platform, requiredMajor: 21, componentHint: Gamma);

        Assert.AreEqual(Delta, selected!.Component, "声明值低于所需主版本时必须让位。");
        Assert.AreEqual(21, selected.MajorVersion);
    }

    [TestMethod]
    public void ExactMajorWinsOverANewerOne()
    {
        var index = RealIndex();

        var for17 = index.Select(JavaRuntimeFixture.Platform, requiredMajor: 17, componentHint: null);
        var for8 = index.Select(JavaRuntimeFixture.Platform, requiredMajor: 8, componentHint: null);

        Assert.AreEqual(17, for17!.MajorVersion, "1.18–1.20.4 要的是 17，不该顺手装 21。");
        Assert.AreEqual(Legacy, for8!.Component, "≤1.12.2 的上限硬性封在 8，只有 jre-legacy 满足。");
    }

    [TestMethod]
    public void FallsBackToTheSmallestNewerMajor()
    {
        // 只登记 21 与 25 两代：所需 22 没有正好对应的，取最小的更高版本（25），而不是更大的那个。
        var index = JavaRuntimeIndex.Parse(JavaRuntimeFixture.IndexJson(
        [
            JavaRuntimeFixture.Release(Delta, "21.0.7"),
            JavaRuntimeFixture.Release(Epsilon, "25.0.1"),
        ]));

        var selected = index.Select(JavaRuntimeFixture.Platform, requiredMajor: 22, componentHint: null);

        Assert.AreEqual(Epsilon, selected!.Component);
    }

    [TestMethod]
    public void WithoutAVersionContextTheNewestGenerationIsChosen()
    {
        var index = RealIndex();

        var selected = index.Select(JavaRuntimeFixture.Platform, requiredMajor: null, componentHint: null);

        // p10 的「获取 Java 25」就是这个语义：没选过版本时取最新的一代。
        Assert.AreEqual(Epsilon, selected!.Component);
        Assert.AreEqual(25, selected.MajorVersion);
    }

    [TestMethod]
    public void SnapshotComponentsLoseToTheStableOneOfTheSameGeneration()
    {
        var index = JavaRuntimeIndex.Parse(JavaRuntimeFixture.IndexJson(
        [
            JavaRuntimeFixture.ReleaseWithManifest(Gamma + "-snapshot", "17.0.15", JavaRuntimeFixture.ManifestJson(), Newer),
            JavaRuntimeFixture.ReleaseWithManifest(Gamma, "17.0.15", JavaRuntimeFixture.ManifestJson(), Older),
        ]));

        var selected = index.Select(JavaRuntimeFixture.Platform, requiredMajor: 17, componentHint: null);

        Assert.AreEqual(Gamma, selected!.Component, "同一代里快照不该被当成默认运行时。");
    }

    [TestMethod]
    public void TheNativeLauncherShellIsNotARuntime()
    {
        var index = JavaRuntimeIndex.Parse(JavaRuntimeFixture.IndexJson(
        [
            JavaRuntimeFixture.Release("minecraft-java-exe", "14"),
            JavaRuntimeFixture.Release(Gamma, "17.0.15"),
        ]));

        // 实测：windows-x64 的 minecraft-java-exe 清单里只有一个 MinecraftJava.exe，不是 JRE。
        var selected = index.Select(JavaRuntimeFixture.Platform, requiredMajor: null, componentHint: null);

        Assert.AreEqual(Gamma, selected!.Component, "选择时跳过启动壳。");

        var onlyShell = JavaRuntimeIndex.Parse(
            JavaRuntimeFixture.IndexJson([JavaRuntimeFixture.Release("minecraft-java-exe", "14")]));

        Assert.IsNull(
            onlyShell.Select(JavaRuntimeFixture.Platform, requiredMajor: null, componentHint: null),
            "只剩启动壳时应当如实说「没有可用运行时」，而不是把它当 JRE 装下来。");
    }

    [TestMethod]
    public void AnUnknownPlatformYieldsNothing()
    {
        var index = RealIndex();

        Assert.IsNull(index.Select("windows-arm64", requiredMajor: 17, componentHint: null));
        Assert.AreEqual(0, index.ForPlatform("windows-arm64").Count);
    }

    [TestMethod]
    public void ParseRejectsGarbage()
    {
        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimeIndex.Parse("not json"));
        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimeIndex.Parse("{}"));
        Assert.ThrowsException<JavaRuntimeDataException>(
            () => JavaRuntimeIndex.Parse("{\"windows-x64\":{\"java-runtime-gamma\":[]}}"));
    }

    [TestMethod]
    public void ParseSkipsReleasesWithoutAManifestUrl()
    {
        // 官方索引里确实存在没有清单地址的条目（其它平台的键常常干脆是空数组）：
        // 跳过它，但不能因此让整份索引作废 —— 同一次响应里的其它组件仍然是可用的。
        var valid = JavaRuntimeFixture.Release(Gamma, "17.0.15");

        var json = "{\"windows-x64\":{\"" + Gamma + "\":[{\"version\":{\"name\":\"17.0.15\"}},"
            + "{\"manifest\":{\"sha1\":\"" + valid.ManifestSha1 + "\",\"size\":" + valid.ManifestSize
            + ",\"url\":\"" + valid.ManifestUrl + "\"},\"version\":{\"name\":\"17.0.15\"}}]}}";

        var index = JavaRuntimeIndex.Parse(json);

        var candidate = index.ForPlatform(JavaRuntimeFixture.Platform).Single();
        Assert.AreEqual(valid.ManifestUrl, candidate.ManifestUrl);
    }

    private static JavaRuntimeCandidate Candidate(string component, string versionName) =>
        JavaRuntimeIndex
            .Parse(JavaRuntimeFixture.IndexJson([JavaRuntimeFixture.Release(component, versionName)]))
            .Candidates.Single();

    /// <summary>一份包含真实几代的索引（版本名与发布时间都按实测的官方索引写）。</summary>
    private static JavaRuntimeIndex RealIndex() => JavaRuntimeIndex.Parse(JavaRuntimeFixture.IndexJson(
    [
        JavaRuntimeFixture.ReleaseWithManifest(Legacy, "8u51-cacert462b08", JavaRuntimeFixture.ManifestJson(), Older),
        JavaRuntimeFixture.ReleaseWithManifest(Beta, "17.0.15", JavaRuntimeFixture.ManifestJson(), Older),
        JavaRuntimeFixture.ReleaseWithManifest(Gamma, "17.0.15", JavaRuntimeFixture.ManifestJson(), Newer),
        JavaRuntimeFixture.ReleaseWithManifest(Delta, "21.0.7", JavaRuntimeFixture.ManifestJson(), Newer),
        JavaRuntimeFixture.ReleaseWithManifest(Epsilon, "25.0.1", JavaRuntimeFixture.ManifestJson(), Newer),
        JavaRuntimeFixture.ReleaseWithManifest("minecraft-java-exe", "14", JavaRuntimeFixture.ManifestJson(), Older),
    ]));
}

/// <summary>
/// 组件文件清单的解析（<c>files</c> 字典里的三种条目类型）。
/// </summary>
[TestClass]
public sealed class JavaRuntimeManifestTests
{
    [TestMethod]
    public void ParseMapsTheThreeEntryTypes()
    {
        var manifest = JavaRuntimeManifest.Parse(JavaRuntimeFixture.ManifestJson(
            JavaRuntimeFixture.Directory("bin"),
            JavaRuntimeFixture.File("bin/javaw.exe", "javaw-bytes"),
            JavaRuntimeFixture.Link("bin/server", "../lib/jvm.dll")));

        Assert.AreEqual(3, manifest.Entries.Count);

        var directory = manifest.Entries[0];
        Assert.AreEqual("bin", directory.RelativePath);
        Assert.AreEqual(JavaRuntimeFileKind.Directory, directory.Kind);
        Assert.IsNull(directory.Url);

        var file = manifest.Entries[1];
        Assert.AreEqual(JavaRuntimeFileKind.File, file.Kind);
        Assert.AreEqual(JavaRuntimeFixture.Sha1Hex("javaw-bytes"), file.Sha1);
        Assert.AreEqual(11, file.Size);
        Assert.IsTrue(file.Executable);
        StringAssert.Contains(file.Url, "/v1/objects/");

        var link = manifest.Entries[2];
        Assert.AreEqual(JavaRuntimeFileKind.Link, link.Kind);
        Assert.AreEqual("../lib/jvm.dll", link.Target);
    }

    [TestMethod]
    public void AnUnknownEntryTypeIsKeptAndRejectedLater()
    {
        var manifest = JavaRuntimeManifest.Parse(
            "{\"files\":{\"whatever\":{\"type\":\"hardlink\",\"target\":\"x\"}}}");

        // 解析只如实映射；"认不出的类型要拒绝整份清单"是计划阶段的决定（它才有路径上下文）。
        Assert.AreEqual(JavaRuntimeFileKind.Unknown, manifest.Entries.Single().Kind);
    }

    [TestMethod]
    public void ParseRejectsAnEmptyManifest()
    {
        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimeManifest.Parse("not json"));
        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimeManifest.Parse("{}"));
        Assert.ThrowsException<JavaRuntimeDataException>(() => JavaRuntimeManifest.Parse("{\"files\":{}}"));
    }

    [TestMethod]
    public void SizesAreCountedInUtf8Bytes()
    {
        // 「字节数」必须是 UTF-8 字节数而不是字符数，否则含非 ASCII 的条目会被判成"大小不符"。
        var manifest = JavaRuntimeManifest.Parse(
            JavaRuntimeFixture.ManifestJson(JavaRuntimeFixture.File("legal/zh.txt", "许可")));

        var file = manifest.Entries.Single();

        Assert.AreEqual(6, file.Size);
        Assert.AreEqual(
            JavaRuntimeFixture.Sha1Hex("许可"),
            file.Sha1,
            string.Create(CultureInfo.InvariantCulture, $"sha1 应当按 UTF-8 字节算：{file.Sha1}"));
    }
}
