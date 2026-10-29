using EiTRVO.Core.Launcher.Diagnostics;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// 只把事件记在内存里的诊断日志：用来断言"发生了什么事"，
/// 而不必去解析落盘文件（那是 <c>DiagnosticsLogTests</c> 的职责）。
/// </summary>
internal sealed class RecordingDiagnosticsLog : IDiagnosticsLog
{
    /// <summary>按写入顺序记录的事件。</summary>
    public List<LauncherEvent> Entries { get; } = [];

    /// <inheritdoc />
    public void Write(
        LauncherEventLevel level,
        string source,
        string messageKey,
        IReadOnlyList<string>? parameters = null,
        string? instanceName = null) =>
        Entries.Add(new LauncherEvent
        {
            Level = level,
            Source = source,
            MessageKey = messageKey,
            Parameters = parameters ?? [],
            InstanceName = instanceName,
        });

    /// <inheritdoc />
    public EventLogSnapshot Read(int maximumCount) =>
        new(Entries.Count, maximumCount <= 0 ? [] : [.. Entries.TakeLast(maximumCount)]);

    /// <inheritdoc />
    /// <remarks>
    /// M6-f：这个替身只记录，不发通知（没有界面在等它）。显式写 add/remove 而不是空事件字段，
    /// 是因为"字段式事件从未被触发"会触发 CS0067，而本项目把警告当错误。
    /// </remarks>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
