using Microsoft.Win32;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// 读注册表的薄封装：只做"HKEY_LOCAL_MACHINE 下取一个字符串值 / 列一层子键"这两件事，
/// 并把所有失败都收敛成"没有"。
/// <para>
/// 三处刻意的选择：
/// </para>
/// <list type="number">
/// <item><b>失败即"没有"，绝不抛</b>：注册表读不到的原因很多（键不存在、权限、非 Windows 平台），
/// 而每一种都不该让"列出本机 Java"整件事失败 —— 少列一条好过白屏。</item>
/// <item><b>64 位视图优先，再读 32 位视图</b>：32 位 JDK 装在 64 位系统上时只登记在 <c>WOW6432Node</c>；
/// 漏掉它，界面会显示"没装 Java"，而用户明明装了（当年很多 1.12.2 玩家装的正是 32 位 Java 8）。</item>
/// <item><b>平台检查写在方法里</b>：Launcher 的目标框架是不带 <c>-windows</c> 的 <c>net8.0</c>
/// （编译器替我们守住"内核不碰 UI"），因此注册表 API 会触发 CA1416；
/// <c>OperatingSystem.IsWindows()</c> 既让分析器满意，也让这个类在别的平台上安全地返回空。</item>
/// </list>
/// </summary>
internal static class WindowsRegistry
{
    /// <summary>读取一个 HKLM 字符串值（64 位视图优先，找不到再找 32 位视图）。</summary>
    /// <param name="subKey">子键路径，如 <c>SOFTWARE\JavaSoft\JDK</c>。</param>
    /// <param name="valueName">值名，如 <c>JavaHome</c>。</param>
    /// <returns>值文本；不存在或读不出来时为 <see langword="null"/>。</returns>
    public static string? ReadLocalMachineString(string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var value = ReadFromView(RegistryView.Registry64, subKey, valueName)
                    ?? ReadFromView(RegistryView.Registry32, subKey, valueName);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>列出 HKLM 某个键下的一层子键名（两个视图合并，去重）。</summary>
    /// <param name="subKey">子键路径。</param>
    /// <returns>子键名；键不存在或读不出来时为空列表。</returns>
    public static IReadOnlyList<string> EnumerateLocalMachineSubKeyNames(string subKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddFromView(RegistryView.Registry64, subKey, names, seen);
        AddFromView(RegistryView.Registry32, subKey, names, seen);

        return names;
    }

    private static string? ReadFromView(RegistryView view, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                       or UnauthorizedAccessException
                                       or IOException
                                       or ObjectDisposedException)
        {
            return null;
        }
    }

    private static void AddFromView(
        RegistryView view,
        string subKey,
        List<string> names,
        HashSet<string> seen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                       or UnauthorizedAccessException
                                       or IOException
                                       or ObjectDisposedException)
        {
            // 与 ReadFromView 同理：读不到就是没有。
        }
    }
}
