using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// Windows Job Object：**只保留 <c>KILL_ON_JOB_CLOSE</c>**（决策 Q3）。
/// <para>
/// 它解决的问题只有一句话：**启动器死了，游戏得跟着死**。没有它，启动器被任务管理器结束、
/// 崩掉、或用户直接关窗口，游戏会变成一个没有界面的孤儿进程继续占着内存与显卡 ——
/// 用户的感受是"启动器关了游戏还在跑，任务管理器里还有一堆 java.exe"。
/// </para>
/// <para>
/// 机制：把游戏进程放进一个 Job，并声明"最后一个句柄关闭时终止 Job 里的所有进程"。
/// 启动器进程一结束，Windows 自动关闭它持有的句柄 —— 于是整棵树（含游戏自己拉起的子进程）被回收。
/// 正常退出时我们主动 <see cref="Dispose"/>，效果相同。
/// </para>
/// <para>
/// 与 Neo 的差别（按 Q3/Q4 裁剪，Neo 的实现有 289 行）：
/// </para>
/// <list type="number">
/// <item><b>没有进程数上限</b>（Neo 设了 50）：整合包把上限撑爆时游戏会被**静默**拒绝创建进程，
/// 而"某个模组打不开子进程"这种故障极难排查。我们的目标是回收，不是限制。</item>
/// <item><b>没有 UI 限制、没有安全限制、没有完成端口</b>：那些是 Neo 的防火墙功能要的，
/// 不属于启动器的启动链。</item>
/// <item><b>不用 <c>CREATE_SUSPENDED</c></b>（Q4）：挂起-恢复只在"必须在进程执行第一条指令前
/// 改它的环境"时才需要，而我们的启动参数全部写在命令行里，不需要。</item>
/// </list>
/// </summary>
internal sealed class JobObject : IDisposable
{
    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>。</summary>
    private const uint KillOnJobCloseFlag = 0x00002000;

    /// <summary><c>JobObjectExtendedLimitInformation</c>（枚举值，不是结构体名）。</summary>
    private const int ExtendedLimitInformationClass = 9;

    private readonly SafeFileHandle _handle;

    private JobObject(SafeFileHandle handle) => _handle = handle;

    /// <summary>创建一个声明了 <c>KILL_ON_JOB_CLOSE</c> 的 Job。</summary>
    /// <param name="failure">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>Job；创建失败返回 <see langword="null"/>。</returns>
    public static JobObject? TryCreate(out string? failure)
    {
        failure = null;

        if (!OperatingSystem.IsWindows())
        {
            failure = "Job Object 只在 Windows 上可用。";
            return null;
        }

        try
        {
            var handle = CreateJobObject(IntPtr.Zero, null);

            if (handle.IsInvalid)
            {
                failure = "CreateJobObject 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                handle.Dispose();
                return null;
            }

            var job = new JobObject(handle);

            if (!job.TrySetKillOnClose(out var limitFailure))
            {
                failure = limitFailure;
                job.Dispose();
                return null;
            }

            return job;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            failure = "调用 kernel32 的 Job Object API 失败：" + exception.Message;
            return null;
        }
    }

    /// <summary>把进程放进 Job。</summary>
    /// <param name="process">已经起来的进程。</param>
    /// <param name="failure">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>成功返回 <see langword="true"/>。</returns>
    public bool TryAssign(Process process, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(process);
        failure = null;

        try
        {
            // Process.Handle 带 PROCESS_ALL_ACCESS，其中就包含 AssignProcessToJobObject
            // 需要的 PROCESS_SET_QUOTA 与 PROCESS_TERMINATE。
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                failure = "AssignProcessToJobObject 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出了（起得太快）：没有树要回收，按"已生效"处理。
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            failure = "调用 AssignProcessToJobObject 失败：" + exception.Message;
            return false;
        }
    }

    /// <summary>关闭句柄 —— <c>KILL_ON_JOB_CLOSE</c> 在这一刻回收整棵进程树。</summary>
    public void Dispose() => _handle.Dispose();

    private bool TrySetKillOnClose(out string? failure)
    {
        failure = null;

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobCloseFlag },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(_handle, ExtendedLimitInformationClass, buffer, (uint)size))
            {
                failure = "SetInformationJobObject 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>：字段顺序即内存布局，不可调整。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
