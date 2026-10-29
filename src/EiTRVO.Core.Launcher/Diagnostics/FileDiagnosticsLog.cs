using System.Text;
using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Diagnostics;

/// <summary>
/// 把诊断事件按行追加到 <c>log/events.jsonl</c>（一行一个 JSON 对象）。
/// <para>
/// 选 JSONL 而不是「一个大 JSON 数组」的理由是**崩溃场景**：数组必须整文件重写才能追加，
/// 而崩溃恰恰发生在写入过程中；JSONL 只追加，进程被杀最多让最后一行残缺，
/// 读的时候跳过坏行即可（<see cref="Read"/> 就是这么做的）。
/// </para>
/// <para>
/// 每写一行都关闭文件句柄，因此不需要在退出时"记得 flush" —— F-10 的崩溃日志正是
/// 在无法保证优雅退出的路径上写的。
/// </para>
/// </summary>
public sealed class FileDiagnosticsLog : IDiagnosticsLog
{
    /// <summary>事件文件名。</summary>
    public const string FileName = "events.jsonl";

    /// <summary>默认单文件上限（4 MiB）。超过就轮转一代。</summary>
    public const long DefaultMaxBytes = 4L * 1024 * 1024;

    /// <summary>轮转后的上一代文件名后缀。</summary>
    public const string RotatedSuffix = ".1";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILauncherPaths _paths;
    private readonly IClock _clock;
    private readonly long _maxBytes;
    private readonly object _gate = new();

    /// <summary>构造诊断日志（默认 4 MiB 轮转）。</summary>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="clock">时间源（时间戳必须可测）。</param>
    public FileDiagnosticsLog(ILauncherPaths paths, IClock clock)
        : this(paths, clock, DefaultMaxBytes)
    {
    }

    /// <summary>构造诊断日志并指定轮转阈值（单测用来把轮转缩小到可观测的规模）。</summary>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="clock">时间源。</param>
    /// <param name="maxBytes">单文件字节上限。</param>
    public FileDiagnosticsLog(ILauncherPaths paths, IClock clock, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        _paths = paths;
        _clock = clock;
        _maxBytes = maxBytes;
    }

    /// <summary>当前事件文件路径。</summary>
    public string FilePath => Path.Combine(_paths.LogDirectory, FileName);

    /// <inheritdoc />
    public void Write(
        LauncherEventLevel level,
        string source,
        string messageKey,
        IReadOnlyList<string>? parameters = null,
        string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);

        var entry = new LauncherEvent
        {
            Timestamp = _clock.Now,
            Level = level,
            Source = source,
            MessageKey = messageKey,
            Parameters = parameters ?? [],
            InstanceName = instanceName,
        };

        var line = LauncherJson.Serialize(entry, LauncherJson.Compact);

        lock (_gate)
        {
            Directory.CreateDirectory(_paths.LogDirectory);
            var path = FilePath;
            RotateIfNeeded(path);
            File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
        }

        // M6-f：**落盘之后**才通知（订阅者立刻去读就能读到这一条），而且锁外通知
        //（订阅者去读日志时会再进 Read，锁里通知会自锁）。逐订阅者吞异常：日志是诊断的
        // 最后一道防线，某个订阅者坏了不能让"写日志"这件事失败。
        RaiseChanged();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>把「有新事件」告诉所有订阅者；任何一个订阅者抛异常都不影响其它订阅者与这次写入。</summary>
    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 故意吞掉：日志写入不能因为订阅者的问题而失败（这条规矩写在 IDiagnosticsLog.Changed 上）。
            }
        }
    }

    /// <inheritdoc />
    public EventLogSnapshot Read(int maximumCount)
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return new EventLogSnapshot(0, []);
        }

        var recent = maximumCount > 0
            ? new Queue<LauncherEvent>(maximumCount)
            : null;
        var total = 0;

        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                LauncherEvent? entry;
                try
                {
                    entry = LauncherJson.Deserialize<LauncherEvent>(line, LauncherJson.Compact);
                }
                catch (JsonException)
                {
                    // 崩溃时被打断写下的半截行：跳过，不让它毁掉整份日志。
                    continue;
                }

                if (entry is null)
                {
                    continue;
                }

                total++;

                if (recent is null)
                {
                    continue;
                }

                if (recent.Count == maximumCount)
                {
                    recent.Dequeue();
                }

                recent.Enqueue(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志读取失败不该让调用方（含崩溃提示路径）再崩一次。
            return new EventLogSnapshot(0, []);
        }

        return new EventLogSnapshot(total, recent is null ? [] : [.. recent]);
    }

    private void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        File.Move(path, path + RotatedSuffix, overwrite: true);
    }
}
