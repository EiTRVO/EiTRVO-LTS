using EiTRVO.Core.Launcher.Security;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 日志配置（<c>logging.client</c>）落盘位置。
/// <para>
/// 这条规则有**两个**使用点：安装时决定下到哪（<c>InstallPlanner</c>），
/// 启动时决定 <c>-Dlog4j.configurationFile</c> 指向哪（M4 的参数构建）。
/// 两处必须完全一致 —— 不一致的表现是"安装时写到一个地方、启动时读另一个地方"，
/// 而 log4j 找不到配置文件**不会报错**，只会静默用默认配置（用户看到的是"日志格式不对"）。
/// 所以规则只留一份，放在这里。
/// </para>
/// </summary>
public static class LoggingConfigLayout
{
    /// <summary>日志配置在游戏目录里的绝对路径；没有可用的日志配置时为 <see langword="null"/>。</summary>
    /// <param name="version">版本 JSON。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <returns>绝对路径，或 <see langword="null"/>。</returns>
    /// <remarks>
    /// 文件名优先用清单给的 <c>id</c>，没有才从 URL 里取最后一段；
    /// 两者都拿不到、或名字不能用作单段路径名时返回 <see langword="null"/>（**不抛异常**）——
    /// 日志配置缺失不该让整个启动或安装失败。
    /// </remarks>
    public static string? TryGetPath(VersionJson? version, string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var file = version?.Logging?.Client?.File;
        if (file?.Url is null)
        {
            return null;
        }

        var name = file.Id;
        if (string.IsNullOrWhiteSpace(name)
            && Uri.TryCreate(file.Url, UriKind.Absolute, out var uri))
        {
            name = Path.GetFileName(uri.LocalPath);
        }

        if (string.IsNullOrWhiteSpace(name) || !PathSafety.IsSafePathSegment(name))
        {
            return null;
        }

        return PathSafety.CombineWithin(gameDirectory, "assets", "log_configs", name);
    }
}
