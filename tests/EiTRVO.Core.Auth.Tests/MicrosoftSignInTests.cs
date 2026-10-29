using System.Net;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Auth.Tests.Fakes;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// 一次完整登录的编排：设备代码流 → 刷新链 → **全链成功才落盘**。
/// <para>
/// 这一组用真网络桩走完整条链，因此它同时证明了"四步的顺序与依赖关系是对的"
/// （XSTS 用的是 XBL 给的 <c>uhs</c>，Minecraft 登录用的是 XSTS 的令牌）。
/// </para>
/// </summary>
[TestClass]
public sealed class MicrosoftSignInTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private LauncherPaths _paths = null!;
    private AccountStore _store = null!;
    private FileAccountCatalog _catalog = null!;
    private StubHandler _handler = null!;
    private StubHttpClientFactory _factory = null!;

    /// <summary>每个用例一套全新的数据目录与网络桩。</summary>
    [TestInitialize]
    public void Setup()
    {
        _paths = TestPaths.Create("signin");
        _paths.EnsureCreated();

        var diagnostics = new RecordingDiagnosticsLog();
        _store = new AccountStore(_paths, diagnostics);
        _handler = new StubHandler();
        _factory = new StubHttpClientFactory(_handler);

        var clock = new FixedClock(Now);
        var chain = new MinecraftAuthChain(_factory, HttpHostPolicy.Default, clock);

        _catalog = new FileAccountCatalog(
            _store,
            clock,
            diagnostics,
            chain,
            new DeviceCodeClient(
                _factory,
                HttpHostPolicy.Default,
                new AuthOptions { ClientId = "11111111-2222-3333-4444-555555555555" },
                clock));
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    /// <summary>释放网络桩（它就是 <see cref="HttpMessageHandler"/>）。</summary>
    public void Dispose() => _handler.Dispose();

    [TestMethod]
    public async Task ASuccessfulSignInStoresTheAccountWithItsTokensAndProfile()
    {
        StubHappyPath();

        var stages = new CollectingProgress<AuthProgress>();
        var flow = NewFlow();

        var account = await new MicrosoftSignIn(flow, NewChain(), _catalog, new RecordingDiagnosticsLog())
            .SignInAsync(stages);

        // 三个用户看得见的时刻 + 完成。
        CollectionAssert.AreEqual(
            new[]
            {
                AuthStage.RequestingCode,
                AuthStage.WaitingForUser,
                AuthStage.Authorized,
                AuthStage.CompletingSignIn,
                AuthStage.CompletingSignIn,
                AuthStage.CompletingSignIn,
                AuthStage.CompletingSignIn,
                AuthStage.Completed,
            },
            stages.Values.Select(static s => s.Stage).ToArray());

        Assert.AreEqual("Steve", account.PlayerName);
        Assert.AreEqual(AccountKind.Microsoft, account.Kind);
        Assert.IsTrue(account.IsDefault, "刚登录的账户就是用户要用的那一个。");

        // 落盘的是密文，但用真目录读回来必须一模一样。
        var stored = _store.Load().Single();
        Assert.AreEqual("Steve", stored.PlayerName);
        Assert.AreEqual("069a79f4-44e9-4726-a5be-fca90e38aaf5", stored.Uuid);
        Assert.AreEqual("ms-refresh", stored.MicrosoftRefreshToken);
        Assert.AreEqual("mc-token", stored.Tokens?.AccessToken);
        Assert.IsNotNull(stored.Profile, "皮肤（D-8）用的档案也在里面。");
    }

    [TestMethod]
    public async Task AFailureInTheChainStoresNothing()
    {
        // 设备代码流成功、链路失败：**一个字都不落盘** —— 否则账户列表里会出现一个
        // 有名字没令牌的号，点启动才发现用不了。
        _handler
            .RespondTo("/devicecode", HttpStatusCode.OK, DeviceCode())
            .RespondTo("/token", HttpStatusCode.OK, MicrosoftTokens())
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.OK, XblToken())
            .RespondTo("xsts.auth.xboxlive.com", HttpStatusCode.Unauthorized, "{\"XErr\":2148916233}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => new MicrosoftSignIn(NewFlow(), NewChain(), _catalog, new RecordingDiagnosticsLog()).SignInAsync());

        Assert.AreEqual(
            "这个 Microsoft 账户还没有 Xbox 档案。请先在 Xbox 官网（或任意 Xbox 应用）里创建一个档案，再回来登录。",
            exception.Reason.Key,
            "链路失败时用户看到的是**那一步的键**，不是一句笼统的「登录失败」。");
        Assert.AreEqual(0, _catalog.GetAccounts().Count, "链路失败时不该出现任何账户。");
        Assert.IsFalse(File.Exists(_store.FilePath), "连账户文件都不该被创建。");
    }

    [TestMethod]
    public async Task AnAbandonedDeviceCodeStoresNothingAndSaysWhy()
    {
        _handler.RespondTo("/devicecode", HttpStatusCode.OK, DeviceCode())
            .RespondTo("/token", HttpStatusCode.BadRequest, "{\"error\":\"expired_token\"}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => new MicrosoftSignIn(NewFlow(), NewChain(), _catalog, new RecordingDiagnosticsLog()).SignInAsync());

        Assert.AreEqual("设备代码已过期，请重新发起登录。", exception.Reason.Key);
        Assert.AreEqual(0, _catalog.GetAccounts().Count);
    }

    /// <summary>走完整条链所需的全部桩响应。</summary>
    private void StubHappyPath() =>
        _handler
            .RespondTo("/devicecode", HttpStatusCode.OK, DeviceCode())
            .RespondTo("/token", HttpStatusCode.OK, MicrosoftTokens())
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.OK, XblToken())
            .RespondTo("xsts.auth.xboxlive.com", HttpStatusCode.OK, XstsToken())
            .RespondTo("authentication/login_with_xbox", HttpStatusCode.OK, MinecraftTokens())
            .RespondTo("minecraft/profile", HttpStatusCode.OK, Profile());

    private DeviceCodeFlow NewFlow() =>
        new(
            new DeviceCodeClient(
                _factory,
                HttpHostPolicy.Default,
                new AuthOptions { ClientId = "11111111-2222-3333-4444-555555555555" },
                new FixedClock(Now)),
            new FixedClock(Now),
            new RecordingDiagnosticsLog(),
            static (_, _) => Task.CompletedTask);

    private MinecraftAuthChain NewChain() =>
        new(_factory, HttpHostPolicy.Default, new FixedClock(Now));

    private static string DeviceCode() =>
        "{\"device_code\":\"DEVICE\",\"user_code\":\"K7QP-4M2X\",\"verification_uri\":\"https://microsoft.com/link\","
        + "\"expires_in\":900,\"interval\":1}";

    private static string MicrosoftTokens() =>
        "{\"access_token\":\"ms-access\",\"refresh_token\":\"ms-refresh\",\"expires_in\":3600}";

    private static string XblToken() =>
        "{\"Token\":\"xbl-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"uhs-123\"}]}}";

    private static string XstsToken() =>
        "{\"Token\":\"xsts-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"uhs-123\"}]}}";

    private static string MinecraftTokens() =>
        "{\"access_token\":\"mc-token\",\"expires_in\":86400}";

    private static string Profile() =>
        "{\"id\":\"069a79f444e94726a5befca90e38aaf5\",\"name\":\"Steve\","
        + "\"skins\":[{\"state\":\"ACTIVE\",\"url\":\"http://textures.minecraft.net/texture/abcdef1234567890\",\"variant\":\"CLASSIC\"}],"
        + "\"capes\":[]}";
}
