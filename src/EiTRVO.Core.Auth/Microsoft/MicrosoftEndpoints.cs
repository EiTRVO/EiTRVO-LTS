namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>
/// Microsoft 与 Xbox / Minecraft 的端点。**全部集中在这里**：认证链的每一步都要被断言到具体 URL
/// （规划 §4 验收第 3 条），散落各处的字符串拼不出一份可核对的清单。
/// </summary>
public static class MicrosoftEndpoints
{
    /// <summary>设备代码申请（消费者租户；Microsoft 账户走 <c>consumers</c>）。</summary>
    public const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    /// <summary>令牌端点（轮询授权 + 刷新令牌都用它）。</summary>
    public const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    /// <summary>Xbox Live 用户认证。</summary>
    public const string XboxUserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";

    /// <summary>XSTS 授权。</summary>
    public const string XstsAuthorizeUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

    /// <summary>用 XSTS 令牌换 Minecraft 访问令牌。</summary>
    public const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";

    /// <summary>玩家档案（名字、UUID、皮肤）。</summary>
    public const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>
    /// 皮肤（D-9）。<c>POST</c> 换皮肤（multipart <c>variant</c>+<c>file</c>，或 JSON <c>variant</c>+<c>url</c>），
    /// <c>DELETE …/active</c> 恢复官方默认皮肤。
    /// </summary>
    public const string MinecraftSkinsUrl = "https://api.minecraftservices.com/minecraft/profile/skins";

    /// <summary>恢复官方默认皮肤（删除当前生效的那一张）。</summary>
    public const string MinecraftActiveSkinUrl = MinecraftSkinsUrl + "/active";

    /// <summary>申请设备代码时要求的权限：登录 Xbox Live + 离线刷新令牌。</summary>
    public const string Scope = "XboxLive.signin offline_access";

    /// <summary>设备代码流的 <c>grant_type</c>。</summary>
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Xbox Live 认证的 <c>RelyingParty</c>。</summary>
    public const string XboxRelyingParty = "http://auth.xboxlive.com";

    /// <summary>XSTS 的 <c>RelyingParty</c>（固定值，写成 Minecraft 服务的地址）。</summary>
    public const string XstsRelyingParty = "rp://api.minecraftservices.com/";

    /// <summary>零售沙箱。</summary>
    public const string RetailSandbox = "RETAIL";
}
