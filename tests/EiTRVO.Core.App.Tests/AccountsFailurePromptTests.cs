using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Auth.Sample;

using EiTRVO.Core.Launcher.Text;
namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M1 / D-5 的**用户可见**那一半：账户文件读不出来时，界面到底显示了什么。
/// <para>
/// 内核侧（<c>FileAccountCatalogTests</c>）已经钉住了"失败要被记录、原文件不能动"；
/// 这里钉的是最后一跳 —— 失败原因必须真的走到界面上。把它显示成「0 个已登录账户」
/// 正是 D-5 禁止的静默清空，而这条链路只有断言界面文案才能守住。
/// </para>
/// </summary>
[TestClass]
public sealed class AccountsFailurePromptTests
{
    [TestMethod]
    [DataRow(
        AccountStoreFailure.DecryptionFailed,
        "账户文件无法读取：换了 Windows 用户或换了机器（密文绑当前用户），请重新登录；原文件未改动")]
    [DataRow(
        AccountStoreFailure.Corrupt,
        "账户文件无法读取：文件已损坏，请重新登录；原文件未改动")]
    [DataRow(
        AccountStoreFailure.UnsupportedPlatform,
        "账户文件无法读取：当前平台不支持账户加密存储")]
    public void TheAccountsPageExplainsWhyTheStoreCouldNotBeRead(
        AccountStoreFailure failure,
        string expected)
    {
        var page = TestViewModels.Accounts(new FailingAccountCatalog(failure));

        Assert.AreEqual(expected, page.SectionSubtitle);
        Assert.AreEqual(0, page.Accounts.Count);
        Assert.IsNull(page.SelectedAccount);
    }

    [TestMethod]
    public void WithoutAFailureTheSubtitleKeepsCountingAccounts()
    {
        var page = TestViewModels.Accounts(new SampleAccountCatalog());

        Assert.AreEqual("2 个已登录账户", page.SectionSubtitle);
    }

    /// <summary>只回答"存储读取失败"的账户目录替身。</summary>
    private sealed class FailingAccountCatalog(AccountStoreFailure failure) : IAccountCatalog
    {
        /// <inheritdoc />
        public AccountStoreStatus? StoreStatus { get; } = new(failure, @"C:\data\accounts.json");

        /// <inheritdoc />
        public IReadOnlyList<Account> GetAccounts() => [];

        /// <inheritdoc />
        public Account? GetDefaultAccount() => null;

        /// <inheritdoc />
        public SkinProfile GetSkin(string playerName) =>
            new(playerName, TextTemplate.Of("未获取"), HasCape: false, SkinModel.Classic, TextTemplate.Of("从未更新"));

        /// <inheritdoc />
        public MinecraftProfile? GetProfile(string playerName) => null;

        /// <inheritdoc />
        public Account UpdateProfile(string playerName, MinecraftProfile profile) =>
            throw new NotSupportedException("替身不支持写入档案。");

        /// <inheritdoc />
        /// <remarks>这个替身只用来验证"读取失败要说出来"，新建/改默认不参与。</remarks>
        public Account AddOffline(string playerName) =>
            throw new NotSupportedException("替身不支持新建账户。");

        /// <inheritdoc />
        public void SetDefault(string playerName) => throw new NotSupportedException("替身不支持改默认账户。");

        /// <inheritdoc />
        public void Remove(string playerName) => throw new NotSupportedException("替身不支持移除账户。");

        /// <inheritdoc />
        public Account AddMicrosoft(MicrosoftLoginResult login) =>
            throw new NotSupportedException("替身不支持写入正版账户。");

        /// <inheritdoc />
        public Task<Account> RefreshMicrosoftAsync(string playerName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("替身没有真实令牌。");

        /// <inheritdoc />
        public Task<LaunchCredential> GetCredentialAsync(string? playerName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("替身没有真实令牌。");

        /// <inheritdoc />
        public void RecordUse(string playerName, DateTimeOffset usedAt)
        {
            // 读取失败的替身没有可写记录。
        }
    }
}
