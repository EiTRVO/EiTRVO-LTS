using System.Text;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-h：安装事务（装之前拍快照，失败就回到装之前）。
/// <para>
/// 这一层的关键不是"能删文件"，而是**只删这次新增的**：用户手放进 <c>libraries</c> 的东西、
/// 装前就存在的版本 JSON，一个都不许动。三条边界各有用例：新增的删掉、原有的留着、
/// 被官方安装器改写的那个文件恢复回去。
/// </para>
/// </summary>
[TestClass]
public sealed class InstallTransactionTests
{
    private string _root = string.Empty;
    private string _game = string.Empty;

    /// <summary>每个用例一套全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "eitrvo-core-tx", Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(_game);
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void RollbackRemovesWhatThisInstallCreatedAndKeepsWhatWasAlreadyThere()
    {
        // 装前就有的东西：一个库、一个版本 JSON、一个用户自己的目录。
        Write("libraries/com/mojang/authlib/6.0.54/authlib-6.0.54.jar", "old");
        Write("versions/1.20.4/1.20.4.json", "{}");
        Write("versions/1.20.4/natives/keep.dll", "keep");

        var transaction = InstallTransaction.Begin(_game);

        // 这次安装新增的东西：客户端 JAR、加载器库、资源对象、一个新的版本目录。
        Write("versions/1.20.4/1.20.4.jar", "half");
        Write("libraries/net/fabricmc/fabric-loader/0.15.7/fabric-loader-0.15.7.jar", "new");
        Write("assets/objects/ab/abcdef", "asset");
        Write("versions/fabric-loader-0.15.7-1.20.4/fabric-loader-0.15.7-1.20.4.json", "{}");

        var result = transaction.Rollback();

        Assert.IsTrue(result.IsClean);
        Assert.AreEqual(4, result.RemovedFiles);
        Assert.IsTrue(result.RemovedDirectories > 0, "新建的空目录也要清掉。");

        Assert.IsFalse(File.Exists(Path.Combine(_game, "versions/1.20.4/1.20.4.jar")), "半成品必须清掉。");
        Assert.IsFalse(Directory.Exists(Path.Combine(_game, "libraries/net")), "这次新建的库目录要清掉。");
        Assert.IsFalse(Directory.Exists(Path.Combine(_game, "assets")), "整棵树都是这次建的。");
        Assert.IsFalse(Directory.Exists(Path.Combine(_game, "versions/fabric-loader-0.15.7-1.20.4")));

        Assert.IsTrue(File.Exists(Path.Combine(_game, "libraries/com/mojang/authlib/6.0.54/authlib-6.0.54.jar")), "装前就有的库不许动。");
        Assert.IsTrue(File.Exists(Path.Combine(_game, "versions/1.20.4/1.20.4.json")), "装前就有的版本 JSON 不许动。");
        Assert.IsTrue(File.Exists(Path.Combine(_game, "versions/1.20.4/natives/keep.dll")), "装前就有的 natives 不许动。");
    }

    [TestMethod]
    public void CommitKeepsEverythingAndEndsTheTransaction()
    {
        var transaction = InstallTransaction.Begin(_game);
        Write("libraries/a/b/c.jar", "x");

        transaction.Commit();

        Assert.IsTrue(File.Exists(Path.Combine(_game, "libraries/a/b/c.jar")));
        Assert.ThrowsException<InvalidOperationException>(() => transaction.Rollback());
    }

    [TestMethod]
    public void ALauncherProfilesFileWeHadToCreateIsRemovedAgain()
    {
        // 官方安装器要求这个文件存在；装失败时，为了满足它而补的那个也不该留下。
        var transaction = InstallTransaction.Begin(_game);

        Write(InstallTransaction.LauncherProfilesFileName, "{\"profiles\":{}}");

        var result = transaction.Rollback();

        Assert.AreEqual(1, result.RemovedFiles);
        Assert.IsFalse(File.Exists(Path.Combine(_game, InstallTransaction.LauncherProfilesFileName)));
    }

    [TestMethod]
    public void ALauncherProfilesFileThatAlreadyExistedIsRestoredByteForByte()
    {
        // 官方安装器会往里面注入 profile —— 装失败就得把用户原来那一份还回去。
        var path = Path.Combine(_game, InstallTransaction.LauncherProfilesFileName);
        var original = "{\"profiles\":{\"我的档案\":{\"name\":\"我的档案\"}}}";
        File.WriteAllText(path, original, new UTF8Encoding(false));

        var transaction = InstallTransaction.Begin(_game);

        File.WriteAllText(path, "{\"profiles\":{\"我的档案\":{},\"forge\":{}}}", new UTF8Encoding(false));

        var result = transaction.Rollback();

        Assert.AreEqual(1, result.RestoredFiles);
        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void RollbackIsBestEffortAndCountsWhatItCouldNotRemove()
    {
        var transaction = InstallTransaction.Begin(_game);
        Write("libraries/locked.jar", "x");

        var locked = Path.Combine(_game, "libraries/locked.jar");

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = transaction.Rollback();

            Assert.IsFalse(result.IsClean, "删不掉的项要如实计数，而不是假装干净。");
            Assert.AreEqual(1, result.FailedItems);
            StringAssert.Contains(TestText.Of(InstallTransaction.Describe(result)), "没删掉");
        }

        Assert.IsTrue(File.Exists(locked), "被占用的文件当然还在（我们没有魔法）。");
    }

    [TestMethod]
    public void NothingToUndoMeansNothingToSay()
    {
        var transaction = InstallTransaction.Begin(_game);

        var result = transaction.Rollback();

        Assert.AreEqual(0, result.Total);
        Assert.IsTrue(result.IsClean);
        Assert.IsNull(InstallTransaction.Describe(result), "什么都没做就别在失败原因里加废话。");
    }

    [TestMethod]
    public void BeginningAgainstAMissingGameDirectoryIsHarmless()
    {
        var game = Path.Combine(_root, "还没有这个目录");
        var transaction = InstallTransaction.Begin(game);

        var result = transaction.Rollback();

        Assert.AreEqual(0, result.Total);
        Assert.IsFalse(Directory.Exists(game), "快照不该顺手把目录建出来。");
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_game, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
