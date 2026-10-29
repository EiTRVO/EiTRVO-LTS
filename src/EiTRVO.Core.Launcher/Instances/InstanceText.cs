using System.Globalization;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Instances;

/// <summary>
/// 实例相关数值的显示文本规则（占用空间、游玩时长、时间、窗口模式）。
/// <para>
/// 与 <see cref="InstanceNaming"/> 同理：这些必须集中在一处 —— 表格里的「12.4 GB」与详情页里的
/// 占用空间不能各算各的。
/// </para>
/// <para>
/// **M6-e-3b 修正了这里原来的一条判断**（M1 时写的是"这是数据呈现、不纳入 `.resx`"）：
/// 时间（`今天 16:01` / `昨天 …` / `从未启动`）、时长（`126 小时 31 分`）与窗口模式（`窗口化`）
/// 里面**混着文案**，英文界面上会露出中文 —— 所以它们现在走「键 + 参数」。
/// 真正纯粹的只有 <see cref="FormatSize"/>（`12.4 GB`：数字 + 国际单位）与绝对时间那一支（`2026/9/20 19:02`）。
/// </para>
/// </summary>
public static class InstanceText
{
    /// <summary>磁盘容量单位。</summary>
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>把字节数写成人读的形式（如 <c>12.4 GB</c>）。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>文本。</returns>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 字节日不到 1 KB 时给整数，免得出现 "0.0 KB" 这种没信息量的写法。
        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} {SizeUnits[unit]}")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {SizeUnits[unit]}");
    }

    /// <summary>把累计秒数写成设计稿的形状（如 <c>126 小时 31 分</c>）。</summary>
    /// <param name="seconds">累计秒数。</param>
    /// <returns>键 + 参数（<c>{0} 分</c> / <c>{0} 小时 {1} 分</c>）；数字由 App 层按当前语言拼。</returns>
    public static TextTemplate FormatPlayTime(long seconds)
    {
        if (seconds <= 0)
        {
            return TextTemplate.Of("{0} 分", "0");
        }

        var totalMinutes = seconds / 60;
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        return hours == 0
            ? TextTemplate.Of("{0} 分", minutes.ToString(CultureInfo.InvariantCulture))
            : TextTemplate.Of(
                "{0} 小时 {1} 分",
                hours.ToString(CultureInfo.InvariantCulture),
                minutes.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>时间戳文本（如 <c>今天 16:01</c> / <c>2026/9/20 19:02</c>）。</summary>
    /// <param name="timestamp">时间；<see langword="null"/> 表示从未发生。</param>
    /// <param name="now">当前时间（"今天 / 昨天"要相对它判断）。</param>
    /// <param name="never">从未发生时的那句话（键 + 参数，由调用方给：从未启动 / 从未校验 / …）。</param>
    /// <returns>键 + 参数；绝对时间那一支是**纯数据键** <c>{0}</c>（<c>2026/9/20 19:02</c> 两种语言逐字相同）。</returns>
    public static TextTemplate FormatTimestamp(DateTimeOffset? timestamp, DateTimeOffset now, TextTemplate never)
    {
        if (timestamp is null)
        {
            return never;
        }

        var local = timestamp.Value.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var day = local.Date;

        if (day == today)
        {
            return TextTemplate.Of("今天 {0}", local.ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        if (day == today.AddDays(-1))
        {
            return TextTemplate.Of("昨天 {0}", local.ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        return TextTemplate.Of("{0}", local.ToString("yyyy/M/d HH:mm", CultureInfo.CurrentCulture));
    }

    /// <summary>游戏窗口文本，如 <c>1920 × 1080 窗口化</c>。</summary>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="mode">启动方式。</param>
    /// <returns>键 + 参数：<c>{0} × {1} 窗口化</c> / <c>{0} × {1} 全屏</c> / <c>{0} × {1} 无边框全屏</c>。</returns>
    /// <remarks>
    /// 尺寸是数据、模式词是文案，所以模式词进**键**（三个键），尺寸进参数 ——
    /// 反过来（把「窗口化」当参数）在英文界面里会露出三个汉字（AGENTS.md §5.3 第 ③ 条）。
    /// </remarks>
    public static TextTemplate FormatWindowMode(int width, int height, WindowLaunchMode mode)
    {
        var key = mode switch
        {
            WindowLaunchMode.Fullscreen => "{0} × {1} 全屏",
            WindowLaunchMode.Borderless => "{0} × {1} 无边框全屏",
            _ => "{0} × {1} 窗口化",
        };

        return TextTemplate.Of(
            key,
            width.ToString(CultureInfo.InvariantCulture),
            height.ToString(CultureInfo.InvariantCulture));
    }
}
