using EiTRVO.Core.Auth.Offline;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// M5 的 D-6：离线玩家名的规则（3–16 位，字母/数字/下划线）。
/// <para>
/// 规则本身与正版用户名一致，但离线模式更"危险"：没有服务端来兜底，
/// 名字会原样进 <c>--username</c>、参与 UUID 派生、并被离线服用它自己算出的名字比对。
/// 名字里多一个空格，命令行参数就会错位；大小写或空格不同，UUID 就完全不同（存档认不出你）。
/// </para>
/// </summary>
[TestClass]
public sealed class OfflineNameTests
{
    [TestMethod]
    [DataRow("abc")]
    [DataRow("Notch")]
    [DataRow("Alex_Offline")]
    [DataRow("Player_123456789")]
    [DataRow("A_1")]
    [DataRow("________________")]
    public void LegalNames(string name)
    {
        Assert.AreEqual(OfflineNameIssue.None, OfflineName.Validate(name));
        Assert.IsTrue(OfflineName.IsValid(name));
    }

    [TestMethod]
    [DataRow(null, OfflineNameIssue.Empty)]
    [DataRow("", OfflineNameIssue.Empty)]
    [DataRow("   ", OfflineNameIssue.Empty)]
    [DataRow("ab", OfflineNameIssue.TooShort)]
    [DataRow("Player_1234567890", OfflineNameIssue.TooLong)]
    [DataRow("玩家一号", OfflineNameIssue.IllegalCharacter)]
    [DataRow("abc def", OfflineNameIssue.IllegalCharacter)]
    [DataRow(" abc", OfflineNameIssue.IllegalCharacter)]
    [DataRow("abc-", OfflineNameIssue.IllegalCharacter)]
    [DataRow("a.b", OfflineNameIssue.IllegalCharacter)]
    public void IllegalNames(string? name, OfflineNameIssue expected)
    {
        Assert.AreEqual(expected, OfflineName.Validate(name));
        Assert.IsFalse(OfflineName.IsValid(name));
    }

    [TestMethod]
    public void BoundaryLengthsAreThreeAndSixteen()
    {
        Assert.AreEqual(OfflineNameIssue.TooShort, OfflineName.Validate(new string('a', 2)));
        Assert.AreEqual(OfflineNameIssue.None, OfflineName.Validate(new string('a', 3)));
        Assert.AreEqual(OfflineNameIssue.None, OfflineName.Validate(new string('a', 16)));
        Assert.AreEqual(OfflineNameIssue.TooLong, OfflineName.Validate(new string('a', 17)));
        Assert.AreEqual(3, OfflineName.MinLength);
        Assert.AreEqual(16, OfflineName.MaxLength);
    }
}
