namespace EiTRVO.Core.Launcher.Storage;

/// <summary>
/// 「同一个文件的同一版内容只报一次警」的闸门。
/// <para>
/// 为什么需要它：读取用户文件的地方（设置、实例元数据）大多在**每次页面激活**时被调用。
/// 文件坏了就每次读都写一条事件的话，日志会被同一条消息刷满，
/// 真正的新事件反而被埋掉 —— 而排障时最需要看到的恰恰是新事件。
/// </para>
/// <para>
/// 判据是（长度, 最后写入时间）：用户改了这个文件就再报一次，只看不改就不重复报。
/// </para>
/// </summary>
public sealed class FileStampGate
{
    private readonly Dictionary<string, (long Length, DateTime LastWrite)> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    /// <summary>判断这一次是否应当报警。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>内容与上次报警时不同则为 <see langword="true"/>。</returns>
    public bool ShouldReport(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);
        var stamp = (info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : default);

        lock (_lock)
        {
            if (_seen.TryGetValue(path, out var previous) && previous == stamp)
            {
                return false;
            }

            _seen[path] = stamp;
            return true;
        }
    }
}
