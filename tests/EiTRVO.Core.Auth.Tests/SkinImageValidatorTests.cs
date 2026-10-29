using System.Buffers.Binary;
using EiTRVO.Core.Auth.Microsoft;

namespace EiTRVO.Core.Auth.Tests;

/// <summary>
/// D-9：上传前的本地 PNG 校验。
/// <para>
/// 这一组用例的价值在于**用户看不到那个 400**：官方对不合规的图片只回一个状态码、不说什么不合规，
/// 所以"哪儿不对"必须由这里在发请求之前说清楚 —— 每一条规则都要被钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class SkinImageValidatorTests
{
    [TestMethod]
    public void A64By64PngIsAccepted()
    {
        var issue = SkinImageValidator.Validate(Png(64, 64), out var width, out var height);

        Assert.AreEqual(SkinImageIssue.None, issue);
        Assert.AreEqual(64, width);
        Assert.AreEqual(64, height);
    }

    [TestMethod]
    public void TheLegacy64By32FormatIsAccepted()
    {
        Assert.AreEqual(SkinImageIssue.None, SkinImageValidator.Validate(Png(64, 32), out _, out _));
    }

    [TestMethod]
    public void AnEmptyFileIsRejectedBeforeAnythingElse()
    {
        var issue = SkinImageValidator.Validate([], out var width, out var height);

        Assert.AreEqual(SkinImageIssue.Empty, issue);
        Assert.AreEqual(0, width);
        Assert.AreEqual(0, height);
    }

    [TestMethod]
    public void SomethingThatIsNotAPngIsRejected()
    {
        // JPEG 的头（FF D8 FF）。
        var jpeg = new byte[64];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;

        Assert.AreEqual(SkinImageIssue.NotPng, SkinImageValidator.Validate(jpeg, out _, out _));
    }

    [TestMethod]
    public void APngWithoutIhdrFirstIsRejected()
    {
        var bytes = Png(64, 64);
        bytes[12] = (byte)'X';

        Assert.AreEqual(
            SkinImageIssue.NotPng,
            SkinImageValidator.Validate(bytes, out _, out _),
            "第一个块必须是 IHDR，否则宽高不在我们读的位置上。");
    }

    [TestMethod]
    public void ATruncatedFileIsRejected()
    {
        var bytes = Png(64, 64)[..20];

        Assert.AreEqual(SkinImageIssue.NotPng, SkinImageValidator.Validate(bytes, out _, out _));
    }

    [TestMethod]
    public void TheWrongDimensionsAreReportedWithTheActualSize()
    {
        var bytes = Png(128, 64);

        var issue = SkinImageValidator.Validate(bytes, out var width, out var height);

        Assert.AreEqual(SkinImageIssue.WrongDimensions, issue);
        Assert.AreEqual(128, width);
        Assert.AreEqual(64, height);
        var description = SkinImageValidator.Describe(issue, bytes.Length, width, height);

        Assert.IsNotNull(description);
        Assert.AreEqual("皮肤必须是 64×64 或 64×32 像素，这张是 {0}×{1}。", description.Key);
        CollectionAssert.AreEqual(new[] { "128", "64" }, description.Parameters.ToArray());
    }

    [TestMethod]
    public void AFourByOneSkinIsNotAccepted()
    {
        // 有些客户端支持 4×1 之类的小尺寸，但**官方皮肤接口**不收它们。
        Assert.AreEqual(
            SkinImageIssue.WrongDimensions,
            SkinImageValidator.Validate(Png(4, 1), out _, out _));
    }

    [TestMethod]
    public void TheSizeCapIsOursAndIsEnforced()
    {
        var tooBig = Png(64, 64, SkinImageValidator.MaximumBytes + 1);

        var issue = SkinImageValidator.Validate(tooBig, out _, out _);

        Assert.AreEqual(SkinImageIssue.TooLarge, issue);
        var description = SkinImageValidator.Describe(issue, tooBig.Length, 0, 0);

        Assert.IsNotNull(description);
        Assert.AreEqual("这张图片有 {0} KB，超过了本启动器的上限（{1} KB）。皮肤图通常只有几 KB，请确认选的是皮肤 PNG 而不是别的图片。", description.Key);
        Assert.AreEqual(
            (SkinImageValidator.MaximumBytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            description.Parameters[1]);
    }

    [TestMethod]
    public void AFileRightAtTheCapIsStillAccepted()
    {
        Assert.AreEqual(
            SkinImageIssue.None,
            SkinImageValidator.Validate(Png(64, 64, SkinImageValidator.MaximumBytes), out _, out _));
    }

    /// <summary>造一段带合法 PNG 签名与 IHDR 的字节（至少 24 字节；<paramref name="totalBytes"/> 用来撑大小）。</summary>
    private static byte[] Png(int width, int height, int totalBytes = 0)
    {
        var bytes = new byte[Math.Max(24, totalBytes)];

        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);

        // IHDR 块：长度 13，类型 IHDR，随后是宽高（大端）。
        bytes[11] = 13;
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);

        return bytes;
    }
}
