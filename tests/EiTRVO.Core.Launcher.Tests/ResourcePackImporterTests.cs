using System.IO.Compression;
using System.Text;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>p09「添加文件」：本地资源包导入 + zip slip / 压缩炸弹检查。</summary>
[TestClass]
public sealed class ResourcePackImporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 13, 30, 0, TimeSpan.FromHours(8));

    private string _root = null!;
    private string _game = null!;
    private FileInstanceCatalog _instances = null!;
    private ResourcePackImporter _importer = null!;

    [TestInitialize]
    public void CreateTempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "eitrvo-core-resourcepack", Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(_game);

        var paths = new LauncherPaths(_root);
        var diagnostics = new RecordingDiagnosticsLog();
        var settings = new FileSettingsCatalog(paths, diagnostics);
        settings.SaveGlobal(SettingsDefaults.CreateGlobal() with { GameDirectory = _game });

        _instances = new FileInstanceCatalog(paths, settings, new FixedClock(Now), diagnostics);
        _instances.CreateInstance(new InstanceCreationRequest("Test", "1.20.4", LoaderKind.Vanilla));
        _importer = new ResourcePackImporter(_instances);
    }

    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AValidPackIsCopiedIntoTheInstanceResourcepacksFolder()
    {
        var source = WriteZip(
            "valid.zip",
            ("pack.mcmeta", "{\"pack\":{\"pack_format\":15,\"description\":\"test\"}}"),
            ("assets/", string.Empty),
            ("assets/test.txt", "hello"));

        var result = await _importer.ImportAsync("Test", source);

        Assert.IsTrue(result.Succeeded, result.Message?.Key);
        var destination = Path.Combine(_game, "versions", "Test", "resourcepacks", "valid.zip");
        Assert.IsTrue(File.Exists(destination));
        CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(destination));
    }

    [TestMethod]
    public async Task AZipSlipEntryIsRejectedBeforeCopying()
    {
        var source = WriteZip(
            "slip.zip",
            ("pack.mcmeta", "{\"pack\":{\"pack_format\":15}}"),
            ("../evil.txt", "escaped"));

        var result = await _importer.ImportAsync("Test", source);

        Assert.IsFalse(result.Succeeded);
        var destination = Path.Combine(_game, "versions", "Test", "resourcepacks", "slip.zip");
        Assert.IsFalse(File.Exists(destination));
        Assert.IsFalse(File.Exists(Path.Combine(_game, "versions", "evil.txt")));
    }

    [TestMethod]
    public async Task AHighCompressionRatioEntryIsRejectedAsABomb()
    {
        var source = Path.Combine(_root, "bomb.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "pack.mcmeta", "{\"pack\":{\"pack_format\":15}}");
            WriteBytes(archive, "big.bin", new byte[4 * 1024 * 1024]);
        }

        var result = await _importer.ImportAsync("Test", source);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(File.Exists(Path.Combine(_game, "versions", "Test", "resourcepacks", "bomb.zip")));
    }

    [TestMethod]
    public async Task APackWithoutPackMetaIsRejected()
    {
        var source = WriteZip("no-meta.zip", ("assets/test.txt", "hello"));

        var result = await _importer.ImportAsync("Test", source);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(File.Exists(Path.Combine(_game, "versions", "Test", "resourcepacks", "no-meta.zip")));
    }

    [TestMethod]
    public async Task AnExistingFileWithTheSameNameIsNotOverwritten()
    {
        var source = WriteZip("same.zip", ("pack.mcmeta", "{\"pack\":{\"pack_format\":15}}"));
        var directory = Path.Combine(_game, "versions", "Test", "resourcepacks");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "same.zip");
        File.WriteAllText(destination, "keep me");

        var result = await _importer.ImportAsync("Test", source);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("keep me", File.ReadAllText(destination));
    }

    private string WriteZip(string name, params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            WriteEntry(archive, entry.Name, entry.Text);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string text) =>
        WriteBytes(archive, name, Encoding.UTF8.GetBytes(text));

    private static void WriteBytes(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
