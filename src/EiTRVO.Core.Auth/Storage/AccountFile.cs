namespace EiTRVO.Core.Auth.Storage;

/// <summary>账户存储失败的原因。</summary>
public enum AccountStoreFailure
{
    /// <summary>当前平台不支持 DPAPI（本产品仅支持 Windows，正常不会出现）。</summary>
    UnsupportedPlatform,

    /// <summary>文件本身不是合法的 JSON 信封（被截断 / 手工改坏）。</summary>
    Corrupt,

    /// <summary>密文解不开：换了 Windows 用户或换了机器（DPAPI 密钥绑用户）。</summary>
    DecryptionFailed,
}

/// <summary>
/// 账户存储无法读取时抛出的异常。
/// <para>
/// 之所以要有专门的类型：调用方**必须**能区分"没有账户"与"账户读不出来"。
/// 把后者当成前者处理（返回空列表）就是"静默清空"—— 用户会以为自己的号被删了。
/// </para>
/// </summary>
public sealed class AccountStoreException : Exception
{
    /// <summary>构造账户存储异常。</summary>
    /// <param name="failure">失败原因。</param>
    /// <param name="filePath">相关文件路径。</param>
    /// <param name="innerException">底层异常。</param>
    public AccountStoreException(AccountStoreFailure failure, string filePath, Exception? innerException = null)
        : base($"账户文件无法读取（{failure}）：{filePath}", innerException)
    {
        Failure = failure;
        FilePath = filePath;
    }

    /// <summary>失败原因。</summary>
    public AccountStoreFailure Failure { get; }

    /// <summary>相关文件路径。</summary>
    public string FilePath { get; }
}

/// <summary>账户文件的读取状态（正常时为 <see langword="null"/>）。</summary>
/// <param name="Failure">失败原因。</param>
/// <param name="FilePath">文件路径。</param>
public sealed record AccountStoreStatus(AccountStoreFailure Failure, string FilePath);

/// <summary>
/// <c>accounts.json</c> 的落盘形状。
/// <para>
/// 磁盘上**看不到任何账户字段**：令牌、玩家名、UUID 全都在
/// <see cref="Payload"/> 这一团 DPAPI 密文里。这不是为了好看，
/// 而是为了不给"顺手读一眼 json"留下任何可能 —— 令牌一旦泄露就是账号被盗。
/// </para>
/// </summary>
public sealed record AccountFile
{
    /// <summary>当前 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>文件格式版本。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>使用的保护方式（将来换算法时用来识别旧文件）。</summary>
    public string Protection { get; init; } = AccountStore.ProtectionName;

    /// <summary>DPAPI 密文的 Base64 文本。</summary>
    public string? Payload { get; init; }
}

/// <summary>
/// 一个已登录账户（<b>明文形状</b>，只在内存与密文内部出现）。
/// </summary>
/// <param name="PlayerName">玩家名。</param>
/// <param name="Kind">账户类型。</param>
/// <param name="Uuid">UUID（离线账户为派生的 UUID；正版账户是官方档案里的那个）。</param>
/// <param name="MicrosoftRefreshToken">Microsoft 刷新令牌；离线账户为 <see langword="null"/>。</param>
/// <param name="LastUsedAt">上次使用时间。</param>
/// <param name="IsDefault">是否为默认账户。</param>
/// <param name="Tokens">
/// Minecraft 访问令牌与到期时刻（M5-1）。**可选字段**：M1 时期写下的文件里没有它，
/// 读进来是 <see langword="null"/>，表现是"首次启动时刷新一次" —— 不需要迁移，也不需要新 schema 版本。
/// </param>
/// <param name="Profile">玩家档案（皮肤等，M5-1 / D-8）；同样可选。</param>
public sealed record StoredAccount(
    string PlayerName,
    AccountKind Kind,
    string Uuid,
    string? MicrosoftRefreshToken,
    DateTimeOffset? LastUsedAt,
    bool IsDefault,
    StoredTokens? Tokens = null,
    StoredProfile? Profile = null);

/// <summary>落盘的访问令牌。</summary>
/// <param name="AccessToken">Minecraft 服务访问令牌（**绝不进日志**）。</param>
/// <param name="ExpiresAt">到期时刻。</param>
public sealed record StoredTokens(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// 落盘的玩家档案（D-8：皮肤展示用的那几个字段）。
/// <para>
/// 皮肤纹理本身**不落盘**：它随时可以从官方重新取，而把用户数据目录变成一个图片缓存
/// 会让"账户文件"这件事变得难以解释。这里存的是"最近一次拿到的档案说了什么"。
/// </para>
/// </summary>
/// <param name="SkinUrl">皮肤纹理地址；没有自定义皮肤时为 <see langword="null"/>。</param>
/// <param name="SkinVariant">模型（<c>CLASSIC</c> / <c>SLIM</c>）。</param>
/// <param name="HasCape">是否拥有披风。</param>
/// <param name="FetchedAt">这次档案是什么时候取到的（界面上「最近更新」用它）。</param>
public sealed record StoredProfile(
    string? SkinUrl,
    string? SkinVariant,
    bool HasCape,
    DateTimeOffset FetchedAt);
