using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Content.Modrinth;

/// <summary>浏览列表的一份快照（时间戳 + 总数 + 行）。</summary>
/// <param name="FetchedAt">取回的时刻（新鲜度判断用它，不用文件时间：文件时间不受 <c>IClock</c> 控制，测不了）。</param>
/// <param name="TotalHits">Modrinth 报告的匹配总数。</param>
/// <param name="Entries">表格行。</param>
public sealed record ModrinthBrowseSnapshot(
    DateTimeOffset FetchedAt,
    int TotalHits,
    IReadOnlyList<ModrinthEntry> Entries);

/// <summary>
/// Modrinth 元数据的**磁盘缓存**（P1-10）：<c>cache\content\modrinth-browse.json</c>。
/// </summary>
/// <remarks>
/// 与版本清单缓存同源的三条规矩：
/// <list type="number">
/// <item><b>存我们自己序列化的快照</b>（不是远端原始文本）：这里要的是"行 + 时间戳"，
/// 而且它跨版本演进（schemaVersion）—— 与"原始响应原样落盘"的清单缓存不是一回事。</item>
/// <item><b>坏缓存当没有缓存，但**不删文件**</b>：下一次成功取回会原子覆盖它；
/// 而删掉就等于把用户手上唯一那份离线数据抹了。</item>
/// <item><b>同一个坏文件只在内容变化后再报一次警</b>（<see cref="FileStampGate"/>）。</item>
/// </list>
/// </remarks>
public sealed class ModrinthCache
{
    /// <summary>浏览列表缓存的文件名。</summary>
    public const string BrowseFileName = "modrinth-browse.json";

    /// <summary>当前快照格式版本（换了字段含义就要 +1，见 <see cref="TryReadBrowse"/>）。</summary>
    private const int CacheSchemaVersion = 1;

    private readonly ILauncherPaths _paths;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly FileStampGate _warnings = new();

    /// <summary>构造缓存。</summary>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public ModrinthCache(ILauncherPaths paths, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _paths = paths;
        _diagnostics = diagnostics;
        _paths.EnsureCreated();
    }

    /// <summary>浏览列表缓存文件路径。</summary>
    public string BrowsePath => Path.Combine(_paths.ContentCacheDirectory, BrowseFileName);

    /// <summary>读快照。</summary>
    /// <returns>快照；没有缓存、缓存坏了或版本不认识时为 <see langword="null"/>。</returns>
    public ModrinthBrowseSnapshot? TryReadBrowse()
    {
        var json = AtomicFile.ReadAllTextOrNull(BrowsePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var file = LauncherJson.Deserialize<BrowseFile>(json, LauncherJson.Indented);

            if (file is null || file.SchemaVersion != CacheSchemaVersion)
            {
                Warn("结构版本不认识");
                return null;
            }

            return new ModrinthBrowseSnapshot(file.FetchedAt, file.TotalHits, file.Entries ?? []);
        }
        catch (JsonException ex)
        {
            Warn(ex.GetType().Name);
            return null;
        }
    }

    /// <summary>写快照（原子写）。</summary>
    /// <param name="snapshot">快照。</param>
    public void WriteBrowse(ModrinthBrowseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        AtomicFile.WriteAllText(
            BrowsePath,
            LauncherJson.Serialize(
                new BrowseFile
                {
                    SchemaVersion = CacheSchemaVersion,
                    FetchedAt = snapshot.FetchedAt,
                    TotalHits = snapshot.TotalHits,
                    Entries = snapshot.Entries,
                },
                LauncherJson.Indented));
    }

    private void Warn(string reason)
    {
        if (!_warnings.ShouldReport(BrowsePath))
        {
            return;
        }

        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.Launcher,
            "Modrinth 浏览缓存不可用（{0}，{1}），本次按无缓存处理（原文件未改动）",
            [BrowseFileName, reason]);
    }

    /// <summary>落盘形状（带结构版本，字段含义变化时能识别出来）。</summary>
    private sealed record BrowseFile
    {
        public int SchemaVersion { get; init; }

        public DateTimeOffset FetchedAt { get; init; }

        public int TotalHits { get; init; }

        public IReadOnlyList<ModrinthEntry>? Entries { get; init; }
    }
}
