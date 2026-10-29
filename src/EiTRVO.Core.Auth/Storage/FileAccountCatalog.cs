using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Offline;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Storage;

/// <summary>
/// <see cref="IAccountCatalog"/> 的真实实现：读 <c>accounts.json</c>（DPAPI 密文）。
/// <para>
/// 读取失败时（D-5）：
/// </para>
/// <list type="number">
/// <item>返回空列表，但把失败原因放在 <see cref="StoreStatus"/> 上，界面必须显式提示；</item>
/// <item>写一条 Error 事件（同一个坏文件只写一次，避免每次进页面刷屏）；</item>
/// <item><b>不动文件</b> —— 不删、不重命名、不"自动修复"。</item>
/// </list>
/// <para>
/// 每次调用都现读文件（不做缓存）：登录、切换账户之后界面必须立刻看到变化，
/// 而这里的代价只是一次 DPAPI 解密（几十微秒级）。
/// </para>
/// <para>
/// M5-1 起它还负责**令牌的生命周期**（刷新、以及启动时的按需刷新）：令牌与账户是同一条记录的两半，
/// 把它们拆到两个服务里最典型的后果是"刷新成功了但账户没更新"—— 密文里还是作废的那个刷新令牌，
/// 而下一个动作（下次启动）会因此要求重新登录。
/// </para>
/// </summary>
public sealed class FileAccountCatalog : IAccountCatalog
{
    /// <summary>访问令牌到期前多久就认为"该刷新了"（界面上显示「即将过期」的窗口）。</summary>
    private static readonly TimeSpan ExpiringWindow = TimeSpan.FromMinutes(30);

    /// <summary>启动时提前多久就顺手刷新（比界面窗口短：早 5 分钟以内就不必等了）。</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly AccountStore _store;
    private readonly IClock _clock;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly IMinecraftAuthChain _chain;
    private readonly IDeviceCodeClient _deviceClient;
    private readonly FileStampGate _reportedFailures = new();

    private AccountStoreStatus? _status;

    /// <summary>构造账户目录。</summary>
    /// <param name="store">加密存储。</param>
    /// <param name="clock">时间源（"上次使用"与令牌到期都要可测）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    /// <param name="chain">令牌刷新链（刷新与启动时按需刷新都走它）。</param>
    /// <param name="deviceClient">设备代码/令牌端点客户端（刷新令牌那一步要用）。</param>
    public FileAccountCatalog(
        AccountStore store,
        IClock clock,
        IDiagnosticsLog diagnostics,
        IMinecraftAuthChain chain,
        IDeviceCodeClient deviceClient)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(deviceClient);

        _store = store;
        _clock = clock;
        _diagnostics = diagnostics;
        _chain = chain;
        _deviceClient = deviceClient;
    }

    /// <inheritdoc />
    public AccountStoreStatus? StoreStatus
    {
        get
        {
            _ = LoadStoredAccounts();
            return _status;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Account> GetAccounts()
    {
        var stored = LoadStoredAccounts();

        return [.. stored.Select(Project)];
    }

    /// <inheritdoc />
    public Account AddOffline(string playerName)
    {
        ArgumentNullException.ThrowIfNull(playerName);

        var issue = OfflineName.Validate(playerName);
        if (issue != OfflineNameIssue.None)
        {
            throw new ArgumentException($"离线玩家名不合规（{issue}）：'{playerName}'。", nameof(playerName));
        }

        // 写入路径用**严格**读取：账户文件读不出来时必须拒绝写入，
        // 否则会把用户那份"解不开但可能还有救"的文件覆盖掉（见 LoadForWrite 的说明）。
        var stored = LoadForWrite().ToList();

        // 同名 = 同一个 UUID = 同一个账号。再存一条只会让列表里出现两行指向同一份存档的账户。
        if (stored.Any(account => account.Kind == AccountKind.Offline
                                  && string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"该玩家名的离线账户已存在：'{playerName}'。", nameof(playerName));
        }

        var created = new StoredAccount(
            PlayerName: playerName,
            Kind: AccountKind.Offline,
            Uuid: OfflineUuid.For(playerName),
            MicrosoftRefreshToken: null,
            LastUsedAt: null,
            // 刚建好的账户就是用户要用的那一个。
            IsDefault: true);

        stored.Add(created);
        MakeDefault(stored, stored.Count - 1);
        _store.Save(stored);

        return Project(created);
    }

    /// <inheritdoc />
    public Account AddMicrosoft(MicrosoftLoginResult login)
    {
        ArgumentNullException.ThrowIfNull(login);

        var stored = LoadForWrite().ToList();
        var index = FindMicrosoft(stored, login.Profile.Name, login.Profile.Uuid);

        var updated = BuildMicrosoft(index >= 0 ? stored[index] : null, login, _clock.Now);

        if (index >= 0)
        {
            stored[index] = updated;
        }
        else
        {
            stored.Add(updated);
            index = stored.Count - 1;
        }

        MakeDefault(stored, index);
        _store.Save(stored);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Auth,
            "已保存正版账户 {0}（明文只存在于 DPAPI 密文里）",
            [updated.PlayerName]);

        return Project(updated);
    }

    /// <inheritdoc />
    public async Task<Account> RefreshMicrosoftAsync(
        string playerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var stored = LoadForWrite().ToList();
        var index = FindByName(stored, playerName);

        if (index < 0)
        {
            throw new KeyNotFoundException($"库里没有玩家「{playerName}」。");
        }

        if (stored[index].Kind != AccountKind.Microsoft)
        {
            throw new ArgumentException($"「{playerName}」是离线账户，没有可刷新的令牌。", nameof(playerName));
        }

        var refreshed = await RefreshStoredAsync(stored[index], cancellationToken).ConfigureAwait(false);

        stored[index] = refreshed;
        _store.Save(stored);

        return Project(refreshed);
    }

    /// <inheritdoc />
    public async Task<LaunchCredential> GetCredentialAsync(
        string? playerName,
        CancellationToken cancellationToken = default)
    {
        var stored = LoadStoredAccounts();
        var wanted = playerName?.Trim() ?? string.Empty;

        var account = wanted.Length == 0
            ? stored.FirstOrDefault(static a => a.IsDefault) ?? (stored.Count > 0 ? stored[0] : null)
            : stored.FirstOrDefault(a => string.Equals(a.PlayerName, wanted, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            throw new CredentialException(CredentialFailure.NoSuchAccount, wanted);
        }

        if (account.Kind == AccountKind.Offline)
        {
            return new LaunchCredential(
                PlayerName: account.PlayerName,
                Uuid: account.Uuid,
                // 离线模式没有会话服务端，"令牌"是一个不会被校验的占位值。
                // 用 "0" 而不是空串：空串在拼命令行时很容易被当成"这个参数没有值"而丢掉，
                // 而参数缺失的表现是启动器报"缺少 --accessToken"（看起来像我们的 bug）。
                AccessToken: "0",
                UserType: "mojang");
        }

        var tokens = account.Tokens;

        if (tokens is null || tokens.ExpiresAt - _clock.Now <= RefreshSkew)
        {
            // 这是**唯一**会在读账户时联网的路径，也正是它存在的理由：
            // 访问令牌只活约 24 小时，而玩家点「启动游戏」时不该被要求先去账户页点一次「刷新令牌」。
            try
            {
                var refreshed = await RefreshStoredAsync(account, cancellationToken).ConfigureAwait(false);
                Persist(refreshed);
                account = refreshed;
                tokens = refreshed.Tokens;
            }
            catch (AuthException ex)
            {
                // 两种失败必须分开：令牌被作废要用户重新登录（要他去浏览器输代码），
                // 网络抖动只要他再点一次启动。把后者说成"登录过期"是在让他做无用功。
                throw new CredentialException(
                    ex.RequiresSignIn ? CredentialFailure.SessionExpired : CredentialFailure.RefreshFailed,
                    account.PlayerName,
                    ex.Reason);
            }
            catch (AccountStoreException ex)
            {
                throw new CredentialException(
                    CredentialFailure.RefreshFailed,
                    account.PlayerName,
                    TextTemplate.Of("账户文件读不出来（{0}），无法刷新令牌。", ex.Failure.ToString()));
            }
        }

        return new LaunchCredential(
            PlayerName: account.PlayerName,
            Uuid: account.Uuid,
            AccessToken: tokens!.AccessToken,
            UserType: "msa");
    }

    /// <inheritdoc />
    public void SetDefault(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var stored = LoadForWrite().ToList();
        var index = FindByName(stored, playerName);

        if (index < 0)
        {
            throw new KeyNotFoundException($"库里没有玩家「{playerName}」。");
        }

        MakeDefault(stored, index);
        _store.Save(stored);
    }

    /// <inheritdoc />
    public void Remove(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var stored = LoadForWrite().ToList();
        var index = FindByName(stored, playerName);

        if (index < 0)
        {
            throw new KeyNotFoundException($"库里没有玩家「{playerName}」。");
        }

        var removed = stored[index];
        stored.RemoveAt(index);

        // 删掉的若是默认账户，把"默认"顺延给剩下的第一条：库里还有账户、却一个默认的都没有，
        // 会让"启动游戏用哪个账户"这件事无从回答。
        if (removed.IsDefault && stored.Count > 0)
        {
            MakeDefault(stored, 0);
        }

        _store.Save(stored);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Auth,
            "已移除账户 {0} 的本地记录（官方账户本身不受影响，重新登录即可恢复）",
            [removed.PlayerName]);
    }

    /// <inheritdoc />
    public Account? GetDefaultAccount()
    {
        var accounts = GetAccounts();

        foreach (var account in accounts)
        {
            if (account.IsDefault)
            {
                return account;
            }
        }

        return accounts.Count > 0 ? accounts[0] : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 皮肤数据来自登录链最后一步的档案（M5-1 起是真的）。没有档案时如实说"未获取" ——
    /// 编一个假皮肤是这里最危险的做法：界面上分不出假数据与真数据。
    /// </remarks>
    public SkinProfile GetSkin(string playerName)
    {
        ArgumentNullException.ThrowIfNull(playerName);

        var account = LoadStoredAccounts().FirstOrDefault(a =>
            string.Equals(a.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

        if (account?.Profile is not { } profile)
        {
            return new SkinProfile(
                playerName,
                TextTemplate.Of("未获取"),
                HasCape: false,
                SkinModel.Classic,
                TextTemplate.Of("从未更新"));
        }

        return new SkinProfile(
            PlayerName: account.PlayerName,
            CurrentSkin: DescribeSkin(profile),
            HasCape: profile.HasCape,
            Model: string.Equals(profile.SkinVariant, "slim", StringComparison.OrdinalIgnoreCase)
                ? SkinModel.Slim
                : SkinModel.Classic,
            LastUpdated: InstanceText.FormatTimestamp(profile.FetchedAt, _clock.Now, TextTemplate.Of("从未更新")));
    }

    /// <summary>皮肤那一格的文本：官方档案里没有"皮肤名字"，只有纹理地址，所以写"默认皮肤"或地址尾部那段哈希。</summary>
    /// <returns>键 + 参数（哈希是原样显示的数据；<c>自定义皮肤</c> 是文案，所以它在键里）。</returns>
    private static TextTemplate DescribeSkin(StoredProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.SkinUrl))
        {
            return TextTemplate.Of("默认皮肤");
        }

        var hash = profile.SkinUrl[(profile.SkinUrl.LastIndexOf('/') + 1)..];

        return TextTemplate.Of(
            "自定义皮肤 · {0}",
            hash.Length > 8 ? hash[..8] : hash);
    }

    /// <inheritdoc />
    public MinecraftProfile? GetProfile(string playerName)
    {
        ArgumentNullException.ThrowIfNull(playerName);

        var account = LoadStoredAccounts().FirstOrDefault(a =>
            string.Equals(a.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

        return account?.Profile is { } profile
            ? new MinecraftProfile(account.Uuid, account.PlayerName, profile.SkinUrl, profile.SkinVariant, profile.HasCape)
            : null;
    }

    /// <inheritdoc />
    public Account UpdateProfile(string playerName, MinecraftProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        ArgumentNullException.ThrowIfNull(profile);

        var stored = LoadForWrite().ToList();
        var index = FindByName(stored, playerName);

        if (index < 0)
        {
            throw new KeyNotFoundException($"库里没有玩家「{playerName}」。");
        }

        // 只换档案那几个字段：令牌、默认标记与"上次使用"都保持原样。
        var updated = stored[index] with
        {
            Profile = new StoredProfile(
                profile.SkinUrl,
                profile.SkinVariant,
                profile.HasCape,
                _clock.Now),
        };

        stored[index] = updated;
        _store.Save(stored);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Auth,
            "已更新账户 {0} 的皮肤档案（{1}）",
            [updated.PlayerName, profile.SkinVariant ?? string.Empty]);

        return Project(updated);
    }

    /// <summary>用刷新令牌重跑整条链，返回**更新后**的那条记录（不落盘，落盘由调用方决定）。</summary>
    private async Task<StoredAccount> RefreshStoredAsync(StoredAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.MicrosoftRefreshToken))
        {
            throw new AuthException(
                TextTemplate.Of("这个账户没有可用的刷新令牌，需要重新登录一次。"),
                requiresSignIn: true);
        }

        var microsoft = await _deviceClient
            .RefreshAsync(account.MicrosoftRefreshToken, cancellationToken)
            .ConfigureAwait(false);

        var login = await _chain
            .CompleteAsync(microsoft, progress: null, cancellationToken)
            .ConfigureAwait(false);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Auth,
            "已刷新正版账户 {0} 的令牌",
            [account.PlayerName]);

        return BuildMicrosoft(account, login, _clock.Now);
    }

    /// <summary>把刷新后的那条记录写回去（严格读取：读不出来就抛，不覆盖用户文件）。</summary>
    private void Persist(StoredAccount updated)
    {
        var stored = LoadForWrite().ToList();
        var index = FindByName(stored, updated.PlayerName);

        if (index < 0)
        {
            return;
        }

        stored[index] = updated;
        _store.Save(stored);
    }

    /// <inheritdoc />
    public void RecordUse(string playerName, DateTimeOffset usedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var stored = LoadForWrite().ToList();
        var index = FindByName(stored, playerName);
        if (index < 0)
        {
            return;
        }

        stored[index] = stored[index] with { LastUsedAt = usedAt };
        _store.Save(stored);
    }

    /// <summary>把一次登录结果变成落盘记录（保留原有的"上次使用时间"）。</summary>
    private static StoredAccount BuildMicrosoft(StoredAccount? existing, MicrosoftLoginResult login, DateTimeOffset now) =>
        new(
            PlayerName: login.Profile.Name,
            Kind: AccountKind.Microsoft,
            Uuid: login.Profile.Uuid,
            // 刷新令牌是**轮换**的：新的那一份优先；万一这次响应没给，就保留原来的（否则账户会当场作废）。
            MicrosoftRefreshToken: login.Microsoft.RefreshToken ?? existing?.MicrosoftRefreshToken,
            LastUsedAt: existing?.LastUsedAt,
            IsDefault: true,
            Tokens: new StoredTokens(login.Minecraft.AccessToken, login.Minecraft.ExpiresAt),
            Profile: new StoredProfile(
                login.Profile.SkinUrl,
                login.Profile.SkinVariant,
                login.Profile.HasCape,
                now));

    /// <summary>按玩家名找（大小写不敏感）。</summary>
    private static int FindByName(List<StoredAccount> stored, string playerName) =>
        stored.FindIndex(account => string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>找一个正版账户：先按玩家名，再按 UUID（玩家改名之后仍然能认出是同一个人）。</summary>
    private static int FindMicrosoft(List<StoredAccount> stored, string playerName, string uuid)
    {
        var byName = FindByName(stored, playerName);
        if (byName >= 0)
        {
            return byName;
        }

        return stored.FindIndex(account =>
            account.Kind == AccountKind.Microsoft
            && string.Equals(account.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把第 <paramref name="index"/> 条设为唯一默认账户。</summary>
    private static void MakeDefault(List<StoredAccount> stored, int index)
    {
        for (var i = 0; i < stored.Count; i++)
        {
            var shouldBeDefault = i == index;
            if (stored[i].IsDefault != shouldBeDefault)
            {
                stored[i] = stored[i] with { IsDefault = shouldBeDefault };
            }
        }
    }

    /// <summary>把一个落盘账户投影成界面行。</summary>
    private Account Project(StoredAccount account) => new(
        PlayerName: account.PlayerName,
        Kind: account.Kind,
        Uuid: account.Uuid,
        LastUsed: InstanceText.FormatTimestamp(account.LastUsedAt, _clock.Now, TextTemplate.Of("从未使用")),
        TokenState: TokenStateOf(account),
        IsDefault: account.IsDefault,
        IsActive: false);

    private IReadOnlyList<StoredAccount> LoadStoredAccounts()
    {
        try
        {
            _status = null;
            return _store.Load();
        }
        catch (AccountStoreException ex)
        {
            ReportFailure(ex);
            return [];
        }
    }

    /// <summary>
    /// 为**写入**而读取：读不出来就抛，绝不继续。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LoadStoredAccounts"/>（读失败即"没有"）的差别是本类最要紧的一条不变式：
    /// 界面上"账户文件读不出来 → 显示 0 个账户"是可以接受的（还有 <see cref="StoreStatus"/> 在提示），
    /// 但如果**写入**也走那条路，新建一个离线账户就会把用户那份解不开、但可能还有救的文件
    /// 覆盖成"只有一个新账户" —— 那是不可逆的数据丢失，正是 AGENTS.md §6 第一条禁止的事。
    /// </remarks>
    private IReadOnlyList<StoredAccount> LoadForWrite()
    {
        try
        {
            _status = null;
            return _store.Load();
        }
        catch (AccountStoreException ex)
        {
            ReportFailure(ex);
            throw;
        }
    }

    /// <summary>把读取失败记录成"界面可见 + 只报一次"的状态。</summary>
    private void ReportFailure(AccountStoreException ex)
    {
        _status = new AccountStoreStatus(ex.Failure, ex.FilePath);

        if (_reportedFailures.ShouldReport(ex.FilePath.Length > 0 ? ex.FilePath : _store.FilePath))
        {
            _diagnostics.Write(
                LauncherEventLevel.Error,
                DiagnosticsSources.Launcher,
                "账户文件无法读取（{0}，{1}，{2}），请重新登录（原文件未改动）",
                [ex.Failure.ToString(), ex.FilePath, ex.InnerException?.GetType().Name ?? string.Empty]);
        }
    }

    /// <summary>
    /// 令牌状态（M5-1 起按**真实到期时刻**判定）。
    /// </summary>
    /// <remarks>
    /// 三种取值的含义在这里被定死（它决定 p11 那一列显示什么，也决定用户下一步该做什么）：
    /// <list type="bullet">
    /// <item><b>Valid</b>：访问令牌还有一个多小时可用；</item>
    /// <item><b>Expiring</b>：访问令牌已过期或即将过期，但**刷新令牌还在** —— 下次启动会自动刷新，
    /// 用户什么都不用做（把这种情况说成"已失效"会让他以为要重新登录）；</item>
    /// <item><b>Expired</b>：连刷新令牌都没有（文件被换过、或从未登录成功）—— 必须重新登录。</item>
    /// </list>
    /// </remarks>
    private TokenState TokenStateOf(StoredAccount account)
    {
        if (account.Kind == AccountKind.Offline)
        {
            return TokenState.Valid;
        }

        if (string.IsNullOrEmpty(account.MicrosoftRefreshToken))
        {
            return TokenState.Expired;
        }

        if (account.Tokens is not { } tokens)
        {
            // 有刷新令牌、没有访问令牌（M1 时期写下的记录）：下次启动会刷新一次。
            return TokenState.Expiring;
        }

        return tokens.ExpiresAt - _clock.Now > ExpiringWindow
            ? TokenState.Valid
            : TokenState.Expiring;
    }
}
