using EiTRVO.Core.Launcher.Net;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>
/// 登录地址的白名单（D-3：设备代码钓鱼防护）。
/// <para>
/// 为什么必须单独一条白名单，而不复用 <see cref="HttpHostPolicy"/>：出站白名单里还有
/// <c>piston-meta.mojang.com</c>、Modrinth、各种 Maven —— 那些地址出现在"请到这里输入代码"
/// 的位置上就是一次钓鱼（用户会照做）。这里的名单**只包含 Microsoft 的登录域**，
/// 而且刻意不做后缀通配：需要哪个子域就显式列出来。
/// </para>
/// <para>
/// 校验四件事（与出站策略同样的四条，理由也一样）：必须 <c>https</c>、必须是默认端口、
/// URL 里不得带 <c>userinfo</c>（<c>https://microsoft.com@evil.example/</c> 这种写法看起来是白名单、
/// 实际连别人）、主机名必须精确命中名单。
/// </para>
/// </summary>
public static class SignInUrlPolicy
{
    /// <summary>允许出现在"在这里输入代码"位置上的主机。</summary>
    /// <remarks>
    /// 前两个是设备代码流实际会返回的（<c>https://microsoft.com/link</c> / <c>www.microsoft.com/link</c>），
    /// 后面几个是 Microsoft 登录域（将来官方换端点时不会立刻变成"无法登录"）。
    /// </remarks>
    private static readonly string[] AllowedHosts =
    [
        "microsoft.com",
        "www.microsoft.com",
        "login.microsoftonline.com",
        "login.live.com",
        "microsoftonline.com",
        "live.com",
    ];

    /// <summary>把端点给的地址规整成绝对 https 地址。</summary>
    /// <param name="value">端点给的地址（官方实际写的是 <c>microsoft.com/link</c>，**不带协议**）。</param>
    /// <returns>绝对地址。</returns>
    public static Uri? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        // 官方返回的形状是 "microsoft.com/link"（没有 scheme）。只补 https —— 绝不根据内容猜 http。
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed.TrimStart('/');
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>这个地址能不能展示给用户、能不能拿系统浏览器打开。</summary>
    /// <param name="uri">地址。</param>
    /// <returns>允许返回 <see langword="true"/>。</returns>
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
