namespace EiTRVO.Core.Launcher.Events;

/// <summary>事件级别。</summary>
public enum EventLevel
{
    /// <summary>信息。</summary>
    Info,

    /// <summary>成功。</summary>
    Success,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error,
}

/// <summary>事件来源。决定来源列是否带小图标。</summary>
public enum EventSource
{
    /// <summary>启动器本体。</summary>
    Launcher,

    /// <summary>下载器。</summary>
    Downloader,

    /// <summary>Modrinth 内容源。</summary>
    Modrinth,

    /// <summary>Java 运行时管理。</summary>
    JavaRuntime,

    /// <summary>模组加载器安装器。</summary>
    ModLoader,

    /// <summary>启动核心。</summary>
    LaunchCore,

    /// <summary>实例配置变更。</summary>
    InstanceSettings,
}

/// <summary>事件表的一行（原图「事件」表）。</summary>
/// <param name="InstanceName">实例名。</param>
/// <param name="Id">事件 ID。</param>
/// <param name="Level">级别。</param>
/// <param name="Source">来源。</param>
/// <param name="SourceText">来源显示文本。</param>
/// <param name="Message">消息。</param>
/// <param name="Time">时间文本。</param>
public sealed record EventEntry(
    string InstanceName,
    int Id,
    EventLevel Level,
    EventSource Source,
    string SourceText,
    string Message,
    string Time);
