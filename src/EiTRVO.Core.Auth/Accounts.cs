using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth;

/// <summary>账户类型。</summary>
public enum AccountKind
{
    /// <summary>Microsoft 正版账户（OAuth 2.0 设备代码流）。</summary>
    Microsoft,

    /// <summary>离线账户（仅单机与离线服可用）。</summary>
    Offline,
}

/// <summary>令牌状态。</summary>
public enum TokenState
{
    /// <summary>有效，可直接启动。</summary>
    Valid,

    /// <summary>即将过期，需要刷新。</summary>
    Expiring,

    /// <summary>已失效，需要重新登录。</summary>
    Expired,
}

/// <summary>皮肤模型。</summary>
public enum SkinModel
{
    /// <summary>经典（Classic）。</summary>
    Classic,

    /// <summary>纤细（Slim）。</summary>
    Slim,
}

/// <summary>已登录账户。</summary>
/// <param name="PlayerName">玩家名。</param>
/// <param name="Kind">账户类型。</param>
/// <param name="Uuid">UUID 文本；离线账户为说明文本。</param>
/// <param name="LastUsed">上次使用时间文本（键 + 参数）。</param>
/// <param name="TokenState">令牌状态。</param>
/// <param name="IsDefault">是否为默认账户。</param>
/// <param name="IsActive">是否被选中（列表高亮项）。</param>
public sealed record Account(
    string PlayerName,
    AccountKind Kind,
    string Uuid,
    TextTemplate LastUsed,
    TokenState TokenState,
    bool IsDefault,
    bool IsActive)
{
    /// <summary>
    /// 列表里那一行摘要：<c>Microsoft 正版 · UUID … · 上次使用 今天 16:01</c>。
    /// </summary>
    /// <remarks>
    /// 键 + 参数（两种账户种类各一个键）：<c>上次使用</c> 后面跟的是一个**时间句子**
    /// （`今天 16:01` / `从未使用`）。时间句子挂在 <c>Note</c> 上 —— 把它的**键**塞进参数会让
    /// 英文界面露出中文（AGENTS.md §5.3 第 ⑤ 条）。
    /// </remarks>
    public TextTemplate Meta =>
        Kind == AccountKind.Microsoft
            ? TextTemplate.Of("Microsoft 正版 · {0} · 上次使用", Uuid) with { Note = LastUsed }
            : TextTemplate.Of("离线模式 · {0} · 上次使用", Uuid) with { Note = LastUsed };
}

/// <summary>皮肤信息（「账户」页的皮肤区块）。</summary>
/// <param name="PlayerName">所属玩家。</param>
/// <param name="CurrentSkin">当前皮肤文本（键 + 参数：<c>未获取</c> / <c>默认皮肤</c> / <c>自定义皮肤 · {0}</c>）。</param>
/// <param name="HasCape">是否拥有披风。</param>
/// <param name="Model">皮肤模型。</param>
/// <param name="LastUpdated">最近更新时间文本（键 + 参数）。</param>
public sealed record SkinProfile(
    string PlayerName,
    TextTemplate CurrentSkin,
    bool HasCape,
    SkinModel Model,
    TextTemplate LastUpdated);

/// <summary>
/// 启动游戏所需的账户凭据（M4 的命令行参数来源）。
/// <para>
/// 单独一个类型而不是让 M4 去翻 <see cref="Account"/>：界面上那个 record 是**给人看的行**
/// （含"上次使用""是否高亮"这类展示字段），而命令行需要的是
/// <c>--username</c> / <c>--uuid</c> / <c>--accessToken</c> / <c>--userType</c> 这四个值。
/// 两者混用最容易发生的事故是"把界面文案当成令牌传进去"，而且第 4 个值在界面上根本不存在。
/// </para>
/// </summary>
/// <param name="PlayerName">玩家名（<c>--username</c>）。</param>
/// <param name="Uuid">UUID 文本（<c>--uuid</c>，带连字符的标准形式）。</param>
/// <param name="AccessToken">访问令牌（<c>--accessToken</c>）；离线账户是占位值。</param>
/// <param name="UserType">用户类型（<c>--userType</c>）：离线 <c>mojang</c>、正版 <c>msa</c>。</param>
public sealed record LaunchCredential(
    string PlayerName,
    string Uuid,
    string AccessToken,
    string UserType);

/// <summary>取启动凭据时账户不可用的原因。</summary>
public enum CredentialFailure
{
    /// <summary>库里没有这个账户（或一个账户都没有）。</summary>
    NoSuchAccount,

    /// <summary>
    /// 正版账户的刷新令牌已失效（被吊销、改过密码、或太久没用），必须重新登录一次。
    /// </summary>
    SessionExpired,

    /// <summary>
    /// 刷新时遇到了**与账户状态无关**的失败（网络断了、服务端 5xx）—— 等会儿再试就行。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SessionExpired"/> 分开是有实际后果的：前者让用户"重新登录一次"（要他去浏览器输代码），
    /// 后者只要他"再点一次启动"。把网络抖动说成"登录过期"是在让用户做无用功。
    /// </remarks>
    RefreshFailed,
}

/// <summary>无法取得启动凭据时抛出的异常。</summary>
/// <remarks>
/// 刻意区分原因：M4 的界面要能把"还没有账户""这个号要重新登录""网络断了"说成三句不同的话，
/// 而不是一律"启动失败（{0}）：{1}"。
/// <para>
/// <see cref="Reason"/> 是底层那次失败的原因（键 + 参数，由 App 层拼句子）；为空时
/// <see cref="Exception.Message"/> 是一句**面向排障**的兜底说明（界面上永远不会显示它 ——
/// 界面按 <see cref="Failure"/> 出文案）。
/// </para>
/// </remarks>
public sealed class CredentialException : Exception
{
    /// <summary>构造凭据异常。</summary>
    /// <param name="failure">失败原因。</param>
    /// <param name="playerName">相关玩家名。</param>
    /// <param name="reason">更具体的一句话（例如底层认证失败的原因）；为空时用一句排障用的说明。</param>
    public CredentialException(CredentialFailure failure, string playerName, TextTemplate? reason = null)
        : base(reason?.Key ?? $"Credential unavailable ({failure}) for '{playerName}'.")
    {
        Failure = failure;
        PlayerName = playerName;
        Reason = reason;
    }

    /// <summary>失败原因。</summary>
    public CredentialFailure Failure { get; }

    /// <summary>相关玩家名。</summary>
    public string PlayerName { get; }

    /// <summary>底层失败的原因（键 + 参数）；没有更具体的原因时为 <see langword="null"/>。</summary>
    public TextTemplate? Reason { get; }
}

/// <summary>设备代码流的进行状态。</summary>
public enum DeviceCodeState
{
    /// <summary>等待用户在浏览器完成授权。</summary>
    Waiting,

    /// <summary>已授权。</summary>
    Authorized,

    /// <summary>代码过期。</summary>
    Expired,

    /// <summary>被拒绝或失败。</summary>
    Failed,
}

/// <summary>Microsoft 设备代码流会话。</summary>
/// <param name="VerificationUrl">用户需要在浏览器打开的地址（**已通过登录域名白名单校验**）。</param>
/// <param name="UserCode">用户需要在浏览器输入的代码。</param>
/// <param name="State">当前状态。</param>
/// <param name="ExpiresInMinutes">代码有效期（分钟）。</param>
/// <param name="Failure">失败原因（<see cref="DeviceCodeState.Failed"/> / <see cref="DeviceCodeState.Expired"/> 时非空）。</param>
public sealed record DeviceCodeSession(
    string VerificationUrl,
    string UserCode,
    DeviceCodeState State,
    int ExpiresInMinutes,
    TextTemplate? Failure = null)
{
    /// <summary>设备代码流进行中时，「添加账户」按钮应被禁用（防止重复发起）。</summary>
    public bool IsInProgress => State == DeviceCodeState.Waiting;
}
