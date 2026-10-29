using System.Net;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Tests.Fakes;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-f：加载器 profile 客户端（地址、形状核对、失败时的说法）。
/// <para>
/// 审计 §4.4 的教训：HTTP 测试必须**逐字符断言请求地址** —— "发错了地址"这类缺陷
/// 在只断言"成功拿到了东西"的测试里是看不见的。
/// </para>
/// </summary>
[TestClass]
public sealed class LoaderProfileClientTests
{
    [TestMethod]
    public void TheProfileUrlsAreTheOnesTheTwoServicesDocument()
    {
        Assert.AreEqual(
            LoaderFixture.FabricProfileUrl,
            LoaderProfileClient.UrlFor(LoaderKind.Fabric, "1.20.4", "0.15.7"));

        Assert.AreEqual(
            LoaderFixture.QuiltProfileUrl,
            LoaderProfileClient.UrlFor(LoaderKind.Quilt, "1.20.4", "0.23.1"));
    }

    [TestMethod]
    public void ForgeIsRejectedWithItsMilestoneInsteadOfBeingSentToAnEndpointThatDoesNotExist()
    {
        var exception = Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LoaderProfileClient.UrlFor(LoaderKind.Forge, "1.20.4", "49.0.30"));

        StringAssert.Contains(exception.Message, "M4-g");
    }

    [TestMethod]
    public void AVersionStringThatWouldChangeTheUrlStructureIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(
            () => LoaderProfileClient.UrlFor(LoaderKind.Fabric, "1.20.4/../x", "0.15.7"));

        Assert.ThrowsException<ArgumentException>(
            () => LoaderProfileClient.UrlFor(LoaderKind.Fabric, "1.20.4", "0.15.7?x=1"));
    }

    [TestMethod]
    public async Task AFabricProfileComesBackWithItsDeclaredIdAndParent()
    {
        var stub = LoaderFixture.Stub();
        var profile = await NewClient(stub).FetchAsync(LoaderKind.Fabric, "1.20.4", "0.15.7");

        HttpAssert.Url(stub, 0, LoaderFixture.FabricProfileUrl);
        Assert.AreEqual(LoaderFixture.FabricId, profile.Id);
        Assert.AreEqual("1.20.4", profile.GameVersion);
        Assert.AreEqual(LoaderFixture.FabricId, profile.Version.Id, "Id 要归一化进版本 JSON。");
        Assert.AreEqual("net.fabricmc.loader.impl.launch.knot.KnotClient", profile.Version.MainClass);
        Assert.AreEqual(1, profile.Version.Libraries!.Count);
    }

    [TestMethod]
    public async Task AProfileWithoutAnIdGetsTheLocalOne()
    {
        // 两家目前都会给 id；万一不给，就按本地命名规则补一个，而不是留一个空 id 让安装失败在别处。
        var stub = new StubHandler().RespondTo(
            "meta.fabricmc.net",
            HttpStatusCode.OK,
            LoaderFixture.ProfileJson(LoaderKind.Fabric, id: string.Empty));

        var profile = await NewClient(stub).FetchAsync(LoaderKind.Fabric, "1.20.4", "0.15.7");

        Assert.AreEqual(LoaderFixture.FabricId, profile.Id);
    }

    [TestMethod]
    public async Task AnErrorStatusIsReportedWithTheUrlAndRecordedAsAnEvent()
    {
        var stub = new StubHandler().RespondTo("meta.fabricmc.net", HttpStatusCode.NotFound);
        var diagnostics = new RecordingDiagnosticsLog();

        var exception = await Assert.ThrowsExceptionAsync<VersionDataException>(
            () => NewClient(stub, diagnostics).FetchAsync(LoaderKind.Fabric, "1.20.4", "0.15.7"));

        StringAssert.Contains(exception.Message, LoaderFixture.FabricProfileUrl);
        StringAssert.Contains(exception.Message, "404");
        Assert.AreEqual("获取加载器 profile 失败：{0} 返回了 {1}", diagnostics.Entries[^1].MessageKey);
    }

    [TestMethod]
    public async Task AResponseThatIsNotJsonIsRejected()
    {
        var stub = new StubHandler().RespondTo("meta.fabricmc.net", HttpStatusCode.OK, "<html>nope</html>");

        var exception = await Assert.ThrowsExceptionAsync<VersionDataException>(
            () => NewClient(stub).FetchAsync(LoaderKind.Fabric, "1.20.4", "0.15.7"));

        StringAssert.Contains(exception.Message, "不是合法 JSON");
    }

    [TestMethod]
    public async Task AProfileThatInheritsSomethingElseIsRejected()
    {
        var stub = LoaderFixture.Stub(inheritsFrom: "1.21");

        var exception = await Assert.ThrowsExceptionAsync<VersionDataException>(
            () => NewClient(stub).FetchAsync(LoaderKind.Fabric, "1.20.4", "0.15.7"));

        StringAssert.Contains(exception.Message, "1.21");
        StringAssert.Contains(exception.Message, "1.20.4");
    }

    private static LoaderProfileClient NewClient(StubHandler stub, RecordingDiagnosticsLog? diagnostics = null) =>
        new(new FakeHttpClientFactory(stub), diagnostics ?? new RecordingDiagnosticsLog());
}
