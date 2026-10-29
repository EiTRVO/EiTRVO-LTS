using System.Text;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Auth.Tests.Fakes;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// M1 / D-4、D-5：账户的 DPAPI 加密持久化。
/// <para>
/// 这里跑的是**真实的 DPAPI**（测试进程就在 Windows 上），因此"密文确实解不开"
/// 这类断言是真的验证，而不是对 mock 的验证。测试项目需要能调用 DPAPI，
/// 而这正是它必须是 Windows 测试项目的原因。
/// </para>
/// </summary>
[TestClass]
public sealed class AccountStoreTests
{
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一个全新的数据目录。</summary>
    [TestInitialize]
    public void CreatePaths()
    {
        _paths = TestPaths.Create("store");
        _diagnostics = new RecordingDiagnosticsLog();
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

    private static StoredAccount MicrosoftAccount(string name = "Steve", bool isDefault = true) =>
        new(
            PlayerName: name,
            Kind: AccountKind.Microsoft,
            Uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5",
            MicrosoftRefreshToken: "M.C532_BAY.0.U.-Csecret-refresh-token",
            LastUsedAt: new DateTimeOffset(2026, 9, 30, 16, 1, 2, TimeSpan.FromHours(8)),
            IsDefault: isDefault);

    // ------------------------------------------------------------ 读写 ---

    [TestMethod]
    public void AMissingFileMeansNoAccountsAndCreatesNothing()
    {
        var store = NewStore();

        Assert.AreEqual(0, store.Load().Count);
        Assert.IsFalse(File.Exists(store.FilePath), "只读不该产生文件。");
    }

    [TestMethod]
    public void AccountsSurviveARoundTrip()
    {
        var store = NewStore();
        var accounts = new[]
        {
            MicrosoftAccount(),
            new StoredAccount(
                PlayerName: "Alex_Offline",
                Kind: AccountKind.Offline,
                Uuid: "由用户名派生",
                MicrosoftRefreshToken: null,
                LastUsedAt: null,
                IsDefault: false),
        };

        store.Save(accounts);

        var loaded = NewStore().Load();
        Assert.AreEqual(2, loaded.Count);
        Assert.AreEqual(accounts[0], loaded[0]);
        Assert.AreEqual(accounts[1], loaded[1]);
    }

    [TestMethod]
    public void SavingAnEmptyListIsAllowedAndStaysEmpty()
    {
        var store = NewStore();

        store.Save([]);

        Assert.AreEqual(0, NewStore().Load().Count);
    }

    [TestMethod]
    public void SavingTwiceKeepsThePreviousVersionAsBackup()
    {
        var store = NewStore();
        store.Save([MicrosoftAccount("Steve")]);
        store.Save([MicrosoftAccount("Alex")]);

        Assert.IsTrue(File.Exists(store.FilePath + AtomicFile.BackupSuffix));
        Assert.AreEqual("Alex", store.Load()[0].PlayerName);
    }

    // ------------------------------------------------------------ 加密 ---

    [TestMethod]
    public void TheTokenNeverAppearsInPlaintextOnDisk()
    {
        var store = NewStore();
        store.Save([MicrosoftAccount()]);

        var text = File.ReadAllText(store.FilePath, Encoding.UTF8);

        Assert.IsFalse(
            text.Contains("M.C532_BAY", StringComparison.Ordinal),
            "刷新令牌一旦明文落盘就等于把账号交出去。");
        Assert.IsFalse(text.Contains("Steve", StringComparison.Ordinal), "玩家名也不该明文出现。");
        StringAssert.Contains(text, "\"protection\": \"" + AccountStore.ProtectionName + "\"");
        StringAssert.Contains(text, "\"schemaVersion\": 1");
    }

    [TestMethod]
    public void ThePayloadIsBase64OfACiphertextThatDiffersFromThePlaintext()
    {
        var store = NewStore();
        store.Save([MicrosoftAccount()]);

        var file = LauncherJson.Deserialize<AccountFile>(
            File.ReadAllText(store.FilePath, Encoding.UTF8),
            LauncherJson.Indented);

        Assert.IsNotNull(file?.Payload);
        var cipher = Convert.FromBase64String(file.Payload);
        Assert.IsTrue(cipher.Length > 0);

        // DPAPI 的输出必然带自己的头，且不等于明文。
        var plaintext = Encoding.UTF8.GetBytes("M.C532_BAY.0.U.-Csecret-refresh-token");
        Assert.IsFalse(cipher.AsSpan().IndexOf(plaintext) >= 0, "密文里不该出现明文片段。");
    }

    // ---------------------------------------------------------- 坏输入 ---

    [TestMethod]
    public void ATamperedPayloadFailsToDecrypt()
    {
        var store = NewStore();
        store.Save([MicrosoftAccount()]);

        var text = File.ReadAllText(store.FilePath, Encoding.UTF8);
        var file = LauncherJson.Deserialize<AccountFile>(text, LauncherJson.Indented)!;
        var cipher = Convert.FromBase64String(file.Payload!);

        // 动一个字节：等价于"文件被别的程序改写"或"这台机器不是加密它的那台"。
        cipher[^1] ^= 0xFF;
        AtomicFile.WriteAllText(
            store.FilePath,
            LauncherJson.Serialize(file with { Payload = Convert.ToBase64String(cipher) }, LauncherJson.Indented));

        var exception = Assert.ThrowsException<AccountStoreException>(() => NewStore().Load());

        Assert.AreEqual(AccountStoreFailure.DecryptionFailed, exception.Failure);
        Assert.AreEqual(store.FilePath, exception.FilePath);
    }

    [TestMethod]
    public void ATruncatedFileIsReportedAsCorruptAndKeptIntact()
    {
        var store = NewStore();
        const string Truncated = "{\"schemaVersion\": 1, \"protection\": \"dpapi-current-user\", \"payl";
        AtomicFile.WriteAllText(store.FilePath, Truncated);

        var exception = Assert.ThrowsException<AccountStoreException>(() => store.Load());

        Assert.AreEqual(AccountStoreFailure.Corrupt, exception.Failure);
        Assert.AreEqual(Truncated, File.ReadAllText(store.FilePath, Encoding.UTF8), "原文件必须原样保留。");
    }

    [TestMethod]
    public void APayloadThatIsNotBase64IsReportedAsCorrupt()
    {
        var store = NewStore();
        AtomicFile.WriteAllText(
            store.FilePath,
            "{\"schemaVersion\":1,\"protection\":\"dpapi-current-user\",\"payload\":\"这不是 base64 !!!\"}");

        var exception = Assert.ThrowsException<AccountStoreException>(() => store.Load());

        Assert.AreEqual(AccountStoreFailure.Corrupt, exception.Failure);
    }

    [TestMethod]
    public void AnEmptyPayloadMeansNoAccounts()
    {
        var store = NewStore();
        AtomicFile.WriteAllText(store.FilePath, "{\"schemaVersion\":1,\"payload\":\"\"}");

        Assert.AreEqual(0, store.Load().Count);
    }

    [TestMethod]
    public void SavingRejectsANullList()
    {
        Assert.ThrowsException<ArgumentNullException>(() => NewStore().Save(null!));
    }

    [TestMethod]
    public void SavingIsRecordedAsAnEvent()
    {
        NewStore().Save([MicrosoftAccount()]);

        var entry = _diagnostics.Entries.Single();
        Assert.AreEqual("已保存账户文件（{0} 个账户）", entry.MessageKey);
        Assert.AreEqual("1", entry.Parameters[0]);
    }
}
