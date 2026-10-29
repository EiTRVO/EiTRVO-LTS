namespace EiTRVO.Core.Launcher.Net;

/// <summary>
/// 命名 <see cref="System.Net.Http.HttpClient"/> 的名字。
/// <para>
/// 内核服务一律构造注入 <c>IHttpClientFactory</c>，再用这里的名字取客户端 ——
/// 这样"域名白名单 + 重试"是挂在管道上强制生效的，而不是靠每个服务自觉。
/// 常量集中在这里，避免"DDoS 自己"式的拼写错误（<c>"core"</c> / <c>"Core"</c> 会创建两个不同的客户端）。
/// </para>
/// </summary>
public static class HttpClientNames
{
    /// <summary>核心出站客户端：白名单 + 重试退避。</summary>
    public const string Core = "core";
}
