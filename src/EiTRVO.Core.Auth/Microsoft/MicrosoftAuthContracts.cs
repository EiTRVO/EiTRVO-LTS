using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>
/// 认证链上的可读失败（网络、状态码、响应形状、令牌被拒、钓鱼防护拦下……）。
/// <para>
/// 与"凭据不可用"（<see cref="CredentialFailure"/>）分开：那一个是**账户状态**的结论，
/// 这里是**这一次请求**的结论。界面要对两者说不同的话 ——
/// "网络断了，稍后再试" 与 "这个号需要重新登录" 不是一回事。
/// </para>
/// <para>
/// 消息**不在这里拼**（Q8）：<see cref="Reason"/> 给的是「模板键 + 参数」，
/// 句子由 App 层用 <c>.resx</c> 拼。<see cref="Exception.Message"/> 里放的是键本身 ——
/// 键就是简体中文原文，所以日志里照样读得懂，而"内核拼了一句中文"这件事不再可能发生。
/// </para>
/// </summary>
public sealed class AuthException : Exception
{
    /// <summary>构造认证异常。</summary>
    /// <param name="reason">失败原因（模板键 + 参数）。</param>
    /// <param name="innerException">底层异常。</param>
    /// <param name="requiresSignIn">
    /// 这次失败是否**只能通过重新登录**解决（刷新令牌被作废、账户被改密码等）。
    /// 为 <see langword="false"/> 时是"等会儿再试"类失败（网络、服务端 5xx）——
    /// 两者在界面上是两句不同的话，把后者说成"登录过期"会让用户白跑一趟浏览器。
    /// </param>
    public AuthException(TextTemplate reason, Exception? innerException = null, bool requiresSignIn = false)
        : base((reason ?? throw new ArgumentNullException(nameof(reason))).Key, innerException)
    {
        Reason = reason;
        RequiresSignIn = requiresSignIn;
    }

    /// <summary>失败原因（模板键 + 参数；句子由 App 层拼）。</summary>
    public TextTemplate Reason { get; }

    /// <summary>是否必须重新登录才能解决。</summary>
    public bool RequiresSignIn { get; }
}

/// <summary>
/// 登录进度（D-1 里"3 个 UI 回调"的落点）。
/// <para>
/// 三个**用户看得见**的时刻：① 拿到用户代码（把链接与代码给用户）→ ② 等他在浏览器里点确定 →
/// ③ 完成（或失败）。中间那些链路步骤（XBL / XSTS / Minecraft 服务）用户不需要逐步知道，
/// 但它们会作为 <see cref="AuthStage.CompletingSignIn"/> 显示出来 —— 那几秒是真的在等网络，
/// 界面上一句话都不说会让人以为卡死了。
/// </para>
/// <para>
/// 只给**阶段**，不给文案（M6-e）：内核不拼句子（Q8），界面按 <see cref="AuthStage"/> 出本地化文案。
/// 这里原本还带一句 <c>Detail</c>，但它没有任何显示来源（界面读的是 Stage）——
/// 留着它就等于在核心里维护一批**永远不会被看见**的中文句子。
/// </para>
/// </summary>
public enum AuthStage
{
    /// <summary>正在向 Microsoft 申请设备代码。</summary>
    RequestingCode,

    /// <summary>代码已拿到，等用户在浏览器完成授权。</summary>
    WaitingForUser,

    /// <summary>用户已授权，正在换取令牌。</summary>
    Authorized,

    /// <summary>正在走 Xbox Live → XSTS → Minecraft 服务的链路。</summary>
    CompletingSignIn,

    /// <summary>链路成功、账户已落盘。</summary>
    Completed,

    /// <summary>失败（原因在 <see cref="AuthException.Reason"/> / 会话的 <c>Failure</c> 里）。</summary>
    Failed,
}

/// <summary>一次进度上报。</summary>
/// <param name="Stage">当前阶段。</param>
public sealed record AuthProgress(AuthStage Stage)
{
    /// <summary>是否还没结束。</summary>
    public bool IsInProgress => Stage is not (AuthStage.Completed or AuthStage.Failed);
}

/// <summary>设备代码端点给出的挑战（还没换到令牌）。</summary>
/// <param name="DeviceCode">轮询用的设备代码（**不给用户看**）。</param>
/// <param name="UserCode">给用户输入的短代码。</param>
/// <param name="VerificationUri">用户要打开的地址（已通过 <see cref="SignInUrlPolicy"/> 校验）。</param>
/// <param name="ExpiresInSeconds">代码有效期（秒）。</param>
/// <param name="IntervalSeconds">官方要求的轮询间隔（秒）。</param>
public sealed record DeviceCodeChallenge(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>一次轮询的结论。</summary>
public enum DeviceCodePollKind
{
    /// <summary>用户还没完成授权（<c>authorization_pending</c>），继续按间隔轮询。</summary>
    Pending,

    /// <summary>轮得太快（<c>slow_down</c>），官方要求拉长间隔。</summary>
    SlowDown,

    /// <summary>代码过期（<c>expired_token</c>）。</summary>
    Expired,

    /// <summary>用户拒绝或代码无效（<c>access_denied</c> / <c>bad_verification_code</c> 等）。</summary>
    Denied,

    /// <summary>拿到令牌了。</summary>
    Tokens,
}

/// <summary>一次轮询的结果。</summary>
/// <param name="Kind">结论。</param>
/// <param name="Tokens">成功时的令牌。</param>
/// <param name="Error">OAuth 的 <c>error</c> 字段（排障用）。</param>
/// <param name="Description">OAuth 的 <c>error_description</c>（给用户看时也要过一遍脱敏）。</param>
public sealed record DeviceCodePoll(
    DeviceCodePollKind Kind,
    MicrosoftTokens? Tokens = null,
    string? Error = null,
    string? Description = null);

/// <summary>一次设备代码流的最终结论。</summary>
/// <param name="Session">会话（给界面看的链接、代码与状态）。</param>
/// <param name="Tokens">成功时的 Microsoft 令牌；失败时为 <see langword="null"/>。</param>
/// <param name="Failure">失败原因（模板键 + 参数）；成功时为 <see langword="null"/>。</param>
/// <remarks>
/// 把它与 <see cref="DeviceCodeSession"/> 分开的理由是**令牌不该出现在界面层的对象里**：
/// 会话会被绑定到 XAML，而任何绑定对象都可能被日志、崩溃报告或调试器顺手打印出来。
/// </remarks>
public sealed record DeviceCodeOutcome(
    DeviceCodeSession Session,
    MicrosoftTokens? Tokens,
    TextTemplate? Failure)
{
    /// <summary>是否拿到了令牌。</summary>
    public bool Succeeded => Tokens is not null;
}

/// <summary>Microsoft 身份令牌。</summary>
/// <param name="AccessToken">访问令牌（**绝不进日志**）。</param>
/// <param name="RefreshToken">刷新令牌；轮询与刷新链都会给一个新的。</param>
/// <param name="ExpiresAt">访问令牌到期时刻（由注入的时钟算出来）。</param>
public sealed record MicrosoftTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Xbox Live 用户令牌。</summary>
/// <param name="Token">JWT。</param>
/// <param name="UserHash">显示声明里的 <c>uhs</c>（XSTS 的 <c>XBL3.0 x=&lt;uhs&gt;;&lt;token&gt;</c> 要用它）。</param>
public sealed record XboxUserToken(string Token, string UserHash);

/// <summary>XSTS 授权令牌。</summary>
/// <param name="Token">JWT。</param>
/// <param name="UserHash">显示声明里的 <c>uhs</c>。</param>
public sealed record XstsToken(string Token, string UserHash);

/// <summary>Minecraft 服务访问令牌。</summary>
/// <param name="AccessToken">访问令牌（**绝不进日志**）。</param>
/// <param name="ExpiresAt">到期时刻。</param>
public sealed record MinecraftToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// 玩家档案（链路最后一步）。
/// </summary>
/// <param name="Uuid">带连字符的标准 UUID（官方给的是 32 位无连字符形式）。</param>
/// <param name="Name">玩家名（正版名，与账户名可能不同）。</param>
/// <param name="SkinUrl">皮肤纹理地址；没有自定义皮肤时为 <see langword="null"/>。</param>
/// <param name="SkinVariant">皮肤模型（<c>CLASSIC</c> / <c>SLIM</c>）。</param>
/// <param name="HasCape">是否拥有披风。</param>
public sealed record MinecraftProfile(
    string Uuid,
    string Name,
    string? SkinUrl,
    string? SkinVariant,
    bool HasCape);

/// <summary>
/// 一次完整的正版登录结果。
/// <para>
/// 只有**每一步都成功**才会有这个对象 —— 这正是"全链成功才落盘"那条设计的形状：
/// 拿不到档案就没有可落盘的账户，界面也不会出现一个"登录了但启动不了"的号。
/// </para>
/// </summary>
/// <param name="Microsoft">Microsoft 令牌（刷新令牌要落盘）。</param>
/// <param name="Minecraft">Minecraft 访问令牌。</param>
/// <param name="Profile">玩家档案。</param>
public sealed record MicrosoftLoginResult(
    MicrosoftTokens Microsoft,
    MinecraftToken Minecraft,
    MinecraftProfile Profile);
