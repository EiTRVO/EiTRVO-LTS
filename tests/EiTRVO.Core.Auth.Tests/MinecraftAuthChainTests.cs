using System.Globalization;
using System.Net;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Tests.Fakes;
using EiTRVO.Core.Launcher.Net;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// 令牌刷新链（D-2）：refresh → Xbox Live → XSTS → Minecraft 服务 → profile。
/// <para>
/// 规划 §4 的验收第 3 条就是这一组的形状：**每个链路步骤的 URL、方法与请求体被逐一断言**。
/// 三个只看状态码看不出来的坑都在这里被钉住：<c>RpsTicket</c> 的 <c>d=</c> 前缀、
/// XSTS 的 <c>x-xbl-contract-version</c>、以及 <c>identityToken</c> 的
/// <c>XBL3.0 x=&lt;uhs&gt;;&lt;xsts&gt;</c> 格式。
/// </para>
/// </summary>
[TestClass]
public sealed class MinecraftAuthChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheFourStepsUseTheDocumentedEndpointsAndBodies()
    {
        var handler = new StubHandler()
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.OK, XblToken("xbl-token", "uhs-123"))
            .RespondTo("xsts.auth.xboxlive.com", HttpStatusCode.OK, Xsts("xsts-token", "uhs-123"))
            .RespondTo("authentication/login_with_xbox", HttpStatusCode.OK, MinecraftToken("mc-token", 86400))
            .RespondTo("minecraft/profile", HttpStatusCode.OK, Profile(
                "069a79f444e94726a5befca90e38aaf5",
                "Steve",
                "http://textures.minecraft.net/texture/abcdef1234567890",
                "CLASSIC",
                capes: 1));

        var chain = NewChain(handler);
        var result = await chain.CompleteAsync(new MicrosoftTokens("msa-token", "refresh", Now.AddHours(1)));

        Assert.AreEqual(4, handler.Requests.Count);

        // ---- ① Xbox Live 用户认证 ----
        var xbl = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Post, xbl.Method);
        Assert.AreEqual("https://user.auth.xboxlive.com/user/authenticate", xbl.Url);
        StringAssert.Contains(xbl.Body, "\"AuthMethod\":\"RPS\"");
        StringAssert.Contains(xbl.Body, "\"SiteName\":\"user.auth.xboxlive.com\"");
        StringAssert.Contains(xbl.Body, "\"RpsTicket\":\"d=msa-token\"", "少了 d= 前缀或大小写不对，XBL 都会直接 400。");
        StringAssert.Contains(xbl.Body, "\"RelyingParty\":\"http://auth.xboxlive.com\"");
        StringAssert.Contains(xbl.Body, "\"TokenType\":\"JWT\"");

        // ---- ② XSTS ----
        var xsts = handler.Requests[1];
        Assert.AreEqual("https://xsts.auth.xboxlive.com/xsts/authorize", xsts.Url);
        Assert.AreEqual("1", xsts.ContractVersion, "XSTS 需要 x-xbl-contract-version: 1。");
        StringAssert.Contains(xsts.Body, "\"SandboxId\":\"RETAIL\"");
        StringAssert.Contains(xsts.Body, "\"UserTokens\":[\"xbl-token\"]");
        StringAssert.Contains(xsts.Body, "\"RelyingParty\":\"rp://api.minecraftservices.com/\"");

        // ---- ③ Minecraft 服务 ----
        var minecraft = handler.Requests[2];
        Assert.AreEqual("https://api.minecraftservices.com/authentication/login_with_xbox", minecraft.Url);
        StringAssert.Contains(
            minecraft.Body,
            "\"identityToken\":\"XBL3.0 x=uhs-123;xsts-token\"",
            "x= 与分号都是格式的一部分。");

        // ---- ④ 玩家档案 ----
        var profile = handler.Requests[3];
        Assert.AreEqual(HttpMethod.Get, profile.Method);
        Assert.AreEqual("https://api.minecraftservices.com/minecraft/profile", profile.Url);
        Assert.AreEqual("Bearer mc-token", profile.Authorization);

        // ---- 结果 ----
        // 官方给的是 http://textures.minecraft.net/...（明文），链路把它升到 https ——
        // 出站策略只允许 https，而"直接丢掉"会让每个真实账户的皮肤都变成未获取。
        Assert.AreEqual("069a79f4-44e9-4726-a5be-fca90e38aaf5", result.Profile.Uuid, "UUID 换成了带连字符的标准形式。");
        Assert.AreEqual("Steve", result.Profile.Name);
        Assert.AreEqual("https://textures.minecraft.net/texture/abcdef1234567890", result.Profile.SkinUrl);
        Assert.AreEqual("CLASSIC", result.Profile.SkinVariant);
        Assert.IsTrue(result.Profile.HasCape);
        Assert.AreEqual("mc-token", result.Minecraft.AccessToken);
        Assert.AreEqual(Now.AddSeconds(86400), result.Minecraft.ExpiresAt, "到期时刻按 expires_in 与注入的时钟算。");
    }

    [DataTestMethod]
    [DataRow(2148916233L, "这个 Microsoft 账户还没有 Xbox 档案。请先在 Xbox 官网（或任意 Xbox 应用）里创建一个档案，再回来登录。")]
    [DataRow(2148916235L, "Xbox Live 在当前地区不可用（或该账户已被封禁），无法登录。")]
    [DataRow(2148916238L, "这是未成年账户，需要先加入一个家庭组（由成年人配置）才能用于 Minecraft。")]
    public async Task XstsErrorsAreTranslatedInsteadOfSurfacedAs401(long xerr, string expected)
    {
        // 官方文档之外几乎没人知道这几个数字，而它们恰好是用户自己能处理的三类情况。
        var handler = new StubHandler()
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.OK, XblToken("xbl", "uhs"))
            .RespondTo(
                "xsts.auth.xboxlive.com",
                HttpStatusCode.Unauthorized,
                "{\"Identity\":\"0\",\"XErr\":" + xerr.ToString(CultureInfo.InvariantCulture) + ",\"Message\":\"\"}");

        var chain = NewChain(handler);

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => chain.CompleteAsync(new MicrosoftTokens("msa", "refresh", Now.AddHours(1))));

        Assert.AreEqual(expected, exception.Reason.Key, "每个 XErr 对应**一个键**（各说各的话，不合并）。");
        Assert.AreEqual(0, exception.Reason.Parameters.Count, "这几句话没有参数。");
    }

    [TestMethod]
    public async Task AnUnknownXstsErrorStillSaysSomethingUseful()
    {
        var handler = new StubHandler()
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.OK, XblToken("xbl", "uhs"))
            .RespondTo("xsts.auth.xboxlive.com", HttpStatusCode.Unauthorized, "{\"XErr\":1234567}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => NewChain(handler).CompleteAsync(new MicrosoftTokens("msa", "refresh", Now.AddHours(1))));

        Assert.AreEqual(
            "Xbox Live 拒绝了这次授权（XErr={0}）。请确认这个账户已创建 Xbox 档案、且所在地区支持 Xbox Live。",
            exception.Reason.Key);
        CollectionAssert.AreEqual(new[] { "1234567" }, exception.Reason.Parameters.ToArray(), "XErr 是参数。");
    }

    [TestMethod]
    public async Task AMissingProfileMeansTheAccountDoesNotOwnTheGame()
    {
        var handler = new StubHandler()
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.OK, XblToken("xbl", "uhs"))
            .RespondTo("xsts.auth.xboxlive.com", HttpStatusCode.OK, Xsts("xsts", "uhs"))
            .RespondTo("authentication/login_with_xbox", HttpStatusCode.OK, MinecraftToken("mc", 86400))
            .RespondTo("minecraft/profile", HttpStatusCode.NotFound, "{\"path\":\"/minecraft/profile\",\"errorType\":\"NOT_FOUND\"}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => NewChain(handler).CompleteAsync(new MicrosoftTokens("msa", "refresh", Now.AddHours(1))));

        Assert.AreEqual("这个 Microsoft 账户没有 Minecraft Java 版档案（尚未购买或尚未创建）。", exception.Reason.Key);
    }

    [TestMethod]
    public async Task AFailedXblStepStopsBeforeXsts()
    {
        var handler = new StubHandler()
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.BadRequest, "{\"Message\":\"bad ticket\"}");

        var exception = await Assert.ThrowsExceptionAsync<AuthException>(
            () => NewChain(handler).CompleteAsync(new MicrosoftTokens("msa", "refresh", Now.AddHours(1))));

        Assert.AreEqual("登录 Xbox Live 失败（{0} {1}）。请稍后再试。", exception.Reason.Key);
        CollectionAssert.AreEqual(new[] { "400", "Bad Request" }, exception.Reason.Parameters.ToArray());
        Assert.AreEqual(1, handler.Requests.Count, "第一步失败就不该继续往下走（更不该落盘）。");
    }

    [TestMethod]
    public async Task ASkinUrlOutsideTheAllowListIsDroppedButTheLoginStillSucceeds()
    {
        var handler = new StubHandler()
            .RespondTo("user.auth.xboxlive.com", HttpStatusCode.OK, XblToken("xbl", "uhs"))
            .RespondTo("xsts.auth.xboxlive.com", HttpStatusCode.OK, Xsts("xsts", "uhs"))
            .RespondTo("authentication/login_with_xbox", HttpStatusCode.OK, MinecraftToken("mc", 86400))
            .RespondTo("minecraft/profile", HttpStatusCode.OK, Profile(
                "069a79f444e94726a5befca90e38aaf5",
                "Steve",
                "https://evil.example/skin.png",
                "SLIM",
                capes: 0));

        var result = await NewChain(handler).CompleteAsync(new MicrosoftTokens("msa", "refresh", Now.AddHours(1)));

        // 一个反常的皮肤地址不该让整次登录失败，但也不该被留下来当"可用地址"。
        Assert.IsNull(result.Profile.SkinUrl);
        Assert.AreEqual("SLIM", result.Profile.SkinVariant);
        Assert.IsFalse(result.Profile.HasCape);
    }

    private static MinecraftAuthChain NewChain(StubHandler handler) =>
        new(new StubHttpClientFactory(handler), HttpHostPolicy.Default, new FixedClock(Now));

    private static string XblToken(string token, string userHash) =>
        "{\"IssueInstant\":\"2026-10-04T12:00:00Z\",\"NotAfter\":\"2026-10-04T18:00:00Z\",\"Token\":\""
        + token + "\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"" + userHash + "\"}]}}";

    private static string Xsts(string token, string userHash) =>
        "{\"IssueInstant\":\"2026-10-04T12:00:00Z\",\"NotAfter\":\"2026-10-04T18:00:00Z\",\"Token\":\""
        + token + "\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"" + userHash + "\"}]}}";

    private static string MinecraftToken(string token, int expiresIn) =>
        "{\"username\":\"069a79f444e94726a5befca90e38aaf5\",\"roles\":[],\"access_token\":\""
        + token + "\",\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn.ToString(CultureInfo.InvariantCulture) + "}";

    private static string Profile(string id, string name, string skinUrl, string variant, int capes) =>
        "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"skins\":[{\"id\":\"s1\",\"state\":\"ACTIVE\",\"url\":\""
        + skinUrl + "\",\"variant\":\"" + variant + "\"}],\"capes\":"
        + (capes > 0 ? "[{\"id\":\"c1\",\"state\":\"ACTIVE\",\"url\":\"http://textures.minecraft.net/texture/cape\"}]" : "[]")
        + "}";
}
