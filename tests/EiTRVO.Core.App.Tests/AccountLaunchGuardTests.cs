using EiTRVO.Core.App.Services;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 2026-10-25 用户要求：一个正版账号都没登录过时，离线账户不允许启动游戏。
/// </summary>
[TestClass]
public sealed class AccountLaunchGuardTests
{
    [TestMethod]
    public async Task OfflineLaunchIsBlockedWhenNoMicrosoftAccountEverSignedIn()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Accounts.Clear();
        accounts.Accounts.Add(OfflineAccount());
        accounts.Credential = OfflineCredential();

        var provider = Create(accounts);

        var exception = await Assert.ThrowsExceptionAsync<LaunchIdentityException>(
            () => provider.GetIdentityAsync("1.20.4-Forge"));

        StringAssert.Contains(exception.Reason.Key, "离线账户不能启动");
        Assert.AreEqual(0, accounts.Used.Count, "被拦下的启动不应记录「上次使用」。");
    }

    [TestMethod]
    public async Task OfflineLaunchIsAllowedAfterAMicrosoftAccountWasLoggedIn()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Credential = OfflineCredential();

        var identity = await Create(accounts).GetIdentityAsync("1.20.4-Forge");

        Assert.AreEqual("Alex_Offline", identity.PlayerName);
        Assert.AreEqual("mojang", identity.UserType);
        Assert.AreEqual(1, accounts.Used.Count, "允许的启动仍然记录「上次使用」。");
    }

    private static AccountLaunchIdentityProvider Create(FakeAccountCatalog accounts) =>
        new(accounts, new FakeInstanceCatalog(), new ManualClock(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(8))));

    private static Account OfflineAccount() => new(
        "Alex_Offline",
        AccountKind.Offline,
        "f284a681-7831-35a6-847e-1234112049bf",
        TextTemplate.Of("从未使用"),
        TokenState.Valid,
        IsDefault: true,
        IsActive: true);

    private static LaunchCredential OfflineCredential() => new(
        "Alex_Offline",
        "f284a681-7831-35a6-847e-1234112049bf",
        AccessToken: "0",
        UserType: "mojang");
}
