using EiTRVO.Core.Launcher.Installer;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// 安装完成后把共享版本**展开成自包含版本**写进实例目录，并清理不再被引用的共享目录。
/// <para>
/// 这一步是"装完却启动不了 / 实例一直显示未安装"的分界线：启动链只认实例目录里的
/// <c>version.json</c>，镜像少一样（JSON / JAR / natives）都会在启动前校验里暴露出来；
/// 而共享目录如果还留着，又会出现"一个实例三个文件夹"。
/// </para>
/// </summary>
[TestClass]
public sealed class InstalledVersionMirrorTests
{
    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;

    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-mirror", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDirectory);
    }

    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public void MirrorCopiesMergedVersionJsonJarAndNativesIntoTheInstanceDirectory()
    {
        var source = Path.Combine(_gameDirectory, "versions", "1.20.4-forge-49.0.30");
        Directory.CreateDirectory(Path.Combine(source, "natives"));
        File.WriteAllText(Path.Combine(source, "1.20.4-forge-49.0.30.json"), "{\"id\":\"x\"}");
        File.WriteAllText(Path.Combine(source, "1.20.4-forge-49.0.30.jar"), "jar");
        File.WriteAllText(Path.Combine(source, "natives", "lwjgl.dll"), "dll");

        InstalledVersionMirror.Mirror(_gameDirectory, "1.20.4-forge-49.0.30", "我的实例");

        var target = Path.Combine(_gameDirectory, "versions", "我的实例");
        var json = File.ReadAllText(Path.Combine(target, "version.json"));

        StringAssert.Contains(json, "\"id\": \"我的实例\"");
        Assert.IsFalse(json.Contains("inheritsFrom", StringComparison.Ordinal), "实例版本必须自包含。");
        Assert.AreEqual(json, File.ReadAllText(Path.Combine(target, "我的实例.json")));
        Assert.AreEqual("jar", File.ReadAllText(Path.Combine(target, "我的实例.jar")));
        Assert.AreEqual("dll", File.ReadAllText(Path.Combine(target, "natives", "lwjgl.dll")));
    }

    [TestMethod]
    public void MirrorRemovesTheSharedVersionDirectoryWhenNoOneElseUsesIt()
    {
        var source = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "1.20.4.json"), "{}");
        File.WriteAllText(Path.Combine(source, "1.20.4.jar"), "jar");

        InstalledVersionMirror.Mirror(_gameDirectory, "1.20.4", "a");

        Assert.IsFalse(Directory.Exists(source), "没有其它实例引用时，共享版本目录要被删掉。");
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "a", "a.jar")));
    }

    [TestMethod]
    public void MirrorKeepsTheSharedVersionDirectoryWhenALegacyInstanceStillNeedsIt()
    {
        // 旧实例：只有 instance.json，没有 version.json；它按游戏版本去共享目录找。
        var legacy = Path.Combine(_gameDirectory, "versions", "旧实例");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(
            Path.Combine(legacy, "instance.json"),
            "{\"gameVersion\":\"1.20.4\",\"loader\":\"Vanilla\",\"loaderVersion\":null}");

        var source = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "1.20.4.json"), "{}");
        File.WriteAllText(Path.Combine(source, "1.20.4.jar"), "jar");

        InstalledVersionMirror.Mirror(_gameDirectory, "1.20.4", "新实例");

        Assert.IsTrue(File.Exists(Path.Combine(source, "1.20.4.json")), "旧实例还指着它，不能删。");
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "新实例", "version.json")));
    }

    [TestMethod]
    public void ASelfContainedLegacyForgeVersionStillRemovesTheSharedVanillaFolder()
    {
        // 旧版 Forge 安装路径产出的版本 JSON 已经自包含（没有 inheritsFrom）；
        // 原版目录只能从 instance.json 的 gameVersion 找到，必须一并清理。
        var instance = Path.Combine(_gameDirectory, "versions", "1.12.2-Forge");
        Directory.CreateDirectory(instance);
        File.WriteAllText(
            Path.Combine(instance, "instance.json"),
            "{\"gameVersion\":\"1.12.2\",\"loader\":\"Forge\",\"loaderVersion\":\"14.23.5.2864\"}");

        var forge = Path.Combine(_gameDirectory, "versions", "1.12.2-forge-14.23.5.2864");
        Directory.CreateDirectory(forge);
        File.WriteAllText(Path.Combine(forge, "1.12.2-forge-14.23.5.2864.json"), "{\"id\":\"1.12.2-forge-14.23.5.2864\"}");

        var vanilla = Path.Combine(_gameDirectory, "versions", "1.12.2");
        Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(vanilla, "1.12.2.json"), "{\"id\":\"1.12.2\"}");
        File.WriteAllText(Path.Combine(vanilla, "1.12.2.jar"), "client");

        InstalledVersionMirror.Mirror(_gameDirectory, "1.12.2-forge-14.23.5.2864", "1.12.2-Forge");

        Assert.IsTrue(File.Exists(Path.Combine(instance, "version.json")));
        Assert.IsTrue(File.Exists(Path.Combine(instance, "1.12.2-Forge.jar")));
        Assert.IsFalse(Directory.Exists(vanilla), "实例版本自包含后，共享原版目录也要删掉。");
        Assert.IsFalse(Directory.Exists(forge), "加载器共享目录要清掉。");
    }

    [TestMethod]
    public void MigratingALegacyForgeInstanceLeavesOnlyItsOwnFolder()
    {
        // 旧布局：实例目录只有 instance.json；版本 JSON / JAR 在共享的 Forge 与 vanilla 目录里。
        var instance = Path.Combine(_gameDirectory, "versions", "1.12.2-Forge");
        Directory.CreateDirectory(instance);
        File.WriteAllText(
            Path.Combine(instance, "instance.json"),
            "{\"gameVersion\":\"1.12.2\",\"loader\":\"Forge\",\"loaderVersion\":\"14.23.5.2864\"}");

        var forge = Path.Combine(_gameDirectory, "versions", "1.12.2-forge-14.23.5.2864");
        Directory.CreateDirectory(forge);
        File.WriteAllText(
            Path.Combine(forge, "1.12.2-forge-14.23.5.2864.json"),
            "{\"id\":\"1.12.2-forge-14.23.5.2864\",\"inheritsFrom\":\"1.12.2\"}");

        var vanilla = Path.Combine(_gameDirectory, "versions", "1.12.2");
        Directory.CreateDirectory(Path.Combine(vanilla, "natives"));
        File.WriteAllText(Path.Combine(vanilla, "1.12.2.json"), "{\"id\":\"1.12.2\"}");
        File.WriteAllText(Path.Combine(vanilla, "1.12.2.jar"), "client");
        File.WriteAllText(Path.Combine(vanilla, "natives", "lwjgl64.dll"), "native");

        InstalledVersionMirror.MigrateExistingInstances(_gameDirectory);

        Assert.IsTrue(File.Exists(Path.Combine(instance, "version.json")));
        Assert.IsTrue(File.Exists(Path.Combine(instance, "1.12.2-Forge.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(instance, "natives", "lwjgl64.dll")));

        var json = File.ReadAllText(Path.Combine(instance, "version.json"));
        StringAssert.Contains(json, "\"id\": \"1.12.2-Forge\"");
        Assert.IsFalse(json.Contains("inheritsFrom", StringComparison.Ordinal));

        Assert.IsFalse(Directory.Exists(forge), "加载器共享目录要清掉。");
        Assert.IsFalse(Directory.Exists(vanilla), "原版共享目录也要清掉。");
    }

    [TestMethod]
    public void MigrationCanResolveAParentFromTheVersionJsonCache()
    {
        // 1.19.4 的共享版本目录实际只剩 JAR，父 JSON 在 cache\versions 里。
        var instance = Path.Combine(_gameDirectory, "versions", "1.19.4-Forge");
        Directory.CreateDirectory(instance);
        File.WriteAllText(
            Path.Combine(instance, "instance.json"),
            "{\"gameVersion\":\"1.19.4\",\"loader\":\"Forge\",\"loaderVersion\":\"45.4.5\"}");
        File.WriteAllText(
            Path.Combine(instance, "version.json"),
            "{\"id\":\"1.19.4-forge-45.4.5\",\"inheritsFrom\":\"1.19.4\",\"mainClass\":\"forge.Main\"}");

        var cache = Path.Combine(_gameDirectory, "cache", "versions");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "1.19.4.json"), "{\"id\":\"1.19.4\",\"mainClass\":\"net.minecraft.client.main.Main\"}");

        InstalledVersionMirror.MigrateExistingInstances(_gameDirectory);

        var json = File.ReadAllText(Path.Combine(instance, "version.json"));
        Assert.IsFalse(json.Contains("inheritsFrom", StringComparison.Ordinal));
        StringAssert.Contains(json, "\"id\": \"1.19.4-Forge\"");
    }

    [TestMethod]
    public void SharedParentWithoutItsJsonIsKeptWhenAnInstanceStillDeclaresIt()
    {
        // 父版本目录可能只剩 JAR（JSON 在 cache 里）；迁移失败时它仍是实例声明的依赖，不能被清理扫掉。
        var instance = Path.Combine(_gameDirectory, "versions", "1.19.4-Forge");
        Directory.CreateDirectory(instance);
        File.WriteAllText(
            Path.Combine(instance, "instance.json"),
            "{\"gameVersion\":\"1.19.4\",\"loader\":\"Forge\",\"loaderVersion\":\"45.4.5\"}");
        File.WriteAllText(
            Path.Combine(instance, "version.json"),
            "{\"id\":\"1.19.4-forge-45.4.5\",\"inheritsFrom\":\"1.19.4\"}");

        var vanilla = Path.Combine(_gameDirectory, "versions", "1.19.4");
        Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(vanilla, "1.19.4.jar"), "client");

        InstalledVersionMirror.MigrateExistingInstances(_gameDirectory);

        Assert.IsTrue(Directory.Exists(vanilla), "实例还声明着这个父版本，不能因为 JSON 在缓存里就删除它。");
    }

    [TestMethod]
    public void MirrorAcceptsANeoStyleSourceWithOnlyVersionJson()
    {
        var source = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "version.json"), "{}");

        InstalledVersionMirror.Mirror(_gameDirectory, "1.20.4", "a");

        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "versions", "a", "version.json")));
    }

    [TestMethod]
    public void MirrorFailsLoudlyWhenTheSharedVersionIsMissing()
    {
        Assert.ThrowsException<DirectoryNotFoundException>(
            () => InstalledVersionMirror.Mirror(_gameDirectory, "1.20.4", "a"));
    }

    [TestMethod]
    public void MirrorFailsLoudlyWhenThereIsNoVersionJson()
    {
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "versions", "1.20.4"));

        Assert.ThrowsException<FileNotFoundException>(
            () => InstalledVersionMirror.Mirror(_gameDirectory, "1.20.4", "a"));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("a/b")]
    [DataRow("CON")]
    [DataRow("")]
    public void MirrorRejectsUnsafeNames(string name)
    {
        var source = Path.Combine(_gameDirectory, "versions", "1.20.4");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "1.20.4.json"), "{}");

        Assert.ThrowsException<ArgumentException>(
            () => InstalledVersionMirror.Mirror(_gameDirectory, "1.20.4", name));
        Assert.ThrowsException<ArgumentException>(
            () => InstalledVersionMirror.Mirror(_gameDirectory, name, "a"));
    }
}
