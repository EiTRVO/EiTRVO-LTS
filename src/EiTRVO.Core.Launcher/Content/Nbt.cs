using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace EiTRVO.Core.Launcher.Content;

/// <summary>NBT 标签（只实现读 <c>level.dat</c> 需要的那几种）。</summary>
/// <remarks>
/// 不引第三方 NBT 库（AGENTS.md §3.2：NBT 自实现），也只实现**读取**：
/// 本仓库目前只需要从存档里读出「名字 / 游戏模式 / 最后游玩时间 / 存档版本」，
/// 写入 NBT 没有需求（改存档不是启动器该做的事）。
/// </remarks>
public abstract record NbtTag;

/// <summary>1 字节整数。</summary>
/// <param name="Value">值。</param>
public sealed record NbtByte(sbyte Value) : NbtTag;

/// <summary>2 字节整数。</summary>
/// <param name="Value">值。</param>
public sealed record NbtShort(short Value) : NbtTag;

/// <summary>4 字节整数。</summary>
/// <param name="Value">值。</param>
public sealed record NbtInt(int Value) : NbtTag;

/// <summary>8 字节整数。</summary>
/// <param name="Value">值。</param>
public sealed record NbtLong(long Value) : NbtTag;

/// <summary>UTF-8 字符串。</summary>
/// <param name="Value">值。</param>
public sealed record NbtString(string Value) : NbtTag;

/// <summary>同类型元素的列表。</summary>
/// <param name="Items">元素。</param>
public sealed record NbtList(IReadOnlyList<NbtTag> Items) : NbtTag;

/// <summary>复合标签（键值对）。</summary>
/// <param name="Values">成员。</param>
public sealed record NbtCompound(IReadOnlyDictionary<string, NbtTag> Values) : NbtTag
{
    /// <summary>取一个成员；不存在时返回 <see langword="null"/>。</summary>
    /// <param name="name">成员名。</param>
    /// <returns>标签。</returns>
    public NbtTag? Find(string name) => Values.TryGetValue(name, out var tag) ? tag : null;

    /// <summary>取字符串成员。</summary>
    /// <param name="name">成员名。</param>
    /// <returns>值；类型不符或不存在时为 <see langword="null"/>。</returns>
    public string? Text(string name) => Find(name) is NbtString text ? text.Value : null;

    /// <summary>取整数成员（NBT 里同一个语义数字可能写成 byte / short / int / long，这里一并收下）。</summary>
    /// <param name="name">成员名。</param>
    /// <returns>值；类型不符或不存在时为 <see langword="null"/>。</returns>
    public long? Number(string name) => Find(name) switch
    {
        NbtByte value => value.Value,
        NbtShort value => value.Value,
        NbtInt value => value.Value,
        NbtLong value => value.Value,
        _ => null,
    };

    /// <summary>取复合成员。</summary>
    /// <param name="name">成员名。</param>
    /// <returns>子复合；类型不符或不存在时为 <see langword="null"/>。</returns>
    public NbtCompound? Compound(string name) => Find(name) as NbtCompound;}

/// <summary>读得出来但不解释的标签（浮点、数组）。</summary>
/// <remarks>
/// 刻意保留这个分支而不是"跳过时返回 null"：**跳过**与**读到了但没有解释**是两件事，
/// 混在一起会让"这个字段存在但类型不认识"看起来像"字段不存在"。
/// </remarks>
public sealed record NbtOpaque : NbtTag;

/// <summary>
/// 极简 NBT 读取器（<c>level.dat</c> 是 gzip 压缩的 NBT）。
/// </summary>
/// <remarks>
/// 所有读取都做**边界与规模检查**：存档文件是用户磁盘上可以被任意程序改写的输入，
/// 一个声称长度 4 GB 的字符串不该让我们去分配 4 GB。
/// 坏数据一律返回 <see langword="null"/>（读不出来不是异常路径，界面按"版本未指定"显示即可）。
/// </remarks>
public static class NbtReader
{
    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagShort = 2;
    private const byte TagInt = 3;
    private const byte TagLong = 4;
    private const byte TagFloat = 5;
    private const byte TagDouble = 6;
    private const byte TagByteArray = 7;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
    private const byte TagIntArray = 11;
    private const byte TagLongArray = 12;

    /// <summary>嵌套深度上限（正常存档不超过 10 层）。</summary>
    private const int MaxDepth = 24;

    /// <summary>集合元素数上限。</summary>
    private const int MaxCollectionItems = 1 << 22;

    /// <summary>读取一个 gzip 压缩的 NBT 文件（<c>level.dat</c>）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>根复合；文件不存在、损坏或类型不符时为 <see langword="null"/>。</returns>
    public static NbtCompound? ReadGzipFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return ReadStream(gzip);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // 存档读不出来不影响别的行：该行按"信息未知"显示（AGENTS.md §6：读失败不得改写文件）。
            return null;
        }
    }

    /// <summary>读取一个流里的 NBT。</summary>
    /// <param name="stream">流（未压缩的 NBT）。</param>
    /// <returns>根复合；损坏时为 <see langword="null"/>。</returns>
    public static NbtCompound? ReadStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        try
        {
            stream.CopyTo(buffer);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }

        return Read(buffer.ToArray());
    }

    /// <summary>读取一段未压缩的 NBT 字节。</summary>
    /// <param name="payload">字节。</param>
    /// <returns>根复合；损坏时为 <see langword="null"/>。</returns>
    public static NbtCompound? Read(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            var cursor = new Cursor(payload);

            if (cursor.ReadByte() != TagCompound)
            {
                return null;
            }

            // 根标签也带名字（通常为空串），必须读掉，否则后续全部错位。
            cursor.ReadName();
            return ReadCompound(cursor, depth: 1);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static NbtCompound ReadCompound(Cursor cursor, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("NBT 嵌套过深。");
        }

        var values = new Dictionary<string, NbtTag>(StringComparer.Ordinal);

        while (true)
        {
            var id = cursor.ReadByte();
            if (id == TagEnd)
            {
                return new NbtCompound(values);
            }

            var name = cursor.ReadName();
            values[name] = ReadPayload(cursor, id, depth);
        }
    }

    private static NbtTag ReadPayload(Cursor cursor, byte id, int depth) => id switch
    {
        TagByte => new NbtByte(unchecked((sbyte)cursor.ReadByte())),
        TagShort => new NbtShort(cursor.ReadInt16()),
        TagInt => new NbtInt(cursor.ReadInt32()),
        TagLong => new NbtLong(cursor.ReadInt64()),
        TagFloat => Skip(cursor, 4),
        TagDouble => Skip(cursor, 8),
        TagByteArray => Skip(cursor, CheckedLength(cursor.ReadInt32())),
        TagString => new NbtString(cursor.ReadString()),
        TagList => ReadList(cursor, depth),
        TagCompound => ReadCompound(cursor, depth + 1),
        TagIntArray => Skip(cursor, CheckedLength(cursor.ReadInt32()) * 4),
        TagLongArray => Skip(cursor, CheckedLength(cursor.ReadInt32()) * 8),
        _ => throw new InvalidDataException($"不认识的 NBT 标签类型：{id}。"),
    };

    private static NbtList ReadList(Cursor cursor, int depth)
    {
        var elementType = cursor.ReadByte();
        var count = CheckedLength(cursor.ReadInt32());

        // 规范允许"空列表把元素类型写成 TAG_End"（写法本身合法，只是没有元素）。
        if (elementType == TagEnd || count == 0)
        {
            return new NbtList([]);
        }

        var items = new List<NbtTag>(Math.Min(count, 1024));
        for (var index = 0; index < count; index++)
        {
            items.Add(ReadPayload(cursor, elementType, depth + 1));
        }

        return new NbtList(items);
    }

    private static NbtOpaque Skip(Cursor cursor, int bytes)
    {
        cursor.Skip(bytes);
        return new NbtOpaque();
    }

    private static int CheckedLength(int count)
    {
        if (count < 0 || count > MaxCollectionItems)
        {
            throw new InvalidDataException($"NBT 集合长度不合理：{count}。");
        }

        return count;
    }

    /// <summary>字节游标（大端；越界一律当成"数据损坏"）。</summary>
    private sealed class Cursor(byte[] data)
    {
        private int _position;

        public byte ReadByte()
        {
            Require(1);
            return data[_position++];
        }

        public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Span(2));

        public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Span(4));

        public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Span(8));

        public string ReadName() => ReadString();

        public string ReadString()
        {
            // 长度是 ushort（NBT 规范），因此天然有上界，不需要额外的大小检查。
            var length = (ReadByte() << 8) | ReadByte();
            Require(length);
            var text = Encoding.UTF8.GetString(data, _position, length);
            _position += length;
            return text;
        }

        public void Skip(int bytes)
        {
            if (bytes < 0)
            {
                throw new InvalidDataException("NBT 跳过的长度为负。");
            }

            Require(bytes);
            _position += bytes;
        }

        private ReadOnlySpan<byte> Span(int bytes)
        {
            Require(bytes);
            var span = data.AsSpan(_position, bytes);
            _position += bytes;
            return span;
        }

        private void Require(int bytes)
        {
            if (bytes < 0 || _position + bytes > data.Length)
            {
                throw new InvalidDataException("NBT 数据提前结束。");
            }
        }
    }
}
