using System.Text.Json.Serialization;

namespace EiTRVO.Core.Launcher.Versions;
/// <summary>
/// 资源索引文件（<c>assets/indexes/&lt;id&gt;.json</c>）的内容。
/// <para>
/// 与 <see cref="AssetIndexInfo"/> 的区别：那个是版本 JSON 里的**指针**（地址 + SHA-1），
/// 这个是指针指向的那份**清单**（每个资源对象的哈希与大小）。
/// </para>
/// </summary>
public sealed record AssetIndexContents
{
    /// <summary>资源对象：名字 → 对象。</summary>
    public IReadOnlyDictionary<string, AssetObjectInfo>? Objects { get; init; }

    /// <summary>
    /// 是否要按「虚拟资源」布局再放一份（1.6 及以前）。
    /// <para>
    /// 老版本的游戏按名字读资源（<c>assets/virtual/legacy/…</c>），新版本按哈希读
    /// （<c>assets/objects/&lt;2位&gt;/&lt;hash&gt;</c>）。只下对象、不做这份布局的话，
    /// 老版本启动后是"没有声音、没有语言文件"，而不是报错 —— 所以这里如实处理。
    /// </para>
    /// </summary>
    public bool Virtual { get; init; }

    /// <summary>是否要直接铺到游戏目录的 <c>resources/</c>（1.6.4 及以前）。</summary>
    /// <remarks>
    /// **必须显式写 JSON 名**：索引里这个字段是蛇形的 <c>map_to_resources</c>，
    /// 而全仓库的 JSON 选项用的是驼峰策略（映射成 <c>mapToResources</c>），
    /// 大小写不敏感也救不了下划线。漏了这个映射的表现不是报错，而是老版本少了整份
    /// <c>resources/</c> 布局 —— 游戏能启动，但没有声音、没有语言文件。
    /// </remarks>
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; init; }

    /// <summary>对象总数。</summary>
    public int Count => Objects?.Count ?? 0;

    /// <summary>所有对象的大小之和。</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;

            foreach (var item in Objects?.Values ?? [])
            {
                total += Math.Max(0, item.Size);
            }

            return total;
        }
    }
}

/// <summary>资源索引里的一个对象。</summary>
/// <param name="Hash">SHA-1（也是它在 <c>objects/</c> 下的路径）。</param>
/// <param name="Size">字节数。</param>
public sealed record AssetObjectInfo(string? Hash, long Size);
