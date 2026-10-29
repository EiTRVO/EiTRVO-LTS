using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// 非 UI 层的纯逻辑测试。
/// <para>
/// 这里刻意只测「有判断的地方」：Java 版本推导、加载器可用性、实例与内容的关联。
/// 按 AGENTS.md §4 的原则，测试要断言<b>具体的期望值</b>，而不是「非空」或「不抛异常」——
/// Neo 的 <c>UuidHelperTests</c> 就是因为只断言非空，才让大小端错误活了下来。
/// </para>
/// </summary>
[TestClass]
public sealed class DomainLogicTests
{
    [TestMethod]
    [DataRow("1.7.10", 8)]
    [DataRow("1.12.2", 8)]
    [DataRow("1.13", 8)]
    [DataRow("1.16.5", 8)]
    // 1.17 要求 Java 16：M2 的规则表把它算成 8，那是错的（Mojang 自己的版本 JSON 里写的就是 16）。
    [DataRow("1.17.1", 16)]
    [DataRow("1.18", 17)]
    [DataRow("1.20.4", 17)]
    [DataRow("1.20.5", 21)]
    [DataRow("1.21.4", 21)]
    [DataRow("26.3", 25)]
    [DataRow("26.4-snapshot-2", 25)]
    // 周次快照按年份分世代（M3 新增）：它们既不是 1.x 也不是"年份.月份"形态。
    [DataRow("13w41a", 8)]
    [DataRow("20w14infinite", 8)]
    [DataRow("20w46a", 16)]
    [DataRow("21w03a", 17)]
    [DataRow("22w13oneblockatatime", 17)]
    [DataRow("24w03a", 21)]
    [DataRow("25w14a", 21)]
    public void RequiredJava_FollowsTheDocumentedBoundaries(string gameVersion, int expected)
    {
        Assert.AreEqual(expected, JavaRequirement.RequiredMajor(gameVersion));
    }

    [TestMethod]
    public void RequiredJava_FallsBackToJava8ForUnparsableVersions()
    {
        Assert.AreEqual(8, JavaRequirement.RequiredMajor("not-a-version"));
        Assert.AreEqual(8, JavaRequirement.RequiredMajor(string.Empty));
    }

    [TestMethod]
    [DataRow("1.12.2", 8, true)]
    [DataRow("1.12.2", 17, false)]
    [DataRow("1.16.5", 8, true)]
    [DataRow("1.16.5", 21, true)]
    [DataRow("1.20.4", 17, true)]
    [DataRow("1.20.4", 21, true)]
    [DataRow("1.20.4", 8, false)]
    [DataRow("1.21.4", 25, true)]
    [DataRow("not-a-version", 17, true)]
    public void JavaUsability_IsAnIntervalNotAFloor(string gameVersion, int javaMajor, bool expected)
    {
        // 只判下限会把"本机只有 Java 17"当成满足 1.12.2（LWJGL 2 在 Java 9+ 上起不来），
        // 于是启动时才崩 —— 这条用例钉住"上限也要看"。
        Assert.AreEqual(expected, JavaRequirement.IsSatisfiedBy(javaMajor, gameVersion));
    }

    [TestMethod]
    [DataRow(8, "≤1.16.5")]
    [DataRow(16, "1.17")]
    [DataRow(17, "1.18–1.20.4")]
    [DataRow(21, "1.20.5+")]
    [DataRow(25, "26.1+")]
    [DataRow(11, "—")]
    public void SupportedGameRange_MatchesTheRequirementTable(int javaMajor, string expected)
    {
        Assert.AreEqual(expected, JavaRequirement.SupportedGameRange(javaMajor));
    }

    [TestMethod]
    public void VersionCatalog_OnlyInstalledVersionsCanCreateAnInstance()
    {
        var versions = new SampleVersionCatalog().GetVersions();

        Assert.AreEqual(10, versions.Count);
        CollectionAssert.AreEqual(
            new[] { "1.21.1", "1.20.4", "1.20.2", "1.20.1", "1.16.5" },
            versions.Where(static v => v.CanCreateInstance).Select(static v => v.Id).ToArray());
    }

    [TestMethod]
    public void VersionCatalog_KeepsManifestTotalsConsistent()
    {
        var catalog = new SampleVersionCatalog();

        Assert.AreEqual(917, catalog.TotalCount);
        Assert.AreEqual(612, catalog.SnapshotCount);
        Assert.AreEqual(305, catalog.ReleaseCount);
    }

    [TestMethod]
    public void InstanceCatalog_ExposesSixInstancesInDesignOrder()
    {
        var catalog = new SampleInstanceCatalog();

        var rows = catalog.GetInstances();
        Assert.AreEqual(6, rows.Count);

        // 顺序必须与设计稿一致，否则表格首列的对勾会落到别的行上。
        CollectionAssert.AreEqual(
            new[] { "1.20.4-Forge", "1.20.1-Fabric", "1.21.4-Vanilla", "1.21.1-NeoForge", "1.12.2-Forge", "1.20.4-OptiFine" },
            rows.Select(static r => r.Name).ToArray());

        // 「哪一行是当前实例」由 ViewModel 按 ActiveInstanceName 标注 —— 数据源不预置高亮，
        // 否则换一行选中之后就没人去把旧的高亮清掉了。
        Assert.AreEqual(0, rows.Count(static r => r.IsActive));
    }

    [TestMethod]
    public void InstanceCatalog_ReturnsPerInstanceDataNotOneSharedRecord()
    {
        var catalog = new SampleInstanceCatalog();

        // 这一条是钉住「GetDetail/GetModSummary 真的按传入的实例返回各自的数据」。
        // 之前它们只把 Name 换成参数、其余字段全是硬编码的 1.20.4-Forge，
        // 所以在列表里换一行选中之后，详情页只会换个名字。
        Assert.AreEqual("1.20.1", TestText.Of(catalog.GetDetail("1.20.1-Fabric").GameVersion));
        Assert.AreEqual("Fabric 0.15.7", TestText.Of(catalog.GetDetail("1.20.1-Fabric").LoaderDisplay));
        Assert.AreEqual("6.1 GB", catalog.GetDetail("1.20.1-Fabric").SizeOnDisk);
        Assert.AreEqual(4096, catalog.GetDetail("1.20.1-Fabric").MemoryMb);

        Assert.AreEqual("Forge 49.0.30", TestText.Of(catalog.GetDetail("1.20.4-Forge").LoaderDisplay));
        Assert.AreEqual(6144, catalog.GetDetail("1.21.1-NeoForge").MemoryMb);
        Assert.AreEqual("1.12.2", TestText.Of(catalog.GetDetail("1.12.2-Forge").GameVersion));

        // 快切摘要：M6-e-3d 起内核只给**三段原料**（加载器模板 / 模组数 / 时间模板），
        // 句子由 App 层拼 —— 因为加载器那一段自己就是模板，内核渲染不了它（§5.3 第 ⑤ 条）。
        var fabric = catalog.GetOverview().Single(static o => o.Name == "1.20.1-Fabric");

        Assert.AreEqual("Fabric 0.15.7", TestText.Of(fabric.LoaderDisplay));
        Assert.AreEqual(42, fabric.ModCount);
        Assert.AreEqual("昨天 {0}", fabric.LastLaunched.Key, "时间戳自己是一句话（键 + 参数）。");
        CollectionAssert.AreEqual(new[] { "21:30" }, fabric.LastLaunched.Parameters.ToArray());

        // 「资源校验」那一格是两句话：`已完成` + 时间（挂在 Note 上）。
        // 真实实现里 `ResourceVerifiedAt` 还没有写入点，所以这一支只有演示数据走得到（见 FileInstanceCatalog 的注释）。
        var verified = catalog.GetDetail("1.20.4-Forge").ResourceVerification;

        Assert.AreEqual("已完成", verified.Key);
        Assert.AreEqual("今天 {0}", verified.Note!.Key);
        CollectionAssert.AreEqual(new[] { "15:52" }, verified.Note.Parameters.ToArray());
    }

    [TestMethod]
    public void InstanceDetail_DerivesDisplayTextFromRawValues()
    {
        var detail = new SampleInstanceCatalog().GetDetail("1.20.4-Forge");

        Assert.AreEqual("4096 MB", TestText.Of(detail.MemoryText));
        Assert.AreEqual("0 (正常退出)", TestText.Of(detail.LastExitCodeText));
        Assert.AreEqual(Tone.Ok, detail.LastExitCodeTone);
        Assert.AreEqual("已启用", TestText.Of(detail.IsolatedDirectoryText));
    }

    [TestMethod]
    public void InstanceActions_TargetSemanticDestinations()
    {
        var actions = new SampleInstanceCatalog().GetActions("1.20.4-Forge");

        CollectionAssert.AreEqual(
            new[]
            {
                InstanceActionTarget.Mods,
                InstanceActionTarget.Resources,
                InstanceActionTarget.InstanceSettings,
            },
            actions.Select(static a => a.Target).ToArray());
    }

    [TestMethod]
    public void LoaderOptions_MarkExactlyOneSelectionAndTheUnavailableOne()
    {
        var loaders = new SampleVersionCatalog().GetLoaders("1.20.4");

        Assert.AreEqual(1, loaders.Count(static l => l.IsActive));
        Assert.AreEqual(LoaderKind.Forge, loaders.Single(static l => l.IsActive).Kind);
        Assert.AreEqual(
            LoaderAvailability.Unavailable,
            loaders.Single(static l => l.Kind == LoaderKind.NeoForge).Availability);
    }

    [TestMethod]
    public void WizardSteps_StartOnTheSecondStep()
    {
        var steps = new SampleVersionCatalog().GetWizardSteps();

        Assert.AreEqual(4, steps.Count);
        Assert.AreEqual(WizardStepState.Done, steps[0].State);
        Assert.AreEqual(WizardStepState.Current, steps[1].State);
        Assert.AreEqual("模组加载器", TestText.Of(steps[1].Title), "步骤名是文案（键 + 参数）。");
    }

    [TestMethod]
    public void ModpackEntry_ComposesItsOwnSummaryLine()
    {
        // 行摘要（`Meta`）属于**记录自己**，与数据来自演示还是 Modrinth 无关 ——
        // 所以 M7-a 删掉演示内容目录之后，这条仍然直接构造一条来钉形状。
        var pack = new ModpackEntry(
            "Better MC [FORGE] 1.20.4",
            "v34",
            216,
            "12.4 GB",
            "1.20.4-Forge",
            TextTemplate.Of("已同步"),
            Tone.Ok,
            IsActive: true);

        Assert.AreEqual("v34 · 216 个模组 · 12.4 GB · 对应实例 1.20.4-Forge", TestText.Of(pack.Meta));
    }

    [TestMethod]
    public void DownloadCatalog_PendingCountMatchesTheQueue()
    {
        var downloads = new SampleDownloadCatalog();

        Assert.AreEqual(3, downloads.PendingTaskCount);
        Assert.AreEqual(3, downloads.GetQueue().Count);
        Assert.AreEqual(1, downloads.GetQueue().Count(static q => q.State == Launcher.Downloads.DownloadQueueState.Running));
    }

    [TestMethod]
    public void SettingsCatalog_RoundTripsSavedValues()
    {
        var settings = new SampleSettingsCatalog();
        var updated = settings.GetGlobal() with { MaxMemoryMb = 8192 };

        settings.SaveGlobal(updated);

        Assert.AreEqual(8192, settings.GetGlobal().MaxMemoryMb);
    }
}
