using System.IO;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Versions;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M2 接线：p05 与 p04 从「一份恒定样本」换成「来自网络的真实清单」之后，
/// 两个页面必须能应付三种真实状态 —— **还没有数据**、**刷新后才有数据**、**数据变了**。
/// <para>
/// 这三种状态以前不存在（样本数据永远非空、永远即时），所以它们是接真实实现时最容易漏的地方：
/// 漏了第一种就是打开向导直接崩，漏了后两种就是页面永远显示进页面那一刻的旧数据。
/// </para>
/// </summary>
[TestClass]
public sealed class VersionWiringTests
{
    private static MinecraftVersion Row(string id, bool isActive = false, VersionKind kind = VersionKind.Release) =>
        new(id, kind, "2024/12/03", "Java 21", VersionLocalState.NotInstalled, TextTemplate.Of("未安装"), isActive);

    /// <summary>
    /// 造一个加载器项：显示名与说明走**真实的那把尺子**（<see cref="InstanceNaming.Display"/>），
    /// 版本列表是**纯版本号** —— M6-e-3d 起内核不再往版本串上贴「（推荐）」，
    /// 那个标记改由 <see cref="LoaderOption.RecommendedVersion"/> 表达（App 层拼显示文本）。
    /// </summary>
    private static LoaderOption Loader(LoaderKind kind, bool isActive, params string[] versions) =>
        new(
            kind,
            InstanceNaming.Display(kind),
            TextTemplate.Of("{0} 可用", kind.ToString()),
            isActive
                ? LoaderAvailability.Recommended
                : kind == LoaderKind.Vanilla
                    ? LoaderAvailability.Discouraged
                    : LoaderAvailability.Available,
            isActive,
            versions,
            RecommendedVersion: versions.Length > 0 ? versions[0] : null);

    // ==================== p05 所有版本 ====================


    [TestMethod]
    public void TheWizardLoaderRowsBindToThePageCommandNotTheRowItself()
    {
        var xaml = File.ReadAllText(Path.Combine(PageFilterRowXamlTests.RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", "P04NewInstanceView.xaml"));

        StringAssert.Contains(
            xaml,
            "Command=\"{Binding DataContext.SelectLoaderCommand, RelativeSource={RelativeSource AncestorType=UserControl}}\"",
            "行内命令必须回到页面 ViewModel；绑到行上时 LoaderOptionViewModel 没有这个命令，整列都点不动。");
    }
    [TestMethod]
    public void VersionsPageShowsTheCachedRowsImmediately()
    {
        var catalog = new FakeVersionCatalog([Row("1.20.4", isActive: true)]);

        var page = NewVersionsPage(catalog);

        Assert.AreEqual(1, page.Versions.Count);
        Assert.AreEqual(0, catalog.RefreshCount, "构造不该联网；刷新发生在页面被导航到时。");
    }


    [TestMethod]
    public void VersionsPageRefreshesAndReplacesTheRowsWhenItIsOpened()
    {
        // 首次运行：本机既没有缓存也没联网过 —— 进页面时是空表。
        var catalog = new FakeVersionCatalog
        {
            RowsAfterRefresh = [Row("1.21.4"), Row("1.20.4", isActive: true)],
        };

        var page = NewVersionsPage(catalog);
        Assert.AreEqual(0, page.Versions.Count);

        // 外壳导航到本页时调用的就是这个（替身同步完成，所以断言可以紧跟其后）。
        page.OnActivated();

        Assert.AreEqual(1, catalog.RefreshCount);
        Assert.AreEqual(2, page.Versions.Count);
        StringAssert.Contains(page.SectionSubtitle, "2", "副标题里的总数要跟着刷新一起更新。");
    }


    [TestMethod]
    public async Task VersionsPageKeepsTheRowsItHasWhenTheRefreshFails()
    {
        // 内核在联网失败时会沿用缓存（见 FileVersionCatalog），ViewModel 这边要做的就是别把表清空。
        var catalog = new FakeVersionCatalog([Row("1.20.4", isActive: true)]);
        var page = NewVersionsPage(catalog);

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual(1, page.Versions.Count);
    }

    // ==================== p04 新建实例向导 ====================


    [TestMethod]
    public void TheWizardDoesNotCrashWhenThereIsNoVersionListYet()
    {
        // 接入真实清单之前，向导用的是样本数据，永远有值；真实数据首次运行就是空的。
        var wizard = NewWizard(new FakeVersionCatalog());

        Assert.IsFalse(wizard.HasVersion);
        Assert.IsNull(wizard.SelectedVersion);
        Assert.AreEqual("还没有版本清单", wizard.SelectedVersionText);
        Assert.AreEqual(string.Empty, wizard.RequiredJavaText);
        Assert.AreEqual(0, wizard.Loaders.Count);
        Assert.AreEqual(0, wizard.LoaderVersionOptions.Count);
        Assert.IsFalse(wizard.NextCommand.CanExecute(null), "没有版本可装时「下一步」应当是灰的。");
    }


    [TestMethod]
    public async Task TheWizardFillsItselfInAfterTheFirstRefresh()
    {
        var catalog = new FakeVersionCatalog
        {
            RowsAfterRefresh = [Row("1.21.4"), Row("1.20.4", isActive: true)],
            Loaders = [Loader(LoaderKind.Vanilla, false), Loader(LoaderKind.Forge, true, "49.0.30", "49.0.29")],
        };

        var wizard = NewWizard(catalog);
        Assert.IsFalse(wizard.HasVersion);

        wizard.OnActivated();
        await wizard.RefreshCommand.ExecuteAsync(null);

        Assert.IsTrue(wizard.HasVersion);
        Assert.AreEqual("1.20.4", wizard.SelectedVersion!.Id, "挑的是清单里的「当前行」。");
        Assert.AreEqual("1.20.4", catalog.LastLoaderGameVersion, "加载器要按选中的版本去取。");
        Assert.AreEqual(2, wizard.Loaders.Count);
        Assert.AreEqual(LoaderKind.Vanilla, wizard.SelectedLoader!.Kind, "默认原版，不再默认 Forge。");
        Assert.AreEqual("原版", wizard.Loaders.Single(static l => l.IsActive).Display, "行上的显示名已本地化。");
        Assert.AreEqual(string.Empty, wizard.SelectedLoaderVersion, "原版没有加载器版本。");
        Assert.AreEqual(0, wizard.LoaderVersionOptions.Count);
        Assert.IsTrue(wizard.NextCommand.CanExecute(null), "有版本之后「下一步」要恢复可用。");
        StringAssert.Contains(wizard.SelectedVersionText, "1.20.4");
    }


    [TestMethod]
    public async Task TheWizardShowsTheSelectedVersionsEstimatedDownloadSize()
    {
        // 412 MB 曾经是设计稿写死的数字；现在必须按所选版本由内核估算。
        var catalog = new FakeVersionCatalog
        {
            RowsAfterRefresh = [Row("1.21.4"), Row("1.20.4", isActive: true)],
            Loaders = [Loader(LoaderKind.Vanilla, false), Loader(LoaderKind.Forge, true, "49.0.30")],
            EstimatedDownload = new VersionDownloadEstimate(
                ClientBytes: 20 * 1024 * 1024,
                LibraryBytes: 200 * 1024 * 1024,
                AssetBytes: 100 * 1024 * 1024,
                NativeBytes: 10 * 1024 * 1024)
            {
                FreeDiskBytes = 500L * 1024 * 1024 * 1024,
            },
        };

        var wizard = NewWizard(catalog);
        wizard.OnActivated();
        await wizard.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual("1.20.4", wizard.SelectedVersion!.Id);
        CollectionAssert.Contains(catalog.EstimateRequests, "1.20.4");
        Assert.AreEqual("330 MB", wizard.DownloadSizeText);
        StringAssert.Contains(wizard.DownloadBreakdownText, "220 MB", "客户端 JAR 与库合并成「库」这一栏。");
        StringAssert.Contains(wizard.DownloadBreakdownText, "100 MB");
        StringAssert.Contains(wizard.DownloadBreakdownText, "10 MB");
        StringAssert.Contains(wizard.DiskFreeText, "空间充足");
    }


    [TestMethod]
    public async Task TheWizardDefaultsToTheNewestLoaderVersionNotTheRecommendedOne()
    {
        // 「（推荐）」是加载器自己标的稳定版，而默认选中项是**最新版**；
        // 两者不是一回事（Fabric 的 stable 往往落后一两个版本）。
        var fabric = new LoaderOption(
            LoaderKind.Fabric,
            InstanceNaming.Display(LoaderKind.Fabric),
            TextTemplate.Of("{0} 可用", "Fabric"),
            LoaderAvailability.Recommended,
            true,
            ["0.20.1", "0.19.5"],
            RecommendedVersion: "0.19.5");

        var catalog = new FakeVersionCatalog
        {
            RowsAfterRefresh = [Row("1.20.1", isActive: true)],
            Loaders = [Loader(LoaderKind.Vanilla, false), fabric],
        };

        var wizard = NewWizard(catalog);
        wizard.OnActivated();
        await wizard.RefreshCommand.ExecuteAsync(null);

        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Fabric));

        Assert.AreEqual(LoaderKind.Fabric, wizard.SelectedLoader!.Kind);
        Assert.AreEqual("0.20.1", wizard.SelectedLoaderVersion, "默认选中最新版，而不是标着推荐的旧稳定版。");
        Assert.AreEqual("0.20.1", wizard.LoaderVersionOptions[0].Text);
        Assert.AreEqual("0.19.5（推荐）", wizard.LoaderVersionOptions[1].Text, "「（推荐）」仍然标在稳定版上。");
    }


    [TestMethod]
    public async Task TheWizardKeepsTheUsersLoaderChoiceWhenTheVersionIsUnchanged()
    {
        var catalog = new FakeVersionCatalog(
            [Row("1.20.4", isActive: true)],
            [Loader(LoaderKind.Vanilla, false), Loader(LoaderKind.Forge, true, "49.0.30"), Loader(LoaderKind.Fabric, false, "0.15.7")]);

        var wizard = NewWizard(catalog);
        // 界面上点的是**投影后的行**（XAML 把 ItemsSource 里那一项当 CommandParameter 传回来）。
        wizard.SelectLoaderCommand.Execute(wizard.Loaders[2]);

        await wizard.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual(LoaderKind.Fabric, wizard.SelectedLoader!.Kind, "刷新不该把用户选好的加载器换掉。");
    }


    [TestMethod]
    public async Task TheWizardMovesToADifferentVersionWhenTheOldOneIsGone()
    {
        // 清单换了（或原来那行没有了）：必须重新挑，而不是留着一个已经不存在的版本。
        var catalog = new FakeVersionCatalog(
            [Row("1.20.4", isActive: true)],
            [Loader(LoaderKind.Vanilla, false), Loader(LoaderKind.Forge, true, "49.0.30")])
        {
            RowsAfterRefresh = [Row("1.21.4", isActive: true)],
        };

        var wizard = NewWizard(catalog);
        await wizard.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual("1.21.4", wizard.SelectedVersion!.Id);
        Assert.AreEqual("1.21.4", catalog.LastLoaderGameVersion);
    }


    [TestMethod]
    public async Task TheWizardDefaultsToVanillaWhenNothingIsMarkedActive()
    {
        var catalog = new FakeVersionCatalog(
            [Row("1.20.4", isActive: true)],
            [Loader(LoaderKind.Vanilla, false), Loader(LoaderKind.Fabric, false, "0.15.7")]);

        var wizard = NewWizard(catalog);
        await wizard.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual(LoaderKind.Vanilla, wizard.SelectedLoader!.Kind, "没有标选中项时默认原版。");
        Assert.AreEqual(string.Empty, wizard.SelectedLoaderVersion, "原版没有加载器版本。");
    }


    [TestMethod]
    public async Task TheWizardCanStillBuildAnInstanceFromRealData()
    {
        var instances = new FakeInstanceCatalog();
        var catalog = new FakeVersionCatalog(
            [Row("1.20.4", isActive: true)],
            [Loader(LoaderKind.Vanilla, false), Loader(LoaderKind.Forge, true, "49.0.30", "49.0.29")]);

        var wizard = NewWizard(catalog, instances);
        await wizard.RefreshCommand.ExecuteAsync(null);
        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Forge));

        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);

        Assert.AreEqual(1, instances.Created.Count);
        Assert.AreEqual("1.20.4-Forge", instances.Created[0].Name);
        Assert.AreEqual("49.0.30", instances.Created[0].LoaderVersion, "落盘的是纯版本号（「（推荐）」只是显示文本）。");
        Assert.AreEqual(LoaderKind.Forge, instances.Created[0].Loader);
    }


    [TestMethod]
    public void AFailedQueueRowGetsTheFailurePrefixFromThePageNotFromTheKernel()
    {
        // 内核只说"失败原因本身"；「失败：」这个前缀是**界面的话**（AGENTS.md §5.3：内核不拼句子）。
        var downloads = new FakeDownloadCatalog();
        var page = new DownloadsViewModel(downloads, new SampleEventLog(), new ImmediateRefreshThrottle());

        downloads.Queue =
        [
            new DownloadQueueItem(
                "aaaa1111",
                TextTemplate.Of("安装 {0}", "1.20.4"),
                TextTemplate.Of("这个实例的文件不完整：缺 {0} 个（例如 {1}）。", "2", "客户端 JAR"),
                DownloadQueueState.Failed,
                false),
            new DownloadQueueItem(
                "bbbb2222",
                TextTemplate.Of("安装 {0}", "1.21.4"),
                TextTemplate.Of("已完成"),
                DownloadQueueState.Completed,
                false),
        ];

        downloads.RaiseChanged();

        var failed = page.Queue[0];
        Assert.AreEqual("安装 1.20.4", failed.Title, "队列行标题也要按当前语言拼出来。");
        Assert.AreEqual(
            "失败：这个实例的文件不完整：缺 2 个（例如 客户端 JAR）。",
            failed.Detail,
            "失败原因由界面加前缀，内核只给原因本身。");
        Assert.AreEqual("已完成", page.Queue[1].Detail, "没失败的那一行不加前缀。");
    }


    [TestMethod]
    public void TheDownloadsPageRefreshesWhenTheQueueChangesInTheBackground()
    {
        // 队列在后台线程报进度；页面必须跟着刷新，否则只会显示"进页面那一刻"的快照。
        var downloads = new FakeDownloadCatalog();
        var page = new DownloadsViewModel(downloads, new SampleEventLog(), new ImmediateRefreshThrottle());

        downloads.Queue =
        [
            new DownloadQueueItem(
                "aaaa1111",
                TextTemplate.Of("安装 {0}", "1.20.4"),
                TextTemplate.Of("{0} · {1}", "128 MB / 256 MB", "8.0 MB/s"),
                DownloadQueueState.Running,
                true),
            new DownloadQueueItem(
                "bbbb2222",
                TextTemplate.Of("安装 {0}", "1.21.4"),
                TextTemplate.Of("等待中 · 已排队 {0} 秒", "3"),
                DownloadQueueState.Queued,
                false),
        ];
        downloads.Progress = downloads.Progress with
        {
            CurrentTask = TextTemplate.Of("安装 {0}", "1.20.4"),
            Percent = 50,
            AmountText = "128 MB / 256 MB",
            SpeedText = "8.0 MB/s",
            FilesText = "1,204 / 3,180",
            RemainingText = TextTemplate.Of("约 {0} 秒", "19"),
            ConnectionsText = "64",
            DataSource = "piston-data.mojang.com",
        };

        downloads.RaiseChanged();

        Assert.AreEqual(2, page.Queue.Count, "变更事件要能把新队列带进页面。");
        Assert.AreEqual("安装 1.20.4", page.CurrentTaskText);
        Assert.AreEqual("1 个活动任务 · 1 个排队", page.SectionSubtitle);

        // 属性表八格都是**队列给的**（内核把格式化的文本交上来，页面不再自己算一遍）。
        Assert.AreEqual("128 MB / 256 MB", page.Progress.AmountText);
        Assert.AreEqual("8.0 MB/s", page.Progress.SpeedText);
        Assert.AreEqual("1,204 / 3,180", page.Progress.FilesText);
        Assert.AreEqual("约 19 秒", page.RemainingTextText);
        Assert.AreEqual("64", page.Progress.ConnectionsText);
        Assert.AreEqual("piston-data.mojang.com", page.Progress.DataSource);
        Assert.AreEqual(50, page.Progress.Percent);
    }


    [TestMethod]
    public void TheDownloadsPageGoesBackToEmptyWhenNothingIsRunning()
    {
        // 用户 2026-10-06 的要求（Bug4.png）：下载完成后清空队列里这个项目的信息。
        // 内核把空闲快照交上来（全「—」、0%），页面照它显示 —— 这一页只负责如实呈现。
        var downloads = new FakeDownloadCatalog();
        var page = new DownloadsViewModel(downloads, new SampleEventLog(), new ImmediateRefreshThrottle());

        downloads.Queue = [new DownloadQueueItem("aaaa1111", TextTemplate.Of("安装 {0}", "1.20.4"), TextTemplate.Of("已完成"), DownloadQueueState.Completed, false)];
        downloads.Progress = downloads.Progress with
        {
            CurrentTask = TextTemplate.Of("—"),
            AmountText = "—",
            SpeedText = "—",
            FilesText = "—",
            RemainingText = TextTemplate.Of("—"),
            ConnectionsText = "—",
            DataSource = "—",
        };

        downloads.RaiseChanged();

        Assert.AreEqual("—", page.CurrentTaskText);
        Assert.AreEqual(0, page.Progress.Percent);
        Assert.AreEqual("—", page.Progress.FilesText);
        Assert.AreEqual("—", page.Progress.DataSource);
        Assert.AreEqual("0 个活动任务 · 0 个排队", page.SectionSubtitle);

        // 队列里那一行是**历史**，仍然留着"已完成" —— 清的只是"当前任务/进度"这一块。
        Assert.AreEqual(DownloadQueueState.Completed, page.Queue.Single().State);
    }

    // ==================== p05 的选中行与动作（Q12 决策 (b)） ====================


    [TestMethod]
    public void TheVersionPageActionIsDisabledUntilARowIsSelected()
    {
        // 「安装所选版本」已按用户要求移除；剩下的「用所选版本新建实例」以有没有选中行为准。
        var page = NewVersionsPage(new FakeVersionCatalog([Row("1.20.4", isActive: true)]));

        Assert.IsNull(page.SelectedVersion, "一进页面不该自带高亮行。");
        Assert.IsFalse(page.CreateInstanceCommand.CanExecute(null));

        page.SelectedVersion = page.FilteredVersions.Single();

        Assert.IsTrue(page.CreateInstanceCommand.CanExecute(null));
    }


    [TestMethod]
    public void CreateInstanceHandsTheSelectedVersionToTheWizard()
    {
        var catalog = new FakeVersionCatalog([Row("1.21.4"), Row("1.20.4", isActive: true)]);
        var navigation = new FakeNavigationService();
        var page = new VersionsPageBuilder(catalog, navigation).Build();

        // 用户点的是 1.21.4，而不是清单里那行"当前版本"（1.20.4）。
        page.SelectedVersion = page.FilteredVersions.First(v => v.Id == "1.21.4");
        page.CreateInstanceCommand.Execute(null);

        Assert.AreEqual("1.21.4", catalog.WrittenActiveVersionId, "所选版本要交给内核，向导才会默认带它。");
        Assert.AreEqual("1.21.4", catalog.ActiveVersionId, "内核解析出来的当前版本就是用户刚选的那一行。");
        CollectionAssert.AreEqual(new[] { PageId.NewInstance }, navigation.Visited);
    }


    [TestMethod]
    public void ARowLinkActsOnItsOwnRowWithoutSelectingItFirst()
    {
        // 表格行上的链接把**本行**作为命令参数传进来，所以不必先点选那一行。
        var catalog = new FakeVersionCatalog([Row("1.21.4"), Row("1.20.4", isActive: true)]);
        var navigation = new FakeNavigationService();
        var page = TestViewModels.Versions(catalog, navigation);

        var row = page.FilteredVersions.First(v => v.Id == "1.21.4");

        Assert.IsTrue(page.CreateInstanceCommand.CanExecute(row), "带参数时应当只看那一行，与选中行无关。");
        page.CreateInstanceCommand.Execute(row);

        Assert.AreEqual("1.21.4", catalog.WrittenActiveVersionId);
        CollectionAssert.AreEqual(new[] { PageId.NewInstance }, navigation.Visited);
    }


    [TestMethod]
    public async Task TheSelectedRowSurvivesARefreshThatReplacesTheRows()
    {
        // 刷新会把行换成新对象，绑定在 SelectedItem 上的选中行会因此丢掉 —— 按 id 找回来。
        var catalog = new FakeVersionCatalog([Row("1.20.4", isActive: true)])
        {
            RowsAfterRefresh = [Row("1.21.4"), Row("1.20.4")],
        };
        var page = NewVersionsPage(catalog);

        page.SelectedVersion = page.FilteredVersions.Single();
        await page.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual(2, page.Versions.Count);
        Assert.IsNotNull(page.SelectedVersion, "刷新后选中行不该凭空消失。");
        Assert.AreEqual("1.20.4", page.SelectedVersion!.Id);
    }

    // ==================== p05 的筛选行（筛选器 + 仅显示 + 两个图标动作） ====================


    [TestMethod]
    public void TheKindFilterShowsOnlyTheChosenKind()
    {
        var page = NewVersionsPage(new FakeVersionCatalog(
        [
            Row("1.21.4"),
            Row("24w03a", kind: VersionKind.Snapshot),
            Row("1.20.4"),
        ]));

        // 默认「正式版」：快照要从「仅显示」里切过去才看得见。
        Assert.IsTrue(page.IsShowingReleases);
        Assert.AreEqual(2, page.FilteredVersions.Count);

        page.ShowSnapshotsCommand.Execute(null);

        Assert.IsTrue(page.IsShowingSnapshots);
        Assert.IsFalse(page.IsShowingAll);
        Assert.AreEqual("24w03a", page.FilteredVersions.Single().Id);

        page.ShowReleasesCommand.Execute(null);

        Assert.IsTrue(page.IsShowingReleases);
        CollectionAssert.AreEqual(new[] { "1.21.4", "1.20.4" }, page.FilteredVersions.Select(v => v.Id).ToArray());

        page.ShowAllCommand.Execute(null);
        Assert.AreEqual(3, page.FilteredVersions.Count);
    }


    [TestMethod]
    public void TheSearchBoxFiltersByVersionIdIgnoringCase()
    {
        var page = NewVersionsPage(new FakeVersionCatalog([Row("1.21.4"), Row("24w03a", kind: VersionKind.Snapshot), Row("1.20.4")]));
        page.ShowAllCommand.Execute(null);

        page.FilterText = "1.20";

        Assert.AreEqual("1.20.4", page.FilteredVersions.Single().Id);

        // 版本 id 里有大写字母时也要能匹配（用户不会照着大小写敲）。
        page.FilterText = "W03";
        Assert.AreEqual("24w03a", page.FilteredVersions.Single().Id);

        page.FilterText = string.Empty;
        Assert.AreEqual(3, page.FilteredVersions.Count);
    }


    [TestMethod]
    public void FilteringOutTheSelectedRowClearsTheSelection()
    {
        // 否则动作按钮会**为一个用户已经看不见的行**保持可用，点下去等于用了看不到的版本。
        var page = NewVersionsPage(new FakeVersionCatalog([Row("1.21.4"), Row("24w03a", kind: VersionKind.Snapshot)]));

        page.SelectedVersion = page.FilteredVersions.First(v => v.Id == "1.21.4");
        Assert.IsTrue(page.CreateInstanceCommand.CanExecute(null));

        page.ShowSnapshotsCommand.Execute(null);

        Assert.IsNull(page.SelectedVersion, "被筛掉的行不该继续算作选中。");
        Assert.IsFalse(page.CreateInstanceCommand.CanExecute(null));
    }


    [TestMethod]
    public void ResetFilterClearsBothTheTextAndTheKind()
    {
        var page = NewVersionsPage(new FakeVersionCatalog([Row("1.21.4"), Row("24w03a", kind: VersionKind.Snapshot)]));

        page.ShowSnapshotsCommand.Execute(null);
        page.FilterText = "zzz";
        Assert.AreEqual(0, page.FilteredVersions.Count);

        page.ResetFilterCommand.Execute(null);

        Assert.IsTrue(page.IsShowingReleases);
        Assert.AreEqual(string.Empty, page.FilterText);
        Assert.AreEqual(1, page.FilteredVersions.Count);
    }


    [TestMethod]
    public void TheTableShowsTheFilteredRowsNotTheRawSnapshot()
    {
        // 「共 N 个版本」是**总数**（设计稿的副标题就是总数），筛选只改表里显示哪些行 —— 两件事不能混。
        var catalog = new FakeVersionCatalog([Row("1.21.4"), Row("24w03a", kind: VersionKind.Snapshot)]);
        var page = NewVersionsPage(catalog);

        page.ShowReleasesCommand.Execute(null);

        Assert.AreEqual(1, page.FilteredVersions.Count);
        Assert.AreEqual(2, page.Versions.Count, "原快照不受筛选影响。");
        StringAssert.Contains(page.SectionSubtitle, "2", "副标题里的总数仍是总数。");
    }

    /// <summary>p05 的构造样板（两个依赖里导航替身是绝大多数用例都一样的）。</summary>
    private sealed class VersionsPageBuilder(FakeVersionCatalog catalog, FakeNavigationService navigation)
    {
        /// <summary>造一个 p05 页面。</summary>
        /// <returns>页面 ViewModel。</returns>
        public VersionsViewModel Build() => TestViewModels.Versions(catalog, navigation);
    }

    private static VersionsViewModel NewVersionsPage(FakeVersionCatalog catalog) =>
        TestViewModels.Versions(catalog, new FakeNavigationService());

    private static NewInstanceViewModel NewWizard(
        FakeVersionCatalog catalog,
        FakeInstanceCatalog? instances = null,
        FakeNavigationService? navigation = null,
        FakeDownloadCatalog? downloads = null) =>
        TestViewModels.NewInstance(
            catalog,
            instances ?? new FakeInstanceCatalog(),
            navigation ?? new FakeNavigationService(),
            downloads ?? new FakeDownloadCatalog());
}
