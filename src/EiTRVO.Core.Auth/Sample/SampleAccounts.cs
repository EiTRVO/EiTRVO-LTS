using EiTRVO.Core.Launcher.Text;

using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;

namespace EiTRVO.Core.Auth.Sample;

// ============================================================================
//  仅前端还原用的内存数据源（与 Launcher 下的 Sample 同性质）：
//  返回设计稿里逐字抄下来的演示数据，不发起任何网络请求、不落盘。
//
//  注意：`IDeviceCodeFlow` 的演示实现已在 M5-1 删除 —— 真实实现就在那里（DeviceCodeFlow），
//  而"返回一组固定链接与代码"的假实现在界面已经接上真实流程之后只剩误导。
//  App 的测试用例改用 `Fakes.cs` 里的替身。
// ============================================================================

/// <summary>演示用账户目录。</summary>
public sealed class SampleAccountCatalog : IAccountCatalog
{
    private static readonly Account[] Accounts =
    [
        new("Steve", AccountKind.Microsoft, "069a79f4-44e9-4726-a5be-fca90e38aaf5", TextTemplate.Of("今天 {0}", "16:01"), TokenState.Valid, true, true),
        // 离线账户的 UUID 是派生值（`OfflinePlayer:Alex_Offline` 的 Java 兼容 MD5 名字型 UUID），
        // 所以演示数据里就放那个真实派生值 —— 放一句"（由用户名派生）"会让界面在英文句子里露出中文，
        // 而 UUID 那一格本来就是**数据**（AGENTS.md §5.3 第 ③ 条）。
        new("Alex_Offline", AccountKind.Offline, "f284a681-7831-35a6-847e-1234112049bf", TextTemplate.Of("{0}", "2026/9/25"), TokenState.Expired, false, false),
    ];

    /// <inheritdoc />
    public IReadOnlyList<Account> GetAccounts() => Accounts;

    /// <inheritdoc />
    public Account? GetDefaultAccount() => Accounts[0];

    /// <inheritdoc />
    public SkinProfile GetSkin(string playerName) => new(
        PlayerName: playerName,
        CurrentSkin: TextTemplate.Of("{0}（默认）", "Steve"),
        HasCape: false,
        Model: SkinModel.Classic,
        LastUpdated: TextTemplate.Of("{0}", "2026/8/14 10:22"));

    /// <inheritdoc />
    /// <remarks>演示数据源没有磁盘文件，因此永远没有"读取失败"这回事。</remarks>
    public Storage.AccountStoreStatus? StoreStatus => null;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">演示数据源不支持新建（它不是真的存储）。</exception>
    public Account AddOffline(string playerName) =>
        throw new NotSupportedException("演示数据源不支持新建离线账户（真实实现见 FileAccountCatalog）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">演示数据源不支持修改（它不是真的存储）。</exception>
    public void SetDefault(string playerName) =>
        throw new NotSupportedException("演示数据源不支持修改默认账户（真实实现见 FileAccountCatalog）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">演示数据源不支持删除（它不是真的存储）。</exception>
    public void Remove(string playerName) =>
        throw new NotSupportedException("演示数据源不支持移除账户（真实实现见 FileAccountCatalog）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">演示数据源不支持写入正版账户。</exception>
    public Account AddMicrosoft(MicrosoftLoginResult login) =>
        throw new NotSupportedException("演示数据源不支持写入正版账户（真实实现见 FileAccountCatalog）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">演示数据源没有真实令牌。</exception>
    public Task<Account> RefreshMicrosoftAsync(string playerName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("演示数据源没有真实令牌（真实实现见 FileAccountCatalog）。");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">演示数据源没有真实令牌。</exception>
    public Task<LaunchCredential> GetCredentialAsync(string? playerName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("演示数据源没有真实令牌（真实实现见 FileAccountCatalog）。");

    /// <inheritdoc />
    /// <remarks>演示数据是静态数组，没有可写的"上次使用"。</remarks>
    public void RecordUse(string playerName, DateTimeOffset usedAt)
    {
        // 没有落盘，也就没有可更新的记录。
    }

    /// <inheritdoc />
    /// <remarks>演示数据没有档案（"皮肤"那四格是设计稿里逐字抄下来的静态文本）。</remarks>
    public MinecraftProfile? GetProfile(string playerName) => null;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">演示数据源不支持修改（它不是真的存储）。</exception>
    public Account UpdateProfile(string playerName, MinecraftProfile profile) =>
        throw new NotSupportedException("演示数据源不支持写入档案（真实实现见 FileAccountCatalog）。");
}
