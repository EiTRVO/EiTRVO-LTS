using System.IO;
using System.Reflection;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 首次运行：一个实例都没有时的界面行为。
/// <para>
/// 接了真实实例目录（M1）之后，"零实例"取代了演示数据里那 6 个实例，成为**正常**的首屏状态。
/// 这条用例是真实的（用真实的 <see cref="FileInstanceCatalog"/> 指着空的临时目录），
/// 因为要防的正是"第一次打开就崩"这类只有真跑才暴露的问题：p03 / p13 / p17 的
/// ViewModel 会在构造与面包屑里直接读当前实例的详情，而面包屑是外壳导航时同步取的 —— 
/// 那里抛异常会让整个窗口打不开。
/// </para>
/// </summary>
[TestClass]
public sealed class EmptyFirstRunTests
{
    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private FileSettingsCatalog _settings = null!;
    private FileInstanceCatalog _instances = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一个全新的空数据目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-firstrun", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
        _diagnostics = new RecordingDiagnosticsLog();
        _settings = new FileSettingsCatalog(_paths, _diagnostics);
        _settings.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = Path.Combine(_tempRoot, "game"),
        });
        _diagnostics.Entries.Clear();
        _instances = new FileInstanceCatalog(_paths, _settings, new SystemClock(), _diagnostics);
    }

    /// <summary>清理临时目录。</summary>
    [TestMethod]
    public void TheFirstRunChecklistExplainsInstancesAccountsAndJava()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Accounts.Clear();

        var page = new DashboardViewModel(
            _instances,
            new FakeSystemInfoProvider(),
            new SampleEventLog(),
            new FakeNavigationService(),
            accounts,
            new FakeJavaRuntimeCatalog());

        page.OnActivated();

        CollectionAssert.AreEqual(
            new[] { "还没有实例", "还没有账户", "没有检测到 Java" },
            page.FirstRunIssues.Select(static issue => issue.Title).ToArray());
        Assert.AreEqual(
            "您需要登录一个有效的微软账号，否则启动器部分功能将受限",
            page.FirstRunIssues[1].Detail);
        Assert.IsTrue(page.HasFirstRunIssues);
    }

    [TestMethod]
    public void WithoutAJavaDetectionResultTheChecklistDoesNotClaimJavaIsMissing()
    {
        var accounts = new FakeAccountCatalog();
        var page = new DashboardViewModel(
            _instances,
            new FakeSystemInfoProvider(),
            new SampleEventLog(),
            new FakeNavigationService(),
            accounts,
            new FakeJavaRuntimeCatalog(hasDetectionResult: false));

        page.OnActivated();

        Assert.AreEqual(1, page.FirstRunIssues.Count, "没有检测结果时只列「没有实例」，不能凭空说缺 Java。");
        Assert.AreEqual("还没有实例", page.FirstRunIssues[0].Title);
    }

    [TestMethod]
    public void FirstRunActionsNavigateToThePageThatSolvesThem()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Accounts.Clear();
        var navigation = new FakeNavigationService();
        var page = new DashboardViewModel(
            _instances,
            new FakeSystemInfoProvider(),
            new SampleEventLog(),
            navigation,
            accounts,
            new FakeJavaRuntimeCatalog());

        page.OnActivated();
        page.FirstRunIssues[1].Command.Execute(null);

        CollectionAssert.Contains(navigation.Visited, PageId.Accounts);
    }
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public void TheInstanceListIsEmptyAndSaysSo()
    {
        var page = TestViewModels.Instances(instances: _instances);

        Assert.AreEqual(0, page.Instances.Count);
        Assert.AreEqual("共 0 个实例", page.SectionSubtitle);
        Assert.IsNull(page.SelectedInstance);
    }

    [TestMethod]
    public void TheInstanceDetailPageOpensWithPlaceholderValues()
    {
        var page = new InstanceDetailViewModel(_instances, new SampleEventLog(), new FakeShellService());

        Assert.IsFalse(page.HasInstance);
        Assert.AreEqual("（没有实例）", page.Detail.Name);
        Assert.AreEqual("用于 （没有实例）", page.SectionSubtitle);
        Assert.AreEqual(0, page.Actions.Count);
        Assert.AreEqual(0, page.Events.Count);
        Assert.AreEqual(0, page.EventTotal);

        // 面包屑由外壳在导航时同步求值 —— 这里取一次就等价于"导航到这一页不崩"。
        Assert.AreEqual(2, page.Breadcrumb.Count);
        Assert.AreEqual("（没有实例）", page.Breadcrumb[1].Text);
    }

    [TestMethod]
    public void OpeningTheGameFolderWithoutAnInstanceDoesNothing()
    {
        var shell = new FakeShellService();
        var page = new InstanceDetailViewModel(_instances, new SampleEventLog(), shell);

        page.OpenGameFolderCommand.Execute(null);

        Assert.AreEqual(0, shell.Folders.Count, "没有实例时不该把空路径交给资源管理器。");
    }

    [TestMethod]
    public void TheDeletePageAndItsDialogOpenWithoutAnInstance()
    {
        var navigation = new FakeNavigationService();
        var dialog = new DeleteConfirmDialogViewModel(_instances, navigation);
        var page = new DeleteInstanceViewModel(_instances, new SampleEventLog(), new FakeShellService(), dialog);

        Assert.IsFalse(page.HasInstance);
        Assert.AreEqual("（没有实例）", page.Detail.Name);
        Assert.AreEqual(0, page.Actions.Count);
        Assert.AreEqual(0, page.Events.Count);

        // 确认删除只离开本页：空实例名不该被送到内核去删。
        dialog.ConfirmCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { PageId.Instances }, navigation.Visited);
    }

    [TestMethod]
    public void TheInstanceSettingsPageInheritsTheGlobalDefaultsWithoutAnInstance()
    {
        _settings.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = Path.Combine(_tempRoot, "game"),
            MaxMemoryMb = 2048,
        });
        var page = TestViewModels.InstanceSettings(_settings, _instances, new SampleEventLog());

        Assert.IsFalse(page.HasInstance);
        Assert.AreEqual("（没有实例）", page.Detail.Name);
        Assert.AreEqual(2048, page.MaxMemoryMb, "没有实例时草稿值应当是全局默认。");
        Assert.AreEqual(0, page.ChangeEvents.Count);
        Assert.IsFalse(page.IsDirty);
    }

    [TestMethod]
    public void TheDashboardSaysThereIsNoInstanceInsteadOfCrashing()
    {
        // 仪表板是**启动页**：零实例时它一定会被渲染。
        // 实测过的崩溃：SectionSubtitle → Detail → GetDetail("") 抛 KeyNotFoundException，
        // 新用户第一次打开就崩在窗口初始化上。
        var page = new DashboardViewModel(
            _instances,
            new FakeSystemInfoProvider(),
            new SampleEventLog(),
            new FakeNavigationService(),
            new FakeAccountCatalog(),
            new FakeJavaRuntimeCatalog());

        Assert.IsFalse(page.HasInstance);
        Assert.AreEqual("（没有实例）", page.Detail.Name);
        Assert.AreEqual("用于 （没有实例）", page.SectionSubtitle);
        Assert.AreEqual(0, page.Overview.Count);
    }

    [TestMethod]
    public void TheLaunchingPageSaysThereIsNoInstanceInsteadOfCrashing()
    {
        // p14 的 SectionSubtitle 与**面包屑**都读 Detail，而面包屑是外壳导航时同步求值的 ——
        // 这里少一个判断，崩的是导航过程本身（整个窗口）。
        var page = TestViewModels.Launching(instances: _instances);

        Assert.AreEqual("（没有实例）", page.Detail.Name);
        Assert.AreEqual("（没有实例）", page.SectionSubtitle);

        // NestedCrumb 的第 0 项是上一级（实例名），第 1 项是当前页标题 ——
        // 崩点就在第 0 项：它要先取 Detail.Name。
        Assert.AreEqual(2, page.Breadcrumb.Count);
        Assert.AreEqual("（没有实例）", page.Breadcrumb[0].Text);
        Assert.AreEqual("正在启动", page.Breadcrumb[1].Text);
    }

    [TestMethod]
    public void TheDeleteDialogShowsZerosInsteadOfCrashingWithoutAnInstance()
    {
        var dialog = new DeleteConfirmDialogViewModel(_instances, new FakeNavigationService());

        Assert.IsFalse(dialog.HasInstance);
        Assert.AreEqual("0 B", dialog.SizeOnDisk);
        Assert.AreEqual(0, dialog.ModCount);
        Assert.AreEqual(0, dialog.SaveCount);
        Assert.AreEqual("该实例占用 0 B，包含 0 个模组与 0 个存档。", dialog.Summary);
    }

    [TestMethod]
    public void TheFolderButtonsDoNothingWithoutAnInstance()
    {
        // 这两个按钮直接拿实例路径去开资源管理器：零实例时若不拦住，
        // 交给系统的是一个空路径（p03 早就有这个规则，p07/p09 当时漏了）。
        var shell = new FakeShellService();
        var mods = new ModsViewModel(
            new FakeContentCatalog(),
            _instances,
            shell,
            new FakeDownloadCatalog(),
            new FakeNavigationService());
        var resources = new ResourcesViewModel(
            new FakeContentCatalog(),
            _instances,
            shell,
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        Assert.IsFalse(mods.HasInstance);
        Assert.IsFalse(resources.HasInstance);

        mods.OpenModsFolderCommand.Execute(null);
        resources.OpenResourcePacksFolderCommand.Execute(null);

        Assert.AreEqual(0, shell.Folders.Count, "没有实例时不该把空路径交给资源管理器。");
    }

    /// <summary>
    /// **每一个页面与对话框的每一个绑定属性**都要能在"零实例"下读出来。
    /// <para>
    /// 这条用例才是这次崩溃真正需要的门禁：上面那些逐页用例只能覆盖"我想到了的那些页面"，
    /// 而实际漏掉的正是没被想到的 p01 与 p14。这里用**真实的组合根、真实的空数据目录、
    /// 真实的页面表**（<see cref="App.ResolvePage"/>）把属性逐个反射读一遍 ——
    /// 属性 getter 抛异常在绑定里就是崩溃，而且导航时取面包屑/副标题还会同步触发。
    /// </para>
    /// </summary>
    [TestMethod]
    public void EveryPageAndDialogSurvivesAnEmptyDataDirectory()
    {
        using var provider = BuildProvider();

        var resolve = provider.GetRequiredService<Func<PageId, PageViewModelBase>>();
        var failures = new List<string>();
        var read = 0;

        var pages = Enum.GetValues<PageId>();
        foreach (var page in pages)
        {
            var result = ReadEveryProperty(resolve(page), "页面 " + page);
            read += result.Read;
            failures.AddRange(result.Failures);
        }

        foreach (var (dialog, label) in new (object, string)[]
        {
            (provider.GetRequiredService<DeleteConfirmDialogViewModel>(), "对话框 删除确认"),
            (provider.GetRequiredService<AddAccountDialogViewModel>(), "对话框 添加账户"),
        })
        {
            var result = ReadEveryProperty(dialog, label);
            read += result.Read;
            failures.AddRange(result.Failures);
        }

        AssertSweepWasReal(pages.Length, read);
        Assert.AreEqual(
            0,
            failures.Count,
            "零实例（首次运行）下这些属性读不出来：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// 同一套页面在"有实例"时也必须都能读 —— 反向的一半：
    /// 修零实例路径时最容易的反效果就是把有实例的路径写坏。
    /// </summary>
    [TestMethod]
    public void EveryPageAndDialogSurvivesHavingExactlyOneInstance()
    {
        using var provider = BuildProvider();

        var instances = provider.GetRequiredService<IInstanceCatalog>();
        var name = InstanceNaming.SuggestUniqueName("1.20.4", LoaderKind.Vanilla);
        instances.CreateInstance(new InstanceCreationRequest(name, "1.20.4", LoaderKind.Vanilla, null));

        var resolve = provider.GetRequiredService<Func<PageId, PageViewModelBase>>();
        var failures = new List<string>();
        var read = 0;

        var pages = Enum.GetValues<PageId>();
        foreach (var page in pages)
        {
            var result = ReadEveryProperty(resolve(page), "页面 " + page);
            read += result.Read;
            failures.AddRange(result.Failures);
        }

        AssertSweepWasReal(pages.Length, read);
        Assert.AreEqual(
            0,
            failures.Count,
            "有实例时这些属性读不出来：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// 确认这次扫描**真的扫到了东西**。
    /// <para>
    /// 一条永远读不到属性的反射用例会安静地"通过"，那比没有这条用例更糟 ——
    /// 本会话已经栽过两次同类问题（一条 grep 因通配不展开而永远 0 命中、一条用例因桩的规则顺序而其实什么都没测）。
    /// </para>
    /// </summary>
    private static void AssertSweepWasReal(int pageCount, int propertiesRead)
    {
        Assert.AreEqual(16, pageCount, "页面表数量变了：页面数必须与设计稿的 16 页一致。");
        Assert.IsTrue(
            propertiesRead > 150,
            $"只读了 {propertiesRead} 个绑定属性 —— 反射没扫到预期的东西，这条门禁本身失效了。");
    }

    /// <summary>真实的组合根，但数据目录指向本用例的空临时目录。</summary>
    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [DataSourceRegistration.ConfigurationKey] = "true",
                })
            .Build();

        var services = new ServiceCollection();
        AppServices.Configure(services, configuration);
        services.AddSingleton<ILauncherPaths>(new LauncherPaths(_tempRoot));
        services.AddSingleton(App.ResolvePage);

        return services.BuildServiceProvider();
    }

    /// <summary>把对象的所有公开实例属性读一遍，返回"读了多少个"与"哪些读不出来"。</summary>
    private static (int Read, List<string> Failures) ReadEveryProperty(object target, string label)
    {
        var failures = new List<string>();
        var read = 0;

        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            read++;

            try
            {
                _ = property.GetValue(target);
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                failures.Add($"{label}.{property.Name} → {inner.GetType().Name}: {inner.Message}");
            }
        }

        return (read, failures);
    }

    [TestMethod]
    public void CreatingTheFirstInstanceMakesTheContextAvailable()
    {
        var name = InstanceNaming.SuggestUniqueName("1.20.4", LoaderKind.Forge);
        _instances.CreateInstance(new InstanceCreationRequest(name, "1.20.4", LoaderKind.Forge, "49.0.30"));

        var page = new InstanceDetailViewModel(_instances, new SampleEventLog(), new FakeShellService());

        Assert.IsTrue(page.HasInstance);
        Assert.AreEqual("1.20.4-Forge", page.Detail.Name);
        Assert.AreEqual("1.20.4", page.Detail.GameVersion);
        Assert.AreEqual("未安装", TestViewModels.Instances(instances: _instances).Instances.Single().StatusText);
    }
}
