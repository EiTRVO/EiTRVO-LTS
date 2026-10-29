using System.IO;
using System.Windows;
using System.Windows.Threading;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.App.Views;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Installer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EiTRVO.Core.App;

/// <summary>
/// 应用程序入口，也是本仓库**唯一**的组合根（AGENTS.md §2.2）。
/// <para>
/// 所有「接口 → 实现」的绑定、所有 ViewModel 的构造参数都只在这里被决定；
/// 其它任何地方都不允许 <c>new</c> 服务实现，也不允许持有静态的 <c>IServiceProvider</c>。
/// 页面解析用的是显式 switch，而不是把容器当定位器。
/// </para>
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private CrashHandler? _crashHandler;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // F-10：全局异常处理必须**在**构建容器之前装上 —— 容器本身构建失败也是一种崩溃，
        // 那一刻还没有诊断服务可用，兜底逻辑因此不依赖容器。
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 2026-10-20：数据目录改到启动器目录；首次启动把旧 %APPDATA% 数据只复制过来（不删除旧数据）。
        PortableDataMigration.TryMigrate();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // WPF 的当前目录不一定是 exe 所在目录，配置文件的基准必须显式给出。
            ContentRootPath = AppContext.BaseDirectory,
        });

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(settingsPath))
        {
            builder.Configuration.AddJsonFile(settingsPath, optional: false, reloadOnChange: false);
        }
        else
        {
            // 单文件自包含 exe 可以只带一个文件；配置作为嵌入资源随 exe 发布。
            using var embedded = typeof(App).Assembly.GetManifestResourceStream("EiTRVO.Core.App.appsettings.json")
                                 ?? throw new InvalidOperationException("单文件启动器缺少内嵌的 appsettings.json。");
            builder.Configuration.AddJsonStream(embedded);
        }

        // 注册清单在 AppServices 里（列表是纯数据，可以脱离 WPF 启动过程单测）；
        // 页面解析留在本文件，理由见下面 ResolvePage 的说明，所以它的注册也留在这里。
        AppServices.Configure(builder.Services, builder.Configuration);
        builder.Services.AddSingleton(ResolvePage);

        _host = builder.Build();
        _crashHandler = new CrashHandler(
            _host.Services.GetRequiredService<IDiagnosticsLog>(),
            _host.Services.GetRequiredService<ILauncherPaths>());

        // 2026-10-26：账户文件迁到 Documents\EiTRVO LTS（MSIX 模式）。
        // 只在新位置没有文件时从旧包目录复制，旧文件保留，迁移失败不阻断启动。
        if (PackagedDataRoot.ResolveAccountsDirectory() is not null)
        {
            PackagedDataRoot.MigrateLegacyAccountFile(
                _host.Services.GetRequiredService<ILauncherPaths>().AccountsFile);
        }

        // 启动语言：设置目录里存过的优先（用户在「Java 与设置」页选过并保存的），
        // 其次 appsettings.json 的部署默认值，最后简体中文。见 LocalizationStartup。
        Loc.Service.SetLanguage(LocalizationStartup.Resolve(
            _host.Services.GetRequiredService<ISettingsCatalog>(),
            _host.Services.GetRequiredService<IConfiguration>()["Language"]));
        _host.Start();

        // 2026-10-23：把旧布局（共享版本目录 + 实例目录）整理成"一个实例一份自包含版本"。
        // 每个实例的失败都在 MigrateExistingInstances 内部吞掉，这里只兜底整体异常，绝不影响启动。
        try
        {
            var gameDirectory = _host.Services.GetRequiredService<ISettingsCatalog>().GetGlobal().GameDirectory;
            var paths = _host.Services.GetRequiredService<ILauncherPaths>();
            InstalledVersionMirror.MigrateExistingInstances(gameDirectory, paths.VersionCacheDirectory);
        }
        catch (Exception exception)
        {
            _host.Services.GetRequiredService<IDiagnosticsLog>().Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Launcher,
                "整理实例版本目录失败（{0}）",
                [exception.Message]);
        }

        // DataContext 在这里赋值，而不是在 MainWindow 的 code-behind 里 ——
        // 这样 View 的 code-behind 可以保持只有 InitializeComponent()。
        var window = new MainWindow
        {
            DataContext = _host.Services.GetRequiredService<ShellViewModel>(),
        };

        this.MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }

    /// <summary>
    /// UI 线程上的未处理异常：记录后**不让进程直接消失** —— 用户至少能看到发生了什么、日志在哪。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var report = Capture(e.Exception, nameof(OnDispatcherUnhandledException));

        // 已记录并已告知用户，再让 WPF 把它升级成进程级崩溃没有意义。
        e.Handled = true;

        if (report is not null)
        {
            ShowCrash(report);
        }
    }

    /// <summary>后台线程上的未处理异常：CLR 随后就会结束进程，这里只来得及记录。</summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.ExceptionObject is Exception exception)
        {
            // 进程即将退出，弹窗没有意义（窗口可能正在拆），但日志必须落下。
            _ = Capture(exception, nameof(OnUnhandledException));
        }
    }

    /// <summary>未被观察的 Task 异常：记录即可，不建议为此打断用户。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _ = Capture(e.Exception, nameof(OnUnobservedTaskException));
        e.SetObserved();
    }

    /// <summary>
    /// 记录崩溃。容器还没建起来时（例如配置读不出来）退化成"只弹窗、不写日志"。
    /// </summary>
    private CrashReport? Capture(Exception exception, string origin)
    {
        if (_crashHandler is null)
        {
            return null;
        }

        try
        {
            return _crashHandler.Capture(exception, origin);
        }
        catch (Exception handlerFailure) when (handlerFailure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ShowCrash(CrashReport report) =>
        MessageBox.Show(report.Detail, report.Title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>
    /// 页面标识 → ViewModel。刻意写成显式 switch：一眼能看完「这个应用到底有哪些页面」，
    /// 也不会出现「随便注入一个 IServiceProvider 到处解析」的隐式依赖。
    /// <para>
    /// 留在 <c>App.xaml.cs</c> 而不是搬进 <see cref="AppServices"/> 是有意的：
    /// 这里要用 <c>GetRequiredService</c>，而 <c>tools\audit-mvvm.ps1</c> 的
    /// "组合根之外不得出现服务定位器"这条检查只豁免本文件 —— 搬走就等于为了让检查通过而放宽检查。
    /// </para>
    /// <para>
    /// <c>internal</c> 而不是 <c>private</c>：<c>CompositionTests</c> 要按**真实的**这张表
    /// 逐个解析页面，否则"注册清单能解析"这件事就只能靠人肉点开每一个页面来验证。
    /// </para>
    /// </summary>
    internal static Func<PageId, PageViewModelBase> ResolvePage(IServiceProvider provider) => page => page switch
    {
        PageId.Dashboard => provider.GetRequiredService<DashboardViewModel>(),
        PageId.Instances => provider.GetRequiredService<InstancesViewModel>(),
        PageId.InstanceDetail => provider.GetRequiredService<InstanceDetailViewModel>(),
        PageId.NewInstance => provider.GetRequiredService<NewInstanceViewModel>(),
        PageId.Versions => provider.GetRequiredService<VersionsViewModel>(),
        PageId.Downloads => provider.GetRequiredService<DownloadsViewModel>(),
        PageId.Mods => provider.GetRequiredService<ModsViewModel>(),
        PageId.Modpacks => provider.GetRequiredService<ModpacksViewModel>(),
        PageId.Resources => provider.GetRequiredService<ResourcesViewModel>(),
        PageId.JavaSettings => provider.GetRequiredService<JavaSettingsViewModel>(),
        PageId.Accounts => provider.GetRequiredService<AccountsViewModel>(),
        PageId.About => provider.GetRequiredService<AboutViewModel>(),
        PageId.DeleteInstance => provider.GetRequiredService<DeleteInstanceViewModel>(),
        PageId.Launching => provider.GetRequiredService<LaunchingViewModel>(),
        PageId.AccountLogin => provider.GetRequiredService<AccountLoginViewModel>(),
        PageId.InstanceSettings => provider.GetRequiredService<InstanceSettingsViewModel>(),
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "未注册的页面。"),
    };
}
