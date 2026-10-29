namespace EiTRVO.Core.Launcher;

/// <summary>
/// 状态色调。只表达“该值是好的/坏的/待确认的”，不表达具体颜色 ——
/// 颜色映射属于 View 层（见 App 的 Resources/Theme.xaml）。
/// </summary>
public enum Tone
{
    /// <summary>普通文本，不着色。</summary>
    Neutral,

    /// <summary>信息（蓝）。</summary>
    Info,

    /// <summary>成功 / 就绪（绿）。</summary>
    Ok,

    /// <summary>警告 / 待处理（橙）。</summary>
    Warn,

    /// <summary>错误 / 失败（红）。</summary>
    Error,

    /// <summary>灰显 / 未启用 / 未安装。</summary>
    Muted,
}
