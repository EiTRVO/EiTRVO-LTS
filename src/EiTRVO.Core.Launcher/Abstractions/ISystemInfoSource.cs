namespace EiTRVO.Core.Launcher.Abstractions;

/// <summary>
/// 本机信息的**原始数据来源**：物理内存（<c>GlobalMemoryStatusEx</c>）与几项注册表文本。
/// <para>
/// 抽成接口有两条实打实的理由：
/// </para>
/// <list type="number">
/// <item><b>可测</b>：内存与注册表读不出来时界面该显示什么、数值该怎么格式化，
/// 是这一层唯一值得测的逻辑，而真实机器上的内存值没法写进断言。</item>
/// <item><b>去掉静态构造</b>：Neo 的 <c>SystemMemoryInfo</c> 把探测结果放在 <c>static</c> 构造函数里，
/// 于是"这一刻读到的内存"被整个进程记住，P/Invoke 失败一次就永远是兜底值，
/// 且测试里无法替换（迁移评估 C-10 点名的一处）。这里改成实例，谁来读、多久读一次由调用方决定。</item>
/// </list>
/// </summary>
public interface ISystemInfoSource
{
    /// <summary>物理内存总量（MB）；读不出来时为 <c>0</c>。</summary>
    long TotalPhysicalMemoryMb { get; }

    /// <summary>当前可用物理内存（MB）；读不出来时为 <c>0</c>。</summary>
    long AvailablePhysicalMemoryMb { get; }

    /// <summary>操作系统产品名，如 <c>Windows 11 专业版</c>；读不出来时为空串。</summary>
    string OperatingSystemProductName { get; }

    /// <summary>操作系统功能更新版本，如 <c>24H2</c>；读不出来时为空串。</summary>
    string OperatingSystemDisplayVersion { get; }

    /// <summary>操作系统内部版本号，如 <c>26100</c>；读不出来时为空串。</summary>
    string OperatingSystemBuild { get; }

    /// <summary>处理器名称，如 <c>12th Gen Intel Core i7-12700</c>；读不出来时为空串。</summary>
    string ProcessorName { get; }

    /// <summary>显卡驱动描述，如 <c>NVIDIA GeForce RTX 4060</c>；读不出来时为空串。</summary>
    string GraphicsAdapterName { get; }
}
