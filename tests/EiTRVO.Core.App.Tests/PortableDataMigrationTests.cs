using System.IO;
using System.Text;
using EiTRVO.Core.App.Services;

namespace EiTRVO.Core.App.Tests;

/// <summary>数据目录改到启动器目录时的旧数据迁移（只复制、不删除）。</summary>
[TestClass]
public sealed class PortableDataMigrationTests
{
    private string _root = string.Empty;
    private string _legacy = string.Empty;
    private string _launcher = string.Empty;

    [TestInitialize]
    public void CreateDirectories()
    {
        _root = Path.Combine(Path.GetTempPath(), "eitrvo-portable", Guid.NewGuid().ToString("N"));
        _legacy = Path.Combine(_root, "legacy");
        _launcher = Path.Combine(_root, "launcher");
        Directory.CreateDirectory(_legacy);
        Directory.CreateDirectory(_launcher);
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void LegacyDataIsCopiedOnceAndTheOldDirectoryIsKept()
    {
        File.WriteAllText(Path.Combine(_legacy, "settings.json"), "{}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_legacy, "accounts.json"), "{}", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(_legacy, "instances", "1.20.4-Forge"));
        File.WriteAllText(Path.Combine(_legacy, "instances", "1.20.4-Forge", "instance.json"), "{}", new UTF8Encoding(false));

        Assert.IsTrue(PortableDataMigration.TryMigrate(_legacy, _launcher));

        Assert.IsTrue(File.Exists(Path.Combine(_launcher, "settings.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_launcher, "accounts.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_launcher, "instances", "1.20.4-Forge", "instance.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_legacy, "settings.json")), "旧目录是用户数据，迁移只复制不删除。");
        Assert.IsFalse(PortableDataMigration.TryMigrate(_legacy, _launcher), "同一台机器只迁移一次。");
    }

    [TestMethod]
    public void ALauncherDirectoryThatAlreadyHasDataIsNeverOverwritten()
    {
        File.WriteAllText(Path.Combine(_legacy, "settings.json"), "{\"legacy\":true}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_launcher, "settings.json"), "{\"new\":true}", new UTF8Encoding(false));

        Assert.IsFalse(PortableDataMigration.TryMigrate(_legacy, _launcher));
        StringAssert.Contains(File.ReadAllText(Path.Combine(_launcher, "settings.json")), "\"new\":true");
    }
}
