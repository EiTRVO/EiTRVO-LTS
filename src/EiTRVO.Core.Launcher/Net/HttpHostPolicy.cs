namespace EiTRVO.Core.Launcher.Net;

/// <summary>
/// 请求被域名/协议策略拒绝时抛出。
/// <para>
/// 与"网络失败"区分开：这类错误**重试没有意义**（策略不会因为你再试一次就放行），
/// 界面应当给出"这个地址不被允许"而不是"网络异常"。
/// </para>
/// </summary>
public sealed class HttpPolicyException : Exception
{
    /// <summary>用消息构造。</summary>
    /// <param name="message">消息。</param>
    public HttpPolicyException(string message)
        : base(message)
    {
    }

    /// <summary>用消息与内部异常构造。</summary>
    /// <param name="message">消息。</param>
    /// <param name="innerException">内部异常。</param>
    public HttpPolicyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 出站请求的**唯一出口策略**：只允许 https、只允许白名单内的主机。
/// <para>
/// 为什么要有它：启动器会按远端 JSON 里的 URL 去下载任意文件（版本清单里就写着库与资源的地址）。
/// 一旦清单被篡改或某个端点的响应被中间人替换，没有白名单就等于"让远端决定我们访问哪里"。
/// 因此这里采取**精确主机匹配**：不做后缀通配，也不允许子域自动放行 ——
/// 需要哪个子域就显式列出来（Neo 的实现里允许了已弃用的 <c>launchermeta</c> 旧端点，属于需要移除的历史包袱）。
/// </para>
/// </summary>
public sealed class HttpHostPolicy
{
    /// <summary>
    /// 默认白名单。按用途分组，新增端点时**必须**同时说明用途。
    /// </summary>
    private static readonly string[] DefaultHosts =
    [
        // 版本清单与官方资源（AGENTS.md §6：必须用 piston-meta + version_manifest_v2）
        "piston-meta.mojang.com",
        "launcher.mojang.com",
        "piston-data.mojang.com",
        "libraries.minecraft.net",
        "resources.download.minecraft.net",

        // 账户与皮肤
        "login.microsoftonline.com",
        "login.live.com",
        "user.auth.xboxlive.com",
        "xsts.auth.xboxlive.com",
        "api.minecraftservices.com",
        "sessionserver.mojang.com",
        "textures.minecraft.net",

        // 模组加载器
        "meta.fabricmc.net",
        "maven.fabricmc.net",
        "meta.quiltmc.org",
        "maven.quiltmc.org",
        "maven.minecraftforge.net",
        "files.minecraftforge.net",
        "maven.neoforged.net",
        "optifine.net",

        // 内容平台
        "api.modrinth.com",
        "cdn.modrinth.com",

        // 整合包（.mrpack）允许的下载源。列表来自 Modrinth 规范原文（`files[].downloads` 的合法域名）：
        // cdn.modrinth.com（上面已有）+ github.com + raw.githubusercontent.com + gitlab.com。
        // **为什么还要下面两个 asset 主机**：GitHub 的发布包下载会 302 到它自己的资源主机，
        // 而我们的重定向是**逐跳校验白名单**的（AGENTS §6）—— 不列出来等于"github.com 这一条
        // 在真实下载里永远走不通"，规范要求的"跟随 3xx"也就落空了。
        // 安全性不靠主机名：这些文件的 sha1 与 sha512 **两个都校验**（规范要求两者都在）。
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    private readonly HashSet<string> _allowedHosts;

    /// <summary>用给定主机集合构造。</summary>
    /// <param name="allowedHosts">允许的主机名（不含协议与端口）。</param>
    public HttpHostPolicy(IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);

        if (_allowedHosts.Count == 0)
        {
            throw new ArgumentException("白名单不能为空。", nameof(allowedHosts));
        }
    }

    /// <summary>内置白名单（覆盖 P0 所需的全部端点）。</summary>
    public static HttpHostPolicy Default { get; } = new(DefaultHosts);

    /// <summary>当前允许的主机集合（只读）。</summary>
    public IReadOnlySet<string> AllowedHosts => _allowedHosts;

    /// <summary>该地址是否允许出站。</summary>
    /// <param name="uri">目标地址。</param>
    /// <returns>允许返回 <see langword="true"/>。</returns>
    public bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 非默认端口一律拒绝：镜像与官方端点都在 443 上，自定义端口没有合法用途。
        if (!uri.IsDefaultPort)
        {
            return false;
        }

        // 挡掉 https://allowed.host@evil.example/ 这种「看起来是白名单、实际连别人」的写法。
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return _allowedHosts.Contains(uri.Host);
    }

    /// <summary>不允许时抛出 <see cref="HttpPolicyException"/>。</summary>
    /// <param name="uri">目标地址。</param>
    public void EnsureAllowed(Uri? uri)
    {
        if (IsAllowed(uri))
        {
            return;
        }

        throw new HttpPolicyException(
            $"出站请求被拒绝：'{uri}' 不在允许的主机白名单内（只允许 https 且端口为 443）。");
    }
}
