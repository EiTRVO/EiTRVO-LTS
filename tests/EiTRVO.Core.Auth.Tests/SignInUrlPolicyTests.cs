using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Launcher.Net;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// 登录地址白名单（D-3：设备代码钓鱼防护）。
/// <para>
/// 它的价值全在"拒绝"上：真地址与钓鱼地址在界面上长得一模一样，
/// 而"请到这里输入代码"这句话本身就是把用户送过去的邀请。
/// </para>
/// </summary>
[TestClass]
public sealed class SignInUrlPolicyTests
{
    [TestMethod]
    public void TheOfficialBareHostIsNormalizedToHttps()
    {
        // 官方返回的就是 "microsoft.com/link"（没有 scheme）—— 补 https，绝不按内容猜 http。
        var uri = SignInUrlPolicy.Normalize("microsoft.com/link");

        Assert.IsNotNull(uri);
        Assert.AreEqual("https://microsoft.com/link", uri.ToString());
        Assert.IsTrue(SignInUrlPolicy.IsAllowed(uri));
    }

    [DataTestMethod]
    [DataRow("https://microsoft.com/link")]
    [DataRow("https://www.microsoft.com/link")]
    [DataRow("https://login.microsoftonline.com/common/oauth2/deviceauth")]
    [DataRow("https://login.live.com/oauth20_remoteconnect.srf")]
    public void MicrosoftSignInHostsAreAllowed(string url) =>
        Assert.IsTrue(SignInUrlPolicy.IsAllowed(SignInUrlPolicy.Normalize(url)), url);

    [DataTestMethod]
    [DataRow("https://evil.example/link")]                  // 完全不相干
    [DataRow("https://microsoft.com.evil.example/link")]    // 后缀伪装
    [DataRow("https://microsoftcom/link")]                  // 少一点
    [DataRow("http://microsoft.com/link")]                  // 明文 http
    [DataRow("https://microsoft.com:8443/link")]            // 非默认端口
    [DataRow("https://microsoft.com@evil.example/link")]    // userinfo 伪装
    [DataRow("https://piston-meta.mojang.com/link")]        // 在出站白名单里、但**不是**登录域
    public void AnythingElseIsRefused(string url) =>
        Assert.IsFalse(SignInUrlPolicy.IsAllowed(SignInUrlPolicy.Normalize(url)), url);

    [TestMethod]
    public void AMissingOrUnparsableValueIsRefused()
    {
        Assert.IsNull(SignInUrlPolicy.Normalize(null));
        Assert.IsNull(SignInUrlPolicy.Normalize("   "));
        Assert.IsFalse(SignInUrlPolicy.IsAllowed(null));
    }

    [TestMethod]
    public void TheOutboundAllowListIsDeliberatelyWiderThanTheSignInAllowList()
    {
        // 两条白名单的用途不同：出站白名单管"能不能去下载"，登录白名单管"能不能让用户去输代码"。
        // 把后者换成前者，等于允许把一个 Maven 仓库地址显示成"请在这里输入你的代码"。
        Assert.IsTrue(HttpHostPolicy.Default.IsAllowed(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")));
        Assert.IsFalse(SignInUrlPolicy.IsAllowed(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")));
    }
}
