using System.Buffers.Binary;
using System.Net;
using System.Text;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Auth.Tests.Fakes;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// D-9：皮肤上传（官方接口 + 令牌 + 本地冷却）。
/// <para>
/// 验收的形状照规划 §12.9 Q6 的四条：**请求形状逐字段断言**（multipart 的字段名与文件媒体类型、
/// JSON 形态的 <c>variant</c>+<c>url</c>、<c>DELETE …/active</c> 的方法与地址）、
/// **状态码分派各说各的话**（401/403、404、400、415、429）、**429 与冷却绝不重试**、
/// 以及**成功之后用响应里的档案刷新四格**。
/// </para>
/// </summary>
[TestClass]
public sealed class MinecraftSkinServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(8));

    private const string TextureUrl = "https://textures.minecraft.net/texture/abcdef1234567890";

    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private MutableClock _clock = null!;
    private FakeMinecraftAuthChain _chain = null!;
    private FakeDeviceCodeClient _deviceClient = null!;
    private string _pngPath = null!;

    /// <summary>每个用例一个全新的数据目录与一张真 PNG。</summary>
    [TestInitialize]
    public void SetUp()
    {
        _paths = TestPaths.Create("skin");
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new MutableClock(Now);
        _chain = new FakeMinecraftAuthChain();
        _deviceClient = new FakeDeviceCodeClient();

        Directory.CreateDirectory(_paths.Root);
        _pngPath = Path.Combine(_paths.Root, "skin.png");
        File.WriteAllBytes(_pngPath, Png(64, 64));

        // 每个用例都从一个"已登录、令牌还有效、已有自定义皮肤"的正版账户开始。
        NewStore().Save([
            new StoredAccount(
                PlayerName: "Steve",
                Kind: AccountKind.Microsoft,
                Uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5",
                MicrosoftRefreshToken: "refresh-token",
                LastUsedAt: Now.AddDays(-1),
                IsDefault: true,
                Tokens: new StoredTokens("mc-access-token", Now.AddHours(20)),
                Profile: new StoredProfile(TextureUrl, "CLASSIC", HasCape: false, Now.AddDays(-1))),
        ]);
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    // ------------------------------------------------------------ 上传 ---

    [TestMethod]
    public async Task UploadSendsMultipartWithTheVariantAndAnImagePngFile()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.OK, Profile(TextureUrl, "SLIM"));

        var skin = await NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Slim);

        Assert.AreEqual(1, handler.Requests.Count);
        var request = handler.Requests[0];

        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(MicrosoftEndpoints.MinecraftSkinsUrl, request.Url);
        Assert.AreEqual("Bearer mc-access-token", request.Authorization, "用的是 Minecraft 访问令牌，不是 MSA 令牌。");
        Assert.AreEqual("multipart/form-data", request.ContentType);

        var body = request.Body.Replace("\"", string.Empty, StringComparison.Ordinal);
        StringAssert.Contains(body, "name=variant");
        StringAssert.Contains(body, "slim");
        StringAssert.Contains(body, "name=file; filename=skin.png");

        // 文件字段的媒体类型必须是 image/png：写成 application/png 的回应是 415，
        // 而那个状态码看起来像"服务器不支持"。
        StringAssert.Contains(body, "Content-Type: image/png");

        Assert.AreEqual("自定义皮肤 · {0}", skin.CurrentSkin.Key);
        CollectionAssert.AreEqual(new[] { "abcdef12" }, skin.CurrentSkin.Parameters.ToArray());
        Assert.AreEqual(SkinModel.Slim, skin.Model);
    }

    [TestMethod]
    public async Task UploadRefreshesTheFourFieldsFromTheResponseWithoutASecondRequest()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.OK, Profile(TextureUrl, "CLASSIC"));

        var skin = await NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic);

        Assert.AreEqual(1, handler.Requests.Count, "响应里就是改完之后的档案，不该再发一次 GET。");
        Assert.AreEqual("Steve", skin.PlayerName);
        Assert.IsFalse(skin.HasCape);
        Assert.AreEqual("自定义皮肤 · {0}", skin.CurrentSkin.Key);
        CollectionAssert.AreEqual(new[] { "abcdef12" }, skin.CurrentSkin.Parameters.ToArray());
        Assert.AreEqual("今天 {0}", skin.LastUpdated.Key, "「最近更新」跟着注入的时钟走。");
        CollectionAssert.AreEqual(new[] { "09:00" }, skin.LastUpdated.Parameters.ToArray());

        // 也落盘了：换一个目录对象去读，读到的还是新的。
        Assert.AreEqual("自定义皮肤 · {0}", NewCatalog().GetSkin("Steve").CurrentSkin.Key);
        CollectionAssert.AreEqual(new[] { "abcdef12" }, NewCatalog().GetSkin("Steve").CurrentSkin.Parameters.ToArray());
    }

    [TestMethod]
    public async Task UploadKeepsTheTokensUntouched()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.OK, Profile(TextureUrl, "CLASSIC"));

        await NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic);

        var stored = NewStore().Load().Single();

        Assert.AreEqual("refresh-token", stored.MicrosoftRefreshToken, "换一张皮肤不该动刷新令牌。");
        Assert.AreEqual("mc-access-token", stored.Tokens?.AccessToken);
        Assert.AreEqual(Now.AddDays(-1), stored.LastUsedAt, "也不该把「上次使用」刷成今天。");
    }

    [TestMethod]
    public async Task UploadRejectsANonPngBeforeSendingAnything()
    {
        var handler = new StubHandler();
        File.WriteAllText(_pngPath, "这不是图片");

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.InvalidImage, exception.Failure);
        StringAssert.Contains(exception.Message, "PNG");
        Assert.AreEqual(0, handler.Requests.Count, "不合规的图片在本地就拦住，不发那一次注定 400 的请求。");
    }

    [TestMethod]
    public async Task UploadRejectsTheWrongDimensionsLocally()
    {
        var handler = new StubHandler();
        File.WriteAllBytes(_pngPath, Png(128, 128));

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.InvalidImage, exception.Failure);
        Assert.AreEqual("皮肤必须是 64×64 或 64×32 像素，这张是 {0}×{1}。", exception.Reason.Key);
        CollectionAssert.AreEqual(new[] { "128", "128" }, exception.Reason.Parameters.ToArray());
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task UploadRejectsAFileThatCannotBeRead()
    {
        var handler = new StubHandler();
        File.Delete(_pngPath);

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(
            SkinChangeFailure.ImageUnreadable,
            exception.Failure,
            "「读不出来」与「图片不合规」必须分开说：前者用户要去检查文件，后者要去换图片。");
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AnOfflineAccountCannotUploadASkin()
    {
        var handler = new StubHandler();
        NewStore().Save([new StoredAccount("Alex", AccountKind.Offline, "离线", null, null, IsDefault: true)]);

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Alex", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.OfflineAccount, exception.Failure);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AnUnknownAccountIsNotReportedAsANetworkProblem()
    {
        var handler = new StubHandler();

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Nobody", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.NoSuchAccount, exception.Failure);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    // ------------------------------------------------------------ 状态码 ---

    [TestMethod]
    public async Task UnauthorizedRefreshesTheTokenOnceAndRetriesOnce()
    {
        var calls = 0;
        var handler = new StubHandler().RespondWith(SkinsFragment, _ =>
        {
            calls++;

            return calls == 1
                ? Json(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}")
                : Json(HttpStatusCode.OK, Profile(TextureUrl, "CLASSIC"));
        });

        _chain.AccessToken = "refreshed-mc-token";

        // 刷新链给回来的令牌要在**未来**才到期，否则取令牌那一步会再刷一次
        // （那是另一条规则：FakeDeviceCodeClient 的默认到期时刻是固定写死的）。
        _deviceClient.Tokens = new MicrosoftTokens(
            "ms-access-token",
            "rotated-refresh-token",
            Now.AddHours(1));

        var skin = await NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic);

        Assert.AreEqual(2, calls, "401 之后应该刷新令牌并重试一次。");
        Assert.AreEqual(1, _chain.Calls, "只刷新一次。");
        Assert.AreEqual("Bearer refreshed-mc-token", handler.Requests[1].Authorization, "重试用的是刷新后的令牌。");
        Assert.AreEqual("自定义皮肤 · {0}", skin.CurrentSkin.Key);
        CollectionAssert.AreEqual(new[] { "abcdef12" }, skin.CurrentSkin.Parameters.ToArray());
    }

    [TestMethod]
    public async Task ForbiddenAfterTheRefreshMeansSignInAgainAndIsNotRetriedForever()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.Forbidden, "{\"error\":\"forbidden\"}");

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.NotSignedIn, exception.Failure);
        StringAssert.Contains(exception.Message, "重新登录");
        Assert.AreEqual(2, handler.Requests.Count, "只重试一次，不循环。");
    }

    [TestMethod]
    public async Task NotFoundMeansTheAccountHasNoJavaProfile()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.NotFound, "{\"errorType\":\"NOT_FOUND\"}");

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.NoJavaProfile, exception.Failure);
        StringAssert.Contains(exception.Message, "Java 版");
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task BadRequestIsNeverWordedAsASignInProblem()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.BadRequest, "{\"errorType\":\"BAD_REQUEST\"}");

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.ImageRejected, exception.Failure);
        Assert.AreNotEqual(SkinChangeFailure.NotSignedIn, exception.Failure, "400 与 401 是两句不同的话。");
        StringAssert.Contains(exception.Message, "400");
    }

    [TestMethod]
    public async Task UnsupportedMediaIsReportedAsOurOwnBug()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.UnsupportedMediaType, "{}");

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.UnsupportedMedia, exception.Failure);
        StringAssert.Contains(exception.Message, "启动器的缺陷");
    }

    [TestMethod]
    public async Task TooManyRequestsIsACooldownAndIsNeverRetried()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.TooManyRequests, "{}");

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.Cooldown, exception.Failure);
        Assert.AreEqual(1, handler.Requests.Count, "429 绝不能循环重试：连续失败会导致账户被临时封禁。");
        StringAssert.Contains(exception.Message, "冷却");
    }

    [TestMethod]
    public async Task AnUnexpectedStatusIsAServerProblem()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.InternalServerError, "{}");

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.ServerError, exception.Failure);
        Assert.AreEqual("官方服务器返回了 {0} {1}，请稍后再试。", exception.Reason.Key);
        Assert.AreEqual("500", exception.Reason.Parameters[0]);
    }

    [TestMethod]
    public async Task ADeadNetworkIsNotWordedAsASignInProblem()
    {
        var handler = new StubHandler().RespondWith(SkinsFragment, _ => throw new HttpRequestException("连接被拒绝"));

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.Network, exception.Failure);
    }

    [TestMethod]
    public async Task ATimeoutIsANetworkProblemNotASilentCancellation()
    {
        // 用户没有取消，但请求被挂住并超时：这必须有一句话（否则界面上什么都不会发生）。
        var handler = new StubHandler().RespondWith(SkinsFragment, _ => throw new TaskCanceledException("超时"));

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.Network, exception.Failure);
        StringAssert.Contains(exception.Message, "超时");
    }

    // ------------------------------------------------------------ 冷却 ---

    [TestMethod]
    public async Task ASuccessfulUploadBlocksTheNextOneForSixtySeconds()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.OK, Profile(TextureUrl, "CLASSIC"));
        var service = NewService(handler);

        await service.UploadAsync("Steve", _pngPath, SkinModel.Classic);

        Assert.AreEqual(MinecraftSkinService.UploadCooldown, service.CooldownRemaining("Steve"));

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => service.UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.AreEqual(SkinChangeFailure.Cooldown, exception.Failure);
        Assert.AreEqual(1, handler.Requests.Count, "冷却窗口内的第二次上传连请求都不发。");
    }

    [TestMethod]
    public async Task TheCooldownExpiresWithTheClock()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.OK, Profile(TextureUrl, "CLASSIC"));
        var service = NewService(handler);

        await service.UploadAsync("Steve", _pngPath, SkinModel.Classic);

        _clock.Now = Now.AddSeconds(59);
        Assert.IsTrue(service.CooldownRemaining("Steve") > TimeSpan.Zero, "59 秒时还在冷却里。");

        _clock.Now = Now.AddSeconds(61);
        Assert.AreEqual(TimeSpan.Zero, service.CooldownRemaining("Steve"));

        await service.UploadAsync("Steve", _pngPath, SkinModel.Classic);

        Assert.AreEqual(2, handler.Requests.Count, "冷却过去之后可以再传。");
    }

    [TestMethod]
    public void CooldownIsPerAccountAndAsksNothingWhenThereIsNoAccount()
    {
        var service = NewService(new StubHandler());

        Assert.AreEqual(TimeSpan.Zero, service.CooldownRemaining("Nobody"));
        Assert.AreEqual(TimeSpan.Zero, service.CooldownRemaining(string.Empty));
    }

    // ------------------------------------------------------------ 换模型 ---

    [TestMethod]
    public async Task ChangingTheModelRepostsTheExistingTextureAsJson()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.OK, Profile(TextureUrl, "SLIM"));

        var skin = await NewService(handler).SetModelAsync("Steve", SkinModel.Slim);

        Assert.AreEqual(1, handler.Requests.Count);
        var request = handler.Requests[0];

        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(MicrosoftEndpoints.MinecraftSkinsUrl, request.Url);
        Assert.AreEqual("application/json", request.ContentType);
        StringAssert.Contains(request.Body, "\"variant\":\"slim\"");
        StringAssert.Contains(request.Body, "\"url\":\"" + TextureUrl + "\"");
        Assert.AreEqual(SkinModel.Slim, skin.Model);
    }

    [TestMethod]
    public async Task ChangingTheModelNeedsACustomSkinToRepost()
    {
        var handler = new StubHandler();

        // 这个账户当前是官方默认皮肤（档案里没有纹理地址）。
        NewStore().Save([
            new StoredAccount(
                "Steve",
                AccountKind.Microsoft,
                "069a79f4-44e9-4726-a5be-fca90e38aaf5",
                "refresh-token",
                Now.AddDays(-1),
                IsDefault: true,
                Tokens: new StoredTokens("mc-access-token", Now.AddHours(20))),
        ]);

        var exception = await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => NewService(handler).SetModelAsync("Steve", SkinModel.Slim));

        Assert.AreEqual(SkinChangeFailure.NoCustomSkin, exception.Failure);
        StringAssert.Contains(exception.Message, "默认皮肤");
        Assert.AreEqual(0, handler.Requests.Count, "没有纹理地址就没得换，不必发一个注定失败的请求。");
    }

    // ------------------------------------------------------------ 恢复默认 ---

    [TestMethod]
    public async Task ResetDeletesTheActiveSkinAndRereadsTheProfile()
    {
        var handler = new StubHandler()
            // 先登记"最不具体"的那条：桩按地址片段匹配，**后登记的优先**，
            // 而 "/skins/active" 的地址里也包含 "/minecraft/profile"。
            .RespondTo("api.minecraftservices.com/minecraft/profile", HttpStatusCode.OK, Profile(skinUrl: null, variant: "CLASSIC"))
            .RespondTo(MicrosoftEndpoints.MinecraftActiveSkinUrl, HttpStatusCode.NoContent);

        var skin = await NewService(handler).ResetAsync("Steve");

        Assert.AreEqual(2, handler.Requests.Count, "DELETE 回 204 没有正文，所以要重新取一次档案。");
        Assert.AreEqual(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.AreEqual("https://api.minecraftservices.com/minecraft/profile/skins/active", handler.Requests[0].Url);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.AreEqual("默认皮肤", skin.CurrentSkin.Key);
    }

    [TestMethod]
    public async Task ResetUsesTheProfileInTheDeleteResponseWhenThereIsOne()
    {
        var handler = new StubHandler().RespondTo(
            MicrosoftEndpoints.MinecraftActiveSkinUrl,
            HttpStatusCode.OK,
            Profile(skinUrl: null, variant: "CLASSIC"));

        var skin = await NewService(handler).ResetAsync("Steve");

        Assert.AreEqual(1, handler.Requests.Count, "DELETE 直接给了档案，就不必再 GET 一次。");
        Assert.AreEqual("默认皮肤", skin.CurrentSkin.Key);
    }

    // ------------------------------------------------------------ 事件 ---

    [TestMethod]
    public async Task BothOutcomesLeaveADiagnosticEvent()
    {
        var handler = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.OK, Profile(TextureUrl, "CLASSIC"));
        var service = NewService(handler);

        await service.UploadAsync("Steve", _pngPath, SkinModel.Classic);

        Assert.IsTrue(
            _diagnostics.Entries.Any(entry => entry.MessageKey == "已上传皮肤（{0}，{1}）"),
            "成功要留痕（后面 M6 的事件表读的就是它）。");

        var failing = new StubHandler().RespondTo(SkinsFragment, HttpStatusCode.Forbidden, "{}");
        var failingService = NewService(failing);

        await Assert.ThrowsExceptionAsync<SkinChangeException>(
            () => failingService.UploadAsync("Steve", _pngPath, SkinModel.Classic));

        Assert.IsTrue(
            _diagnostics.Entries.Any(entry => entry.MessageKey == "皮肤操作未成功（{0}）：{1}"),
            "失败也要留痕，并且带上失败原因。");
    }

    // ------------------------------------------------------------ 基础设施 ---

    private const string SkinsFragment = "api.minecraftservices.com/minecraft/profile/skins";

    private AccountStore NewStore() => new(_paths, _diagnostics);

    private FileAccountCatalog NewCatalog() => new(NewStore(), _clock, _diagnostics, _chain, _deviceClient);

    private MinecraftSkinService NewService(StubHandler handler) => new(
        new StubHttpClientFactory(handler),
        HttpHostPolicy.Default,
        NewCatalog(),
        _clock,
        _diagnostics);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Profile(string? skinUrl, string variant) =>
        "{\"id\":\"069a79f444e94726a5befca90e38aaf5\",\"name\":\"Steve\",\"skins\":["
        + (skinUrl is null
            ? string.Empty
            : "{\"id\":\"s1\",\"state\":\"ACTIVE\",\"url\":\"" + skinUrl + "\",\"variant\":\"" + variant + "\"}")
        + "],\"capes\":[]}";

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[64];

        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);

        return bytes;
    }
}
