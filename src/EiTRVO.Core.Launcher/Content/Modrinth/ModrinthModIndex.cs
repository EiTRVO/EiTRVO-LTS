using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Content.Modrinth;

/// <summary>一个本地 jar 与 Modrinth 对上的账。</summary>
/// <param name="Hash">jar 的 SHA-1（小写十六进制）。</param>
/// <param name="ProjectId">Modrinth 项目 id。</param>
/// <param name="VersionId">**已装**那个版本的 id（后续"更新所选"要下它对应的文件）。</param>
/// <param name="VersionNumber">已装那个版本的版本号。</param>
/// <param name="LatestVersionId">"在当前游戏版本 + 加载器下"可用的最新版本 id；没有结果时为 <see langword="null"/>。</param>
/// <param name="LatestVersionNumber">最新版本的版本号。</param>
public sealed record ModrinthModMatch(
    string Hash,
    string? ProjectId,
    string? VersionId,
    string? VersionNumber,
    string? LatestVersionId,
    string? LatestVersionNumber)
{
    /// <summary>
    /// 有没有更新。
    /// </summary>
    /// <remarks>
    /// 判据是**版本号不同**，而不是"官方回了结果"：`/version_files/update` 对已经是最新的模组
    /// 也会返回它自己那个版本（它就是"当前可用的最新版"），所以"有响应"不等于"有更新"。
    /// 版本号拿不到时一律算没有更新 —— 不能凭一个空值说"你该更新了"。
    /// </remarks>
    public bool HasUpdate =>
        LatestVersionNumber is { Length: > 0 } latest
        && !string.Equals(latest, VersionNumber, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 本地模组与 Modrinth 的对账结果（<c>cache\content\modrinth-mods.json</c>）。
/// </summary>
/// <remarks>
/// 两半查询，缺一不可：
/// <list type="number">
/// <item><b>精确查</b>（<c>/version_files</c>）：这个哈希是不是 Modrinth 上的文件 —— 决定「来源」列；</item>
/// <item><b>查更新</b>（<c>/version_files/update</c>，带当前实例的游戏版本与加载器）：有没有更新的版本 ——
/// 决定「可更新」与「最新 {0}」。</item>
/// </list>
/// <para>
/// 两条刻意记下的语义：
/// </para>
/// <list type="bullet">
/// <item><b>"查过但没查到"与"没查过"必须分开</b>：前者才敢说「不在 Modrinth 收录」，
/// 后者只能说"未知"（沿用 M7-a 的显示）。所以文件里存的是"查过的哈希集合" + "查到的匹配"，
/// 而不是只存匹配。</item>
/// <item><b>两个请求要么都成功、要么整次算失败</b>：一半新一半旧的结果会让"来源"列自相矛盾，
/// 而保留上一次的完整结果至少是自洽的。</item>
/// </list>
/// </remarks>
public sealed class ModrinthModIndex
{
    /// <summary>文件名。</summary>
    public const string FileName = "modrinth-mods.json";

    /// <summary>当前格式版本。</summary>
    private const int CacheSchemaVersion = 1;

    /// <summary>新鲜度窗口：同条件、同一批哈希，12 小时内不重复问。</summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(12);

    private readonly ModrinthClient _client;
    private readonly ILauncherPaths _paths;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly FileStampGate _warnings = new();

    private readonly HashSet<string> _lookedUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModrinthModMatch> _matches = new(StringComparer.OrdinalIgnoreCase);

    // 依赖 id → 这个 id 在 Modrinth 上是否存在。只缓存**确定的结果**（200 / 404），
    // 网络失败不写：否则一次超时会让一条真实缺失从此被静默忽略。
    private readonly Dictionary<string, bool> _dependencyExistence = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset? _fetchedAt;
    private string? _gameVersion;
    private string? _loader;

    /// <summary>构造对账索引（并把上次的结果读进内存）。</summary>
    /// <param name="client">Modrinth 客户端。</param>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="clock">时间源（新鲜度要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public ModrinthModIndex(
        ModrinthClient client,
        ILauncherPaths paths,
        IClock clock,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _client = client;
        _paths = paths;
        _clock = clock;
        _diagnostics = diagnostics;
        _paths.EnsureCreated();

        Load();
    }

    /// <summary>索引文件路径。</summary>
    public string FilePath => Path.Combine(_paths.ContentCacheDirectory, FileName);

    /// <summary>上次成功对账的时刻。</summary>
    public DateTimeOffset? FetchedAt => _fetchedAt;

    /// <summary>查过的哈希数量。</summary>
    public int LookedUpCount => _lookedUp.Count;

    /// <summary>查到的匹配数量。</summary>
    public int MatchCount => _matches.Count;

    /// <summary>这个哈希**查过**吗（查过但没匹配 ⇒ 确实不在 Modrinth 收录）。</summary>
    /// <param name="sha1">哈希。</param>
    /// <returns>查过返回 <see langword="true"/>。</returns>
    public bool WasLookedUp(string sha1)
    {
        ArgumentNullException.ThrowIfNull(sha1);
        return _lookedUp.Contains(sha1);
    }

    /// <summary>取一个哈希的匹配结果。</summary>
    /// <param name="sha1">哈希。</param>
    /// <returns>匹配；没查到或没查过时为 <see langword="null"/>（用 <see cref="WasLookedUp"/> 区分这两者）。</returns>
    public ModrinthModMatch? Find(string sha1)
    {
        ArgumentNullException.ThrowIfNull(sha1);
        return _matches.GetValueOrDefault(sha1);
    }

    /// <summary>
    /// 一个缺失依赖 id 是否已被 Modrinth 明确判定为“不存在”。
    /// </summary>
    /// <remarks>
    /// 只认 404 的结果。没查过、查不到（网络失败）都返回 <see langword="false"/>，
    /// 界面仍按真实缺失显示 —— “不知道”不能当成“不存在”。
    /// </remarks>
    /// <param name="modId">依赖 id。</param>
    /// <returns>明确不存在返回 <see langword="true"/>。</returns>
    public bool IsDependencyKnownAbsent(string modId)
    {
        ArgumentNullException.ThrowIfNull(modId);
        return _dependencyExistence.TryGetValue(modId, out var exists) && !exists;
    }

    /// <summary>
    /// 对一批缺失依赖 id 查 Modrinth 是否存在。
    /// </summary>
    /// <param name="modIds">缺失的依赖 id。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否新增了“明确不存在”的结论（界面需要因此重扫模组）。</returns>
    /// <remarks>
    /// 逐条查 <c>/project/{id}</c>：依赖 id 就是 Modrinth 项目 id 或 slug 时能精确判定。
    /// 任何一条网络失败都**不写缓存**，并且本次返回 <see langword="false"/>。
    /// </remarks>
    public async Task<bool> RefreshMissingDependenciesAsync(
        IEnumerable<string> modIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        List<string> wanted = [.. modIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (wanted.Count == 0)
        {
            return false;
        }

        var changed = false;
        var dirty = false;

        foreach (var id in wanted)
        {
            if (_dependencyExistence.ContainsKey(id))
            {
                continue;
            }

            try
            {
                var exists = await _client
                    .ProjectExistsAsync(id, cancellationToken)
                    .ConfigureAwait(false);

                _dependencyExistence[id] = exists;
                dirty = true;
                changed |= !exists;
            }
            catch (OperationCanceledException)
            {
                if (dirty)
                {
                    Save();
                }

                return changed;
            }
            catch (ModrinthException)
            {
                // 客户端已经记了事件；这次按“未知”处理，不缓存、不抑制真实缺失。
                if (dirty)
                {
                    Save();
                }

                return changed;
            }
        }

        if (dirty)
        {
            Save();
        }

        return changed;
    }

    /// <summary>
    /// 对一批哈希做对账（精确查 + 查更新），成功后落盘。
    /// </summary>
    /// <param name="sha1s">要查的哈希（小写十六进制）。</param>
    /// <param name="gameVersion">当前实例的游戏版本（可为空）。</param>
    /// <param name="loader">当前实例的加载器短名（可为空）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>手上这份对账结果是否可用（新鲜度窗口内、或这次成功）。失败返回 <see langword="false"/> 并保留旧结果。</returns>
    /// <remarks>
    /// **不抛异常**：调用方是页面导航。失败原因由客户端记事件；这里只回答"结果能不能用"。
    /// </remarks>
    public async Task<bool> RefreshAsync(
        IReadOnlyList<string> sha1s,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sha1s);

        List<string> wanted = [.. sha1s
            .Where(static hash => !string.IsNullOrWhiteSpace(hash))
            .Select(static hash => hash.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (wanted.Count == 0)
        {
            return false;
        }

        var facets = (GameVersion: Blank(gameVersion), Loader: Blank(loader));

        if (IsFresh(facets) && wanted.All(_lookedUp.Contains))
        {
            return true;
        }

        IReadOnlyDictionary<string, ModrinthVersion> exact;
        IReadOnlyDictionary<string, ModrinthVersion> updates;

        try
        {
            exact = await _client.LookupHashesAsync(wanted, cancellationToken).ConfigureAwait(false);
            updates = await _client
                .FindUpdatesAsync(wanted, facets.GameVersion, facets.Loader, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModrinthException)
        {
            // 客户端已经记过事件。**两个请求要么都算数、要么整次不算**（见类型注释）。
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 与浏览列表同一条纪律：没预料到的失败也要留痕（调用方是 AsyncRelayCommand，异常会被吞掉）。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.Modrinth,
                "获取 Modrinth 数据失败（{0}）：{1}",
                ["mod-reconcile", ex.GetType().Name]);

            return false;
        }

        // 条件换了（另一个实例的游戏版本 / 加载器）就把旧账清掉重建：那一半结果是按条件算的，
        // 混在一起会让「可更新」说不清是按哪个条件判的。
        if (!SameFacets(facets))
        {
            _lookedUp.Clear();
            _matches.Clear();
        }

        foreach (var hash in wanted)
        {
            _lookedUp.Add(hash);
            _matches.Remove(hash);

            var hasExact = exact.TryGetValue(hash, out var version);
            var hasUpdate = updates.TryGetValue(hash, out var latest);

            // "查到更新"本身就说明这个哈希是 Modrinth 上的文件（update 端点也是按哈希匹配的）。
            if (!hasExact && !hasUpdate)
            {
                continue;
            }

            _matches[hash] = new ModrinthModMatch(
                Hash: hash,
                ProjectId: version?.ProjectId ?? latest?.ProjectId,
                VersionId: version?.Id,
                VersionNumber: version?.VersionNumber,
                LatestVersionId: hasUpdate ? latest?.Id : null,
                LatestVersionNumber: hasUpdate ? latest?.VersionNumber : null);
        }

        _gameVersion = facets.GameVersion;
        _loader = facets.Loader;
        _fetchedAt = _clock.Now;

        Save();

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Modrinth,
            "已完成本地模组与 Modrinth 的对账（{0} 个文件，{1} 个在 Modrinth 上，{2}）",
            [
                wanted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _matches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _clock.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            ]);

        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool IsFresh((string? GameVersion, string? Loader) facets) =>
        _fetchedAt is { } fetched
        && _clock.Now >= fetched
        && _clock.Now - fetched < Freshness
        && SameFacets(facets);

    private bool SameFacets((string? GameVersion, string? Loader) facets) =>
        string.Equals(_gameVersion, facets.GameVersion, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_loader, facets.Loader, StringComparison.OrdinalIgnoreCase);

    private void Save() =>
        AtomicFile.WriteAllText(
            FilePath,
            LauncherJson.Serialize(
                new RawFile
                {
                    SchemaVersion = CacheSchemaVersion,
                    FetchedAt = _fetchedAt,
                    GameVersion = _gameVersion,
                    Loader = _loader,
                    LookedUp = [.. _lookedUp],
                    DependencyExistence = _dependencyExistence.ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase),
                    Matches = _matches.ToDictionary(
                        static pair => pair.Key,
                        static pair => new RawMatch
                        {
                            ProjectId = pair.Value.ProjectId,
                            VersionId = pair.Value.VersionId,
                            VersionNumber = pair.Value.VersionNumber,
                            LatestVersionId = pair.Value.LatestVersionId,
                            LatestVersionNumber = pair.Value.LatestVersionNumber,
                        },
                        StringComparer.OrdinalIgnoreCase),
                },
                LauncherJson.Indented));

    private void Load()
    {
        var json = AtomicFile.ReadAllTextOrNull(FilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var file = LauncherJson.Deserialize<RawFile>(json, LauncherJson.Indented);

            if (file is null || file.SchemaVersion != CacheSchemaVersion)
            {
                Warn("结构版本不认识");
                return;
            }

            _fetchedAt = file.FetchedAt;
            _gameVersion = file.GameVersion;
            _loader = file.Loader;

            foreach (var hash in file.LookedUp ?? [])
            {
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    _lookedUp.Add(hash);
                }
            }

            if (file.Matches is { Count: > 0 } matches)
            {
                foreach (var (hash, match) in matches)
                {
                    _matches[hash] = new ModrinthModMatch(
                        Hash: hash,
                        ProjectId: match.ProjectId,
                        VersionId: match.VersionId,
                        VersionNumber: match.VersionNumber,
                        LatestVersionId: match.LatestVersionId,
                        LatestVersionNumber: match.LatestVersionNumber);
                }
            }

            if (file.DependencyExistence is { Count: > 0 } dependencyExistence)
            {
                foreach (var (id, exists) in dependencyExistence)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        _dependencyExistence[id] = exists;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            Warn(ex.GetType().Name);
        }
    }

    private void Warn(string reason)
    {
        if (!_warnings.ShouldReport(FilePath))
        {
            return;
        }

        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.Launcher,
            "Modrinth 对账缓存不可用（{0}，{1}），本次按无缓存处理（原文件未改动）",
            [FileName, reason]);
    }

    /// <summary>落盘形状。</summary>
    private sealed record RawFile
    {
        public int SchemaVersion { get; init; }

        public DateTimeOffset? FetchedAt { get; init; }

        public string? GameVersion { get; init; }

        public string? Loader { get; init; }

        public IReadOnlyList<string>? LookedUp { get; init; }

        public IReadOnlyDictionary<string, bool>? DependencyExistence { get; init; }

        public IReadOnlyDictionary<string, RawMatch>? Matches { get; init; }
    }

    /// <summary>一条匹配的落盘形状。</summary>
    private sealed record RawMatch
    {
        public string? ProjectId { get; init; }

        public string? VersionId { get; init; }

        public string? VersionNumber { get; init; }

        public string? LatestVersionId { get; init; }

        public string? LatestVersionNumber { get; init; }
    }
}
