namespace EiTRVO.Core.Launcher.Net;

/// <summary>
/// 把旧版本 JSON 里的**已弃用 Mojang 端点**改写到今天仍在的白名单主机。
/// </summary>
/// <remarks>
/// 1.11 及以前的 <c>version.json</c> 里，客户端 JAR 与资源索引地址还写着
/// <c>launcher.mojang.com</c> / <c>launchermeta.mojang.com</c>。这两个端点是 Mojang 早年发布的，
/// 今天仍然能下载，但 <see cref="HttpHostPolicy"/> **刻意没有收录** <c>launchermeta</c>
/// （白名单测试里明确把它当作"已弃用的旧端点"拒绝）—— 于是装 1.7.10 时资源索引会直接
/// "所有候选地址都不可用"。
/// <para>
/// 处理办法不是放宽白名单，而是**把地址改写到现行端点**：两份数据在 piston 主机上逐字节相同
/// （1.7.10 的索引与 client.jar 都实测 200）。这样白名单仍然只有一份、且不包含历史包袱。
/// </para>
/// </remarks>
public static class LegacyMojangUrls
{
    private const string LaunchermetaOld = "http://launchermeta.mojang.com/";
    private const string LaunchermetaOldHttps = "https://launchermeta.mojang.com/";
    private const string PistonMeta = "https://piston-meta.mojang.com/";

    private const string LauncherOld = "http://launcher.mojang.com/";
    private const string LauncherOldHttps = "https://launcher.mojang.com/";
    private const string PistonData = "https://piston-data.mojang.com/";

    private const string ForgeFilesOld = "http://files.minecraftforge.net/maven";
    private const string ForgeFilesOldHttps = "https://files.minecraftforge.net/maven";
    private const string ForgeMaven = "https://maven.minecraftforge.net";

    /// <summary>改写一个远端地址；不是旧端点时原样返回。</summary>
    /// <param name="url">版本 JSON 里的地址。</param>
    /// <returns>可下载的地址。</returns>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var text = url.Trim();

        return text switch
        {
            _ when StartsWith(text, LaunchermetaOld) => PistonMeta + text[LaunchermetaOld.Length..],
            _ when StartsWith(text, LaunchermetaOldHttps) => PistonMeta + text[LaunchermetaOldHttps.Length..],
            _ when StartsWith(text, LauncherOld) => PistonData + text[LauncherOld.Length..],
            _ when StartsWith(text, LauncherOldHttps) => PistonData + text[LauncherOldHttps.Length..],
            _ when StartsWith(text, ForgeFilesOld) => ForgeMaven + text[ForgeFilesOld.Length..],
            _ when StartsWith(text, ForgeFilesOldHttps) => ForgeMaven + text[ForgeFilesOldHttps.Length..],
            _ => text,
        };
    }

    private static bool StartsWith(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
