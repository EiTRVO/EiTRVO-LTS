using System.Globalization;
using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// <see cref="ISystemInfoProvider"/> 的真实实现：把 <see cref="ISystemInfoSource"/> 的原始事实
/// 拼成界面上那几行文本，并按物理内存给出内存推荐值（C-11）。
/// <para>
/// 文本本身**不走本地化**：它与 <c>MinecraftVersion.RequiredJava</c>、实例行里的"已安装"一样，
/// 是内核报出的事实（这里就是本机硬件的原文），不是界面文案（AGENTS.md §5.3）。
/// 唯一需要处理的是"读不出来"：一律显示 <c>—</c>，**不编数字**。
/// </para>
/// </summary>
public sealed class SystemInfoProvider : ISystemInfoProvider
{
    /// <summary>读不出来时的占位文本。</summary>
    private const string Unknown = "—";

    private readonly ISystemInfoSource _source;

    /// <summary>构造本机信息提供者。</summary>
    /// <param name="source">原始数据来源（注册表 + kernel32）。</param>
    public SystemInfoProvider(ISystemInfoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public string OperatingSystem
    {
        get
        {
            var name = CorrectedProductName(_source.OperatingSystemProductName, _source.OperatingSystemBuild);
            var parts = new List<string>(3);

            if (name.Length > 0)
            {
                parts.Add(name);
            }

            if (_source.OperatingSystemDisplayVersion.Length > 0)
            {
                parts.Add(_source.OperatingSystemDisplayVersion);
            }

            if (_source.OperatingSystemBuild.Length > 0)
            {
                parts.Add($"({_source.OperatingSystemBuild})");
            }

            return parts.Count == 0 ? Unknown : string.Join(" ", parts);
        }
    }

    /// <inheritdoc />
    public string Processor => Value(_source.ProcessorName);

    /// <inheritdoc />
    public string InstalledMemory
    {
        get
        {
            var total = _source.TotalPhysicalMemoryMb;
            var available = _source.AvailablePhysicalMemoryMb;

            return total <= 0
                ? Unknown
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Gb(total)} (可用 {(available > 0 ? Gb(available) : Unknown)})");
        }
    }

    /// <inheritdoc />
    public string TotalMemory => _source.TotalPhysicalMemoryMb <= 0 ? Unknown : Gb(_source.TotalPhysicalMemoryMb);

    /// <inheritdoc />
    public MemoryRecommendation Memory => MemoryRecommendations.For(_source.TotalPhysicalMemoryMb);

    /// <inheritdoc />
    public string GraphicsCard => Value(_source.GraphicsAdapterName);

    /// <summary>MB → <c>32.0 GB</c> / <c>512 MB</c>。</summary>
    private static string Gb(long megabytes) => megabytes >= 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024.0:F1} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{megabytes} MB");

    private static string Value(string text) => string.IsNullOrWhiteSpace(text) ? Unknown : text.Trim();

    /// <summary>
    /// 修正一处众所周知的注册表谎报：Windows 11 的 <c>ProductName</c> 至今写着
    /// <c>Windows 10 Pro</c>，只能靠内部版本号（≥ 22000）分辨。
    /// 直接照抄的话，界面会在 Windows 11 上显示"Windows 10"——用户一眼就会觉得启动器不可信。
    /// </summary>
    private static string CorrectedProductName(string productName, string build)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return string.Empty;
        }

        var name = productName.Trim();
        if (int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var buildNumber)
            && buildNumber >= 22000)
        {
            return name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        return name;
    }
}
