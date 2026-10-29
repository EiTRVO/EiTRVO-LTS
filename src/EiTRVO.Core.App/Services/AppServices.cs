using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Content.Modpack;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Versions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 组合根的**服务注册清单**。
/// <para>
/// 组合根仍然是 <c>App.OnStartup</c>（唯一一处构建容器的地方），这里只是把"注册了什么"抽出来 ——
/// 抽出来的理由很实际：注册清单是**纯数据**，可以脱离 WPF 启动过程验证，
/// 于是「某个 ViewModel 依赖的服务忘了注册」这类错误能在单测里当场暴露，
/// 而不是等到用户点开那个页面时才崩（M2 接入真实实现时一口气加了四个服务，正是这种风险最高的时候）。
/// </para>
/// <para>
/// 页面解析（<c>PageId</c> → ViewModel 的 switch）**刻意留在 App.xaml.cs**：
/// 它要用 <c>IServiceProvider.GetRequiredService</c>，而 <c>tools\audit-mvvm.ps1</c> 的
/// "组合根之外不得出现服务定位器"这条检查只豁免 App.xaml.cs —— 把解析搬到这里，
/// 就等于为了让检查通过而放宽检查。
/// </para>
/// </summary>
internal static class AppServices
{
    /// <summary>按配置注册全部服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">配置（<c>appsettings.json</c>）。</param>
    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ---------- 配置 ----------
        // Client ID 必须来自配置，禁止硬编码（AGENTS.md §6 / §7）
        // M0-7：演示数据源开关。关掉之后，未接入真实实现的接口会当场报错并指出里程碑，
        // 而不是安静地继续返回假数据（`appsettings.json` 的 DataSources:UseSampleData）。
        var useSampleData = configuration.GetValue(DataSourceRegistration.ConfigurationKey, defaultValue: true);

        var authOptions = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
                          ?? new AuthOptions();
        services.AddSingleton(authOptions);

        // ---------- 非 UI 层：Model / Service ----------
        // 尚未接入真实实现的接口在这里挂演示数据源；已接入的（设置、实例、版本清单、Java、本机信息）是
        // **无条件**的真实实现，不留两条分支 —— 留着"演示分支"最容易发生的事故是"以为功能已经做好了"。
        // M7-a：内容目录接上真实实现（实例游戏目录下的 mods / resourcepacks / saves 扫描）。
        // **已删除对应的演示注册**（§7 自检项）：p07 模组、p09 资源包/存档读的都是它。
        // M7-b：p08 的「从 Modrinth 浏览」接上真实接口（搜索 + 磁盘缓存；读同步、刷新在页面导航时异步发生）。
        // M7-c：p07 的模组表接上 Modrinth 对账（SHA-1 索引 + 来源/可更新/备注；算哈希在后台线程）。
        services.AddSingleton<ModrinthClient>();
        services.AddSingleton<ModrinthCache>();
        services.AddSingleton<ModrinthCatalog>();
        services.AddSingleton<ModHashIndex>();
        services.AddSingleton<ModrinthModIndex>();
        services.AddSingleton<IContentCatalog, FileContentCatalog>();

        // ---- M3：Java 运行时与本机信息 ----
        // Java 检测要跑若干次 java -version，因此结果落 cache\java-runtimes.json：
        // 冷启动同步读缓存即可填表，真正的检测发生在页面导航时的刷新里。
        services.AddSingleton<IJavaEnvironment, WindowsJavaEnvironment>();
        services.AddSingleton<JavaDiscovery>();
        services.AddSingleton<IJavaProcessProbe, ProcessJavaVersionProbe>();
        // **已删除对应的演示注册**（§7 自检项）：p10 的运行时表、p04/p17 的「所需 Java」都读它。
        services.AddSingleton<IJavaRuntimeCatalog, FileJavaRuntimeCatalog>();

        // 本机信息（p01 属性区、p10 的内存推荐）：注册表 + GlobalMemoryStatusEx。
        services.AddSingleton<ISystemInfoSource, WindowsSystemInfoSource>();
        services.AddSingleton<ISystemInfoProvider, SystemInfoProvider>();

        // ---- M2：版本清单、版本 JSON 与加载器元数据 ----
        // 清单与版本 JSON 都落在 cache\versions\：离线启动时 p05 直接读它，不必等网络超时。
        services.AddSingleton<VersionManifestClient>();
        services.AddSingleton<LoaderMetadataClient>();
        // M4-i：OptiFine 的"版本列表"来自 optifine.net 官方 downloads 页（与 Neo 同一套抓取方式）。
        services.AddSingleton<OptiFineCatalogClient>();
        services.AddSingleton<VersionJsonStore>();
        // 版本目录：清单 + 本地安装状态 + 四个加载器元数据源。**已删除对应的演示注册**（§7 自检项）。
        services.AddSingleton<IVersionCatalog, FileVersionCatalog>();

        // ---- M2：下载与安装 ----
        // 安装任务队列（p06 的队列与进度、p14 的阶段表都读它）。**已删除对应的演示注册**。
        services.AddSingleton<FileDownloader>();
        services.AddSingleton<DiskSpaceChecker>();
        services.AddSingleton<NativeExtractor>();
        services.AddSingleton<VanillaInstaller>();
        // M4-f：Fabric / Quilt 的 profile 客户端与安装器。
        services.AddSingleton<LoaderProfileClient>();
        services.AddSingleton<LoaderInstaller>();
        // M4-g：Forge / NeoForge —— 跑官方安装器（工具运行器与游戏进程运行器刻意分开）。
        services.AddSingleton<IToolRunner, ToolProcessRunner>();
        services.AddSingleton<ForgeInstaller>();
        // M4-i：OptiFine —— 沙箱里跑它自己的官方安装器（`optifine.Installer`，headless），再搬运产物。
        services.AddSingleton<OptiFineInstaller>();
        // §12.7：Mojang 官方 Java 运行时（p10 的「获取 Java 25」与「缺失时自动获取」都走它）。
        services.AddSingleton<JavaRuntimeManifestClient>();
        services.AddSingleton<JavaRuntimeInstaller>();
        // M7-d：Modrinth 内容下载（p07 的「更新所选」：依赖递归 + 更新替换）。
        services.AddSingleton<ContentInstaller>();
        // 2026-10-24：Forge→OptiFine、Fabric→Fabric API 的可选组件。
        services.AddSingleton<OptionalComponentInstaller>();
        // M7-e-2：整合包（p08 的「从文件导入 .mrpack」：安全扫描 → 建实例 → 装游戏与加载器 → 铺内容）。
        // 扫描器与下载器共用**同一个**出站白名单实例：两把尺子迟早量出两个答案。
        services.AddSingleton(HttpHostPolicy.Default);
        services.AddSingleton<ArchiveSafetyScanner>();
        services.AddSingleton<MrpackInstaller>();
        // 2026-10-24：p09「添加文件」——本地资源包导入（zip slip / 压缩炸弹扫描后落到 resourcepacks）。
        services.AddSingleton<IResourcePackImporter, ResourcePackImporter>();
        // M7-f：存档备份 / 恢复（p09 的「备份所选 / 恢复所选 / 备份全部存档」：压缩 → 复核 → 换目录）。
        services.AddSingleton<WorldBackupInstaller>();
        services.AddSingleton<IDownloadCatalog, FileDownloadCatalog>();

        // ---- M1：本地状态与持久化 ----
        // 诊断事件（F-10 的崩溃日志与后面事件表读的是同一个文件）。
        services.AddSingleton<IDiagnosticsLog, FileDiagnosticsLog>();
        // M6-a：事件表读的就是上面那份 events.jsonl —— 句子在这一层查 .resx 拼（Q8）。
        services.AddSingleton<IEventLog, FileEventLog>();
        // 设置：settings.json（全局）+ instances/<实例>/overrides.json（逐实例覆盖）。
        services.AddSingleton<ISettingsCatalog, FileSettingsCatalog>();
        // 实例：instances/ 下的真实目录（instance.json），新建 / 删除进回收目录。
        services.AddSingleton<IInstanceCatalog, FileInstanceCatalog>();
        // p03「校验文件」：按实例版本 JSON 的 SHA-1 校验客户端 JAR 与库。
        services.AddSingleton<IInstanceFileVerifier, InstanceFileVerifier>();
        // 账户：accounts.json（DPAPI 密文）。真实实现从 M1 起就在，只是要等 M5 才能登录写入。
        services.AddSingleton<AccountStore>();
        // M5-1：真实的认证链（设备代码流 → XBL → XSTS → Minecraft 服务 → 档案）。
        // **已删除对应的演示注册**（§7 自检项）：p15 的对话框与 p11 的刷新都走它。
        // 注意只注册接口：注册两遍再让其中一个转调另一个，会让这份清单里出现服务定位器
        // （audit-mvvm 第 5 条检查专门盯这个，与下面两个 handler 的注册同一个道理）。
        services.AddSingleton<IDeviceCodeClient, DeviceCodeClient>();
        services.AddSingleton<IDeviceCodeFlow, DeviceCodeFlow>();
        services.AddSingleton<IMinecraftAuthChain, MinecraftAuthChain>();
        services.AddSingleton<IAccountCatalog, FileAccountCatalog>();
        services.AddSingleton<IAccountDownloadPolicy, AccountDownloadPolicy>();
        services.AddSingleton<IMicrosoftSignIn, MicrosoftSignIn>();
        // M5 的 D-9：皮肤上传 / 换模型 / 恢复默认（官方接口 + 本地 PNG 校验 + 冷却）。
        services.AddSingleton<ISkinService, MinecraftSkinService>();

        // M0 的存储地基：数据目录布局与时间源。真实实现（M1 起）通过它们落盘，
        // 单测则把 ILauncherPaths 指到临时目录 —— 这也是它们存在的理由。
        // 便携运行：exe 旁边；MSIX：包内 LocalState\EiTRVO LTS（安装目录只读）。
        services.AddSingleton<ILauncherPaths>(_ => new LauncherPaths(
            PackagedDataRoot.Resolve(),
            PackagedDataRoot.ResolveJavaRuntimeDirectory(),
            PackagedDataRoot.ResolveAccountsDirectory()));
        services.AddSingleton<IClock, SystemClock>();
        DataSourceRegistration.AddCatalog<ILauncherInfo, SampleLauncherInfo>(services, useSampleData, "M0（常量即可，无需真实实现）");

        // ---- M4：启动链（参数构建 → 日志脱敏 → 进程生命周期 → 编排）----
        // Client ID 来自配置（AGENTS.md §6），与 AuthOptions 一样按实例注册 —— 不用工厂委托，
        // 「组合根之外不得出现服务定位器」这条门禁（audit-mvvm 第 5 条）就是为它立的。
        services.AddSingleton(new LaunchOptions { ClientId = authOptions.ClientId });
        services.AddSingleton<IGameProcessRunner, GameProcessRunner>();
        // 凭据由界面层提供：内核不引用认证层（Auth → Launcher 的单向依赖）。
        services.AddSingleton<ILaunchIdentityProvider, AccountLaunchIdentityProvider>();
        services.AddSingleton<IGameLauncher, GameLauncher>();

        // ---------- View 层服务 ----------
        services.AddSingleton<IShellService, ShellService>();

        // 界面语言：全进程共用一个实例。这里交出去的是 Loc 自己持有的那一个 ——
        // XAML 的 {loc:Text} 绑定在解析期就抓住了它，换成新实例会让语言切换失效。
        services.AddSingleton<ILocalizationService>(static _ => Loc.Service);

        // ---- 出站请求策略（F-8）----
        // 内核服务构造注入 IHttpClientFactory 并用这个命名客户端；白名单与重试挂在管道上，
        // 因此没有任何一条出站请求能绕过它们（版本清单里的 URL 来自远端，不能靠调用方自觉）。
        // "没有任何一条"包含**重定向**：底层处理器的自动重定向必须关掉，否则它发生在管道之后，
        // 白名单只挡得住第一跳。关掉后由 AllowedHostHandler 逐跳跟随并逐跳校验。
        services.AddSingleton(HttpHostPolicy.Default);
        services.AddSingleton(RetryPolicy.Default);

        // 两个 handler 用**类型化**注册，而不是在管道里手写 provider.GetRequiredService ——
        // 后者会让这份注册清单里出现服务定位器，正好撞上 tools\audit-mvvm.ps1 的第 5 条检查
        // （组合根之外不得出现服务定位器）。类型化注册是同样效果的地道写法，且顺带让 DI 负责它们的生命周期。
        services.AddTransient<AllowedHostHandler>();
        services.AddTransient<RetryingHandler>();

        services
            .AddHttpClient(HttpClientNames.Core, static client =>
            {
                // 单个文件可能几十 MB：整体超时交给调用方的 CancellationToken，这里不设固定上限。
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "EiTRVO-Core/" + (typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.1"));
            })
            .ConfigurePrimaryHttpMessageHandler(HttpPrimaryHandler.Create)
            .AddHttpMessageHandler<AllowedHostHandler>()
            .AddHttpMessageHandler<RetryingHandler>();

        // ---- View 层服务（F-4）----
        services.AddSingleton<IDispatcherService, DispatcherService>();
        // M6-d：界面刷新节流。transient 是刻意的 —— 合并的是"同一个刷新动作"，
        // 全进程共用一个实例会让页头徽标与下载页表格互相顶掉对方的待刷新动作。
        services.AddTransient<IUiRefreshThrottle, UiRefreshThrottle>();
        services.AddSingleton<IWindowService, WindowService>();
        // 帮助与支持中心是独立窗口（不是页面）：窗口自己从容器里解析 HelpViewModel。
        services.AddSingleton<IHelpWindowService, HelpWindowService>();
        services.AddSingleton<HelpViewModel>();
        // 2026-10-24：p12「查看第三方许可」独立窗口。
        services.AddSingleton<ThirdPartyLicensesViewModel>();
        services.AddSingleton<ILicenseWindowService, LicenseWindowService>();
        services.AddSingleton<IDialogService, DialogService>();
        // 注意：页面解析函数（Func<PageId, PageViewModelBase>）由 App.xaml.cs 在调用本方法之后注册 ——
        // 它要用 GetRequiredService，属于组合根本体的职责，不放进这份纯注册清单。
        services.AddSingleton<INavigationService, NavigationService>();

        // ---------- 页面 ViewModel ----------
        // 单例：切走再切回来时保留各自状态（例如 p17 上尚未保存的覆盖项、p05 的选中行）。
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<InstancesViewModel>();
        services.AddSingleton<InstanceDetailViewModel>();
        services.AddSingleton<NewInstanceViewModel>();
        services.AddSingleton<VersionsViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<ModsViewModel>();
        services.AddSingleton<ModpacksViewModel>();
        services.AddSingleton<ResourcesViewModel>();
        services.AddSingleton<JavaSettingsViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<DeleteInstanceViewModel>();
        services.AddSingleton<LaunchingViewModel>();
        services.AddSingleton<AccountLoginViewModel>();
        services.AddSingleton<InstanceSettingsViewModel>();

        // 模态层内容（由 p13 / p15 / p11 页面持有，外壳负责画遮罩）
        services.AddSingleton<DeleteConfirmDialogViewModel>();
        services.AddSingleton<AddAccountDialogViewModel>();
        services.AddSingleton<OfflineAccountDialogViewModel>();
        services.AddSingleton<RenameInstanceDialogViewModel>();
        services.AddSingleton<RemoveAccountDialogViewModel>();
        services.AddSingleton<JavaRuntimeWarningDialogViewModel>();
        // 2026-10-21：p07 / p09 的「从 Modrinth 下载」共用同一个搜索对话框。
        services.AddSingleton<ModrinthDownloadDialogViewModel>();
        services.AddSingleton<EventSearchViewModel>();
        services.AddSingleton<IEventSearchWindowService, EventSearchWindowService>();

        services.AddSingleton<ShellViewModel>();
    }
}
