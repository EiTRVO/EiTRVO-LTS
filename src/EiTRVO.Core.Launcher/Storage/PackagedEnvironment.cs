using System.Runtime.InteropServices;

namespace EiTRVO.Core.Launcher.Storage;

/// <summary>
/// 运行形态判断：便携版的数据跟 exe 走；MSIX 包的安装目录只读，数据必须落到用户目录。
/// </summary>
public static class PackagedEnvironment
{
    /// <summary>MSIX 包内使用的数据目录名。</summary>
    public const string DataFolderName = "EiTRVO LTS";

    /// <summary>当前进程是否有 MSIX 包身份。</summary>
    /// <remarks>
    /// 未打包时 <c>GetCurrentPackageFullName</c> 返回 <c>APPMODEL_ERROR_NO_PACKAGE</c>（15700）；
    /// 打包时第一次调用返回 <c>ERROR_INSUFFICIENT_BUFFER</c>（122）。两种成功形态都说明“在包里”。
    /// </remarks>
    public static bool IsPackaged
    {
        get
        {
            var length = 0u;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return result is 0 or 122;
        }
    }

    /// <summary>解析本次运行的数据根目录。</summary>
    /// <returns>
    /// 便携模式为 exe 目录；MSIX 模式为
    /// <c>%LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;\LocalState\EiTRVO LTS</c>。
    /// </returns>
    /// <remarks>
    /// MSIX 进程里 Windows 会把 <see cref="Environment.SpecialFolder.LocalApplicationData"/>
    /// 重定向到包内 <c>LocalCache\Local</c>；这是缓存目录，不适合放账户、设置和运行时。
    /// 这里从重定向路径反推出包根目录（<c>...\Packages\&lt;PackageFamilyName&gt;</c>），
    /// 再显式使用同级的 <c>LocalState</c>，因此 MSIX 的持久数据不再落在 LocalCache。
    /// </remarks>
    public static string ResolveDataRoot() =>
        IsPackaged
            ? Path.Combine(ResolvePackageLocalState(), DataFolderName)
            : AppContext.BaseDirectory;

    /// <summary>
    /// 从 MSIX 重定向后的 LocalApplicationData 路径推导包内 LocalState 目录。
    /// </summary>
    /// <returns>包内 LocalState 的绝对路径。</returns>
    private static string ResolvePackageLocalState()
    {
        var redirectedLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localCache = Directory.GetParent(redirectedLocal);
        var packageRoot = localCache is null ? null : Directory.GetParent(localCache.FullName);

        // 只有当结构确实是 ...\Packages\<PFN>\LocalCache\Local 时才改写；
        // 其它环境（例如系统没有重定向）保持原路径，避免拼出一个错误的 LocalState。
        var packagesRoot = packageRoot?.Parent;
        if (packageRoot is null
            || packagesRoot is null
            || !string.Equals(packagesRoot.Name, "Packages", StringComparison.OrdinalIgnoreCase))
        {
            return redirectedLocal;
        }

        return Path.Combine(packageRoot.FullName, "LocalState");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        IntPtr packageFullName);
}
