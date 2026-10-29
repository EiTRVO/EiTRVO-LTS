using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Sample;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 外壳 ViewModel 的测试：导航、面包屑、左导航高亮、页头四按钮的状态。
/// 这些都是「页面上看得见的行为」，也正是最容易在改动中被悄悄弄坏的部分。
/// </summary>
[TestClass]
public sealed class ShellViewModelTests
{
    private static (ShellViewModel Shell, FakeNavigationService Navigation) CreateShell()
    {
        var pages = new Dictionary<PageId, PageViewModelBase>();
        var instances = new SampleInstanceCatalog();
        var versionCatalog = new SampleVersionCatalog();
        var content = new FakeContentCatalog();
        var downloads = new SampleDownloadCatalog();
        var events = new SampleEventLog();
        var settings = new SampleSettingsCatalog();
        var systemInfo = new FakeSystemInfoProvider();
        var javaRuntimes = new FakeJavaRuntimeCatalog();
        var launcherInfo = new SampleLauncherInfo();
        var accounts = new SampleAccountCatalog();
        var deviceCode = new FakeDeviceCodeFlow();
        var shellService = new FakeShellService();
        var throttle = new ImmediateRefreshThrottle();

        var navigation = new FakeNavigationService(page => pages[page]);

        pages[PageId.Dashboard] = new DashboardViewModel(instances, systemInfo, events, navigation, accounts, javaRuntimes);
        pages[PageId.Instances] = TestViewModels.Instances(instances: instances);
        pages[PageId.InstanceDetail] = new InstanceDetailViewModel(instances, events, shellService);
        pages[PageId.NewInstance] = TestViewModels.NewInstance(versionCatalog, instances, navigation, downloads, javaRuntimes);
        pages[PageId.Versions] = TestViewModels.Versions(versionCatalog, navigation, javaRuntimes);
        pages[PageId.Downloads] = new DownloadsViewModel(downloads, events, throttle);
        pages[PageId.Mods] = new ModsViewModel(
            content,
            instances,
            shellService,
            new FakeDownloadCatalog(),
            new FakeNavigationService());
        pages[PageId.Modpacks] = new ModpacksViewModel(content, new FakeDownloadCatalog(), new FakeDialogService(), new FakeNavigationService(), new SampleVersionCatalog(), new FakeInstanceCatalog());
        pages[PageId.Resources] = new ResourcesViewModel(
            content,
            instances,
            shellService,
            new FakeDownloadCatalog(),
            new FakeNavigationService());
        pages[PageId.JavaSettings] = TestViewModels.JavaSettings(settings, java: javaRuntimes, systemInfo: systemInfo);
        pages[PageId.Accounts] = TestViewModels.Accounts(accounts);
        pages[PageId.About] = new AboutViewModel(launcherInfo, new AuthOptions { ClientId = "11111111-2222-3333-4444-555555555555" }, shellService);
        pages[PageId.AccountLogin] = new AccountLoginViewModel(
            accounts,
            new ViewModels.Dialogs.AddAccountDialogViewModel(
                new FakeMicrosoftSignIn(),
                deviceCode,
                shellService,
                navigation),
            navigation);
        pages[PageId.InstanceSettings] = TestViewModels.InstanceSettings(settings, instances, events, javaRuntimes, systemInfo);
        pages[PageId.DeleteInstance] = new DeleteInstanceViewModel(instances, events, shellService, new ViewModels.Dialogs.DeleteConfirmDialogViewModel(instances, navigation));
        pages[PageId.Launching] = TestViewModels.Launching(instances: instances);

        var shell = new ShellViewModel(navigation, accounts, downloads, Loc.Service, throttle, new FakeHelpWindowService(), new FakeEventSearchWindowService());
        return (shell, navigation);
    }

    [TestMethod]
    public void Shell_StartsOnTheDashboard()
    {
        var (shell, _) = CreateShell();

        Assert.AreEqual(PageId.Dashboard, shell.CurrentPage.Id);
        Assert.AreEqual("仪表板", shell.CurrentPage.Title);
    }

    [TestMethod]
    public void Shell_HasTenNavigationItemsInThreeGroups()
    {
        var (shell, _) = CreateShell();

        Assert.AreEqual(3, shell.NavigationGroups.Count);
        Assert.AreEqual(10, shell.NavigationGroups.Sum(static g => g.Items.Count));
        Assert.IsFalse(shell.NavigationGroups[0].HasTitle, "第一组不带分组标题。");
        Assert.AreEqual("资源与配置", shell.NavigationGroups[1].Title);
        Assert.AreEqual("其他", shell.NavigationGroups[2].Title);
    }

    [TestMethod]
    public void Shell_HighlightsExactlyTheCurrentDomain()
    {
        var (shell, _) = CreateShell();

        shell.NavigateCommand.Execute(PageId.InstanceDetail);

        var active = shell.NavigationGroups
            .SelectMany(static g => g.Items)
            .Where(static i => i.IsActive)
            .Select(static i => i.Key)
            .ToArray();

        // 实例详情不是导航项，但它属于「本地实例」这个分域。
        Assert.AreEqual(1, active.Length);
        Assert.AreEqual(NavKey.Instances, active[0]);
    }

    [TestMethod]
    public void Shell_LeavesNoNavigationHighlightedForInstanceSettings()
    {
        var (shell, _) = CreateShell();

        shell.NavigateCommand.Execute(PageId.InstanceSettings);

        Assert.AreEqual(NavKey.None, shell.CurrentPage.NavKey);
        Assert.AreEqual(0, shell.NavigationGroups.SelectMany(static g => g.Items).Count(static i => i.IsActive));
    }

    [TestMethod]
    public void Breadcrumb_NeverRepeatsTheProductName()
    {
        var (shell, _) = CreateShell();

        foreach (var page in new[] { PageId.Dashboard, PageId.Instances, PageId.About, PageId.InstanceSettings })
        {
            shell.NavigateCommand.Execute(page);
            var parts = shell.Breadcrumb;

            Assert.IsTrue(parts[^1].IsCurrent, "末级必须是当前页（不可点）。");
            Assert.IsFalse(
                parts.Any(static p => p.Text.Contains("EiTRVO Core", StringComparison.Ordinal)),
                "面包屑不应出现产品名根节点（AGENTS.md §0 / §7）。");
        }
    }

    [TestMethod]
    public void Breadcrumb_ShowsNoSeparatorWhenThereIsOnlyOneLevel()
    {
        var (shell, _) = CreateShell();

        Assert.AreEqual(1, shell.Breadcrumb.Count);
        Assert.IsFalse(shell.Breadcrumb[0].ShowSeparator);
    }

    [TestMethod]
    public void Breadcrumb_UsesTheInstanceNameAndAPointableParent()
    {
        var (shell, _) = CreateShell();

        shell.NavigateCommand.Execute(PageId.InstanceDetail);

        Assert.AreEqual(2, shell.Breadcrumb.Count);
        Assert.AreEqual("本地实例", shell.Breadcrumb[0].Text);
        Assert.AreEqual(PageId.Instances, shell.Breadcrumb[0].Target);
        Assert.AreEqual("1.20.4-Forge", shell.Breadcrumb[1].Text);
        Assert.IsFalse(shell.Breadcrumb[0].ShowSeparator);
        Assert.IsTrue(shell.Breadcrumb[1].ShowSeparator);
    }

    [TestMethod]
    public void HeaderTools_ShowTheQueueBadgeAndThePlayerName()
    {
        var (shell, _) = CreateShell();

        Assert.AreEqual("3", shell.QueueBadge);
        Assert.AreEqual("Steve", shell.AccountLabel);
        Assert.AreEqual(PageId.Accounts, shell.AccountTarget);
    }

    [TestMethod]
    public void HeaderTools_DisableTheAccountButtonOnlyWhileTheLoginPageIsOpen()
    {
        var (shell, _) = CreateShell();

        Assert.IsTrue(shell.IsAccountToolEnabled);

        shell.NavigateCommand.Execute(PageId.AccountLogin);
        Assert.IsFalse(shell.IsAccountToolEnabled, "设备代码流进行中要防重复发起（p16 §2）。");

        shell.NavigateCommand.Execute(PageId.Accounts);
        Assert.IsTrue(shell.IsAccountToolEnabled);
    }

    [TestMethod]
    public void HeaderTools_IgnoreNavigationParametersThatAreNotPageIds()
    {
        var (shell, navigation) = CreateShell();
        var before = navigation.Visited.Count;

        shell.NavigateCommand.Execute(null);
        shell.NavigateCommand.Execute("Dashboard");

        Assert.AreEqual(before, navigation.Visited.Count, "非 PageId 参数应被忽略，而不是抛异常。");
    }

    /// <summary>
    /// 页头那道扫光（2026-10-06）只在**真的有任务**时动：空闲时必须静止。
    /// </summary>
    /// <remarks>
    /// 这条不是在测动画（那要看像素），而是测"什么时候该动"这个判断：
    /// 它必须与队列徽标同源，并且队列一变就通知出去 —— 少一次通知，
    /// 表现就是"任务跑完了扫光还在扫"或"任务开始了它不动"，两种都像坏了。
    /// </remarks>
    [TestMethod]
    public void HeaderSweep_FollowsTheRealQueueAndStopsWhenIdle()
    {
        var downloads = new FakeDownloadCatalog();
        var shell = new ShellViewModel(
            new FakeNavigationService(),
            new SampleAccountCatalog(),
            downloads,
            Loc.Service,
            new ImmediateRefreshThrottle(),
            new FakeHelpWindowService(), new FakeEventSearchWindowService());

        Assert.IsFalse(shell.IsBusy, "没有任务时页头必须静止。");

        var changed = new List<string>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        downloads.EnqueueInstall(new InstallRequest("测试实例", "1.20.4", LoaderKind.Vanilla, null));
        downloads.RaiseChanged();

        Assert.IsTrue(shell.IsBusy, "有任务在队里 → 扫光才该动。");
        CollectionAssert.Contains(changed, "IsBusy", "队列变化必须通知页头，否则扫光会停在错的状态上。");
    }
}
