using System.Globalization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// <see cref="IVersionCatalog"/> 的真实实现：清单来自 <c>piston-meta</c>（缓存到 <c>cache\versions\</c>），
/// 每行的"本地状态"来自游戏目录里的真实目录，加载器版本来自四家元数据接口。
/// <para>
/// 几个刻意的选择：
/// </para>
/// <list type="bullet">
/// <item><b>构造时先读缓存</b>：离线启动也能立刻列出上次拿到的清单，不必等网络超时。</item>
/// <item><b>"所需 Java"以版本 JSON 的声明值为准，规则表只兜底</b>（M3）：
/// 本地有 <c>&lt;id&gt;.json</c> 时读它的 <c>javaVersion.majorVersion</c> —— 那是 Mojang 自己写的答案；
/// 没有本地 JSON 时才用 <see cref="JavaRequirement.RequiredMajor"/> 推断。
/// 之所以不"为 900 个版本逐个联网取 JSON"：那既慢又毫无必要，而**兜底值已经足够准**（见规则表）。</item>
/// <item><b>"缺少 Java"必须有检测结果支撑</b>（M3）：<see cref="IJavaRuntimeCatalog.HasDetectionResult"/>
/// 为假时一律显示"已安装"—— 把"还没查过"说成"缺 Java"，等于凭空报一个用户会去处理的假故障。</item>
/// <item><b>只展示正式版与快照</b>：设计稿 p05 的「共 917 个」正是这两类之和（305 + 612），
/// 把 2011 年前后的 <c>old_alpha</c>/<c>old_beta</c> 也算进去，界面上的数字就对不上设计稿。</item>
/// <item><b>"当前行"取最新已安装版本</b>：设计稿高亮的是 1.20.4（本机已装的），而不是清单里最新的 1.21.4 ——
/// 打开启动器时用户想要的通常是"我能玩的那个"。</item>
/// </list>
/// </summary>
public sealed class FileVersionCatalog : IVersionCatalog
{
    /// <summary>
    /// 清单的新鲜度窗口：缓存比这更新时不再联网。
    /// <para>
    /// 存在的理由：p05 没有「刷新」按钮（设计稿里没有），刷新只能发生在「页面被导航到」时；
    /// 没有窗口的话，用户在版本页与实例页之间来回点几次就会反复拉那份 300KB 的清单。
    /// 与加载器元数据的 10 分钟 TTL 是同一个思路。
    /// </para>
    /// </summary>
    private static readonly TimeSpan ManifestFreshness = TimeSpan.FromMinutes(15);

    private readonly VersionManifestClient _manifestClient;
    private readonly LoaderMetadataClient _loaderClient;
    private readonly OptiFineCatalogClient _optiFineClient;
    private readonly VersionJsonStore _versionJsons;
    private readonly IJavaRuntimeCatalog _javaRuntimes;
    private readonly ISettingsCatalog _settings;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;

    private readonly Dictionary<string, IReadOnlyList<LoaderOption>> _loaders = new(StringComparer.Ordinal);

    /// <summary>已经读到过声明值的版本（<c>javaVersion.majorVersion</c>）。</summary>
    private readonly Dictionary<string, int> _declaredJava = new(StringComparer.Ordinal);

    /// <summary>已经确认"没有本地 JSON / JSON 里没有声明值"的**未安装**版本。</summary>
    private readonly HashSet<string> _probedWithoutDeclared = new(StringComparer.Ordinal);

    private VersionManifest? _manifest;

    /// <summary>构造版本目录。</summary>
    /// <param name="manifestClient">清单客户端（含磁盘缓存）。</param>
    /// <param name="loaderClient">加载器元数据客户端。</param>
    /// <param name="optiFineClient">OptiFine 列表客户端（M4-i：它的"版本列表"来自 optifine.net 官方 downloads 页）。</param>
    /// <param name="versionJsons">版本 JSON 存储（读"所需 Java"的声明值）。</param>
    /// <param name="javaRuntimes">Java 运行时检测（"缺少 Java"这一态的依据）。</param>
    /// <param name="settings">设置（游戏根目录要从这里取）。</param>
    /// <param name="clock">时间源（清单新鲜度要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public FileVersionCatalog(
        VersionManifestClient manifestClient,
        LoaderMetadataClient loaderClient,
        OptiFineCatalogClient optiFineClient,
        VersionJsonStore versionJsons,
        IJavaRuntimeCatalog javaRuntimes,
        ISettingsCatalog settings,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manifestClient);
        ArgumentNullException.ThrowIfNull(loaderClient);
        ArgumentNullException.ThrowIfNull(optiFineClient);
        ArgumentNullException.ThrowIfNull(versionJsons);
        ArgumentNullException.ThrowIfNull(javaRuntimes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _manifestClient = manifestClient;
        _loaderClient = loaderClient;
        _optiFineClient = optiFineClient;
        _versionJsons = versionJsons;
        _javaRuntimes = javaRuntimes;
        _settings = settings;
        _clock = clock;
        _diagnostics = diagnostics;

        // 离线也要能开局：先用上次的缓存把表填上，刷新成功后再覆盖。
        _manifest = manifestClient.TryReadCache();
    }

    /// <inheritdoc />
    public int TotalCount => Displayed().Count;

    /// <inheritdoc />
    public int SnapshotCount => Displayed().Count(static v => VersionTypes.ToKind(v.Type) == VersionKind.Snapshot);

    /// <inheritdoc />
    public int ReleaseCount => Displayed().Count(static v => VersionTypes.ToKind(v.Type) == VersionKind.Release);

    /// <inheritdoc />
    public string ActiveVersionId
    {
        get
        {
            var displayed = Displayed();

            return displayed.Count == 0
                ? string.Empty
                : ResolveActiveIdOrPinned(displayed, InstalledVersionIds());
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var id = value.Trim();
            if (id.Length == 0)
            {
                // 交还给自动判断（最新已安装 → 最新正式版 → 第一行）。
                _pinnedVersionId = string.Empty;
                return;
            }

            // 版本 id 会变成 versions\<id>\ 目录名，所以先过路径安全检查 —— 它来自界面输入路径，
            // 不该被当成"可信字符串"（实例名走的是同一个检查）。
            if (!PathSafety.IsSafePathSegment(id))
            {
                throw new ArgumentException($"版本 id 不能用作目录名：'{value}'。", nameof(value));
            }

            // 清单已经加载时就必须真的存在：写一个清单里没有的版本，界面会没有任何一行被高亮，
            // 而调用方还以为自己选中了东西 —— 这种失败要当场说清楚。
            var displayed = Displayed();
            if (displayed.Count > 0
                && !displayed.Any(version => string.Equals(version.Id, id, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"清单里没有版本「{id}」。", nameof(value));
            }

            _pinnedVersionId = id;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MinecraftVersion> GetVersions()
    {
        var displayed = Displayed();
        if (displayed.Count == 0)
        {
            return [];
        }

        var installed = InstalledVersionIds();
        var activeId = ResolveActiveIdOrPinned(displayed, installed);

        // 检测结果读一次就够（每行都读会变成 856 次重复判断）。
        var hasJavaResult = _javaRuntimes.HasDetectionResult;
        var installations = _javaRuntimes.GetInstallations();
        var gameDirectory = _settings.GetGlobal().GameDirectory;

        return
        [
            .. displayed.Select(version =>
            {
                var id = version.Id ?? string.Empty;
                var isInstalled = installed.Contains(id);
                var requiredJava = RequiredJavaFor(gameDirectory, id, isInstalled);

                // 「缺少 Java」只在**装是装上了、但本机没有能跑它的 Java**时成立。
                // 没有检测结果时不做这个断言（见类注释）。
                var missingJava = isInstalled
                                  && hasJavaResult
                                  && !JavaPreflight.Check(id, null, installations, hasDetectionResult: true).IsSatisfied;

                return new MinecraftVersion(
                    Id: id,
                    Kind: VersionTypes.ToKind(version.Type),
                    ReleaseDate: FormatReleaseDate(version),
                    RequiredJava: string.Create(CultureInfo.InvariantCulture, $"Java {requiredJava}"),
                    LocalState: !isInstalled
                        ? VersionLocalState.NotInstalled
                        : missingJava
                            ? VersionLocalState.MissingJava
                            : VersionLocalState.Installed,
                    // 「已安装 / 未安装 / 缺少 Java N」是**文案**（键写成字面量），N 是数据（参数）。
                    LocalStateText: !isInstalled
                        ? TextTemplate.Of("未安装")
                        : missingJava
                            ? TextTemplate.Of(
                                "缺少 Java {0}",
                                requiredJava.ToString(CultureInfo.InvariantCulture))
                            : TextTemplate.Of("已安装"),
                    IsActive: string.Equals(id, activeId, StringComparison.Ordinal));
            }),
        ];
    }

    /// <inheritdoc />
    public async Task<VersionDownloadEstimate?> EstimateDownloadAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var gameDirectory = _settings.GetGlobal().GameDirectory;
        var json = _versionJsons.Load(gameDirectory, versionId);

        if (json is null)
        {
            // 本地没有缓存：按清单里的地址取一次（只有几 KB），失败就如实返回 null。
            var entry = _manifest?.Versions.FirstOrDefault(version =>
                string.Equals(version.Id, versionId, StringComparison.Ordinal));

            if (entry?.Url is not { Length: > 0 } url)
            {
                return null;
            }

            try
            {
                json = await _versionJsons.FetchAsync(versionId, url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or HttpPolicyException or VersionDataException)
            {
                return null;
            }
        }

        return VersionDownloadEstimator.Estimate(json) with { FreeDiskBytes = FreeDiskBytes(gameDirectory) };
    }

    /// <summary>游戏目录所在卷的剩余字节数；拿不到时返回 0（界面不显示"空间充足"）。</summary>
    private static long FreeDiskBytes(string gameDirectory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(gameDirectory));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 某个版本需要哪个 Java：**先看版本 JSON 里的声明值**，没有本地 JSON 才用规则表推断。
    /// </summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="versionId">版本 id。</param>
    /// <param name="isInstalled">该版本是否已安装（已安装的要重读：安装器可能刚把 JSON 写进去）。</param>
    /// <returns>所需 Java 主版本号。</returns>
    private int RequiredJavaFor(string gameDirectory, string versionId, bool isInstalled)
    {
        if (_declaredJava.TryGetValue(versionId, out var declared))
        {
            return declared;
        }

        if (!isInstalled && _probedWithoutDeclared.Contains(versionId))
        {
            return JavaRequirement.RequiredMajor(versionId);
        }

        var json = _versionJsons.Load(gameDirectory, versionId);
        if (json?.JavaVersion?.MajorVersion is > 0 and var majorVersion)
        {
            _declaredJava[versionId] = majorVersion;
            return majorVersion;
        }

        // 「读过了、没有声明值」只对未安装的版本记下来：已安装的版本随时可能被写入 JSON。
        if (!isInstalled)
        {
            _probedWithoutDeclared.Add(versionId);
        }

        return JavaRequirement.RequiredMajor(versionId);
    }

    /// <inheritdoc />
    public IReadOnlyList<LoaderOption> GetLoaders(string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        if (_loaders.TryGetValue(gameVersion, out var cached))
        {
            return cached;
        }

        // 还没问过网络：先给可点击的占位项，用户选中哪一家就只取哪一家的版本。
        // 不把六项都标成"该版本没有"（那是在编事实），也不等四家一起回来。
        return PendingLoaders();
    }

    /// <summary>还没取过列表时的六行（原版 + 五家加载器）。</summary>
    /// <returns>可点击的占位项。</returns>
    private static IReadOnlyList<LoaderOption> PendingLoaders() =>
    [
        new(LoaderKind.Vanilla, InstanceNaming.Display(LoaderKind.Vanilla),
            TextTemplate.Of("不安装任何加载器，纯净游戏"),
            LoaderAvailability.Discouraged, false, []),
        Pending(LoaderKind.Fabric),
        Pending(LoaderKind.Forge),
        Pending(LoaderKind.NeoForge),
        Pending(LoaderKind.Quilt),
        Pending(LoaderKind.OptiFine),
    ];

    /// <inheritdoc />
    public async Task RefreshLoaderAsync(LoaderKind loader, string gameVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        if (loader == LoaderKind.Vanilla)
        {
            return;
        }

        var options = _loaders.TryGetValue(gameVersion, out var existing)
            ? existing.ToList()
            : PendingLoaders().ToList();

        var index = options.FindIndex(option => option.Kind == loader);
        if (index < 0)
        {
            return;
        }

        LoaderOption fetched;

        if (loader == LoaderKind.OptiFine)
        {
            try
            {
                var builds = await _optiFineClient.FetchAsync(gameVersion, cancellationToken).ConfigureAwait(false);
                fetched = OptiFineOption(builds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.ModLoader,
                    "获取 {0} 的 OptiFine 列表失败（{1}），这一项本次不可用",
                    [gameVersion, ex.Message]);

                fetched = new LoaderOption(
                    loader,
                    InstanceNaming.Display(loader),
                    TextTemplate.Of("获取加载器版本失败（网络）"),
                    LoaderAvailability.Failed,
                    false,
                    []);
            }
        }
        else
        {
            try
            {
                var list = await _loaderClient.GetAsync(loader, gameVersion, cancellationToken).ConfigureAwait(false);
                fetched = LoaderOptionOf(loader, list, failed: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.ModLoader,
                    "获取 {0} 的加载器列表失败（{1}），这一项本次不可用",
                    [loader.ToString(), ex.Message]);

                fetched = LoaderOptionOf(loader, LoaderVersionList.None(loader), failed: true);
            }
        }

        options[index] = fetched;
        _loaders[gameVersion] = options;
    }

    /// <summary>还没取过列表的加载器项：先让用户能点，点中后再单独取这一家的版本。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>向导里的一项（可点击、暂无版本）。</returns>
    private static LoaderOption Pending(LoaderKind kind) =>
        new(
            kind,
            InstanceNaming.Display(kind),
            TextTemplate.Of("点击后获取版本"),
            LoaderAvailability.Available,
            false,
            []);

    /// <inheritdoc />
    public IReadOnlyList<WizardStep> GetWizardSteps() =>
    [
        // 状态由向导自己按当前步骤重算（见 NewInstanceViewModel），这里只给四步的名称与顺序。
        // 名称是文案（键写成字面量），界面经 WizardStepViewModel 投影成当前语言。
        new(1, TextTemplate.Of("游戏版本"), WizardStepState.Pending),
        new(2, TextTemplate.Of("模组加载器"), WizardStepState.Pending),
        new(3, TextTemplate.Of("名称与目录"), WizardStepState.Pending),
        new(4, TextTemplate.Of("确认并安装"), WizardStepState.Pending),
    ];

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var writtenAt = _manifestClient.CacheWrittenAt;
        if (writtenAt is not null && _clock.Now - writtenAt.Value < ManifestFreshness)
        {
            // 刚拿过：界面已经能显示完整列表，不必为了"再确认一次"去联网。
            return;
        }

        try
        {
            _manifest = await _manifestClient.FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VersionDataException ex)
        {
            // 联网失败不是"清单不存在"：先把上次的缓存用上，再记一条事件让用户知道看到的是旧数据。
            var cached = _manifestClient.TryReadCache();
            if (cached is not null)
            {
                _manifest = cached;
            }

            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Downloader,
                cached is null ? "获取版本清单失败，且本地没有缓存（{0}）" : "获取版本清单失败，已沿用本地缓存（{0}）",
                [ex.Message]);
        }
    }

    /// <inheritdoc />
    public async Task RefreshLoadersAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        var vanilla = new LoaderOption(
            LoaderKind.Vanilla,
            InstanceNaming.Display(LoaderKind.Vanilla),
            TextTemplate.Of("不安装任何加载器，纯净游戏"),
            LoaderAvailability.Discouraged,
            false,
            []);

        // 四个端点各自失败、各自降级：某一家（例如 maven.neoforged.net）连不上时，
        // 另外三家仍然要可选 —— 否则用户会看到"所有加载器都不可用"，而其实只是其中一个域名超时。
        // 四家**并发**取：一个慢端点不该把其它已经回来的结果一起拖到超时之后才显示。
        async Task<(LoaderVersionList List, bool Failed)> FetchAsync(LoaderKind kind)
        {
            try
            {
                return (await _loaderClient.GetAsync(kind, gameVersion, cancellationToken).ConfigureAwait(false), false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 任何一家的失败都只降级这一项：哪怕抛的是没预料到的异常，也不能把其它三家拖下水。
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.ModLoader,
                    "获取 {0} 的加载器列表失败（{1}），这一项本次不可用",
                    [kind.ToString(), ex.Message]);
                return (LoaderVersionList.None(kind), true);
            }
        }

        // OptiFine 单独取、单独兜底：它走的是 optifine.net 官方下载页（第三方站点，没有 API），
        // 让它的可用性去决定"官方四家能不能显示"是错的 —— 反过来也一样。
        async Task<IReadOnlyList<OptiFineBuild>> FetchOptiFineAsync()
        {
            try
            {
                return await _optiFineClient.FetchAsync(gameVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _diagnostics.Write(
                    LauncherEventLevel.Warning,
                    DiagnosticsSources.ModLoader,
                    "获取 {0} 的 OptiFine 列表失败（{1}），本次不提供 OptiFine",
                    [gameVersion, ex.Message]);

                return [];
            }
        }

        var optiFineTask = FetchOptiFineAsync();
        var loaderResults = await Task.WhenAll(
            FetchAsync(LoaderKind.Fabric),
            FetchAsync(LoaderKind.Quilt),
            FetchAsync(LoaderKind.Forge),
            FetchAsync(LoaderKind.NeoForge)).ConfigureAwait(false);

        var (fabric, fabricFailed) = loaderResults[0];
        var (quilt, quiltFailed) = loaderResults[1];
        var (forge, forgeFailed) = loaderResults[2];
        var (neoForge, neoForgeFailed) = loaderResults[3];
        var optiFineBuilds = await optiFineTask.ConfigureAwait(false);
        List<LoaderOption> options =
        [
            vanilla,
            LoaderOptionOf(LoaderKind.Fabric, fabric, fabricFailed),
            LoaderOptionOf(LoaderKind.Forge, forge, forgeFailed),
            LoaderOptionOf(LoaderKind.NeoForge, neoForge, neoForgeFailed),
            LoaderOptionOf(LoaderKind.Quilt, quilt, quiltFailed),
            OptiFineOption(optiFineBuilds),
        ];

        // 选中位：有 Forge 就选它（设计稿把 Forge 画成选中项），否则让给第一个可用的加载器 ——
        // 向导不能开局一个选中项都没有（NewInstanceViewModel 会去取选中项来决定下拉框内容）。
        var selected = forge.IsAvailable ? 2 : options.FindIndex(HasVersions);
        if (selected >= 0)
        {
            options[selected] = options[selected] with
            {
                // 「已选择」在界面上就是这个枚举值渲染出来的（见 P04NewInstanceView.xaml 的 DataTrigger），
                // 所以它表示**选中态**，不是"该加载器标了稳定版"。
                Availability = LoaderAvailability.Recommended,
                IsActive = true,
            };
        }

        _loaders[gameVersion] = options;
    }
    /// <summary>该选项是否有可安装的版本（用来挑"第一个可用的加载器"）。</summary>
    private static bool HasVersions(LoaderOption option) =>
        option.Availability == LoaderAvailability.Available;

    /// <summary>把一份加载器版本列表变成向导里的一项。</summary>
    /// <remarks>
    /// 注意两个"推荐"是两件事：**下拉框里的「（推荐）」**表示加载器自己标记的稳定版
    /// （<see cref="LoaderVersionList.Default"/>），而**行尾徽章的「已选择」**表示向导当前选中的是谁
    /// （由 <see cref="LoaderAvailability.Recommended"/> 渲染）。前者可以有多个加载器同时具备，后者只能有一个。
    /// <para>
    /// M6-e-3d：前者不再往版本串上贴「（推荐）」四个字（那会让版本段变成脏数据），
    /// 而是单独放在 <see cref="LoaderOption.RecommendedVersion"/> 里，由 App 层拼显示文本。
    /// </para>
    /// </remarks>
    /// <param name="kind">加载器种类。</param>
    /// <param name="list">该加载器的版本列表。</param>
    /// <param name="failed">这一项是不是取列表失败（网络原因，不等于"该版本没有"）。</param>
    /// <returns>向导里的一项。</returns>
    private static LoaderOption LoaderOptionOf(LoaderKind kind, LoaderVersionList list, bool failed)
    {
        if (failed)
        {
            return new LoaderOption(
                kind,
                InstanceNaming.Display(kind),
                TextTemplate.Of("获取加载器版本失败（网络）"),
                LoaderAvailability.Failed,
                false,
                []);
        }

        var display = InstanceNaming.Display(kind);

        if (!list.IsAvailable)
        {
            return new LoaderOption(
                kind,
                display,
                TextTemplate.Of("该游戏版本没有对应版本"),
                LoaderAvailability.Unavailable,
                false,
                []);
        }

        return new LoaderOption(
            kind,
            display,
            LoaderBlurb(kind, list.Default),
            LoaderAvailability.Available,
            false,
            [.. list.Versions],
            list.Default);
    }

    /// <summary>
    /// 加载器那一行的说明文本（键 + 参数）：**说明词进键、版本号进参数**。
    /// </summary>
    /// <remarks>
    /// 键必须写成**字面量**（不能由调用方当参数传进来）——否则 <c>UserTextKeyTests</c> 的扫描器
    /// 看不见这些键，"键都在资源里"这条检查就静默失效了（AGENTS.md §5.3 第 ① / ⑧ 条）。
    /// </remarks>
    /// <param name="kind">加载器种类。</param>
    /// <param name="version">该加载器的推荐版本。</param>
    /// <returns>键 + 参数。</returns>
    private static TextTemplate LoaderBlurb(LoaderKind kind, string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? TextTemplate.Of("该游戏版本没有对应版本")
            : kind switch
            {
                LoaderKind.Fabric => TextTemplate.Of("{0} · 轻量 · 更新快", version),
                LoaderKind.Forge => TextTemplate.Of("{0} · 生态兼容性最好", version),
                LoaderKind.NeoForge => TextTemplate.Of("{0} · Forge 的新一代", version),
                LoaderKind.Quilt => TextTemplate.Of("{0} · Fabric 的分支", version),
                _ => TextTemplate.Of("{0} · 光影与画质增强", version),
            };

    /// <summary>
    /// OptiFine（M4-i）：它的"版本列表"来自 optifine.net 官方 downloads 页，版本串就是官方文件名里的版本段
    /// （<c>HD_U_I8</c> 这种），选中它之后安装器会在沙箱里跑官方安装器。
    /// <para>
    /// 列表里**只列正式版**：预览版的版本段形如 <c>HD_U_I8_pre4</c>，需要额外标注才不至于被误当成正式版，
    /// 而 <see cref="LoaderOption.Versions"/> 里的字符串会**原样**变成安装请求里的版本段（不能加装饰），
    /// 所以这一版干脆不列（安装器本身接受预览版，缺的只是向导里的入口）。
    /// </para>
    /// </summary>
    /// <param name="builds">官方 downloads 页解析出的构建列表。</param>
    /// <returns>向导里的一项。</returns>
    private static LoaderOption OptiFineOption(IReadOnlyList<OptiFineBuild> builds)
    {
        var stable = builds.Where(static build => !build.IsPreview).ToList();

        if (stable.Count == 0)
        {
            return new LoaderOption(
                LoaderKind.OptiFine,
                InstanceNaming.Display(LoaderKind.OptiFine),
                TextTemplate.Of("该游戏版本没有对应版本"),
                LoaderAvailability.Unavailable,
                false,
                []);
        }

        var latest = stable[0];
        var previews = builds.Count - stable.Count;

        // 说明文本里有可翻译的词（"光影与画质增强"、"另有 … 个预览版未列出"），所以整句是一个键。
        var detail = previews > 0
            ? TextTemplate.Of(
                "{0} · 光影与画质增强（另有 {1} 个预览版未列出）",
                latest.Edition,
                previews.ToString(CultureInfo.InvariantCulture))
            : TextTemplate.Of("{0} · 光影与画质增强", latest.Edition);

        return new LoaderOption(
            LoaderKind.OptiFine,
            InstanceNaming.Display(LoaderKind.OptiFine),
            detail,
            LoaderAvailability.Available,
            false,
            [.. stable.Select(static build => build.Edition)]);
    }

    /// <summary>还没取过列表时的 OptiFine 项：如实说"尚未检查"，不编一个可用性出来。</summary>
    private static LoaderOption OptiFineOption() =>
        new(
            LoaderKind.OptiFine,
            InstanceNaming.Display(LoaderKind.OptiFine),
            TextTemplate.Of("尚未检查"),
            LoaderAvailability.Unavailable,
            false,
            []);

    private List<ManifestVersion> Displayed() =>
        _manifest?.Versions.Where(static version => VersionTypes.IsDisplayed(version.Type)).ToList() ?? [];

    /// <summary>
    /// 游戏目录里已安装的版本 id 集合。
    /// </summary>
    /// <remarks>
    /// 判据与 <see cref="VersionJsonStore.TryReadInstalled"/> 一致：<c>version.json</c> 或旧布局的
    /// <c>&lt;id&gt;.json</c>。含 <c>instance.json</c> 的目录是**实例**（其版本 JSON 属于那个实例），
    /// 不能因为目录名恰好等于某个版本 id 就把那一版算成"已安装到共享目录"。
    /// </remarks>
    private HashSet<string> InstalledVersionIds()
    {
        HashSet<string> installed = new(StringComparer.Ordinal);

        var versionsDirectory = Path.Combine(_settings.GetGlobal().GameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return installed;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
            {
                var id = Path.GetFileName(directory);

                if (File.Exists(Path.Combine(directory, InstancePaths.MetadataFileName)))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(directory, InstancePaths.VersionJsonFileName))
                    || File.Exists(Path.Combine(directory, id + ".json")))
                {
                    installed.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就当没有已安装版本：列表照常显示，只是不高亮、都算未安装。
        }

        return installed;
    }

    /// <summary>用户显式选过的版本（空串 = 交给自动判断）。</summary>
    private string _pinnedVersionId = string.Empty;

    /// <summary>决定哪一行是"当前行"：用户选过就用他选的，否则 最新已安装 → 清单里的最新正式版 → 第一行。</summary>
    private string ResolveActiveIdOrPinned(List<ManifestVersion> displayed, HashSet<string> installed) =>
        _pinnedVersionId.Length > 0 ? _pinnedVersionId : ResolveActiveId(displayed, installed) ?? string.Empty;

    /// <summary>决定哪一行是"当前行"：最新已安装 → 清单里的最新正式版 → 第一行。</summary>
    private static string? ResolveActiveId(List<ManifestVersion> displayed, HashSet<string> installed)
    {
        foreach (var version in displayed)
        {
            if (version.Id is { Length: > 0 } id && installed.Contains(id))
            {
                return id;
            }
        }

        foreach (var version in displayed)
        {
            if (string.Equals(version.Type, VersionTypes.Release, StringComparison.OrdinalIgnoreCase))
            {
                return version.Id;
            }
        }

        return displayed.Count > 0 ? displayed[0].Id : null;
    }

    private static string FormatReleaseDate(ManifestVersion version)
    {
        var timestamp = version.ReleaseTime ?? version.Time;
        return timestamp is null
            ? string.Empty
            : timestamp.Value.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    }
}
