using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-b：启动命令行的脱敏（C-15）。
/// <para>
/// 这一层存在的唯一理由：命令行是排查"游戏起不来"时第一个要看的东西（p03/p14 的事件表、
/// 崩溃报告、用户贴到 issue 里的日志），而它里面正好有访问令牌 —— 一次复制粘贴就等于把账户交出去。
/// </para>
/// <para>
/// 所以断言写成"**在用于日志的文本里搜不到原值**"，并且每条用例都先确认原值**确实**在未脱敏的实参表里：
/// 否则一旦参数构建不再传令牌，"搜不到"会因为"压根没传"而永远为真 ——
/// 那正是 Neo 的 <c>UuidHelperTests</c> 犯过的错（四条断言全过，而结果是错的）。
/// </para>
/// </summary>
[TestClass]
public sealed class LaunchRedactionTests : LaunchTestData
{
    [TestMethod]
    public void TheRealCommandLineHidesTheTokenTheUuidAndTheXuid()
    {
        var launch = LaunchArgumentBuilder.Build(Request(identity: MicrosoftIdentity));

        // 先确认这些东西真的在命令行里，否则下面的断言全是空转。
        Assert.IsTrue(launch.Arguments.Contains(TestAccessToken), "前提：令牌确实在未脱敏的实参表里。");
        Assert.IsTrue(launch.Arguments.Contains(MicrosoftIdentity.UuidWithoutDashes));
        Assert.IsTrue(launch.Arguments.Contains("2535429452678874"));

        var text = launch.RedactedCommandText;

        Assert.IsFalse(text.Contains(TestAccessToken, StringComparison.Ordinal), "令牌不得出现在用于日志的文本里。");
        Assert.IsFalse(text.Contains(MicrosoftIdentity.UuidWithoutDashes, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(MicrosoftIdentity.Uuid, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("2535429452678874", StringComparison.Ordinal));

        Assert.IsTrue(text.Contains("--accessToken " + LaunchRedaction.Mask, StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("--uuid " + LaunchRedaction.Mask, StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("--xuid " + LaunchRedaction.Mask, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThePlayerNameAndTheClientIdAreDeliberatelyKept()
    {
        // 排查"进不去服务器"必须知道是谁在登录、用的哪个应用注册；两者都不是秘密
        // （Client ID 本来就写在每个认证请求里，玩家名也会出现在游戏自己的日志里）。
        var launch = LaunchArgumentBuilder.Build(Request(identity: MicrosoftIdentity));
        var text = launch.RedactedCommandText;

        Assert.IsTrue(text.Contains("--username " + PlayerName, StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("--clientId " + ClientId, StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("--versionType release", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BothSpellingsAndMixedCaseAreMasked()
    {
        string[] arguments =
        [
            "-Xmx2G",
            "--accessToken", TestAccessToken,
            "--uuid=" + OfflineUuid.Replace("-", string.Empty, StringComparison.Ordinal),
            "--ACCESSTOKEN", "a-second-token-value",
            "--userProperties", "{}",
        ];

        var masked = LaunchRedaction.Redact(arguments);

        AssertSequence(
            [
                "-Xmx2G",
                "--accessToken", LaunchRedaction.Mask,
                "--uuid=" + LaunchRedaction.Mask,
                "--ACCESSTOKEN", LaunchRedaction.Mask,
                "--userProperties", "{}",
            ],
            masked,
            "脱敏后的实参表（合并写法与大小写都要认）");
    }

    [TestMethod]
    public void AShortOfflineTokenIsMaskedPositionallyButNeverSwept()
    {
        // 离线令牌是 "0"：按位置必须抹掉，但**不能**拿它去做全局替换 ——
        // 一个字符的清扫会把 --width 1280 之类全毁掉。
        string[] arguments = ["--accessToken", "0", "--width", "1280", "--height", "720"];

        var masked = LaunchRedaction.Redact(arguments);

        AssertSequence(
            ["--accessToken", LaunchRedaction.Mask, "--width", "1280", "--height", "720"],
            masked,
            "短令牌");

        Assert.AreEqual("0", arguments[1], "原表不得被就地改动（调用方还要拿它去启动进程）。");
    }

    [TestMethod]
    public void AnOptionWithoutAValueDoesNotSwallowTheNextOption()
    {
        // --accessToken 后面跟着另一个选项 = 这个选项没给值。此时把下一个词当值抹掉，
        // 日志里就会少掉一个真实参数（"用户名明明传了，日志里却看不到"）。
        string[] arguments = ["--accessToken", "--username", PlayerName];

        var masked = LaunchRedaction.Redact(arguments);

        AssertSequence(["--accessToken", "--username", PlayerName], masked, "缺值的选项");
    }

    [TestMethod]
    public void AnEmptyValueIsStillMasked()
    {
        // 空串是**值**（不是"没有值"）：某些版本 JSON 会写成 --accessToken ""。
        var masked = LaunchRedaction.Redact(["--accessToken", string.Empty, "--uuid", string.Empty]);

        AssertSequence(
            ["--accessToken", LaunchRedaction.Mask, "--uuid", LaunchRedaction.Mask],
            masked,
            "空值");
    }

    [TestMethod]
    public void ASecretCopiedIntoAnotherArgumentIsAlsoMasked()
    {
        // 自定义/整合包版本 JSON 完全可以把 ${auth_access_token} 放在别的参数里 ——
        // 那时按"选项名"脱敏抓不到那一份，所以要有一层按"值"的清扫。
        var version = Real1204Json() with
        {
            Arguments = new VersionArguments
            {
                Jvm = [VersionArgument.Plain("-Doddity=${auth_access_token}"), .. Real1204JvmArguments()],
                Game = Real1204GameArguments(),
            },
        };

        var launch = LaunchArgumentBuilder.Build(Request(version: version, identity: MicrosoftIdentity));

        Assert.IsTrue(launch.Arguments.Contains("-Doddity=" + TestAccessToken), "前提：令牌确实被复制到了另一个参数里。");
        Assert.IsFalse(
            launch.RedactedCommandText.Contains(TestAccessToken, StringComparison.Ordinal),
            "同一个令牌被别处复制时也要抹掉。");
        Assert.IsTrue(launch.RedactedCommandText.Contains("-Doddity=" + LaunchRedaction.Mask, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ARejectedRemoteArgumentCannotSmuggleTheTokenIntoTheEventLog()
    {
        // 版本 JSON 是远端数据：一份恶意 JSON 可以写 -javaagent:${auth_access_token}。
        // 它会被安全策略拒掉，然后作为"被拒参数"出现在事件日志里 —— 那就是"让启动器自己把令牌写出来"。
        var version = Real1204Json() with
        {
            Arguments = new VersionArguments
            {
                Jvm = [VersionArgument.Plain("-javaagent:${auth_access_token}"), .. Real1204JvmArguments()],
                Game = Real1204GameArguments(),
            },
        };

        var launch = LaunchArgumentBuilder.Build(Request(version: version, identity: MicrosoftIdentity));
        var rejected = launch.ProblemsOf(LaunchProblemKind.RejectedArgument);

        Assert.AreEqual(1, rejected.Count);
        Assert.IsFalse(
            rejected[0].Subject.Contains(TestAccessToken, StringComparison.Ordinal),
            "被拒参数的说明里也不许出现令牌。");
        Assert.IsFalse(rejected[0].Reason.Contains(TestAccessToken, StringComparison.Ordinal));
        Assert.IsTrue(rejected[0].Subject.Contains(LaunchRedaction.Mask, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ArgumentsWithSpacesAreQuotedInTheLogText()
    {
        var launch = LaunchArgumentBuilder.Build(
            Request() with { InstanceDirectory = @"C:\Program Files\mc\instances\1.20.4" });

        Assert.IsTrue(
            launch.RedactedCommandText.Contains(@"""C:\Program Files\mc\instances\1.20.4""", StringComparison.Ordinal),
            "含空格的实参要加引号，否则日志里的命令行会被误读成两个参数。");
    }

    [TestMethod]
    public void RedactionIsIdempotent()
    {
        var launch = LaunchArgumentBuilder.Build(Request(identity: MicrosoftIdentity));

        var once = LaunchRedaction.Redact(launch.Arguments);
        var twice = LaunchRedaction.Redact(once);

        AssertSequence(once.ToArray(), twice, "二次脱敏不得再改动任何东西");
    }

    [TestMethod]
    public void RedactTextOnlyHandlesValuesLongEnoughToBeSecrets()
    {
        Assert.AreEqual("token=" + LaunchRedaction.Mask, LaunchRedaction.RedactText("token=" + TestAccessToken, [TestAccessToken]));
        Assert.AreEqual(
            "count=0",
            LaunchRedaction.RedactText("count=0", ["0"]),
            "一个字符的值不参与清扫，否则离线令牌会把普通数字全抹成 ***。");
    }
}
