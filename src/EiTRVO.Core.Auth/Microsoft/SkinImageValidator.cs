using System.Buffers.Binary;
using System.Globalization;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>一张皮肤图片的问题。</summary>
public enum SkinImageIssue
{
    /// <summary>没问题。</summary>
    None,

    /// <summary>空文件（0 字节，或选了个空目录项）。</summary>
    Empty,

    /// <summary>超过本地自定的上限。</summary>
    TooLarge,

    /// <summary>不是 PNG（签名或 IHDR 头不对）。</summary>
    NotPng,

    /// <summary>不是 64×64 或 64×32。</summary>
    WrongDimensions,
}

/// <summary>
/// 上传前的**本地** PNG 校验（D-9）。
/// <para>
/// 为什么要有它：官方接口对"图片不合规"只回一个 <c>400</c>，正文里给不出"哪儿不合规"。
/// 而 64×64 / 64×32 这类规则是完全确定的 —— 与其让用户上传一次、等一个 400、再猜，
/// 不如在上传之前就说清楚（也正是 AGENTS.md §6 里"上传前本地校验 PNG"那一条）。
/// </para>
/// <para>
/// <b>大小上限是本仓库自定的策略</b>：官方没有公布上限（见 <c>后端实施规划.md</c> §12.9 Q6
/// 的"未验事项"）。64×64 的皮肤 PNG 通常只有几 KB，256 KB 已经宽到不会误伤任何人，
/// 同时挡得住"手滑选了张壁纸"。
/// </para>
/// </summary>
public static class SkinImageValidator
{
    /// <summary>本地自定的大小上限（字节）。官方未公布上限，这是我们的策略。</summary>
    public const int MaximumBytes = 256 * 1024;

    /// <summary>PNG 的 8 字节签名（<c>89 50 4E 47 0D 0A 1A 0A</c>）。</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 校验一段字节是不是可用的皮肤图片。
    /// </summary>
    /// <param name="bytes">文件内容。</param>
    /// <param name="width">读出的宽度（校验不通过时为 0）。</param>
    /// <param name="height">读出的高度（校验不通过时为 0）。</param>
    /// <returns>结论。</returns>
    /// <remarks>
    /// 只读 PNG 的**文件头**（签名 + IHDR 的宽高），不解码整张图：解码一张 64×64 的图
    /// 要引 <c>System.Drawing</c>/<c>WIC</c> 这一层 UI 相邻的依赖，而这里需要的判定
    /// 完全由文件头决定 —— 宽高就写在错误的位置上，改不出来。
    /// </remarks>
    public static SkinImageIssue Validate(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (bytes.IsEmpty)
        {
            return SkinImageIssue.Empty;
        }

        if (bytes.Length > MaximumBytes)
        {
            return SkinImageIssue.TooLarge;
        }

        // 签名 8 字节 + IHDR 的长度/类型 8 字节 + 宽高 8 字节 = 24。
        if (bytes.Length < 24)
        {
            return SkinImageIssue.NotPng;
        }

        if (!bytes[..8].SequenceEqual(PngSignature))
        {
            return SkinImageIssue.NotPng;
        }

        // 第一个块必须是 IHDR，否则宽高不在我们读的位置上（PNG 规范要求如此）。
        if (bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R')
        {
            return SkinImageIssue.NotPng;
        }

        var readWidth = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
        var readHeight = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);

        if (readWidth <= 0 || readHeight <= 0)
        {
            return SkinImageIssue.NotPng;
        }

        width = readWidth;
        height = readHeight;

        // 64×64 是现行格式，64×32 是旧格式（官方两者都收）。
        return readWidth == 64 && (readHeight == 64 || readHeight == 32)
            ? SkinImageIssue.None
            : SkinImageIssue.WrongDimensions;
    }

    /// <summary>把结论翻成一句给用户看的话（键 + 参数）。</summary>
    /// <param name="issue">结论。</param>
    /// <param name="byteCount">文件字节数（用于 <see cref="SkinImageIssue.TooLarge"/> 的说明）。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <returns>一句话；没有问题时为 <see langword="null"/>。</returns>
    public static TextTemplate? Describe(SkinImageIssue issue, int byteCount, int width, int height) => issue switch
    {
        SkinImageIssue.None => null,
        SkinImageIssue.Empty => TextTemplate.Of("选中的文件是空的。"),
        SkinImageIssue.TooLarge =>
            TextTemplate.Of(
                "这张图片有 {0} KB，超过了本启动器的上限（{1} KB）。皮肤图通常只有几 KB，请确认选的是皮肤 PNG 而不是别的图片。",
                (byteCount / 1024).ToString(CultureInfo.InvariantCulture),
                (MaximumBytes / 1024).ToString(CultureInfo.InvariantCulture)),
        SkinImageIssue.NotPng => TextTemplate.Of("这不是一个 PNG 文件（皮肤必须是 PNG）。"),
        SkinImageIssue.WrongDimensions =>
            TextTemplate.Of(
                "皮肤必须是 64×64 或 64×32 像素，这张是 {0}×{1}。",
                width.ToString(CultureInfo.InvariantCulture),
                height.ToString(CultureInfo.InvariantCulture)),
        _ => TextTemplate.Of("这张图片不可用。"),
    };
}
