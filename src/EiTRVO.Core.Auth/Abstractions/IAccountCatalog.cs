using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Abstractions;

/// <summary>账户目录。</summary>
public interface IAccountCatalog
{
    /// <summary>全部已登录账户。</summary>
    /// <returns>账户。</returns>
    IReadOnlyList<Account> GetAccounts();

    /// <summary>默认账户（页头「添加账户」按钮的标签取它的玩家名）。</summary>
    /// <returns>默认账户；没有账户时为 <see langword="null"/>。</returns>
    Account? GetDefaultAccount();

    /// <summary>某个玩家的皮肤信息。</summary>
    /// <param name="playerName">玩家名。</param>
    /// <returns>皮肤信息。</returns>
    SkinProfile GetSkin(string playerName);

    /// <summary>
    /// 某个账户最近一次拿到的官方档案（**原始形状**：纹理地址、模型、披风）。
    /// <para>
    /// 与 <see cref="GetSkin"/> 的差别是"给人看的四格"与"拿去做请求的数据"：
    /// D-9 换模型时要**档案里的那个纹理地址**（界面上的「自定义皮肤 · ab12cd34」发不出去），
    /// 而少了这个访问器，实现方就只能去解析界面文案 —— 那是把展示格式当协议用。
    /// </para>
    /// </summary>
    /// <param name="playerName">玩家名。</param>
    /// <returns>档案；没有或还没取到过时为 <see langword="null"/>。</returns>
    MinecraftProfile? GetProfile(string playerName);

    /// <summary>
    /// 用一次接口响应里的档案更新某个账户的皮肤数据（D-9：上传 / 换模型 / 恢复默认都会拿到新档案）。
    /// </summary>
    /// <param name="playerName">玩家名。</param>
    /// <param name="profile">新档案。</param>
    /// <returns>更新后的账户行。</returns>
    /// <exception cref="KeyNotFoundException">库里没有这个账户。</exception>
    /// <exception cref="AccountStoreException">
    /// 账户文件读不出来 —— 写入路径**拒绝继续**（不覆盖用户那份可能还有救的文件）。
    /// </exception>
    /// <remarks>
    /// 只动档案那几个字段：令牌、默认标记、上次使用时间都不受影响 ——
    /// 换一张皮肤不该顺带把"上次使用"刷新成今天。
    /// </remarks>
    Account UpdateProfile(string playerName, MinecraftProfile profile);

    /// <summary>
    /// 账户文件的读取状态（D-5）。
    /// <para>
    /// 正常时为 <see langword="null"/>。非 null 表示"账户文件在，但读不出来" ——
    /// 调用方必须把这件事**显式告诉用户**，而不是把它显示成"没有账户"。
    /// </para>
    /// </summary>
    AccountStoreStatus? StoreStatus { get; }

    /// <summary>
    /// 新建一个离线账户（D-6）：UUID 由玩家名派生（<c>OfflinePlayer:&lt;名字&gt;</c> 的
    /// Java 兼容 MD5 名字型 UUID），并**立即落盘**。
    /// <para>
    /// 新建的账户会成为**默认账户**：用户刚刚创建它，就是要用它。
    /// </para>
    /// </summary>
    /// <param name="playerName">玩家名（已由调用方 Trim）。</param>
    /// <returns>新建的账户。</returns>
    /// <exception cref="ArgumentException">名字不合规，或同名离线账户已存在。</exception>
    Account AddOffline(string playerName);

    /// <summary>把某个账户设为默认账户（其余账户的默认标记会被清掉）。</summary>
    /// <param name="playerName">玩家名。</param>
    /// <exception cref="KeyNotFoundException">库里没有这个账户。</exception>
    void SetDefault(string playerName);

    /// <summary>
    /// 移除一个账户：**只删掉本机这一份记录**（含它保存的令牌）。
    /// <para>
    /// 官方账户本身不受影响 —— 删掉之后用同一个账号重新登录就能回来，所以界面上必须如实这么说，
    /// 不能写成"删除账号"。整个文件仍然走原子写。
    /// </para>
    /// <para>
    /// 被移除的若是默认账户，**默认标记顺延给剩下的第一条**：否则会留下"库里还有账户、却一个默认的都没有"
    /// 这种状态，而启动游戏时必须能挑出一个账户来。
    /// </para>
    /// </summary>
    /// <param name="playerName">玩家名。</param>
    /// <exception cref="KeyNotFoundException">库里没有这个账户。</exception>
    /// <exception cref="AccountStoreException">
    /// 账户文件读不出来 —— 写入路径**拒绝继续**（不覆盖用户那份可能还有救的文件）。
    /// </exception>
    void Remove(string playerName);

    /// <summary>
    /// 把一次**完整的**正版登录落盘（新建或就地更新同名/同 UUID 的那一条），并设为默认账户。
    /// </summary>
    /// <param name="login">登录结果（链路四步全成功才有它）。</param>
    /// <returns>落盘后的账户行。</returns>
    /// <remarks>
    /// 传进来的是"已经走完全链的结果"，而不是"去登录"：这样"读失败不得写文件"这条纪律
    /// （AGENTS.md §6）与错误处理都留在调用方，而落盘这一段只有一条路径 ——
    /// 半成品账户（有名字没令牌、有令牌没档案）在类型上就不存在。
    /// </remarks>
    Account AddMicrosoft(MicrosoftLoginResult login);

    /// <summary>
    /// 用存储里的刷新令牌重跑一遍刷新链，并把**新的**令牌与档案写回去。
    /// <para>
    /// 刷新令牌是**轮换**的：每一次成功刷新都会作废旧的那个，所以"换到了新令牌"与"存下来"
    /// 必须是同一次操作的上下两步，中间断掉就等于把账户弄丢（下次只能重新登录）。
    /// </para>
    /// </summary>
    /// <param name="playerName">玩家名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>更新后的账户行。</returns>
    /// <exception cref="KeyNotFoundException">库里没有这个账户。</exception>
    /// <exception cref="ArgumentException">这个账户是离线账户（它没有可刷新的令牌）。</exception>
    /// <exception cref="AuthException">刷新链失败。</exception>
    Task<Account> RefreshMicrosoftAsync(string playerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取启动游戏所需的凭据（M4 的命令行参数）。
    /// </summary>
    /// <param name="playerName">玩家名；为空表示用默认账户。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>凭据。</returns>
    /// <exception cref="CredentialException">
    /// 没有这个账户 / 一个账户都没有（<see cref="CredentialFailure.NoSuchAccount"/>）、
    /// 刷新令牌失效需要重新登录（<see cref="CredentialFailure.SessionExpired"/>）、
    /// 或刷新时遇到网络类失败（<see cref="CredentialFailure.RefreshFailed"/>）。
    /// </exception>
    /// <remarks>
    /// 这是**唯一**会在读账户时联网的方法，而这正是它存在的理由：Minecraft 的访问令牌只活约 24 小时，
    /// 而玩家点「启动游戏」时不该被要求先去账户页点一次「刷新令牌」。
    /// 令牌还没到期的情形完全不联网（直接返回存储里的那一个）。
    /// </remarks>
    Task<LaunchCredential> GetCredentialAsync(
        string? playerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录一次账户使用（启动游戏时调用）：只更新「上次使用」，不改令牌、默认标记与档案。
    /// </summary>
    /// <param name="playerName">玩家名。</param>
    /// <param name="usedAt">使用时间。</param>
    /// <exception cref="AccountStoreException">
    /// 账户文件读不出来 —— 写入路径拒绝继续（不覆盖用户那份可能还有救的文件）。
    /// </exception>
    void RecordUse(string playerName, DateTimeOffset usedAt);
}

/// <summary>认证配置。Client ID 必须来自配置，禁止硬编码（AGENTS.md §6 / §7）。</summary>
public sealed class AuthOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Auth";

    /// <summary>Microsoft Azure 应用注册的 Client ID。EiTRVO 产品线共用一个值。</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>用于界面展示的脱敏 Client ID（只露前 8 位）；没配置时是「（未配置）」那个键。</summary>
    public TextTemplate MaskedClientId =>
        string.IsNullOrEmpty(ClientId)
            ? TextTemplate.Of("（未配置）")
            : TextTemplate.Of("{0}", ClientId.Length <= 8 ? ClientId : ClientId[..8] + "-…");
}
