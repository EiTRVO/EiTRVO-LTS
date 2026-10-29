using System.Net;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M0 的出站请求策略：域名白名单（F-8）与重试退避。
/// <para>
/// 断言全部落到**具体值**上：白名单判定、重试次数、每次等待的秒数、请求打到的 URL。
/// 这是审计 §4.4 那条要求（"HTTP 测试必须断言请求 URL 与查询参数"）的落地。
/// </para>
/// </summary>
[TestClass]
public sealed class HttpPolicyTests
{
    // ------------------------------------------------------- 域名与协议策略 ---

    [TestMethod]
    [DataRow("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")]
    [DataRow("https://launcher.mojang.com/v1/objects/abc/client.jar")]
    [DataRow("https://libraries.minecraft.net/com/example/lib.jar")]
    [DataRow("https://resources.download.minecraft.net/ab/abcdef")]
    [DataRow("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode")]
    [DataRow("https://user.auth.xboxlive.com/user/authenticate")]
    [DataRow("https://xsts.auth.xboxlive.com/xsts/authorize")]
    [DataRow("https://api.minecraftservices.com/minecraft/profile")]
    [DataRow("https://textures.minecraft.net/texture/abc")]
    [DataRow("https://meta.fabricmc.net/v2/versions/loader/1.20.1")]
    [DataRow("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml")]
    [DataRow("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml")]
    [DataRow("https://api.modrinth.com/v2/search?query=sodium")]
    [DataRow("https://cdn.modrinth.com/data/abc/versions/xyz.jar")]
    // M7-e：.mrpack 规范列出的下载源（+ GitHub 发布包会 302 到的资源主机）。
    [DataRow("https://github.com/owner/repo/releases/download/v1/pack.jar")]
    [DataRow("https://raw.githubusercontent.com/owner/repo/main/config/x.toml")]
    [DataRow("https://gitlab.com/owner/repo/-/archive/v1/pack.zip")]
    [DataRow("https://objects.githubusercontent.com/github-production-release-asset/x")]
    [DataRow("https://release-assets.githubusercontent.com/github-production-release-asset/x")]
    [DataRow("https://optifine.net/downloads")]
    public void DefaultPolicyAllowsEveryEndpointCoreNeeds(string url)
    {
        Assert.IsTrue(HttpHostPolicy.Default.IsAllowed(new Uri(url)), $"{url} 应当在白名单内。");
    }

    [TestMethod]
    [DataRow("http://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "非 https")]
    [DataRow("https://example.invalid/x", "未知主机")]
    [DataRow("https://piston-meta.mojang.com.evil.example/x", "形近域名")]
    [DataRow("https://evil.example/piston-meta.mojang.com", "主机在路径里")]
    [DataRow("https://launchermeta.mojang.com/mc/game/version_manifest.json", "已弃用的旧端点")]
    [DataRow("https://piston-meta.mojang.com:8443/x", "非默认端口")]
    [DataRow("https://notgithub.com/x", "GitHub 的形近域名（白名单是精确匹配，不做后缀通配）")]
    [DataRow("https://github.com.evil.example/x", "把白名单主机当前缀")]
    [DataRow("/relative/path", "相对地址")]
    public void DefaultPolicyRejectsEverythingElse(string url, string why)
    {
        var uri = url.StartsWith('/') ? new Uri(url, UriKind.Relative) : new Uri(url);

        Assert.IsFalse(HttpHostPolicy.Default.IsAllowed(uri), $"{url} 不应当被允许（{why}）。");
    }

    [TestMethod]
    public void PolicyRejectsTheUserInfoTrick()
    {
        // https://<白名单主机>@evil.example/ —— 看起来像白名单，实际连的是 evil.example。
        var uri = new Uri("https://piston-meta.mojang.com@evil.example/x");

        Assert.AreEqual("evil.example", uri.Host);
        Assert.IsFalse(HttpHostPolicy.Default.IsAllowed(uri));
    }

    [TestMethod]
    public void PolicyRejectsNullAndEmptyAllowList()
    {
        Assert.IsFalse(HttpHostPolicy.Default.IsAllowed(null));
        Assert.ThrowsException<ArgumentException>(() => new HttpHostPolicy([]));
    }

    [TestMethod]
    public void EnsureAllowedThrowsWithTheOffendingAddressInTheMessage()
    {
        var ex = Assert.ThrowsException<HttpPolicyException>(
            () => HttpHostPolicy.Default.EnsureAllowed(new Uri("https://evil.example/payload")));

        StringAssert.Contains(ex.Message, "evil.example");
    }

    [TestMethod]
    public void AllowedHostHandlerBlocksBeforeTheRequestLeaves()
    {
        var stub = new StubHandler().Respond(HttpStatusCode.OK);
        var handler = new AllowedHostHandler(HttpHostPolicy.Default) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        Assert.ThrowsExceptionAsync<HttpPolicyException>(
            () => client.GetAsync(new Uri("https://evil.example/payload"))).GetAwaiter().GetResult();

        // 关键：被拒的请求不能落到内层处理器上（0 次），否则"拦住了"只是嘴上说说。
        HttpAssert.RequestCount(stub, 0);
    }

    [TestMethod]
    public void AllowedHostHandlerForwardsAllowedRequestsUnchanged()
    {
        var stub = new StubHandler().Respond(HttpStatusCode.OK, "{}");
        var handler = new AllowedHostHandler(HttpHostPolicy.Default) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        var response = client
            .GetAsync(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json?x=1"))
            .GetAwaiter().GetResult();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        HttpAssert.RequestCount(stub, 1);
        HttpAssert.Url(stub, 0, "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json?x=1");
        HttpAssert.Query(stub, 0, "x", "1");
    }

    // ------------------------------------------------------------- 重定向 ---

    [TestMethod]
    public void AllowedHostHandlerFollowsAnAllowedRedirect()
    {
        var stub = new StubHandler()
            .RedirectTo("https://piston-meta.mojang.com/mc/real.json")
            .Respond(HttpStatusCode.OK, "{}");

        using var client = NewClient(stub);

        var response = client
            .GetAsync(new Uri("https://piston-data.mojang.com/mc/game/version_manifest_v2.json"))
            .GetAwaiter().GetResult();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        HttpAssert.RequestCount(stub, 2);
        HttpAssert.Url(stub, 1, "https://piston-meta.mojang.com/mc/real.json");
    }

    [TestMethod]
    public void AllowedHostHandlerResolvesARelativeRedirect()
    {
        var stub = new StubHandler().RedirectTo("/mc/real.json").Respond(HttpStatusCode.OK, "{}");

        using var client = NewClient(stub);
        client.GetAsync(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"))
            .GetAwaiter().GetResult();

        HttpAssert.Url(stub, 1, "https://piston-meta.mojang.com/mc/real.json");
    }

    [TestMethod]
    public void AllowedHostHandlerBlocksARedirectToADisallowedHost()
    {
        // 这就是修这个缺陷的原因：自动重定向打开时，这张 302 会被**底层处理器**跟走，
        // AllowedHostHandler 一次都不会被调用，白名单形同虚设。
        var stub = new StubHandler().RedirectTo("https://evil.example/steal");
        using var client = NewClient(stub);

        var ex = Assert.ThrowsExceptionAsync<HttpPolicyException>(
            () => client.GetAsync(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")))
            .GetAwaiter().GetResult();

        StringAssert.Contains(ex.Message, "evil.example");

        // 关键：被拒的那一跳不能落到内层处理器上 —— 否则"拦住了"只是嘴上说说。
        HttpAssert.RequestCount(stub, 1);
    }

    [TestMethod]
    public void AllowedHostHandlerStopsAfterTheRedirectLimit()
    {
        var stub = new StubHandler();
        for (var i = 0; i < 8; i++)
        {
            stub.RedirectTo("https://piston-meta.mojang.com/mc/loop");
        }

        using var client = NewClient(stub);

        var ex = Assert.ThrowsExceptionAsync<HttpPolicyException>(
            () => client.GetAsync(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")))
            .GetAwaiter().GetResult();

        StringAssert.Contains(ex.Message, "5 跳");

        // 5 跳上限意味着第 6 个响应（hops 0…5）之后就该停手，不能无限跟下去。
        HttpAssert.RequestCount(stub, 6);
    }

    [TestMethod]
    public void AllowedHostHandlerRefusesARedirectThatWouldResendABody()
    {
        var stub = new StubHandler().RedirectTo("https://api.minecraftservices.com/elsewhere", HttpStatusCode.TemporaryRedirect);
        using var client = NewClient(stub);

        var ex = Assert.ThrowsExceptionAsync<HttpPolicyException>(
            () => client.PostAsync(
                new Uri("https://api.minecraftservices.com/minecraft/profile/skins"),
                new StringContent("x")))
            .GetAwaiter().GetResult();

        StringAssert.Contains(ex.Message, "POST");
        HttpAssert.RequestCount(stub, 1);
    }

    [TestMethod]
    public void AllowedHostHandlerKeepsIdentityHeadersOnlyOnSameOriginRedirects()
    {
        var stub = new StubHandler()
            .RedirectTo("https://api.minecraftservices.com/minecraft/profile")
            .Respond(HttpStatusCode.OK, "{}");

        using var client = NewClient(stub);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("https://api.minecraftservices.com/minecraft/profile"));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");

        client.SendAsync(request).GetAwaiter().GetResult();

        HttpAssert.RequestCount(stub, 2);
        Assert.IsNotNull(
            stub.Requests[1].Headers.Authorization,
            "同源重定向应当保留身份头（否则重定向后必然 401）。");
    }

    [TestMethod]
    public void AllowedHostHandlerDropsIdentityHeadersOnCrossHostRedirects()
    {
        var stub = new StubHandler()
            .RedirectTo("https://sessionserver.mojang.com/session/minecraft/profile/abc")
            .Respond(HttpStatusCode.OK, "{}");

        using var client = NewClient(stub);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("https://api.minecraftservices.com/minecraft/profile"));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");

        client.SendAsync(request).GetAwaiter().GetResult();

        HttpAssert.RequestCount(stub, 2);
        Assert.IsNull(
            stub.Requests[1].Headers.Authorization,
            "跨主机重定向绝不能把访问令牌带过去 —— 那正是白名单想防的事。");
    }

    private static HttpClient NewClient(StubHandler stub)
    {
        // 桩不会自己跟重定向，所以这些用例测的正是本类的跟随逻辑。
        // 真实管道里"底层不许自动跟"这条由 HttpPrimaryHandler.Create() 保证（见它的用例）。
        return new HttpClient(new AllowedHostHandler(HttpHostPolicy.Default) { InnerHandler = stub });
    }

    [TestMethod]
    public void ThePrimaryHandlerNeverFollowsRedirectsItself()
    {
        // 这条是上面那组用例成立的前提：自动重定向一旦打开，底层处理器会**在 DelegatingHandler 之后**
        // 把请求带走，AllowedHostHandler 只看得到第一跳。组合根用的是同一个工厂，所以这条断言钉住的是真实管道。
        using var primary = HttpPrimaryHandler.Create();

        Assert.IsFalse(primary.AllowAutoRedirect);
    }

    // ----------------------------------------------------------- 重试策略 ---

    [TestMethod]
    [DataRow(1, 1.0)]
    [DataRow(2, 2.0)]
    [DataRow(3, 4.0)]
    [DataRow(4, 8.0)]
    [DataRow(5, 8.0)]
    public void DelayDoublesUntilTheCap(int failedAttempt, double expectedSeconds)
    {
        Assert.AreEqual(
            TimeSpan.FromSeconds(expectedSeconds),
            RetryPolicy.Default.GetDelay(failedAttempt));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.RequestTimeout, true)]
    [DataRow(HttpStatusCode.TooManyRequests, true)]
    [DataRow(HttpStatusCode.InternalServerError, true)]
    [DataRow(HttpStatusCode.BadGateway, true)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.GatewayTimeout, true)]
    [DataRow(HttpStatusCode.OK, false)]
    [DataRow(HttpStatusCode.MovedPermanently, false)]
    [DataRow(HttpStatusCode.BadRequest, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public void OnlyTransientStatusesAreRetryable(HttpStatusCode statusCode, bool expected)
    {
        Assert.AreEqual(expected, RetryPolicy.IsRetryableStatus(statusCode));
    }

    [TestMethod]
    public void TransientExceptionsAreRetryableButPolicyRejectionsAreNot()
    {
        Assert.IsTrue(RetryPolicy.IsRetryableException(new HttpRequestException("reset")));
        Assert.IsTrue(RetryPolicy.IsRetryableException(new IOException("disk")));
        Assert.IsTrue(RetryPolicy.IsRetryableException(new TaskCanceledException("timeout")));

        // 策略拒绝重试一百次也不会放行；程序错误更不该重试。
        Assert.IsFalse(RetryPolicy.IsRetryableException(new HttpPolicyException("blocked")));
        Assert.IsFalse(RetryPolicy.IsRetryableException(new InvalidOperationException("bug")));
    }

    [TestMethod]
    public void PolicyValidatesItsArguments()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RetryPolicy(maxAttempts: 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RetryPolicy(baseDelay: TimeSpan.Zero));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new RetryPolicy(baseDelay: TimeSpan.FromSeconds(4), maxDelay: TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public void OnlyIdempotentMethodsAreRetriedByDefault()
    {
        Assert.IsTrue(RetryPolicy.Default.IsRetryableMethod(HttpMethod.Get));
        Assert.IsTrue(RetryPolicy.Default.IsRetryableMethod(HttpMethod.Head));
        Assert.IsFalse(RetryPolicy.Default.IsRetryableMethod(HttpMethod.Post));
        Assert.IsFalse(RetryPolicy.Default.IsRetryableMethod(HttpMethod.Delete));

        var aggressive = new RetryPolicy(idempotentOnly: false);
        Assert.IsTrue(aggressive.IsRetryableMethod(HttpMethod.Post));
    }

    // ------------------------------------------------------- 重试处理器 ---

    [TestMethod]
    public void RetriesTransientStatusThenSucceeds()
    {
        var stub = new StubHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK, "ok");
        var delays = new DelayRecorder();

        var response = SendWithRetry(stub, delays, HttpMethod.Get, "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        HttpAssert.RequestCount(stub, 2);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, delays.Delays);
    }

    [TestMethod]
    public void RetriesWithTheDoublingScheduleAcrossMultipleFailures()
    {
        var stub = new StubHandler()
            .Respond(HttpStatusCode.BadGateway)
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);
        var delays = new DelayRecorder();

        var response = SendWithRetry(stub, delays, HttpMethod.Get, "https://libraries.minecraft.net/x.jar");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        HttpAssert.RequestCount(stub, 3);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            delays.Delays);
    }

    [TestMethod]
    public void RetriesTransientExceptions()
    {
        var stub = new StubHandler()
            .Throw(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK);
        var delays = new DelayRecorder();

        var response = SendWithRetry(stub, delays, HttpMethod.Get, "https://piston-meta.mojang.com/x");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        HttpAssert.RequestCount(stub, 2);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, delays.Delays);
    }

    [TestMethod]
    public void GivesUpAfterMaxAttemptsAndReturnsTheLastResponse()
    {
        var stub = new StubHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable);
        var delays = new DelayRecorder();

        var response = SendWithRetry(stub, delays, HttpMethod.Get, "https://piston-meta.mojang.com/x");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        HttpAssert.RequestCount(stub, 3);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            delays.Delays);
    }

    [TestMethod]
    public void GivesUpAfterMaxAttemptsAndRethrowsTheLastException()
    {
        var stub = new StubHandler()
            .Throw(new HttpRequestException("a"))
            .Throw(new HttpRequestException("b"))
            .Throw(new HttpRequestException("c"));
        var delays = new DelayRecorder();
        var handler = new RetryingHandler(RetryPolicy.Default, NullLogger<RetryingHandler>.Instance, delays.DelayAsync) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        var ex = Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => client.GetAsync(new Uri("https://piston-meta.mojang.com/x"))).GetAwaiter().GetResult();

        Assert.AreEqual("c", ex.Message, "最终抛出的应当是最后一次的异常。");
        HttpAssert.RequestCount(stub, 3);
    }

    [TestMethod]
    public void DoesNotRetryNonRetryableStatuses()
    {
        var stub = new StubHandler().Respond(HttpStatusCode.NotFound);
        var delays = new DelayRecorder();

        var response = SendWithRetry(stub, delays, HttpMethod.Get, "https://piston-meta.mojang.com/missing");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        HttpAssert.RequestCount(stub, 1);
        Assert.AreEqual(0, delays.Delays.Count);
    }

    [TestMethod]
    public void DoesNotRetryNonIdempotentRequests()
    {
        var stub = new StubHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);
        var delays = new DelayRecorder();

        var response = SendWithRetry(stub, delays, HttpMethod.Post, "https://login.microsoftonline.com/consumers/oauth2/v2.0/token");

        // 默认只重试幂等方法：POST 原样返回（认证链自己用新请求重试）。
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        HttpAssert.RequestCount(stub, 1);
        Assert.AreEqual(0, delays.Delays.Count);
    }

    private static HttpResponseMessage SendWithRetry(
        StubHandler stub,
        DelayRecorder delays,
        HttpMethod method,
        string url)
    {
        var handler = new RetryingHandler(RetryPolicy.Default, NullLogger<RetryingHandler>.Instance, delays.DelayAsync) { InnerHandler = stub };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(method, new Uri(url));

        return client.SendAsync(request).GetAwaiter().GetResult();
    }

    /// <summary>记录等待时长而不真的等待。</summary>
    private sealed class DelayRecorder
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
