using System.Diagnostics;
using EiTRVO.Core.Launcher.Security;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M1 / F-7 路径安全：集中式路径穿越与 NTFS 重解析点防护。
/// <para>
/// 用例由 Neo 的 <c>PathSafetyHelperTests</c> 迁移而来（AGENTS.md §5.1 允许借鉴内核逻辑），
/// 并补上了 Neo 那版测不到的两点：判定结果要能区分「越界」与「重解析点」；
/// 越界异常的**消息**不得泄露绝对路径。
/// </para>
/// <para>
/// 重解析点那两条需要真的建一个 Junction（需要开发者模式或管理员权限），
/// 建不出来时按 <see cref="Assert.Inconclusive(string)"/> 记为未决，而不是假装通过。
/// </para>
/// </summary>
[TestClass]
public sealed class PathSafetyTests
{
    private string _tempRoot = string.Empty;

    /// <summary>每个用例一个全新的临时根目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-pathtests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>清理临时目录；遇到 Junction 时先删链接本身，否则递归删除会跟着走进去。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (!Directory.Exists(_tempRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(_tempRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不该让用例变红；临时目录由系统回收。
            }
        }

        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------- 名称消毒 ---

    [TestMethod]
    [DataRow("normal", "normal")]
    [DataRow("../../../evil", "evil")]
    [DataRow("test\\evil", "evil")]
    [DataRow("test/evil", "evil")]
    [DataRow("..\\..\\Windows\\System32", "System32")]
    [DataRow("foo/bar/baz", "baz")]
    [DataRow("", PathSafety.FallbackName)]
    [DataRow("   ", PathSafety.FallbackName)]
    [DataRow("  valid  ", "valid")]
    public void SanitizeNameComponentKeepsOnlyTheLeaf(string? input, string expected) =>
        Assert.AreEqual(expected, PathSafety.SanitizeNameComponent(input));

    [TestMethod]
    public void SanitizeNameComponentMapsNullToTheFallbackName() =>
        Assert.AreEqual(PathSafety.FallbackName, PathSafety.SanitizeNameComponent(null));

    // --------------------------------------------------- 包容性判定 ---

    [TestMethod]
    public void NestedPathUnderTheBaseIsContained()
    {
        var baseDirectory = Path.Combine(_tempRoot, "sandbox");
        Directory.CreateDirectory(Path.Combine(baseDirectory, "sub"));

        Assert.AreEqual(
            PathContainment.Contained,
            PathSafety.Classify(baseDirectory, Path.Combine(baseDirectory, "sub", "file.txt")));
    }

    [TestMethod]
    public void TheBaseItselfIsContained()
    {
        var baseDirectory = Path.Combine(_tempRoot, "sandbox");
        Directory.CreateDirectory(baseDirectory);

        Assert.IsTrue(PathSafety.IsContained(baseDirectory, baseDirectory));
    }

    [TestMethod]
    public void TraversalOutOfTheBaseIsRejected()
    {
        var baseDirectory = Path.Combine(_tempRoot, "safe");
        var escaped = Path.Combine(baseDirectory, "..", "..", "Windows", "System32", "evil.exe");

        Assert.AreEqual(PathContainment.Outside, PathSafety.Classify(baseDirectory, escaped));
        Assert.IsFalse(PathSafety.IsContained(baseDirectory, escaped));
    }

    [TestMethod]
    public void ASiblingDirectorySharingTheNamePrefixIsRejected()
    {
        // C:\Temp\SafeDir 与 C:\Temp\SafeDirMalicious 只差前缀，必须判为越界。
        var baseDirectory = Path.Combine(_tempRoot, "SafeDir");
        var sibling = Path.Combine(_tempRoot, "SafeDirMalicious", "file.txt");

        Assert.IsFalse(PathSafety.IsContained(baseDirectory, sibling));
    }

    [TestMethod]
    public void ValidateContainedAcceptsAContainedPathWithoutThrowing() =>
        PathSafety.ValidateContained(_tempRoot, Path.Combine(_tempRoot, "a", "b", "c.txt"));

    [TestMethod]
    public void ValidateContainedThrowsForTraversal()
    {
        var baseDirectory = Path.Combine(_tempRoot, "safe");
        var escaped = Path.Combine(baseDirectory, "..", "..", "Windows", "System32", "evil.exe");

        Assert.ThrowsException<InvalidDataException>(
            () => PathSafety.ValidateContained(baseDirectory, escaped));
    }

    [TestMethod]
    public void ValidateContainedDoesNotLeakTheAbsolutePathInItsMessage()
    {
        var baseDirectory = Path.Combine(_tempRoot, "secrets");
        var escaped = Path.Combine(baseDirectory, "..", "..", "C", "Users", "victim", "Desktop", "malware.exe");

        var exception = Assert.ThrowsException<InvalidDataException>(
            () => PathSafety.ValidateContained(baseDirectory, escaped));

        StringAssert.Contains(exception.Message, "路径穿越检测");
        Assert.IsFalse(exception.Message.Contains(@":\", StringComparison.Ordinal), "消息里不应出现绝对路径。");
        Assert.IsFalse(exception.Message.Contains("victim", StringComparison.Ordinal), "消息里不应出现路径分段。");
    }

    [TestMethod]
    public void ValidateContainedRejectsEmptyArguments()
    {
        Assert.ThrowsException<ArgumentException>(() => PathSafety.ValidateContained("  ", "x"));
        Assert.ThrowsException<ArgumentException>(() => PathSafety.ValidateContained(_tempRoot, "  "));
    }

    // --------------------------------------------- 拼接与重解析点 ---

    [TestMethod]
    public void CombineWithinBuildsAContainedPath()
    {
        var combined = PathSafety.CombineWithin(_tempRoot, "versions", "1.20.4", "client.jar");

        Assert.AreEqual(
            Path.Combine(_tempRoot, "versions", "1.20.4", "client.jar"),
            combined);
    }

    [TestMethod]
    public void CombineWithinRejectsATraversalSegment()
    {
        // 归档条目的分段是外部输入，这一段就是 Zip Slip 的入口。
        Assert.ThrowsException<InvalidDataException>(
            () => PathSafety.CombineWithin(_tempRoot, "..", "..", "Windows", "System32", "evil.dll"));
    }

    [TestMethod]
    public void CombineWithinIgnoresEmptySegments()
    {
        Assert.AreEqual(
            Path.Combine(_tempRoot, "a", "b"),
            PathSafety.CombineWithin(_tempRoot, "a", string.Empty, "b"));
    }

    [TestMethod]
    public void RegularDirectoriesHaveNoReparsePoint()
    {
        var baseDirectory = Path.Combine(_tempRoot, "normal");
        Directory.CreateDirectory(Path.Combine(baseDirectory, "sub"));

        Assert.IsFalse(PathSafety.HasReparsePoint(baseDirectory, Path.Combine(baseDirectory, "sub", "file.txt")));
        Assert.AreEqual(
            PathContainment.Contained,
            PathSafety.Classify(baseDirectory, Path.Combine(baseDirectory, "sub", "file.txt")));
    }

    [TestMethod]
    public void APathThroughAJunctionIsRejectedEvenThoughItLooksContained()
    {
        var sandbox = Path.Combine(_tempRoot, "sandbox");
        var outside = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(sandbox);
        Directory.CreateDirectory(outside);

        var junction = Path.Combine(sandbox, "escape_link");
        if (!TryCreateJunction(junction, outside))
        {
            Assert.Inconclusive("无法创建目录 Junction（需要开发者模式或管理员权限）。");
            return;
        }

        Assert.IsTrue(Directory.Exists(junction), "Junction 应已创建。");

        // 名义上在 sandbox 内，实际会落到 outside —— 前缀比较通过，重解析点检查必须拦住。
        var escaped = Path.Combine(junction, "malicious.dll");
        Assert.AreEqual(PathContainment.ReparsePoint, PathSafety.Classify(sandbox, escaped));
        Assert.IsFalse(PathSafety.IsContained(sandbox, escaped));
        Assert.ThrowsException<InvalidDataException>(() => PathSafety.ValidateContained(sandbox, escaped));

        // 链接本身也应当被识别为重解析点（删除实例目录时不能跟着它走）。
        Assert.AreEqual(PathContainment.ReparsePoint, PathSafety.Classify(sandbox, junction));
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit(5000);
        return process.ExitCode == 0 && Directory.Exists(junctionPath);
    }
}
