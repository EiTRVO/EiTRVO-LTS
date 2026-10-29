using System.IO;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 组合根的两件事：**注册清单能解析**，以及**接上真实实现的接口确实没有演示分支**。
/// <para>
/// 为什么要单测这个：注册清单的错误（漏注册、构造参数对不上、把演示实现留在了真实接口上）
/// 全都要等到用户点开那个页面才会暴露，而且现场往往只是一个 MessageBox。
/// M2 一口气加了四个内核服务，正是这类错误最容易发生的时候。
/// </para>
/// </summary>
[TestClass]
public sealed class CompositionTests
{
    private string _tempRoot = string.Empty;

    /// <summary>数据目录指向临时目录：清单里的真实实现会读写它。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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

    private ServiceProvider BuildProvider(bool useSampleData)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [DataSourceRegistration.ConfigurationKey] = useSampleData ? "true" : "false",
                })
            .Build();

        var services = new ServiceCollection();
        AppServices.Configure(services, configuration);

        // 组合根在 App.xaml.cs 里另外做的两件事，这里照做：数据目录与页面解析。
        services.AddSingleton<ILauncherPaths>(new LauncherPaths(_tempRoot));
        services.AddSingleton(App.ResolvePage);

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void EveryPageResolvesThroughTheRealPageTable()
    {
        using var provider = BuildProvider(useSampleData: true);
        var resolve = provider.GetRequiredService<Func<PageId, PageViewModelBase>>();

        foreach (var page in Enum.GetValues<PageId>())
        {
            var viewModel = resolve(page);

            Assert.IsNotNull(viewModel, $"{page} 解析不出来。");
            Assert.AreEqual(page, viewModel.Id, "页面表把两个页面接错了。");
        }
    }

    [TestMethod]
    public void TheShellResolvesWithAllItsDependencies()
    {
        using var provider = BuildProvider(useSampleData: true);

        Assert.IsNotNull(provider.GetRequiredService<ShellViewModel>());
    }

    [TestMethod]
    public void TheVersionAndDownloadCatalogsAreTheRealOnesEvenWhileSampleDataIsOn()
    {
        // §7 自检项「接上真实实现的接口已删除对应的演示注册」的可执行版本：
        // 开关开着也不该解析出 Sample* —— 留着两条分支正是最容易发生的事故。
        using var provider = BuildProvider(useSampleData: true);

        Assert.IsInstanceOfType<FileVersionCatalog>(provider.GetRequiredService<IVersionCatalog>());
        Assert.IsInstanceOfType<FileDownloadCatalog>(provider.GetRequiredService<IDownloadCatalog>());

        // M6-a：事件表读的也是真实日志（events.jsonl）—— 它曾经挂在演示数据上，
        // 于是五页「事件」区块显示的是四条编好的假事件。
        Assert.IsInstanceOfType<FileEventLog>(provider.GetRequiredService<IEventLog>());
    }

    [TestMethod]
    public void TheJavaRuntimeAndMachineInfoAreTheRealOnesEvenWhileSampleDataIsOn()
    {
        // M3 接入的两个接口。检测与注册表读取都不会在这里真的发生（构造是纯的），
        // 但"解析出来的是真实实现"必须钉住 —— 演示实现会让 p10 显示四个不存在的 JDK。
        using var provider = BuildProvider(useSampleData: true);

        Assert.IsInstanceOfType<FileJavaRuntimeCatalog>(provider.GetRequiredService<IJavaRuntimeCatalog>());
        Assert.IsInstanceOfType<SystemInfoProvider>(provider.GetRequiredService<ISystemInfoProvider>());

        // Java 检测是单例：状态（最近一次结果）与缓存不能每个页面各来一份。
        Assert.AreSame(
            provider.GetRequiredService<IJavaRuntimeCatalog>(),
            provider.GetRequiredService<IJavaRuntimeCatalog>());
    }

    [TestMethod]
    public void AnEmptyDataDirectoryMeansNoJavaWasScannedYet()
    {
        // 冷启动（缓存目录是空的、也还没跑过检测）时，"已检测"必须是假：
        // 否则 p05 会把每一个已安装版本都说成"缺少 Java"，而用户其实装了 Java。
        using var provider = BuildProvider(useSampleData: true);

        var java = provider.GetRequiredService<IJavaRuntimeCatalog>();

        Assert.IsFalse(java.HasDetectionResult);
        Assert.AreEqual(0, java.GetInstallations().Count);
    }

    [TestMethod]
    public void TheVersionServicesAreSingletonsSoTheCacheIsNotRebuiltPerPage()
    {
        using var provider = BuildProvider(useSampleData: true);

        Assert.AreSame(
            provider.GetRequiredService<VersionManifestClient>(),
            provider.GetRequiredService<VersionManifestClient>());
    }

    [TestMethod]
    public void TurningSampleDataOffStillLeavesTheRealInterfacesWorking()
    {
        using var provider = BuildProvider(useSampleData: false);

        // 真实实现不受开关影响。
        Assert.IsNotNull(provider.GetRequiredService<IVersionCatalog>());
        Assert.IsNotNull(provider.GetRequiredService<IInstanceCatalog>());
        Assert.IsNotNull(provider.GetRequiredService<ISettingsCatalog>());
        Assert.IsNotNull(provider.GetRequiredService<IDownloadCatalog>());

        // M7-a：内容目录也接了真实实现（实例游戏目录扫描），所以它不再受这个开关影响 ——
        // 关掉演示数据之后仍然解析得出来，而且是**真实**那一份（AGENTS.md §7：不留两条分支）。
        Assert.IsInstanceOfType<FileContentCatalog>(provider.GetRequiredService<IContentCatalog>());

        // 现在只剩 ILauncherInfo（「关于」页那几个常量）还挂在演示数据源上：
        // 它一关就当场报错并指出里程碑 —— 不允许安静地继续给假数据。
        var exception = Assert.ThrowsException<NotSupportedException>(
            () => provider.GetRequiredService<ILauncherInfo>());

        StringAssert.Contains(exception.Message, "ILauncherInfo");
    }
}
