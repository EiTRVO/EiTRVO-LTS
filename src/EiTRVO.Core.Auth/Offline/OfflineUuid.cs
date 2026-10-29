using System.Security.Cryptography;
using System.Text;

namespace EiTRVO.Core.Auth.Offline;

/// <summary>
/// 离线账户的 UUID 派生：Java <c>UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(UTF_8))</c>
/// 的等价实现（D-6）。
/// <para>
/// <b>为什么值得单独一个类型并附带参考向量测试</b>：这个算法只有十几行，但它有一个
/// 会静默生效的坑 —— <b>字节序</b>。Java 把 MD5 的 16 个字节**按大端**依次排进
/// <c>8-4-4-4-12</c> 五段；而 .NET 的 <c>new Guid(byte[])</c> 会把前三段当作
/// <b>小端</b>解释。两者都"能编出一个合法的 Guid"，所以
/// 「解析得出来 / 同名同值 / 异名异值 / 长度 36」这类断言**全部会通过** —— Neo 的
/// <c>UuidHelperTests</c> 正是只测了这四件事，于是这个缺陷一路活到了审计才发现（P0）。
/// </para>
/// <para>
/// 后果不是崩溃，而是"离线世界里认不出你"：单人存档的玩家数据是按 UUID 存的，
/// 用错字节序得到的 UUID 与 Java 版、与服务端、与别的启动器算出来的都不是同一个 ——
/// 玩家会看到自己的存档"换了个人"，或者进服务器被当成新玩家。
/// </para>
/// <para>
/// 因此这里只做一件事，并且用**真的 Java 算出来的向量**钉住（见 <c>OfflineUuidTests</c>）。
/// </para>
/// </summary>
public static class OfflineUuid
{
    /// <summary>Java 侧的前缀（一个字节都不能差）。</summary>
    private const string Prefix = "OfflinePlayer:";

    /// <summary>按玩家名派生离线 UUID（形如 <c>b50ad385-829d-3141-a216-7e7d7539ba7f</c>）。</summary>
    /// <param name="playerName">玩家名。</param>
    /// <returns>带连字符的 36 字符 UUID 文本（小写十六进制，与 Java 的 <c>UUID.toString()</c> 一致）。</returns>
    /// <exception cref="ArgumentException">玩家名为空。</exception>
    public static string For(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        // CA5351（MD5 是弱算法）在这里**不适用**：这个哈希不是我们选的安全机制，
        // 而是 Java 的 `UUID.nameUUIDFromBytes`（version 3 名字型 UUID）**规定的**算法 ——
        // 整个 Minecraft 生态（Java 版、服务端、别的启动器）都用它算离线 UUID。
        // 换成 SHA-256 会让同一个玩家名算出不同的 UUID，也就是"换个启动器就认不出你的存档"。
        // 它只用来说"这个名字对应哪串字节"，不承担任何抗碰撞/防伪职责。
#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(Prefix + playerName));
#pragma warning restore CA5351

        // 版本位：MD5 名字型 UUID 是 version 3（第 7 个字节的高 4 位 = 0011）。
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);

        // 变体位：RFC 4122（第 9 个字节的高 2 位 = 10）。
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        // 关键：把 16 个字节**按原顺序**写成十六进制再分段，也就是大端。
        // 写成 new Guid(hash) 会得到另一串（前三段小端）—— 测试里有一条专门盯这个。
        return Format(Convert.ToHexString(hash));
    }

    /// <summary>
    /// 把 32 位无连字符的十六进制 UUID 文本加上连字符；已经是标准形式（36 字符）或长度不对时原样返回。
    /// </summary>
    /// <remarks>
    /// M4 会用到它：Minecraft Services 与部分旧接口给出的是**无连字符**形式，
    /// 而存档/服务器/日志里都是带连字符的标准形式。
    /// </remarks>
    /// <param name="uuid">UUID 文本。</param>
    /// <returns>带连字符的形式。</returns>
    public static string Format(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return string.Empty;
        }

        var text = uuid.Trim();
        if (text.Length != 32)
        {
            return text;
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{text[..8]}-{text[8..12]}-{text[12..16]}-{text[16..20]}-{text[20..]}").ToLowerInvariant();
    }
}
