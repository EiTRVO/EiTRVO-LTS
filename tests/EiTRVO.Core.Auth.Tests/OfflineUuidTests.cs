using EiTRVO.Core.Auth.Offline;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// M5 的 D-6：离线 UUID 的派生。
/// <para>
/// <b>这些期望值是拿真的 Java 算出来的</b>（<c>UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(UTF_8))</c>，
/// 在 JDK 21 上跑出来逐条抄进来），不是我们自己算一遍再抄一遍 —— 后者只能证明"实现与实现一致"，
/// 而这里要证明的是"与 Java 版、与服务端、与别的启动器算出同一个 UUID"。
/// </para>
/// <para>
/// 为什么这条测试值得这么写：Neo 的 <c>UuidHelperTests</c> 断言了
/// 「解析得出来 / 同名同值 / 异名异值 / 长度 36」四件事，**四条全过**，
/// 但它的字节序是错的（<c>new Guid(byte[])</c> 的前三段按小端解释）。
/// 也就是说，只测"形状"的测试对这类缺陷是**零覆盖**；只有与外部权威实现对齐的向量才能抓住它。
/// </para>
/// </summary>
[TestClass]
public sealed class OfflineUuidTests
{
    /// <summary>Java 侧算出的参考向量（名字 → UUID）。</summary>
    public static IEnumerable<object[]> Vectors =>
    [
        ["Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f"],
        ["Steve", "5627dd98-e6be-3c21-b8a8-e92344183641"],
        ["Alex_Offline", "f284a681-7831-35a6-847e-1234112049bf"],
        ["abc", "3d5cec06-bd15-31fa-982f-5dac8c06f1c7"],
        ["Player_1234567890", "c5061033-daf9-3f22-a45b-9df19b68dfea"],
        // 非 ASCII：Java 用 UTF-8 取字节，这里也必须一样（用 UTF-16 或别的编码会得到完全不同的 UUID）。
        ["玩家一号", "563807d4-80f7-3b64-9ebb-4dbce5bc9f41"],
    ];

    [TestMethod]
    [DynamicData(nameof(Vectors))]
    public void TheUuidMatchesWhatJavaComputes(string playerName, string expected)
    {
        Assert.AreEqual(expected, OfflineUuid.For(playerName));
    }

    [TestMethod]
    public void TheNeoByteOrderWouldProduceADifferentUuid()
    {
        // Neo 的写法：把 MD5 的字节直接交给 new Guid(byte[])（前三段小端）。
        // 这条用例把差异**钉在墙上**：如果哪天有人"简化"成 new Guid(hash)，
        // 上面那条向量测试会红，而这条会告诉大家红的是哪件事。
        var correct = OfflineUuid.For("Notch");

        // CA5351 在这里不适用：MD5 是 Java 名字型 UUID（version 3）规定的算法，不是我们选的安全机制。
#pragma warning disable CA5351
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:Notch"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var neo = new Guid(hash).ToString("D");
#pragma warning restore CA5351

        Assert.AreEqual("85d30ab5-9d82-4131-a216-7e7d7539ba7f", neo, "Neo 的（错误）结果是这个。");
        Assert.AreNotEqual(neo, correct, "正确实现必须给出与 Neo 不同的 UUID —— 差别就在字节序。");

        // 一个直观的旁证：正确结果的**版本位是 3**（MD5 名字型），
        // 而小端解释会把版本位挪到别处，于是第三段不再是 3xxx。
        Assert.AreEqual('3', correct[14], "连字符后第三段的首位应当是版本号 3。");
    }

    [TestMethod]
    public void TheUuidIsStableAndNameDependent()
    {
        Assert.AreEqual(OfflineUuid.For("Steve"), OfflineUuid.For("Steve"));
        Assert.AreNotEqual(OfflineUuid.For("Steve"), OfflineUuid.For("steve"), "名字大小写不同就是不同的 UUID。");
        Assert.AreNotEqual(OfflineUuid.For("Steve"), OfflineUuid.For("Steve1"));
    }

    [TestMethod]
    public void TheUuidIsLowercaseAndDashed()
    {
        var uuid = OfflineUuid.For("Player");

        Assert.AreEqual(36, uuid.Length);
        Assert.AreEqual('-', uuid[8]);
        Assert.AreEqual('-', uuid[13]);
        Assert.AreEqual('-', uuid[18]);
        Assert.AreEqual('-', uuid[23]);
        Assert.AreEqual(uuid.ToLowerInvariant(), uuid, "Java 的 UUID.toString() 是小写。");
        Assert.IsTrue(Guid.TryParse(uuid, out _));
    }

    [TestMethod]
    public void AnEmptyNameIsRefused()
    {
        Assert.ThrowsException<ArgumentException>(() => OfflineUuid.For(string.Empty));
        Assert.ThrowsException<ArgumentException>(() => OfflineUuid.For("   "));
    }

    [TestMethod]
    [DataRow("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6")]
    [DataRow("A1B2C3D4E5F6A7B8C9D0E1F2A3B4C5D6", "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6")]
    [DataRow("abc", "abc")]
    [DataRow("", "")]
    public void FormatAddsDashesToTheCompactForm(string input, string expected)
    {
        // M4 会用到它：Minecraft Services 给出的是无连字符形式，而存档与日志里是标准形式。
        Assert.AreEqual(expected, OfflineUuid.Format(input));
    }
}
