using System.Net;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// §12.7 的清单获取：索引（钉住的地址）+ 组件清单（**必须核对 sha1 与字节数**）。
/// <para>
/// 所有请求都断言到具体 URL：审计 §4.4 的教训是"请求发到了哪"从不被断言，
/// 于是"地址拼错"这类缺陷没有任何回归保护。
/// </para>
/// </summary>
[TestClass]
public sealed class JavaRuntimeManifestClientTests
{
    [TestMethod]
    public async Task IndexIsFetchedFromThePinnedUrl()
    {
        var release = JavaRuntimeFixture.Release(
            "java-runtime-gamma",
            "17.0.15",
            JavaRuntimeFixture.File("bin/javaw.exe", "javaw"));

        var handler = new StubHandler().RespondTo(
            "/v1/products/java-runtime/",
            HttpStatusCode.OK,
            JavaRuntimeFixture.IndexJson([release]));

        var client = NewClient(handler);
        var index = await client.FetchIndexAsync();

        HttpAssert.RequestCount(handler, 1);
        HttpAssert.Url(handler, 0, JavaRuntimeManifestClient.IndexUrl);
        Assert.AreEqual("java-runtime-gamma", index.Candidates.Single().Component);
    }

    [TestMethod]
    public async Task AnIndexThatIsNotFoundSaysSoWithTheUrl()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.NotFound);
        var client = NewClient(handler);

        var exception = await Assert.ThrowsExceptionAsync<JavaRuntimeDataException>(() => client.FetchIndexAsync());

        // 这个地址是钉住的常量（官方没有"索引版本发现"端点）：失败时必须把地址写出来，
        // 否则排障的人根本不知道是哪个端点变了。
        StringAssert.Contains(exception.Message, JavaRuntimeManifestClient.IndexUrl);
        StringAssert.Contains(exception.Message, "404");
    }

    [TestMethod]
    public async Task AnIndexThatIsNotJsonIsRejected()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "<html>rate limited</html>");
        var client = NewClient(handler);

        await Assert.ThrowsExceptionAsync<JavaRuntimeDataException>(() => client.FetchIndexAsync());
    }

    [TestMethod]
    public async Task TheComponentManifestIsVerifiedAgainstItsDeclaredSha1AndSize()
    {
        var world = new JavaRuntimeWorld();
        var release = world.Publish(
            "java-runtime-gamma",
            "17.0.15",
            [JavaRuntimeFixture.Directory("bin"), JavaRuntimeFixture.File("bin/javaw.exe", "javaw")]);
        world.PublishIndex();

        var client = NewClient(world.Handler);
        var index = await client.FetchIndexAsync();
        var candidate = index.Candidates.Single();

        var manifest = await client.FetchManifestAsync(candidate);

        HttpAssert.Url(world.Handler, 1, release.ManifestUrl);
        Assert.AreEqual(2, manifest.Entries.Count);
    }

    [TestMethod]
    public async Task AManifestWhoseContentDoesNotMatchItsSha1IsRejected()
    {
        // 索引声明的 sha1 是"真清单"的，但响应体是另一份内容：这正是内容寻址要挡住的事。
        var release = JavaRuntimeFixture.Release(
            "java-runtime-gamma",
            "17.0.15",
            JavaRuntimeFixture.File("bin/javaw.exe", "javaw"));

        var tampered = new StubHandler().RespondTo(
            release.ManifestUrl,
            HttpStatusCode.OK,
            JavaRuntimeFixture.ManifestJson([JavaRuntimeFixture.File("bin/javaw.exe", "MALICIOUS")]));

        var candidate = new JavaRuntimeCandidate(
            JavaRuntimeFixture.Platform,
            "java-runtime-gamma",
            "17.0.15",
            null,
            release.ManifestSha1,
            release.ManifestSize,
            release.ManifestUrl);

        var exception = await Assert.ThrowsExceptionAsync<JavaRuntimeDataException>(
            () => NewClient(tampered).FetchManifestAsync(candidate));

        StringAssert.Contains(exception.Message, "校验不通过");
    }

    [TestMethod]
    public async Task AManifestWithTheWrongByteCountIsRejected()
    {
        var release = JavaRuntimeFixture.Release(
            "java-runtime-gamma",
            "17.0.15",
            JavaRuntimeFixture.File("bin/javaw.exe", "javaw"));

        var handler = new StubHandler().RespondTo(release.ManifestUrl, HttpStatusCode.OK, release.ManifestJson);

        var candidate = new JavaRuntimeCandidate(
            JavaRuntimeFixture.Platform,
            "java-runtime-gamma",
            "17.0.15",
            null,
            release.ManifestSha1,
            release.ManifestSize + 100,
            release.ManifestUrl);

        var exception = await Assert.ThrowsExceptionAsync<JavaRuntimeDataException>(
            () => NewClient(handler).FetchManifestAsync(candidate));

        StringAssert.Contains(exception.Message, "不完整");
    }

    [TestMethod]
    public async Task AManifestUrlOutsideTheAllowListIsRefusedBeforeAnyRequestIsSent()
    {
        // 清单里的地址来自远端：白名单是"远端决定我们访问哪里"这件事的唯一闸门。
        var handler = new StubHandler();
        var candidate = new JavaRuntimeCandidate(
            JavaRuntimeFixture.Platform,
            "java-runtime-gamma",
            "17.0.15",
            null,
            "sha1",
            1,
            "https://evil.example/v1/packages/sha1/manifest.json");

        var exception = await Assert.ThrowsExceptionAsync<JavaRuntimeDataException>(
            () => NewClient(handler).FetchManifestAsync(candidate));

        StringAssert.Contains(exception.Message, "不被允许出站");
        HttpAssert.RequestCount(handler, 0);
    }

    [TestMethod]
    public async Task ANonSuccessStatusOnTheComponentManifestIsReported()
    {
        var handler = new StubHandler().RespondTo(
            "/v1/packages/",
            HttpStatusCode.InternalServerError);

        var candidate = new JavaRuntimeCandidate(
            JavaRuntimeFixture.Platform,
            "java-runtime-gamma",
            "17.0.15",
            null,
            "sha1",
            1,
            "https://piston-meta.mojang.com/v1/packages/sha1/manifest.json");

        var exception = await Assert.ThrowsExceptionAsync<JavaRuntimeDataException>(
            () => NewClient(handler).FetchManifestAsync(candidate));

        StringAssert.Contains(exception.Message, "500");
    }

    /// <summary>该客户端用哪套桩（诊断日志用记录型替身，方便断言事件）。</summary>
    private static JavaRuntimeManifestClient NewClient(StubHandler handler) =>
        new(new FakeHttpClientFactory(handler), HttpHostPolicy.Default, new RecordingDiagnosticsLog());
}
