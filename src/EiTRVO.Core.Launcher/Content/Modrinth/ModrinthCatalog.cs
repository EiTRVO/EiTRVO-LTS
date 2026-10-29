using System.Globalization;
using System.Security.Cryptography;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content.Modrinth;

/// <summary>
/// Modrinth 浏览列表（p08 的「从 Modrinth 浏览」）：**同步读**、**异步刷新**、磁盘缓存。
/// </summary>
/// <remarks>
/// 形状刻意与 <c>FileVersionCatalog</c> 一致，理由也一样：界面绑定只认同步属性，
/// 而数据来自网络 —— 所以"先刷新、再读"，刷新是异步且可以失败的，读永远同步。
/// <para>
/// 三个时间窗口：
/// <list type="bullet">
/// <item><b>新鲜度 30 分钟</b>：p08 没有搜索框也没有刷新按钮（设计稿里那两个下拉是静态的，接它们属 S9），
/// 刷新只能发生在"页面被导航到"时 —— 没有窗口的话，用户在几个页面之间来回点就会反复打 Modrinth；
/// 而 Modrinth 对匿名请求有限流（429），没必要去试探它。</item>
/// <item><b>失败后退避 2 分钟</b>：网络断了的时候，每次进页面都重试一遍会拖慢导航并刷满事件表。</item>
/// <item><b>空响应不覆盖已有列表</b>：Modrinth 回一份 0 条（限流页、镜像故障）时，
/// 若照写就把用户能看的列表毁了 —— 与版本清单"空清单不覆盖缓存"同一条纪律。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ModrinthCatalog
{
    /// <summary>p08 浏览表的固定 6 行（设计稿那张表就是 6 行）。</summary>
    public const int PageSize = 6;

    /// <summary>新鲜度窗口。</summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);

    /// <summary>失败后的退避窗口。</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(2);

    private readonly ModrinthClient _client;
    private readonly ModrinthCache _cache;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;

    private ModrinthBrowseSnapshot? _snapshot;
    private DateTimeOffset? _lastAttemptAt;

    /// <summary>构造浏览目录。</summary>
    /// <param name="client">Modrinth 客户端。</param>
    /// <param name="cache">磁盘缓存。</param>
    /// <param name="clock">时间源（新鲜度与退避都要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public ModrinthCatalog(
        ModrinthClient client,
        ModrinthCache cache,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _client = client;
        _cache = cache;
        _clock = clock;
        _diagnostics = diagnostics;

        // 离线也要能开局：构造时先读上次的快照（读不出来就是空表 + 一句"还没取到"）。
        _snapshot = cache.TryReadBrowse();
    }

    /// <summary>浏览列表（同步快照）。</summary>
    /// <returns>行；还没取到过时为空列表。</returns>
    public IReadOnlyList<ModrinthEntry> GetBrowse() => _snapshot?.Entries ?? [];

    /// <summary>
    /// 是否**取到过**数据（含从缓存恢复）。
    /// </summary>
    /// <remarks>
    /// "还没查过"与"查过但没有结果"必须分开：前者该说"联网后会自动获取"，
    /// 后者该说"Modrinth 没有返回任何整合包" —— 把没查过说成"没有结果"就是在编。
    /// </remarks>
    public bool HasFetched => _snapshot is not null;

    /// <summary>这份快照是什么时候取回来的（界面上的「上次更新」）。</summary>
    public DateTimeOffset? FetchedAt => _snapshot?.FetchedAt;

    /// <summary>Modrinth 报告的匹配总数。</summary>
    public int TotalHits => _snapshot?.TotalHits ?? 0;

    /// <summary>
    /// 刷新浏览列表（联网）。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否拿到了可用结果（新鲜度窗口内、退避期内、或失败都返回 <see langword="false"/> 之外的语义见备注）。</returns>
    /// <remarks>
    /// **不抛异常**：调用方是页面导航，网络失败不该让页面挂掉 —— 事件由客户端记，
    /// 这里只回答"这次刷新有没有改动手上的数据"。
    /// </remarks>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.Now;

        if (_snapshot is { } current && IsFresh(current.FetchedAt, now))
        {
            return true;
        }

        if (_lastAttemptAt is { } attempted && now >= attempted && now - attempted < FailureBackoff)
        {
            return false;
        }

        _lastAttemptAt = now;

        ModrinthSearchPage page;

        try
        {
            page = await _client.SearchModpacksAsync(PageSize, 0, cancellationToken).ConfigureAwait(false);
        }
        catch (ModrinthException)
        {
            // 客户端已经把失败原因写成事件了（含状态码 / 网络异常 / JSON 坏），这里不再记第二遍。
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // **任何没预料到的失败也必须留痕**：调用方是 `AsyncRelayCommand`，
            // 它会把异常吞进 `ExecutionTask` —— 界面上什么都不会发生，
            // 于是"一次都没取到"看起来和"取到了但没有结果"一模一样（无声的失败最坏）。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "获取 Modrinth 数据失败（{0}）：{1}",
                ["refresh", ex.GetType().Name]);

            return false;
        }

        var hits = page.Hits ?? [];

        if (hits.Count == 0)
        {
            // 空响应不覆盖已有列表：宁可让用户继续看上一次的结果，也不把表清空。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "Modrinth 浏览列表返回了 0 条（匹配总数 {0}），已保留上一次的 {1} 条结果",
                [
                    page.TotalHits.ToString(CultureInfo.InvariantCulture),
                    GetBrowse().Count.ToString(CultureInfo.InvariantCulture),
                ]);

            return false;
        }

        var entries = await BuildEntriesAsync(hits, cancellationToken).ConfigureAwait(false);
        var snapshot = new ModrinthBrowseSnapshot(now, page.TotalHits, entries);

        _snapshot = snapshot;
        _cache.WriteBrowse(snapshot);
        _lastAttemptAt = null;

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Modrinth,
            "已更新 Modrinth 浏览列表（{0} 个整合包，{1}）",
            [
                entries.Count.ToString(CultureInfo.InvariantCulture),
                now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ]);

        return true;
    }

    /// <summary>
    /// 按搜索词 / 游戏版本 / 加载器查一次（仅内存，不写浏览快照）。
    /// </summary>
    /// <param name="query">查询条件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果；<see langword="null"/> 表示请求失败（调用方保留旧结果）。</returns>
    /// <remarks>
    /// 与 <see cref="RefreshAsync"/> 分开：那个是"默认浏览列表"（有新鲜度窗口与磁盘缓存），
    /// 这个是**一次性的筛选查询** —— 用户换一个关键词就应当真的再问一次 Modrinth，
    /// 但结果不能覆盖磁盘上的默认快照（否则下次进 p08 会看到上次搜的词）。
    /// </remarks>
    public async Task<IReadOnlyList<ModrinthEntry>?> SearchAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        ModrinthSearchPage page;

        try
        {
            page = await _client
                .SearchModpacksAsync(PageSize, 0, query.Query, query.GameVersion, query.Loader, query.Sort, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModrinthException)
        {
            // 客户端已经记过事件（状态码 / 网络 / JSON），这里只回答"这次没成"。
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "搜索 Modrinth 整合包失败（{0}）：{1}",
                ["search", ex.GetType().Name]);

            return null;
        }

        var hits = page.Hits ?? [];

        // 空列表是**合法结果**（查到了，只是没有匹配）—— 与"请求失败"必须分开。
        return hits.Count == 0
            ? []
            : await BuildEntriesAsync(hits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按种类查一次 Modrinth（模组 / 资源包；仅内存，不写浏览快照）。
    /// </summary>
    /// <param name="kind">内容种类。</param>
    /// <param name="query">查询条件（加载器只对模组生效）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果；<see langword="null"/> 表示请求失败。</returns>
    public async Task<IReadOnlyList<ModrinthEntry>?> SearchContentAsync(
        ModrinthContentKind kind,
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var projectType = kind == ModrinthContentKind.Mod
            ? ModrinthClient.ModProjectType
            : ModrinthClient.ResourcePackProjectType;

        var facet = ModrinthClient.BuildFacets(
            projectType,
            query.GameVersion,
            kind == ModrinthContentKind.Mod ? query.Loader : null);

        ModrinthSearchPage page;

        try
        {
            page = await _client
                .SearchAsync(facet, PageSize, 0, query.Query, ModrinthClient.SortIndex(query.Sort), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModrinthException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "搜索 Modrinth 内容失败（{0}）：{1}",
                ["search", ex.GetType().Name]);

            return null;
        }

        var hits = page.Hits ?? [];
        return hits.Count == 0
            ? []
            : await BuildEntriesAsync(hits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 为当前实例挑一个 Modrinth 模组 / 资源包的兼容版本，生成可入队的下载目标。
    /// </summary>
    /// <param name="kind">内容种类。</param>
    /// <param name="projectId">Modrinth 项目 id。</param>
    /// <param name="displayName">界面上显示的名字（通常是项目名）。</param>
    /// <param name="gameVersion">实例的游戏版本；资源包也按它筛。</param>
    /// <param name="loader">实例的加载器短名（模组用；资源包传 <see langword="null"/>）。</param>
    /// <param name="requireBothHashes">
    /// 是否要求官方同时给出 SHA-1 与 SHA-512（「修复依赖」的交叉验证；普通下载为假）。
    /// </param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>下载目标；没有兼容版本时带原因。</returns>
    public async Task<ModrinthContentResult> PrepareContentAsync(
        ModrinthContentKind kind,
        string projectId,
        string displayName,
        string? gameVersion,
        string? loader,
        bool requireBothHashes = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        IReadOnlyList<ModrinthVersion> versions;

        try
        {
            versions = await _client.GetProjectVersionsAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (ModrinthException)
        {
            return new ModrinthContentResult(false, null, TextTemplate.Of("Modrinth 请求失败，稍后再试。"));
        }
        catch (OperationCanceledException)
        {
            return new ModrinthContentResult(false, null, TextTemplate.Of("已取消。"));
        }

        var compatible = versions
            .Where(version => MatchesContentVersion(version, kind, gameVersion, loader))
            .OrderByDescending(static version => version.DatePublished ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (compatible?.Id is not { Length: > 0 } versionId)
        {
            return new ModrinthContentResult(
                false,
                null,
                TextTemplate.Of("Modrinth 上没有适配当前实例的可用版本。"));
        }

        if (requireBothHashes)
        {
            // 「修复依赖」的交叉验证：官方必须同时给出 SHA-1 与 SHA-512，
            // 下载器随后会用两个独立摘要各校验一遍；缺任何一个都不下载。
            ModrinthVersionFile? file = null;

            foreach (var candidate in compatible.Files ?? [])
            {
                if (candidate.Primary)
                {
                    file = candidate;
                    break;
                }
            }

            if (file is null)
            {
                foreach (var candidate in compatible.Files ?? [])
                {
                    file = candidate;
                    break;
                }
            }

            if (file is null
                || string.IsNullOrWhiteSpace(file.Hashes?.Sha1)
                || string.IsNullOrWhiteSpace(file.Hashes?.Sha512))
            {
                return new ModrinthContentResult(
                    false,
                    null,
                    TextTemplate.Of(
                        "依赖 {0} 的官方校验值不完整（缺少 SHA-1 或 SHA-512），已拒绝下载。",
                        displayName));
            }
        }

        var versionLabel = string.IsNullOrWhiteSpace(compatible.VersionNumber)
            ? displayName
            : $"{displayName} {compatible.VersionNumber}";

        return new ModrinthContentResult(
            true,
            new ContentDownloadTarget(projectId, versionId, versionLabel, ReplacePath: null),
            null);
    }

    private static bool MatchesContentVersion(
        ModrinthVersion version,
        ModrinthContentKind kind,
        string? gameVersion,
        string? loader)
    {
        var gameMatches = string.IsNullOrWhiteSpace(gameVersion)
            || version.GameVersions?.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) == true;

        if (!gameMatches)
        {
            return false;
        }

        return kind == ModrinthContentKind.ResourcePack
               || string.IsNullOrWhiteSpace(loader)
               || version.Loaders?.Any(candidate => string.Equals(candidate, loader, StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// 准备一次「从 Modrinth 直接安装」：挑兼容版本 → 下载主文件到缓存目录 → 校验 SHA-1。
    /// </summary>
    /// <param name="request">项目与筛选条件。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>可交给导入器的本地归档；失败时带原因（**不抛异常**）。</returns>
    /// <remarks>
    /// 下载发生在**入队之前**：p08 的按钮是同步命令，而导入本身要几分钟 ——
    /// 先下好一个几十 MB 的 .mrpack、再交给已有的四步导入器，队列里看到的才是真正的安装进度。
    /// 归档留在 <c>cache\content\modpacks</c>：同一版本重复安装直接复用，校验不过就重下。
    /// </remarks>
    public async Task<ModrinthModpackResult> PrepareModpackAsync(
        ModrinthModpackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return new(false, null, TextTemplate.Of("这个整合包缺少项目 id（旧缓存快照），请刷新页面后再试。"));
        }

        try
        {
            var versions = await _client.GetProjectVersionsAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);

            var match = versions
                .Where(version => MatchesVersion(version, request))
                .OrderByDescending(static version => version.DatePublished ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

            if (match is null)
            {
                return new(false, null, TextTemplate.Of("Modrinth 上没有适配当前游戏版本与加载器的整合包版本。"));
            }

            var file = match.Files?
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Url)
                                           && !string.IsNullOrWhiteSpace(candidate.Hashes?.Sha1))
                .OrderByDescending(static candidate => candidate.Primary)
                .FirstOrDefault();

            if (file is null)
            {
                return new(false, null, TextTemplate.Of("这个整合包版本没有可下载的主文件。"));
            }

            var directory = Path.Combine(Path.GetDirectoryName(_cache.BrowsePath)!, "modpacks");
            Directory.CreateDirectory(directory);
            var archivePath = Path.Combine(directory, "modpack-" + match.Id + ".mrpack");
            var expectedSha1 = file.Hashes!.Sha1!.Trim();

            if (!await FileMatchesAsync(archivePath, expectedSha1, cancellationToken).ConfigureAwait(false))
            {
                await _client
                    .DownloadFileAsync(new Uri(file.Url!), archivePath, cancellationToken)
                    .ConfigureAwait(false);

                if (!await FileMatchesAsync(archivePath, expectedSha1, cancellationToken).ConfigureAwait(false))
                {
                    TryDelete(archivePath);
                    return new(false, null, TextTemplate.Of("下载的整合包校验失败（SHA-1 不符），已丢弃。"));
                }
            }

            var title = string.IsNullOrWhiteSpace(match.Name)
                ? request.ProjectId
                : match.Name!;

            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.Modrinth,
                "已准备好整合包 {0}（{1}）",
                [title, match.VersionNumber ?? string.Empty]);

            return new(true, new ModrinthModpackArchive(archivePath, title), null);
        }
        catch (ModrinthException)
        {
            return new(false, null, TextTemplate.Of("Modrinth 请求失败，稍后再试。"));
        }
        catch (OperationCanceledException)
        {
            return new(false, null, TextTemplate.Of("已取消。"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "准备 Modrinth 整合包失败（{0}）：{1}",
                [request.ProjectId, ex.GetType().Name]);

            return new(false, null, TextTemplate.Of("下载整合包失败（{0}）。", ex.GetType().Name));
        }
    }

    private static bool MatchesVersion(ModrinthVersion version, ModrinthModpackRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.GameVersion)
            && version.GameVersions?.Any(game => string.Equals(game, request.GameVersion, StringComparison.OrdinalIgnoreCase)) != true)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(request.Loader)
               || version.Loaders?.Any(loader => string.Equals(loader, request.Loader, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static async Task<bool> FileMatchesAsync(
        string path,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉残留文件不影响"这次失败"的结论。
        }
    }
    private static bool IsFresh(DateTimeOffset fetchedAt, DateTimeOffset now) =>
        now >= fetchedAt && now - fetchedAt < Freshness;

    /// <summary>把搜索命中的项目变成表格行（版本号要另外批量查一次）。</summary>
    private async Task<IReadOnlyList<ModrinthEntry>> BuildEntriesAsync(
        IReadOnlyList<ModrinthSearchHit> hits,
        CancellationToken cancellationToken)
    {
        // 搜索接口只给"最新版本的 id"；表格的「版本」列要的是版本号。
        // 一次批量查询换 6 次单查（`/v2/versions?ids=[…]`）。
        List<string> versionIds = [];
        foreach (var hit in hits)
        {
            if (!string.IsNullOrWhiteSpace(hit.LatestVersionId))
            {
                versionIds.Add(hit.LatestVersionId);
            }
        }

        Dictionary<string, string> versionNumbers = new(StringComparer.Ordinal);

        if (versionIds.Count > 0)
        {
            try
            {
                var versions = await _client.GetVersionsAsync(versionIds, cancellationToken).ConfigureAwait(false);

                foreach (var version in versions)
                {
                    if (!string.IsNullOrWhiteSpace(version.Id) && !string.IsNullOrWhiteSpace(version.VersionNumber))
                    {
                        versionNumbers[version.Id] = version.VersionNumber!;
                    }
                }
            }
            catch (ModrinthException)
            {
                // 版本号取不到不影响列表本身：那一列显示「—」，而不是让整张表失败。
            }
        }

        List<ModrinthEntry> entries = [];

        foreach (var hit in hits)
        {
            var version = hit.LatestVersionId is { Length: > 0 } id && versionNumbers.TryGetValue(id, out var number)
                ? number
                : "—";

            entries.Add(new ModrinthEntry(
                Name: string.IsNullOrWhiteSpace(hit.Title) ? hit.Slug ?? "—" : hit.Title,
                Summary: hit.Description ?? string.Empty,
                Downloads: ModrinthText.Downloads(hit.Downloads),
                Version: version,
                ProjectId: hit.ProjectId,
                VersionId: hit.LatestVersionId));
        }

        return entries;
    }
}
