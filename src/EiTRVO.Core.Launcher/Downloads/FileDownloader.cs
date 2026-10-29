using System.Buffers;
using System.Security.Cryptography;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Downloads;

/// <summary>
/// 并发下载器（F-9）：校验优先、<c>.part</c> 原子落盘、文件级重试、镜像回退。
/// <para>
/// 六条刻意的设计，每条都对应一类真实事故：
/// </para>
/// <list type="number">
/// <item><b>先校验再下载</b>：目标文件已存在且 SHA-1 正确就跳过。重装/续装时这一步能省掉 90% 的流量
/// （玩家最常见的操作就是"装到一半断了再点一次"）。</item>
/// <item><b>写 <c>.part</c> 再改名</b>：任何时刻中断都不会留下一个"看起来完整、其实是半截"的 JAR ——
/// 半截 JAR 的后果是启动时一个莫名其妙的 <c>ClassNotFoundException</c>，而不是"重新下载一次"。</item>
/// <item><b>校验不通过就换候选重下</b>：镜像回退不只是"连不上才回退"。镜像返回一份被篡改/过期的文件时，
/// SHA-1 是唯一能发现它的东西。</item>
/// <item><b>没有 SHA-1 的项如实标记为"不可校验"</b>（老版 Forge 的库就是这样），下载但不假装校验过。</item>
/// <item><b>进度按时间节流</b>：几千个资源文件逐个报告会把 UI 线程淹没（AGENTS §6）。</item>
/// <item><b>失败清单带原因</b>：校验失败与网络失败在界面上是两句不同的话，不能都叫"下载失败"。</item>
/// </list>
/// </summary>
public sealed class FileDownloader
{
    /// <summary>临时文件后缀。</summary>
    public const string PartSuffix = ".part";

    /// <summary>进度报告的最小间隔。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpHostPolicy _hostPolicy;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    /// <summary>构造下载器。</summary>
    /// <param name="httpClientFactory">命名客户端工厂。</param>
    /// <param name="hostPolicy">出站域名策略（镜像地址也要过）。</param>
    /// <param name="clock">时间源（速度与剩余时间要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    /// <param name="delayAsync">重试前的等待实现；缺省用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。</param>
    public FileDownloader(
        IHttpClientFactory httpClientFactory,
        HttpHostPolicy hostPolicy,
        IClock clock,
        IDiagnosticsLog diagnostics,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(hostPolicy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _hostPolicy = hostPolicy;
        _clock = clock;
        _diagnostics = diagnostics;
        _delayAsync = delayAsync ?? (static (delay, token) => Task.Delay(delay, token));
    }

    /// <summary>下载一批文件。</summary>
    /// <param name="items">待下载项。</param>
    /// <param name="options">下载选项。</param>
    /// <param name="progress">进度接收者；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果（含逐条失败原因）。</returns>
    public async Task<DownloadOutcome> DownloadAsync(
        IReadOnlyList<DownloadItem> items,
        DownloadOptions options,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        List<DownloadItem> pending = [];
        var skipped = 0;
        long announcedBytes = 0;

        foreach (var item in items)
        {
            if (await IsAlreadyCorrectAsync(item, cancellationToken).ConfigureAwait(false))
            {
                skipped++;

                // 已存在的文件也算进度：续装时用户看到的应该是"已经完成了 90%"，而不是从 0 开始。
                announcedBytes += Math.Max(0, item.Size);
                continue;
            }

            pending.Add(item);
            announcedBytes += Math.Max(0, item.Size);
        }

        var state = new ProgressState(items.Count, announcedBytes, _clock.Now, skipped);

        state.Report(currentFile: string.Empty, progress, _clock.Now, force: true);

        if (pending.Count == 0)
        {
            return new DownloadOutcome(0, 0, skipped, 0, []);
        }

        var concurrency = Math.Max(1, options.Concurrency);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DownloadFailure>();

        var workers = pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var failure = await DownloadOneAsync(item, options, state, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (failure is not null)
                {
                    failures.Enqueue(failure);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(workers).ConfigureAwait(false);

        // **再强制报一次终态**：报告是按时间节流的（200ms），如果整批文件在 200ms 内就下完了
        // （小文件、缓存、或者测试里的固定时钟），节流会让"最后一个文件完成了"这件事永远报不出去，
        // 界面上就会永远停在进度的中间。终态必须无条件送达。
        state.Report(currentFile: string.Empty, progress, _clock.Now, force: true);

        List<DownloadFailure> ordered = [.. failures];

        _diagnostics.Write(
            failures.IsEmpty ? LauncherEventLevel.Information : LauncherEventLevel.Warning,
            DiagnosticsSources.Downloader,
            failures.IsEmpty ? "下载完成（{0} 个文件，跳过 {1} 个，共 {2} 个）" : "下载完成但有文件失败（{0} 个文件，跳过 {1} 个，共 {2} 个）",
            [
                items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                skipped.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);

        return new DownloadOutcome(
            TotalFiles: pending.Count,
            CompletedFiles: pending.Count - ordered.Count,
            SkippedFiles: skipped,
            DownloadedBytes: state.DownloadedBytes,
            Failures: ordered);
    }

    /// <summary>下载单个文件；成功返回 <see langword="null"/>，失败返回失败记录。</summary>
    private async Task<DownloadFailure?> DownloadOneAsync(
        DownloadItem item,
        DownloadOptions options,
        ProgressState state,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partPath = item.TargetPath + PartSuffix;
        string[] candidates = [item.Url];
        var attempts = Math.Max(1, options.MaxAttemptsPerFile);

        TextTemplate? lastReason = null;
        var lastWasChecksum = false;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            foreach (var url in candidates)
            {
                if (!TryEnsureAllowed(url))
                {
                    // 候选地址不在白名单里（例如被改过的清单）：跳过它，而不是把请求发出去。
                    continue;
                }

                state.SourceHost = new Uri(url).Host;
                state.Report(item.DisplayName, progress, _clock.Now, force: false);

                try
                {
                    var written = await FetchAsync(item, url, partPath, state, progress, cancellationToken)
                        .ConfigureAwait(false);

                    if (item.NeedsVerification)
                    {
                        var (sha1, sha512) = await ComputeHashesAsync(
                                partPath,
                                needSha1: item.IsVerifiable,
                                needSha512: item.HasSha512,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (item.IsVerifiable
                            && !string.Equals(sha1, item.Sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            lastReason = TextTemplate.Of(
                                "{0} 校验不通过：期望 {1}，实际 {2}",
                                item.DisplayName,
                                item.Sha1,
                                sha1);
                            lastWasChecksum = true;

                            // 这一份的字节不算进度：否则校验失败时百分比会虚高。
                            state.SubtractBytes(written);
                            continue;
                        }

                        if (item.HasSha512
                            && !string.Equals(sha512, item.Sha512, StringComparison.OrdinalIgnoreCase))
                        {
                            // SHA-512 也要报出**期望值**：两个摘要同时不符时，用户/排障要能看出
                            // 是"内容被换了"而不是"我们算错了"。
                            lastReason = TextTemplate.Of(
                                "{0} 校验不通过：期望 {1}，实际 {2}",
                                item.DisplayName,
                                item.Sha512,
                                sha512);
                            lastWasChecksum = true;
                            state.SubtractBytes(written);
                            continue;
                        }
                    }

                    AtomicFile.CommitDownload(partPath, item.TargetPath);
                    state.MarkCompleted();

                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                {
                    // 参数里放的是**运行时给的**消息（BCL / 网络层的原文），它算"技术细节"不是我们的文案。
                    lastReason = TextTemplate.Of("{0} 下载失败：{1}", item.DisplayName, ex.Message);
                    lastWasChecksum = false;
                }
            }

            if (attempt < attempts)
            {
                await _delayAsync(RetryPolicy.Default.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        DeleteQuietly(partPath);

        return new DownloadFailure(
            item,
            lastReason ?? TextTemplate.Of("{0} 下载失败：所有候选地址都不可用", item.DisplayName),
            lastWasChecksum);
    }

    private async Task<long> FetchAsync(
        DownloadItem item,
        string url,
        string partPath,
        ProgressState state,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var response = await client
            .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase} @ {new Uri(url).Host}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(partPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            partPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long written = 0;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                state.AddBytes(read);
                state.Report(item.DisplayName, progress, _clock.Now, force: false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return written;
    }

    /// <summary>目标文件是否已存在且内容正确。</summary>
    /// <remarks>
    /// 刻意做成异步：几千个资源文件要逐个算 SHA-1，同步阻塞在这里等于让调用线程白等；
    /// 而 <c>.GetAwaiter().GetResult()</c> 那类写法被 AGENTS §2.2 明令禁止（无论等的是网络还是磁盘）。
    /// </remarks>
    private static async Task<bool> IsAlreadyCorrectAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        if (!File.Exists(item.TargetPath))
        {
            return false;
        }

        if (!item.NeedsVerification)
        {
            // 没有校验值时只比大小；连大小都没有就只认"文件在"。
            var length = new FileInfo(item.TargetPath).Length;
            return item.Size <= 0 || length == item.Size;
        }

        var (sha1, sha512) = await ComputeHashesAsync(
                item.TargetPath,
                needSha1: item.IsVerifiable,
                needSha512: item.HasSha512,
                cancellationToken)
            .ConfigureAwait(false);

        return (!item.IsVerifiable || string.Equals(sha1, item.Sha1, StringComparison.OrdinalIgnoreCase))
               && (!item.HasSha512 || string.Equals(sha512, item.Sha512, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 算文件的摘要（**一次读盘算完两个**）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="needSha1">是否要 SHA-1。</param>
    /// <param name="needSha512">是否要 SHA-512。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>两个摘要（不需要的那个是空串），小写十六进制。</returns>
    /// <remarks>
    /// 两个都要时**不读两遍**：一次安装要下几千个资源对象，多读一遍等于白花一倍磁盘 IO。
    /// 调用方按需挑：游戏的资源对象只要 sha1，<c>.mrpack</c> 的文件两个都要（规范要求）。
    /// </remarks>
    private static async Task<(string Sha1, string Sha512)> ComputeHashesAsync(
        string path,
        bool needSha1,
        bool needSha512,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        // CA5350（SHA-1 是弱算法）在这里**不适用**：这个哈希不是我们选的安全机制，
        // 而是 Mojang 清单协议里**规定的**完整性字段（版本 JSON 的 sha1、资源对象的文件名就是它的哈希）。
        // 换 SHA-256 只会让所有校验都失败；这里校验的是"下载有没有错/是不是半截"，
        // 传输本身走 HTTPS，清单也来自 HTTPS。Modrinth 的 .mrpack 两个摘要都校验。
#pragma warning disable CA5350
        using var sha1 = needSha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
#pragma warning restore CA5350
        using var sha512 = needSha512 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512) : null;

        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                // 刻意用 `AppendData(byte[], int, int)` 而不是 `AsSpan(...)`：Span 是 ref struct，
                // 在 async 方法里根本活不下来（C# 12 报 CS9202）。
                sha1?.AppendData(buffer, 0, read);
                sha512?.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (
            sha1 is null ? string.Empty : Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            sha512 is null ? string.Empty : Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant());
    }

    private bool TryEnsureAllowed(string url)
    {
        try
        {
            _hostPolicy.EnsureAllowed(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null);
            return true;
        }
        catch (HttpPolicyException)
        {
            return false;
        }
    }

    private static void DeleteQuietly(string path)
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
            // 删不掉一个 .part 不值得让整次下载失败。
        }
    }

    /// <summary>共享的进度累加器（多个下载线程同时读写）。</summary>
    private sealed class ProgressState(int totalFiles, long totalBytes, DateTimeOffset startedAt, int skippedFiles)
    {
        private readonly object _gate = new();
        private long _bytes;
        private int _completed;
        private long _downloadedBytes;
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        /// <summary>当前实际在用的下载主机（镜像回退时会变）；进度区「数据源」读它。</summary>
        public string SourceHost { get; set; } = string.Empty;

        /// <summary>本次实际写入磁盘的字节数（不含已存在而跳过的）。</summary>
        public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

        /// <summary>累加已写入的字节。</summary>
        /// <param name="count">字节数。</param>
        public void AddBytes(long count)
        {
            Interlocked.Add(ref _bytes, count);
            Interlocked.Add(ref _downloadedBytes, count);
        }

        /// <summary>回退一次失败尝试的字节（校验不通过时）。</summary>
        /// <param name="count">字节数。</param>
        public void SubtractBytes(long count)
        {
            Interlocked.Add(ref _bytes, -count);
            Interlocked.Add(ref _downloadedBytes, -count);
        }

        /// <summary>标记一个文件完成。</summary>
        public void MarkCompleted() => Interlocked.Increment(ref _completed);

        /// <summary>报告一次进度（按时间节流）。</summary>
        /// <param name="currentFile">当前文件名。</param>
        /// <param name="progress">接收者。</param>
        /// <param name="now">当前时间。</param>
        /// <param name="force">是否忽略节流强制报告。</param>
        public void Report(string currentFile, IProgress<FileTransferProgress>? progress, DateTimeOffset now, bool force)
        {
            if (progress is null)
            {
                return;
            }

            lock (_gate)
            {
                if (!force && now - _lastReport < ProgressInterval)
                {
                    return;
                }

                _lastReport = now;
            }

            var bytes = Interlocked.Read(ref _bytes);
            var elapsed = now - startedAt;
            var speed = elapsed.TotalSeconds > 0 ? bytes / elapsed.TotalSeconds : 0;

            TimeSpan? remaining = null;
            if (speed > 0 && totalBytes > bytes)
            {
                remaining = TimeSpan.FromSeconds((totalBytes - bytes) / speed);
            }

            progress.Report(new FileTransferProgress(
                CompletedFiles: Volatile.Read(ref _completed) + skippedFiles,
                TotalFiles: totalFiles,
                CompletedBytes: bytes,
                TotalBytes: totalBytes,
                CurrentFile: currentFile,
                BytesPerSecond: speed,
                Remaining: remaining)
            {
                SourceHost = SourceHost,
            });
        }
    }
}
