using System.Security.Cryptography;
using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Content.Modrinth;

/// <summary>
/// 本地模组 jar 的 SHA-1 索引（<c>cache\content\mod-hashes.json</c>）。
/// </summary>
/// <remarks>
/// 存在的理由很实际：**对账要用哈希，而哈希要把整个 jar 读一遍**。
/// 一个两百个模组的整合包就是 1 GB 上下的磁盘读，这件事**只能在异步刷新里发生一次**，
/// 绝不能在"每次绑定求值"的同步扫描里发生 —— 所以：
/// <list type="bullet">
/// <item><b>同步读索引、异步算哈希</b>：<see cref="TryGetSha1(string, out string)"/> 只查表（不碰磁盘内容），
/// <see cref="Sha1Of"/> 才真的读一遍并记下结果；</item>
/// <item><b>落盘</b>：不然每次重启启动器就要把几百兆重读一遍；</item>
/// <item><b>判据是（长度, 最后写入时间）</b>：用户换了 jar 就重算，只读不写就复用 ——
/// 与 <see cref="FileStampGate"/> 同一条判据。</item>
/// </list>
/// <para>
/// 纪律与其它缓存一致：坏文件**当没有缓存处理且不删文件**，同一版内容只报一次警，
/// 写入走原子写。
/// </para>
/// </remarks>
public sealed class ModHashIndex
{
    /// <summary>索引文件名。</summary>
    public const string FileName = "mod-hashes.json";

    /// <summary>索引的算法（写进文件，将来换算法时能识别出来）。</summary>
    public const string AlgorithmName = "sha1";

    /// <summary>当前格式版本。</summary>
    private const int CacheSchemaVersion = 1;

    /// <summary>读哈希时的缓冲区（64 KiB；SHA-1 是流式的，不需要把整个 jar 读进内存）。</summary>
    private const int BufferSize = 64 * 1024;

    private readonly ILauncherPaths _paths;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly FileStampGate _warnings = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private bool _dirty;

    /// <summary>构造索引（并把上次的结果读进内存）。</summary>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public ModHashIndex(ILauncherPaths paths, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _paths = paths;
        _diagnostics = diagnostics;
        _paths.EnsureCreated();

        Load();
    }

    /// <summary>索引文件路径。</summary>
    public string FilePath => Path.Combine(_paths.ContentCacheDirectory, FileName);

    /// <summary>已经记下的条目数（排障与测试用）。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 查一个 jar 的 SHA-1（**只查表，不读文件内容**）。
    /// </summary>
    /// <param name="jarPath">jar 路径。</param>
    /// <param name="sha1">查到的哈希（小写十六进制）。</param>
    /// <returns>表里有、且（长度, 最后写入时间）与当前文件一致时为 <see langword="true"/>。</returns>
    public bool TryGetSha1(string jarPath, out string sha1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);

        sha1 = string.Empty;

        if (!_entries.TryGetValue(jarPath, out var entry))
        {
            return false;
        }

        var info = new FileInfo(jarPath);
        if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc.Ticks != entry.LastWriteTicks)
        {
            return false;
        }

        sha1 = entry.Sha1;
        return true;
    }

    /// <summary>
    /// 取一个 jar 的 SHA-1：索引里有就用它，没有就**真的读一遍**并记下结果（不落盘，由调用方 <see cref="Save"/>）。
    /// </summary>
    /// <param name="jarPath">jar 路径。</param>
    /// <returns>小写十六进制的 SHA-1；文件读不出来时为 <see langword="null"/>。</returns>
    public string? Sha1Of(string jarPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);

        if (TryGetSha1(jarPath, out var cached))
        {
            return cached;
        }

        var info = new FileInfo(jarPath);
        if (!info.Exists)
        {
            return null;
        }

        string sha1;

        try
        {
            using var stream = File.OpenRead(jarPath);

            // CA5350（SHA-1 是弱算法）在这里**不适用**：这个哈希不是我们选的安全机制，
            // 而是 Modrinth 协议本身的内容寻址方式（`/version_files` 只认 sha1 与 sha512）——
            // 它在这里回答的是"这份 jar 是哪一份"，不是防篡改的手段。
            // 与下载器、官方运行时清单三处的处置一致。
#pragma warning disable CA5350
            sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
#pragma warning restore CA5350
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个 jar 读不出来（被占用 / 权限）不该让整次对账失败：这一行留在"来源未知"。
            return null;
        }

        _entries[jarPath] = new Entry(sha1, info.Length, info.LastWriteTimeUtc.Ticks);
        _dirty = true;
        return sha1;
    }

    /// <summary>
    /// 文件改名之后，把索引记录**跟着搬过去**（M7-g-2：启用 / 禁用模组是改名）。
    /// </summary>
    /// <param name="oldPath">原路径。</param>
    /// <param name="newPath">新路径。</param>
    /// <remarks>
    /// 不搬的话，禁用再启用一次模组就会丢掉「来源：Modrinth」—— 索引按路径查，改名后那个路径
    /// 没有记录，而 <see cref="Sha1Of"/> 只会等到下一次异步对账才重算一遍（对两百个模组就是 1 GB 的磁盘读）。
    /// 改名不改变文件内容与（长度, 最后写入时间），所以搬过去的记录仍然有效。
    /// </remarks>
    public void Move(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        if (!_entries.Remove(oldPath, out var entry))
        {
            // 没算过哈希的模组本来就没有记录：没什么可搬的，也不必让索引变脏。
            return;
        }

        _entries[newPath] = entry;
        _dirty = true;
    }

    /// <summary>忘掉一个文件的哈希（删除模组时用，免得索引里留一个永远不存在的路径）。</summary>
    /// <param name="jarPath">被删掉的文件路径。</param>
    public void Forget(string jarPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);

        if (_entries.Remove(jarPath))
        {
            _dirty = true;
        }
    }

    /// <summary>把内存里的索引写回磁盘（剪掉已经不在的文件，别让索引无限长大）。</summary>
    public void Save()
    {
        if (!_dirty)
        {
            return;
        }

        List<string> missing = [.. _entries.Keys.Where(static path => !File.Exists(path))];

        foreach (var path in missing)
        {
            _entries.Remove(path);
        }

        Dictionary<string, RawEntry> files = new(StringComparer.Ordinal);

        foreach (var (path, entry) in _entries)
        {
            files[path] = new RawEntry
            {
                Sha1 = entry.Sha1,
                Length = entry.Length,
                LastWriteTicks = entry.LastWriteTicks,
            };
        }

        AtomicFile.WriteAllText(
            FilePath,
            LauncherJson.Serialize(
                new RawFile
                {
                    SchemaVersion = CacheSchemaVersion,
                    Algorithm = AlgorithmName,
                    Files = files,
                },
                LauncherJson.Indented));

        _dirty = false;
    }

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

            if (file is null || file.SchemaVersion != CacheSchemaVersion
                || !string.Equals(file.Algorithm, AlgorithmName, StringComparison.OrdinalIgnoreCase))
            {
                Warn("结构版本或算法不认识");
                return;
            }

            if (file?.Files is { Count: > 0 } files)
            {
                foreach (var (path, entry) in files)
                {
                    if (entry?.Sha1 is { Length: > 0 } sha1)
                    {
                        _entries[path] = new Entry(sha1, entry.Length, entry.LastWriteTicks);
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
            "模组哈希缓存不可用（{0}，{1}），本次按无缓存处理（原文件未改动）",
            [FileName, reason]);
    }

    /// <summary>一条索引记录。</summary>
    private sealed record Entry(string Sha1, long Length, long LastWriteTicks);

    /// <summary>落盘形状。</summary>
    private sealed record RawFile
    {
        public int SchemaVersion { get; init; }

        public string? Algorithm { get; init; }

        public IReadOnlyDictionary<string, RawEntry>? Files { get; init; }
    }

    /// <summary>落盘的一条记录。</summary>
    private sealed record RawEntry
    {
        public string? Sha1 { get; init; }

        public long Length { get; init; }

        public long LastWriteTicks { get; init; }
    }
}
