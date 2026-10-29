using System.Text;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Auth.Tests.Fakes;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// M1 / D-5：账户目录在"读不出来"时的行为。
/// <para>
/// 这一组用例是本里程碑最要紧的部分：把"读不出来"当成"没有账户"就是静默清空，
/// 用户会以为号被删了。因此每条断言都在钉"失败必须可见、文件必须不动"。
/// </para>
/// </summary>
[TestClass]
public sealed class FileAccountCatalogTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8));

    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;
    private FakeMinecraftAuthChain _chain = null!;
    private FakeDeviceCodeClient _deviceClient = null!;

    /// <summary>每个用例一个全新的数据目录。</summary>
    [TestInitialize]
    public void CreatePaths()
    {
        _paths = TestPaths.Create("catalog");
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(Now);
        _chain = new FakeMinecraftAuthChain();
        _deviceClient = new FakeDeviceCodeClient();
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeletePaths()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    private AccountStore NewStore() => new(_paths, _diagnostics);

    private FileAccountCatalog NewCatalog() => new(NewStore(), _clock, _diagnostics, _chain, _deviceClient);

    // ------------------------------------------------------------ 正常 ---

    [TestMethod]
    public void WithoutAccountsThereIsNothingAndNoFailure()
    {
        var catalog = NewCatalog();

        Assert.AreEqual(0, catalog.GetAccounts().Count);
        Assert.IsNull(catalog.GetDefaultAccount());
        Assert.IsNull(catalog.StoreStatus);
        // 没有档案时如实说"未获取"（键 + 参数：它是一句话，由 App 层拼）。
        Assert.AreEqual("未获取", catalog.GetSkin("Steve").CurrentSkin.Key);
    }

    [TestMethod]
    public void StoredAccountsAreMappedForDisplay()
    {
        NewStore().Save([
            new StoredAccount(
                PlayerName: "Steve",
                Kind: AccountKind.Microsoft,
                Uuid: "069a79f4…38aaf5",
                MicrosoftRefreshToken: "token",
                LastUsedAt: Now,
                IsDefault: true),
        ]);

        var account = NewCatalog().GetAccounts().Single();

        Assert.AreEqual("Steve", account.PlayerName);
        Assert.AreEqual(AccountKind.Microsoft, account.Kind);
        Assert.AreEqual("今天 {0}", account.LastUsed.Key, "上次使用时间按注入的时钟格式化。");
        CollectionAssert.AreEqual(new[] { "16:01" }, account.LastUsed.Parameters.ToArray());
        Assert.AreEqual(
            TokenState.Expiring,
            account.TokenState,
            "有刷新令牌、但还没有访问令牌（M1 时期写下的记录）→ 下次启动会顺手刷新一次，不需要重新登录。");
        Assert.IsTrue(account.IsDefault);
        Assert.IsFalse(account.IsActive, "列表高亮由 ViewModel 决定。");
    }

    [TestMethod]
    public void RecordingUseUpdatesOnlyTheLastUsedField()
    {
        NewStore().Save([
            new StoredAccount(
                PlayerName: "Steve",
                Kind: AccountKind.Microsoft,
                Uuid: "uuid",
                MicrosoftRefreshToken: "token",
                LastUsedAt: null,
                IsDefault: true,
                Tokens: new StoredTokens("access", Now.AddHours(1))),
        ]);

        var catalog = NewCatalog();
        catalog.RecordUse("Steve", Now.AddHours(2));

        var account = catalog.GetAccounts().Single();
        Assert.AreEqual("今天 {0}", account.LastUsed.Key);
        CollectionAssert.AreEqual(new[] { "18:01" }, account.LastUsed.Parameters.ToArray());
        Assert.IsTrue(account.IsDefault, "只更新「上次使用」，默认标记不变。");
        Assert.AreEqual(TokenState.Valid, account.TokenState, "令牌状态也不该被改写。");
    }

    [TestMethod]
    public void TheDefaultAccountIsTheFlaggedOneOtherwiseTheFirst()
    {
        var catalog = NewCatalog();
        NewStore().Save([
            new StoredAccount("Alex", AccountKind.Offline, "离线", null, null, IsDefault: false),
            new StoredAccount("Steve", AccountKind.Microsoft, "uuid", "token", Now, IsDefault: true),
        ]);

        Assert.AreEqual("Steve", catalog.GetDefaultAccount()?.PlayerName);

        NewStore().Save([
            new StoredAccount("Alex", AccountKind.Offline, "离线", null, null, IsDefault: false),
        ]);

        Assert.AreEqual("Alex", NewCatalog().GetDefaultAccount()?.PlayerName);
    }

    [TestMethod]
    public void TokenStatesFollowTheRealExpiry()
    {
        NewStore().Save([
            new StoredAccount("Offline", AccountKind.Offline, "离线", null, null, IsDefault: false),
            new StoredAccount("NoToken", AccountKind.Microsoft, "uuid", null, null, IsDefault: false),
            new StoredAccount(
                "Fresh",
                AccountKind.Microsoft,
                "uuid-2",
                "refresh",
                null,
                IsDefault: false,
                Tokens: new StoredTokens("access", Now.AddHours(20))),
            new StoredAccount(
                "Stale",
                AccountKind.Microsoft,
                "uuid-3",
                "refresh",
                null,
                IsDefault: false,
                Tokens: new StoredTokens("access", Now.AddMinutes(10))),
        ]);

        var accounts = NewCatalog().GetAccounts();

        Assert.AreEqual(TokenState.Valid, accounts[0].TokenState, "离线账户不需要令牌。");
        Assert.AreEqual(TokenState.Expired, accounts[1].TokenState, "连刷新令牌都没有 → 必须重新登录。");
        Assert.AreEqual(TokenState.Valid, accounts[2].TokenState, "访问令牌还有 20 小时。");
        Assert.AreEqual(
            TokenState.Expiring,
            accounts[3].TokenState,
            "只剩 10 分钟 → 显示「即将过期」，但刷新令牌还在，用户什么都不用做。");
    }

    // ------------------------------------------------------------ 失败 ---

    [TestMethod]
    public void AFailureIsVisibleAndTheFileIsLeftAlone()
    {
        var store = NewStore();
        store.Save([
            new StoredAccount("Steve", AccountKind.Microsoft, "uuid", "token", Now, IsDefault: true),
        ]);

        // 篡改密文，模拟"换了机器 / 换了 Windows 用户"。
        var text = File.ReadAllText(store.FilePath, Encoding.UTF8);
        var file = LauncherJson.Deserialize<AccountFile>(text, LauncherJson.Indented)!;
        var cipher = Convert.FromBase64String(file.Payload!);
        cipher[0] ^= 0xFF;
        var tampered = LauncherJson.Serialize(
            file with { Payload = Convert.ToBase64String(cipher) },
            LauncherJson.Indented);
        AtomicFile.WriteAllText(store.FilePath, tampered);

        var catalog = NewCatalog();

        Assert.AreEqual(0, catalog.GetAccounts().Count);
        Assert.IsNotNull(catalog.StoreStatus, "失败必须能被界面看到，否则就是静默清空。");
        Assert.AreEqual(AccountStoreFailure.DecryptionFailed, catalog.StoreStatus.Failure);
        Assert.AreEqual(store.FilePath, catalog.StoreStatus.FilePath);
        Assert.AreEqual(tampered, File.ReadAllText(store.FilePath, Encoding.UTF8), "原文件必须原样保留。");

        var entry = _diagnostics.Entries[^1];
        Assert.AreEqual(LauncherEventLevel.Error, entry.Level);
        Assert.AreEqual("账户文件无法读取（{0}，{1}，{2}），请重新登录（原文件未改动）", entry.MessageKey);
        Assert.AreEqual(nameof(AccountStoreFailure.DecryptionFailed), entry.Parameters[0]);
    }

    [TestMethod]
    public void TheSameFailureIsOnlyReportedOnce()
    {
        AtomicFile.WriteAllText(_paths.AccountsFile, "{ not json");
        var catalog = NewCatalog();

        _ = catalog.GetAccounts();
        _ = catalog.StoreStatus;
        _ = catalog.GetAccounts();

        Assert.AreEqual(1, _diagnostics.Entries.Count, "同一个坏文件不该每次进页面都刷一条事件。");
    }

    [TestMethod]
    public void ACorruptFileIsAlsoVisibleToTheUi()
    {
        AtomicFile.WriteAllText(_paths.AccountsFile, "{ not json");

        var catalog = NewCatalog();

        Assert.AreEqual(AccountStoreFailure.Corrupt, catalog.StoreStatus?.Failure);
        Assert.AreEqual(0, catalog.GetAccounts().Count);
    }

    [TestMethod]
    public void ReadingRejectsANullPlayerName()
    {
        Assert.ThrowsException<ArgumentNullException>(() => NewCatalog().GetSkin(null!));
    }

    // ------------------------------------------------- D-6：离线账户 ---

    [TestMethod]
    public void AnOfflineAccountIsCreatedWithTheJavaCompatibleUuid()
    {
        var catalog = NewCatalog();

        var created = catalog.AddOffline("Alex_Offline");

        Assert.AreEqual("Alex_Offline", created.PlayerName);
        Assert.AreEqual(AccountKind.Offline, created.Kind);
        Assert.AreEqual("f284a681-7831-35a6-847e-1234112049bf", created.Uuid, "UUID 必须与 Java 版算出来的一致。");
        Assert.AreEqual(TokenState.Valid, created.TokenState);
        Assert.IsTrue(created.IsDefault, "刚建的账户就是用户要用的那一个。");
        Assert.AreEqual("从未使用", created.LastUsed.Key);

        // 真的落盘了：换一个全新的 catalog 实例也读得到。
        var reopened = NewCatalog().GetAccounts().Single();
        Assert.AreEqual("Alex_Offline", reopened.PlayerName);
        Assert.AreEqual(created.Uuid, reopened.Uuid);
    }

    [TestMethod]
    public void AddingAnOfflineAccountMakesItTheOnlyDefault()
    {
        NewStore().Save([
            new StoredAccount("Steve", AccountKind.Microsoft, "uuid", "token", Now, IsDefault: true),
        ]);

        var catalog = NewCatalog();
        catalog.AddOffline("Alex_Offline");

        var accounts = NewCatalog().GetAccounts();

        Assert.AreEqual(2, accounts.Count, "已有的账户不能被新账户顶掉。");
        Assert.AreEqual("Alex_Offline", catalog.GetDefaultAccount()?.PlayerName);
        Assert.AreEqual(1, accounts.Count(static account => account.IsDefault), "默认账户只能有一个。");
        Assert.IsFalse(accounts.Single(account => account.PlayerName == "Steve").IsDefault);
    }

    [TestMethod]
    public void TheSameOfflineNameIsRefusedInsteadOfStoredTwice()
    {
        var catalog = NewCatalog();
        catalog.AddOffline("Alex_Offline");

        // 同名 = 同一个 UUID = 同一个账号：再存一条只会让列表里出现两行指向同一份存档的账户。
        Assert.ThrowsException<ArgumentException>(() => catalog.AddOffline("Alex_Offline"));
        Assert.ThrowsException<ArgumentException>(() => catalog.AddOffline("alex_offline"));

        Assert.AreEqual(1, NewCatalog().GetAccounts().Count);
    }

    [TestMethod]
    [DataRow("ab")]
    [DataRow("玩家一号")]
    [DataRow("")]
    [DataRow("abc def")]
    public void AnIllegalOfflineNameIsRefused(string playerName)
    {
        Assert.ThrowsException<ArgumentException>(() => NewCatalog().AddOffline(playerName));
        Assert.AreEqual(0, NewCatalog().GetAccounts().Count, "被拒的输入不该留下任何东西。");
    }

    [TestMethod]
    public void SetDefaultMovesTheFlagAndKeepsEveryoneElse()
    {
        var catalog = NewCatalog();
        catalog.AddOffline("Alex_Offline");
        catalog.AddOffline("Steve_2");

        catalog.SetDefault("Alex_Offline");

        var accounts = NewCatalog().GetAccounts();
        Assert.AreEqual(2, accounts.Count);
        Assert.IsTrue(accounts.Single(account => account.PlayerName == "Alex_Offline").IsDefault);
        Assert.IsFalse(accounts.Single(account => account.PlayerName == "Steve_2").IsDefault);
        Assert.AreEqual("Alex_Offline", catalog.GetDefaultAccount()?.PlayerName);
    }

    [TestMethod]
    public void SetDefaultOnAMissingNameIsRefusedLoudly()
    {
        Assert.ThrowsException<KeyNotFoundException>(() => NewCatalog().SetDefault("nobody"));
    }

    [TestMethod]
    public void RemovingAnAccountDropsItFromTheLibraryAndFromTheDisk()
    {
        var catalog = NewCatalog();
        catalog.AddOffline("Alex_Offline");
        catalog.AddOffline("Steve_2");

        catalog.Remove("Alex_Offline");

        // 界面上少了一行……
        var accounts = NewCatalog().GetAccounts();
        Assert.AreEqual(1, accounts.Count);
        Assert.AreEqual("Steve_2", accounts[0].PlayerName);

        // ……再删一次会被拒绝：它真的不在库里了，而不是只被"藏"起来。
        Assert.ThrowsException<KeyNotFoundException>(() => catalog.Remove("Alex_Offline"));
    }

    [TestMethod]
    public void RemovingTheDefaultAccountPromotesTheNextOne()
    {
        NewStore().Save([
            new StoredAccount("Steve", AccountKind.Microsoft, "uuid", "token", Now, IsDefault: true),
            new StoredAccount("Alex_Offline", AccountKind.Offline, "f284a681-7831-35a6-847e-1234112049bf", null, null, IsDefault: false),
        ]);

        var catalog = NewCatalog();
        catalog.Remove("Steve");

        // 库里还有账户，就必须还有一个"默认"——否则"启动游戏用哪个账户"无从回答。
        var accounts = NewCatalog().GetAccounts();
        Assert.AreEqual(1, accounts.Count);
        Assert.AreEqual("Alex_Offline", accounts[0].PlayerName);
        Assert.IsTrue(accounts[0].IsDefault, "默认标记要顺延给剩下的那一条。");
        Assert.AreEqual("Alex_Offline", catalog.GetDefaultAccount()?.PlayerName);
    }

    [TestMethod]
    public void RemovingTheLastAccountLeavesNoDefault()
    {
        var catalog = NewCatalog();
        catalog.AddOffline("Alex_Offline");

        catalog.Remove("Alex_Offline");

        Assert.AreEqual(0, NewCatalog().GetAccounts().Count);
        Assert.IsNull(NewCatalog().GetDefaultAccount());
    }

    [TestMethod]
    public void RemovingAMissingNameIsRefusedLoudly()
    {
        Assert.ThrowsException<KeyNotFoundException>(() => NewCatalog().Remove("nobody"));
    }

    [TestMethod]
    public void RemovingRefusesToTouchAnUnreadableAccountFile()
    {
        // 与"新建账户不覆盖读不出来的文件"同一条规矩（AGENTS.md §6）：移除也是写入路径。
        NewStore().Save([
            new StoredAccount("Alex_Offline", AccountKind.Offline, "f284a681-7831-35a6-847e-1234112049bf", null, null, IsDefault: true),
        ]);

        var store = NewStore();
        var text = File.ReadAllText(store.FilePath, Encoding.UTF8);
        var file = LauncherJson.Deserialize<AccountFile>(text, LauncherJson.Indented)!;
        var cipher = Convert.FromBase64String(file.Payload!);
        cipher[0] ^= 0xFF;
        var tampered = LauncherJson.Serialize(file with { Payload = Convert.ToBase64String(cipher) }, LauncherJson.Indented);
        AtomicFile.WriteAllText(store.FilePath, tampered);

        Assert.ThrowsException<AccountStoreException>(() => NewCatalog().Remove("Alex_Offline"));

        // 那份"解不开但可能还有救"的文件必须原样留着。
        Assert.AreEqual(tampered, File.ReadAllText(store.FilePath, Encoding.UTF8));
    }

    [TestMethod]
    public async Task AnOfflineCredentialCarriesEverythingTheCommandLineNeeds()
    {
        var catalog = NewCatalog();
        catalog.AddOffline("Alex_Offline");

        var credential = await catalog.GetCredentialAsync(null);

        Assert.AreEqual("Alex_Offline", credential.PlayerName);
        Assert.AreEqual("f284a681-7831-35a6-847e-1234112049bf", credential.Uuid);
        Assert.AreEqual("0", credential.AccessToken, "离线令牌是一个不会被校验的占位值，但不能是空串。");
        Assert.AreEqual("mojang", credential.UserType);
    }

    [TestMethod]
    public async Task TheCredentialFollowsTheRequestedNameToo()
    {
        var catalog = NewCatalog();
        catalog.AddOffline("Alex_Offline");
        catalog.AddOffline("Steve_2");
        catalog.SetDefault("Alex_Offline");

        Assert.AreEqual("Steve_2", (await catalog.GetCredentialAsync("Steve_2")).PlayerName);
        Assert.AreEqual(
            "Alex_Offline",
            (await catalog.GetCredentialAsync("alex_offline")).PlayerName,
            "名字大小写不敏感。");
    }

    [TestMethod]
    public async Task AMicrosoftCredentialIsRefreshedOnDemandAndTheNewTokensArePersisted()
    {
        var catalog = NewCatalog();

        // 登录落盘（链路已走完的结果）—— 这正是 MicrosoftSignIn 成功之后做的事。
        var account = catalog.AddMicrosoft(new MicrosoftLoginResult(
            new MicrosoftTokens("ms-access", "first-refresh", Now.AddHours(1)),
            new MinecraftToken("mc-access-first", Now.AddHours(1)),
            _chain.Profile));

        Assert.AreEqual("Steve", account.PlayerName);

        // 把访问令牌改成已过期：再取凭据时必须**自动刷新**，而不是要求用户重新登录。
        var stored = NewStore().Load().Single();
        NewStore().Save([stored with { Tokens = new StoredTokens("expired", Now.AddMinutes(-1)) }]);
        _deviceClient.RefreshCalls = 0;

        var credential = await catalog.GetCredentialAsync("Steve");

        Assert.AreEqual("mc-access-token", credential.AccessToken);
        Assert.AreEqual("msa", credential.UserType, "正版账户的 --userType 是 msa（离线是 mojang）。");
        Assert.AreEqual("069a79f4-44e9-4726-a5be-fca90e38aaf5", credential.Uuid, "UUID 是官方档案里那个。");
        Assert.AreEqual(1, _deviceClient.RefreshCalls);

        // 新的访问令牌与**轮换后的**刷新令牌都必须写回去：只存一半会让账户在下次刷新时作废。
        var after = NewStore().Load().Single();
        Assert.AreEqual("mc-access-token", after.Tokens?.AccessToken);
        Assert.AreEqual("rotated-refresh-token", after.MicrosoftRefreshToken);
    }

    [TestMethod]
    public async Task AValidAccessTokenIsUsedWithoutAnyNetworkCall()
    {
        var catalog = NewCatalog();

        catalog.AddMicrosoft(new MicrosoftLoginResult(
            new MicrosoftTokens("ms-access", "refresh", Now.AddHours(1)),
            new MinecraftToken("mc-access", Now.AddHours(20)),
            _chain.Profile));

        var credential = await catalog.GetCredentialAsync("Steve");

        Assert.AreEqual("mc-access", credential.AccessToken);
        Assert.AreEqual(0, _deviceClient.RefreshCalls, "令牌还有 20 小时，取凭据不该联网。");
    }

    [TestMethod]
    public async Task AnExpiredRefreshTokenRequiresSigningInAgain()
    {
        var catalog = NewCatalog();

        catalog.AddMicrosoft(new MicrosoftLoginResult(
            new MicrosoftTokens("ms-access", "refresh", Now.AddHours(1)),
            new MinecraftToken("mc-access", Now.AddMinutes(-1)),
            _chain.Profile));

        _deviceClient.RefreshFailure = new AuthException(
            TextTemplate.Of("这个账户的登录已过期，需要重新登录一次。"),
            requiresSignIn: true);

        var exception = await Assert.ThrowsExceptionAsync<CredentialException>(
            () => catalog.GetCredentialAsync("Steve"));

        Assert.AreEqual(CredentialFailure.SessionExpired, exception.Failure);
        Assert.AreEqual("Steve", exception.PlayerName);
    }

    [TestMethod]
    public async Task ANetworkFailureWhileRefreshingIsNotReportedAsAnExpiredSession()
    {
        var catalog = NewCatalog();

        catalog.AddMicrosoft(new MicrosoftLoginResult(
            new MicrosoftTokens("ms-access", "refresh", Now.AddHours(1)),
            new MinecraftToken("mc-access", Now.AddMinutes(-1)),
            _chain.Profile));

        // 关键区别：网络抖一下要用户"再点一次启动"，而不是"去浏览器重新登录"。
        _deviceClient.RefreshFailure = new AuthException(
            TextTemplate.Of("刷新令牌失败：{0}。", "连接被重置"));

        var exception = await Assert.ThrowsExceptionAsync<CredentialException>(
            () => catalog.GetCredentialAsync("Steve"));

        Assert.AreEqual(CredentialFailure.RefreshFailed, exception.Failure);
        Assert.AreEqual("刷新令牌失败：{0}。", exception.Reason!.Key, "底层原因按「键 + 参数」原样带上来。");
        CollectionAssert.AreEqual(new[] { "连接被重置" }, exception.Reason.Parameters.ToArray());
    }

    [TestMethod]
    public async Task ACredentialForAnUnknownOrAbsentAccountIsRefused()
    {
        var catalog = NewCatalog();

        var none = await Assert.ThrowsExceptionAsync<CredentialException>(() => catalog.GetCredentialAsync(null));
        Assert.AreEqual(CredentialFailure.NoSuchAccount, none.Failure);

        catalog.AddOffline("Alex_Offline");
        var unknown = await Assert.ThrowsExceptionAsync<CredentialException>(() => catalog.GetCredentialAsync("nobody"));
        Assert.AreEqual(CredentialFailure.NoSuchAccount, unknown.Failure);
    }

    [TestMethod]
    public void TheSkinComesFromTheStoredProfile()
    {
        var catalog = NewCatalog();

        catalog.AddMicrosoft(new MicrosoftLoginResult(
            new MicrosoftTokens("ms-access", "refresh", Now.AddHours(1)),
            new MinecraftToken("mc-access", Now.AddHours(1)),
            _chain.Profile));

        var skin = catalog.GetSkin("Steve");

        // 官方档案里没有皮肤名，只有纹理地址 —— 所以是「自定义皮肤 · 地址尾部那段哈希」：
        // 文案在**键**里、哈希在**参数**里（把哈希写进键会让每个账户一个键）。
        Assert.AreEqual("自定义皮肤 · {0}", skin.CurrentSkin.Key, "官方档案里没有皮肤名，只有纹理地址。");
        CollectionAssert.AreEqual(new[] { "abcdef12" }, skin.CurrentSkin.Parameters.ToArray());
        Assert.AreEqual(SkinModel.Classic, skin.Model);
        Assert.IsFalse(skin.HasCape);
        Assert.AreEqual("今天 {0}", skin.LastUpdated.Key);
        CollectionAssert.AreEqual(new[] { "16:01" }, skin.LastUpdated.Parameters.ToArray());
    }

    [TestMethod]
    public void AddingAnAccountNeverOverwritesAFileThatCouldNotBeRead()
    {
        // 界面上"读不出来 = 0 个账户"可以接受（副标题会提示），但**写入**绝不能走同一条路：
        // 新建一个离线账户就会把用户那份解不开、但可能还有救的文件覆盖成"只有一个新账户" ——
        // 那是不可逆的数据丢失（AGENTS.md §6 第一条）。
        NewStore().Save([
            new StoredAccount("Steve", AccountKind.Microsoft, "uuid", "token", Now, IsDefault: true),
        ]);

        var store = NewStore();
        var text = File.ReadAllText(store.FilePath, Encoding.UTF8);
        var file = LauncherJson.Deserialize<AccountFile>(text, LauncherJson.Indented)!;
        var cipher = Convert.FromBase64String(file.Payload!);
        cipher[0] ^= 0xFF;
        var tampered = LauncherJson.Serialize(file with { Payload = Convert.ToBase64String(cipher) }, LauncherJson.Indented);
        AtomicFile.WriteAllText(store.FilePath, tampered);

        var catalog = NewCatalog();

        // 读：按"没有账户"处理，但状态可见。
        Assert.AreEqual(0, catalog.GetAccounts().Count);
        Assert.IsNotNull(catalog.StoreStatus);

        // 写：当场拒绝，且文件一个字都没变。
        Assert.ThrowsException<AccountStoreException>(() => catalog.AddOffline("Alex_Offline"));
        Assert.ThrowsException<AccountStoreException>(() => catalog.SetDefault("Alex_Offline"));
        Assert.AreEqual(tampered, File.ReadAllText(store.FilePath, Encoding.UTF8), "原文件必须原样保留。");
    }

    // ------------------------------------------------------------ D-9 档案 ---

    [TestMethod]
    public void TheRawProfileIsAvailableForRequestsNotJustForDisplay()
    {
        var catalog = NewCatalog();

        Assert.IsNull(catalog.GetProfile("Steve"), "还没有档案时如实返回 null。");

        catalog.AddMicrosoft(new MicrosoftLoginResult(
            new MicrosoftTokens("ms-access", "refresh", Now.AddHours(1)),
            new MinecraftToken("mc-access", Now.AddHours(1)),
            _chain.Profile));

        var profile = catalog.GetProfile("Steve");

        Assert.IsNotNull(profile);
        Assert.AreEqual(
            "http://textures.minecraft.net/texture/abcdef1234567890",
            profile!.SkinUrl,
            "这里是**原始形状**：D-9 换模型要把这个地址原样发回去，界面文案（「自定义皮肤 · abcdef12」）发不出去。");
        Assert.AreEqual("CLASSIC", profile.SkinVariant);
        Assert.IsFalse(profile.HasCape);
    }

    [TestMethod]
    public void UpdatingTheProfileOnlyTouchesTheSkinFields()
    {
        var catalog = NewCatalog();

        catalog.AddMicrosoft(new MicrosoftLoginResult(
            new MicrosoftTokens("ms-access", "refresh", Now.AddHours(1)),
            new MinecraftToken("mc-access", Now.AddHours(1)),
            _chain.Profile));

        var before = NewStore().Load().Single();

        var account = catalog.UpdateProfile(
            "Steve",
            new MinecraftProfile(
                "069a79f4-44e9-4726-a5be-fca90e38aaf5",
                "Steve",
                "https://textures.minecraft.net/texture/0123456789abcdef",
                "SLIM",
                HasCape: true));

        Assert.AreEqual("Steve", account.PlayerName);
        Assert.AreEqual(TokenState.Valid, account.TokenState);

        var after = NewStore().Load().Single();

        Assert.AreEqual("refresh", after.MicrosoftRefreshToken);
        Assert.AreEqual("mc-access", after.Tokens?.AccessToken);
        Assert.AreEqual(before.LastUsedAt, after.LastUsedAt, "换皮肤不该顺带刷新「上次使用」。");

        // 四格跟着档案走。
        var skin = catalog.GetSkin("Steve");
        Assert.AreEqual("自定义皮肤 · {0}", skin.CurrentSkin.Key);
        CollectionAssert.AreEqual(new[] { "01234567" }, skin.CurrentSkin.Parameters.ToArray());
        Assert.AreEqual(SkinModel.Slim, skin.Model);
        Assert.IsTrue(skin.HasCape);
    }

    [TestMethod]
    public void UpdatingTheProfileOfAnUnknownPlayerIsRefused()
    {
        var catalog = NewCatalog();

        Assert.ThrowsException<KeyNotFoundException>(() => catalog.UpdateProfile(
            "Nobody",
            new MinecraftProfile("uuid", "Nobody", null, null, HasCape: false)));
    }

    [TestMethod]
    public void UpdatingTheProfileNeverOverwritesAFileThatCouldNotBeRead()
    {
        NewStore().Save([
            new StoredAccount("Steve", AccountKind.Microsoft, "uuid", "token", Now, IsDefault: true),
        ]);

        var store = NewStore();
        var text = File.ReadAllText(store.FilePath, Encoding.UTF8);
        var file = LauncherJson.Deserialize<AccountFile>(text, LauncherJson.Indented)!;
        var cipher = Convert.FromBase64String(file.Payload!);
        cipher[0] ^= 0xFF;
        var tampered = LauncherJson.Serialize(file with { Payload = Convert.ToBase64String(cipher) }, LauncherJson.Indented);
        AtomicFile.WriteAllText(store.FilePath, tampered);

        Assert.ThrowsException<AccountStoreException>(() => NewCatalog().UpdateProfile(
            "Steve",
            new MinecraftProfile("uuid", "Steve", null, null, HasCape: false)));

        Assert.AreEqual(tampered, File.ReadAllText(store.FilePath, Encoding.UTF8), "原文件必须原样保留。");
    }
}
