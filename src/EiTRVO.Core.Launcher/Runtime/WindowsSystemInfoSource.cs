using System.Runtime.InteropServices;
using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// <see cref="ISystemInfoSource"/> 的真实实现：kernel32 的 <c>GlobalMemoryStatusEx</c> + 三处注册表。
/// <para>
/// 与 Neo 的 <c>SystemMemoryInfo</c> 的两点差别都是刻意的（迁移评估 C-10）：
/// </para>
/// <list type="number">
/// <item><b>没有静态构造</b>：Neo 把探测结果放进 <c>static</c> 构造函数，于是整个进程只读一次，
/// P/Invoke 失败一次就永远是兜底值；这里每次读都重新问系统（调用方决定多久读一次）。</item>
/// <item><b>不猜</b>：读不出来就返回空串 / 0，由上层决定显示什么。Neo 的兜底是"假设 8 GB 总内存、4 GB 可用"，
/// 那会让一个 4 GB 的机器在设置页上看到"物理内存 8 GB"，进而按错误的量分配内存。</item>
/// </list>
/// </summary>
public sealed class WindowsSystemInfoSource : ISystemInfoSource
{
    private const string WindowsVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000";

    /// <inheritdoc />
    public long TotalPhysicalMemoryMb => ReadMemory().TotalMb;

    /// <inheritdoc />
    public long AvailablePhysicalMemoryMb => ReadMemory().AvailableMb;

    /// <inheritdoc />
    public string OperatingSystemProductName =>
        WindowsRegistry.ReadLocalMachineString(WindowsVersionKey, "ProductName") ?? string.Empty;

    /// <inheritdoc />
    public string OperatingSystemDisplayVersion =>
        // Windows 11 之前叫 ReleaseId（1909/2004/21H2 之类），11 起叫 DisplayVersion。
        WindowsRegistry.ReadLocalMachineString(WindowsVersionKey, "DisplayVersion")
        ?? WindowsRegistry.ReadLocalMachineString(WindowsVersionKey, "ReleaseId")
        ?? string.Empty;

    /// <inheritdoc />
    public string OperatingSystemBuild =>
        WindowsRegistry.ReadLocalMachineString(WindowsVersionKey, "CurrentBuildNumber") ?? string.Empty;

    /// <inheritdoc />
    public string ProcessorName =>
        WindowsRegistry.ReadLocalMachineString(ProcessorKey, "ProcessorNameString") ?? string.Empty;

    /// <inheritdoc />
    public string GraphicsAdapterName =>
        WindowsRegistry.ReadLocalMachineString(DisplayClassKey, "DriverDesc") ?? string.Empty;

    /// <summary>物理内存（总量与可用量，MB）。</summary>
    private static (long TotalMb, long AvailableMb) ReadMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0);
        }

        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status)
                ? ((long)(status.TotalPhys / (1024 * 1024)), (long)(status.AvailPhys / (1024 * 1024)))
                : (0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // 极老的系统或受限环境：当作读不出来，让上层显示占位值。
            return (0, 0);
        }
    }

    /// <summary><c>MEMORYSTATUSEX</c>：字段顺序即内存布局，不可调整。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
