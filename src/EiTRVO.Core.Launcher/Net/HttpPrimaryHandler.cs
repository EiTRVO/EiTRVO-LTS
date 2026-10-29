namespace EiTRVO.Core.Launcher.Net;

/// <summary>
/// 命名客户端**底层处理器**的唯一构造点。
/// <para>
/// 存在的理由只有一个：<c>AllowAutoRedirect</c> 必须是 <see langword="false"/>，
/// 而这件事既不能在调用点靠注释保证，也不能在测试里靠反射去翻 <see cref="System.Net.Http.HttpClient"/> 的私有字段 ——
/// 于是把它收进一个工厂，组合根与用例看的是同一份代码。
/// </para>
/// <para>
/// 为什么必须关：自动重定向发生在底层处理器内部，也就是在
/// <see cref="AllowedHostHandler"/> 这条链**之后**。开着它，白名单只挡得住第一跳，
/// 一个白名单主机回一张 <c>302 Location: https://evil.example/</c> 就能把请求带去任意地方。
/// 关掉之后由 <see cref="AllowedHostHandler"/> 自己逐跳跟随，每一跳都要过策略。
/// </para>
/// </summary>
public static class HttpPrimaryHandler
{
    /// <summary>创建底层处理器。</summary>
    /// <returns>已关闭自动重定向的处理器。</returns>
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
    };
}
