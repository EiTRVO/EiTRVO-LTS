using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Content.Modpack;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M7-e：<c>.mrpack</c> 的读取、安全扫描与导入。
/// <para>
/// 这一组用例的分量在**安全**与**落盘事实**上：两条路径穿越面（清单的 <c>files[].path</c> 与
/// zip 条目名）各要有反例、下载源白名单要真的拦、压缩比异常要拦、sha1 与 sha512 **两个都**要校验、
/// 而导入完成之后"每个文件都在且哈希与清单一致"要能逐步核对（导入 → 复核 = 规范要求的往返）。
/// </para>
/// <para>
/// 网络是桩、文件是真文件：这一层的价值恰恰在于"磁盘上的东西对不对"。
/// </para>
/// </summary>
[TestClass]
public sealed class MrpackImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 22, 30, 0, TimeSpan.FromHours(8));

    private string _tempRoot = string.Empty;
    private string _gameDirectory = string.Empty;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一套全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-mrpack", Guid.NewGuid().ToString("N"));
        _gameDirectory = Path.Combine(_tempRoot, "instance");
        Directory.CreateDirectory(_gameDirectory);
        _diagnostics = new RecordingDiagnosticsLog();
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    // ============================================================ 清单解析 ===

    [TestMethod]
    public void TheIndexIsReadWithItsDependenciesAndClientSupport()
    {
        var index = MrpackReader.Parse("""
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "1.2.0",
              "name": "Better MC",
              "summary": "任务线 + 性能优化",
              "files": [
                { "path": "mods\\foo.jar", "hashes": { "sha1": "aa", "sha512": "bb" }, "downloads": ["https://cdn.modrinth.com/x"], "fileSize": 42 },
                { "path": "mods/server-only.jar", "hashes": { "sha1": "cc", "sha512": "dd" }, "downloads": ["https://cdn.modrinth.com/y"], "env": { "client": "unsupported", "server": "required" } },
                { "path": "config/x.toml", "hashes": { "sha1": "ee", "sha512": "ff" }, "downloads": ["https://cdn.modrinth.com/z"], "env": { "client": "optional" } }
              ],
              "dependencies": { "minecraft": "1.20.4", "forge": "49.0.30", "optifine": "HD_U_I8" }
            }
            """);

        Assert.AreEqual(1, index.FormatVersion);
        Assert.AreEqual("Better MC", index.Name);
        Assert.AreEqual("1.20.4", index.GameVersion);
        Assert.AreEqual(LoaderKind.Forge, index.Loader);
        Assert.AreEqual("49.0.30", index.LoaderVersion);

        // 未知依赖键**容忍**（规范：随时可能加新的），但要如实收集起来。
        CollectionAssert.AreEqual(new[] { "optifine=HD_U_I8" }, index.UnknownDependencies.ToArray());

        var foo = index.Files.Single(static file => file.Path == "mods/foo.jar");

        Assert.AreEqual("aa", foo.Sha1);
        Assert.AreEqual("bb", foo.Sha512);
        Assert.AreEqual(42, foo.Size);
        Assert.IsTrue(foo.ClientSupported, "没有 env 的文件照装。");
        CollectionAssert.AreEqual(new[] { "mods", "foo.jar" }, foo.Segments.ToArray());

        // `env.client = "unsupported"` 是服务端专用的文件：客户端不装它。
        Assert.IsFalse(index.Files.Single(static file => file.Path == "mods/server-only.jar").ClientSupported);
        Assert.IsTrue(index.Files.Single(static file => file.Path == "config/x.toml").ClientSupported, "optional 也要装（没有对话框问用户时，少装一个模组更偏离作者意图）。");
    }

    [TestMethod]
    public void AnUnsupportedFormatVersionIsRefused()
    {
        var exception = Assert.ThrowsException<ModpackException>(() => MrpackReader.Parse("""
            { "formatVersion": 2, "game": "minecraft", "files": [], "dependencies": {} }
            """));

        Assert.AreEqual("这个整合包的格式版本是 {0}，本启动器只支持 {1}。", exception.Reason.Key);
        CollectionAssert.AreEqual(new[] { "2", "1" }, exception.Reason.Parameters.ToArray());
    }

    [TestMethod]
    public void APackForAnotherGameIsRefused()
    {
        var exception = Assert.ThrowsException<ModpackException>(() => MrpackReader.Parse("""
            { "formatVersion": 1, "game": "terraria", "files": [], "dependencies": {} }
            """));

        Assert.AreEqual("这个整合包是给 {0} 做的，不是 Minecraft。", exception.Reason.Key);
    }

    [TestMethod]
    public void APathThatEscapesTheInstanceDirectoryIsRefused()
    {
        // **规范点名的攻击面①**：files[].path 不能越出实例目录。
        foreach (var evil in new[] { "../evil.jar", "mods/../../evil.jar", @"..\evil.jar", "C:/evil.jar", "/etc/evil.jar" })
        {
            var exception = Assert.ThrowsException<ModpackException>(() => MrpackReader.Parse(Index(evil)), evil);
            Assert.AreEqual("清单里的文件路径越出了实例目录（{0}）。", exception.Reason.Key, evil);
        }
    }

    [TestMethod]
    public void APathWithoutBothHashesIsRefused()
    {
        var exception = Assert.ThrowsException<ModpackException>(() => MrpackReader.Parse("""
            { "formatVersion": 1, "game": "minecraft", "dependencies": {},
              "files": [ { "path": "mods/foo.jar", "hashes": { "sha1": "aa" }, "downloads": ["https://cdn.modrinth.com/x"] } ] }
            """));

        Assert.AreEqual("清单里的 {0} 缺少 SHA-1 或 SHA-512 校验值，无法校验下载内容。", exception.Reason.Key);
    }

    [TestMethod]
    public void APackThatDeclaresTwoLoadersIsRefused()
    {
        var exception = Assert.ThrowsException<ModpackException>(() => MrpackReader.Parse("""
            { "formatVersion": 1, "game": "minecraft", "files": [],
              "dependencies": { "minecraft": "1.20.4", "forge": "49.0.30", "fabric-loader": "0.15.7" } }
            """));

        Assert.AreEqual("这个整合包同时声明了多个加载器（{0}），无法安装。", exception.Reason.Key);
    }

    [TestMethod]
    public void TheSamePathTwiceIsRefused()
    {
        var exception = Assert.ThrowsException<ModpackException>(() => MrpackReader.Parse("""
            { "formatVersion": 1, "game": "minecraft", "dependencies": {},
              "files": [
                { "path": "mods/foo.jar", "hashes": { "sha1": "aa", "sha512": "bb" }, "downloads": ["https://cdn.modrinth.com/x"] },
                { "path": "mods/FOO.jar", "hashes": { "sha1": "cc", "sha512": "dd" }, "downloads": ["https://cdn.modrinth.com/y"] }
              ] }
            """));

        Assert.AreEqual("清单里同一个路径出现了两次（{0}）。", exception.Reason.Key);
    }

    // ============================================================ 安全扫描 ===

    [TestMethod]
    public void TheDownloadSourcesMustBeHttpsAndWhitelisted()
    {
        var json = Index("mods/foo.jar", "https://evil.example.com/foo.jar");
        var report = ScanPack(WriteMrpack(json, []), json);

        Assert.IsTrue(report.HasFindings);
        Assert.AreEqual("清单里 {0} 的下载源不在允许的域名内（{1}）。", report.Findings[0].Key);
        CollectionAssert.AreEqual(new[] { "mods/foo.jar", "evil.example.com" }, report.Findings[0].Parameters.ToArray());

        var insecureJson = Index("mods/foo.jar", "http://cdn.modrinth.com/foo.jar");
        var insecure = ScanPack(WriteMrpack(insecureJson, []), insecureJson);

        Assert.AreEqual("清单里 {0} 的下载地址不是 https（{1}）。", insecure.Findings[0].Key);
    }

    [TestMethod]
    public void TheHostsTheSpecAllowsAreAccepted()
    {
        // 规范列出的四个域名（加 GitHub 的资源主机，因为发布包会 302 到那里）。
        foreach (var host in new[] { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" })
        {
            var json = Index("mods/foo.jar", "https://" + host + "/x/foo.jar");
            var report = ScanPack(WriteMrpack(json, []), json);

            Assert.AreEqual(0, report.Findings.Count, host + "：" + string.Join(" / ", report.Findings.Select(static f => f.Key)));
        }
    }

    [TestMethod]
    public void AnExecutableInTheOverridesIsBlocked()
    {
        var json = Index("mods/foo.jar");
        var pack = WriteMrpack(
            json,
            [("overrides/tools/run.exe", "MZ"), ("client-overrides/mods/ok.jar", "jar")]);

        var report = ScanPack(pack, json);

        Assert.AreEqual(1, report.Findings.Count);
        Assert.AreEqual("压缩包里有不允许的文件类型（{0}：{1}）。", report.Findings[0].Key);
        CollectionAssert.AreEqual(new[] { ".exe", "overrides/tools/run.exe" }, report.Findings[0].Parameters.ToArray());
    }

    [TestMethod]
    public void AZipEntryThatEscapesTheInstanceDirectoryIsBlocked()
    {
        // **规范点名的攻击面②**：zip 条目名（覆盖层里的路径）。
        var json = Index("mods/foo.jar");
        var pack = WriteMrpack(json, [(@"overrides/../../escaped.txt", "boom")]);

        var report = ScanPack(pack, json);

        Assert.IsTrue(report.HasFindings);
        Assert.AreEqual("压缩包里的条目路径越出了实例目录（{0}）。", report.Findings[0].Key);
    }

    [TestMethod]
    public void ASuspiciousCompressionRatioIsBlocked()
    {
        // 20 MB 的零字节：压缩比远高于上限（zip 炸弹的判据是"解压后很大而压缩后很小"）。
        var json = Index("mods/foo.jar");
        var pack = WriteMrpack(json, [("overrides/big.bin", new string('\0', 20 * 1024 * 1024))]);

        var report = ScanPack(pack, json);

        Assert.IsTrue(report.HasFindings);
        Assert.AreEqual("压缩包里的 {0} 压缩比异常（{1}:1），看起来像 zip 炸弹。", report.Findings[0].Key);
    }

    [TestMethod]
    public void AFileTheClientNeverInstallsIsNotCheckedForItsSource()
    {
        // 客户端不装的文件不该拿它的下载源把整个导入拦下来。
        var json = """
            { "formatVersion": 1, "game": "minecraft", "dependencies": {},
              "files": [ { "path": "mods/server.jar", "hashes": { "sha1": "aa", "sha512": "bb" },
                           "downloads": ["https://evil.example.com/server.jar"], "env": { "client": "unsupported" } } ] }
            """;

        Assert.AreEqual(0, ScanPack(WriteMrpack(json, []), json).Findings.Count);
    }

    // ============================================================ 往返导入 ===

    [TestMethod]
    public async Task ARoundTripMaterializesEveryFileAndItsHashesMatchTheIndex()
    {
        var modBytes = Encoding.UTF8.GetBytes("mod jar bytes");
        var libBytes = Encoding.UTF8.GetBytes("library bytes");

        var handler = new StubHandler()
            .RespondBytesTo("cdn.modrinth.com/data/MOD/foo.jar", modBytes)
            .RespondBytesTo("cdn.modrinth.com/data/LIB/lib.jar", libBytes);

        var pack = WriteMrpack(
            Index(
                ("mods/foo.jar", "https://cdn.modrinth.com/data/MOD/foo.jar", modBytes),
                ("mods/lib.jar", "https://cdn.modrinth.com/data/LIB/lib.jar", libBytes)),
            [
                ("overrides/config/a.toml", "a = 1"),
                ("overrides/options.txt", "lang:zh_CN"),
                ("client-overrides/options.txt", "lang:en_US"),
            ]);

        var result = await NewInstaller(handler).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsTrue(result.Succeeded, Why(result));
        Assert.AreEqual(2, result.FilesDownloaded);
        Assert.AreEqual(3, result.NativeFilesExtracted, "覆盖层写入的文件数（含被后一层覆盖的那个）。");

        // ① 清单里的每个文件都在，而且**内容与清单声明的哈希一致**（这就是"往返一次"的判据）。
        Assert.AreEqual("mod jar bytes", File.ReadAllText(Path.Combine(_gameDirectory, "mods", "foo.jar")));
        Assert.AreEqual("library bytes", File.ReadAllText(Path.Combine(_gameDirectory, "mods", "lib.jar")));
        Assert.AreEqual(Sha1Hex(modBytes), Sha1Of(Path.Combine(_gameDirectory, "mods", "foo.jar")));
        Assert.AreEqual(Sha512Hex(libBytes), Sha512Of(Path.Combine(_gameDirectory, "mods", "lib.jar")));

        // ② 覆盖层按**顺序**叠加：client-overrides 覆盖 overrides 里的同名文件。
        Assert.AreEqual("a = 1", File.ReadAllText(Path.Combine(_gameDirectory, "config", "a.toml")));
        Assert.AreEqual("lang:en_US", File.ReadAllText(Path.Combine(_gameDirectory, "options.txt")));

        // ③ instance 目录之外一个字节都不该有。
        CollectionAssert.AreEqual(
            new[] { "instance" },
            Directory.GetDirectories(_tempRoot).Select(Path.GetFileName).ToArray());

        // ④ 再跑一次：两个文件都"已经正确"，于是全部跳过（断点续装的语义）。
        var again = await NewInstaller(handler).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsTrue(again.Succeeded, again.Failure?.Key);
        Assert.AreEqual(0, again.FilesDownloaded);
        Assert.AreEqual(2, again.FilesSkipped);
    }

    [TestMethod]
    public async Task AFilesSha512IsCheckedToo()
    {
        var bytes = Encoding.UTF8.GetBytes("the real bytes");

        var handler = new StubHandler().RespondBytesTo("cdn.modrinth.com/foo.jar", bytes);

        // sha1 是对的、sha512 是错的（清单被改过一个字段）：**必须失败** —— 只校验 sha1 的话
        // 这一份会静默通过，而规范要求两个都在。
        var pack = WriteMrpack(
            """
            { "formatVersion": 1, "game": "minecraft", "dependencies": {},
              "files": [ { "path": "mods/foo.jar", "hashes": { "__SHA1__": "x", "sha512": "deadbeef" },
                           "downloads": ["https://cdn.modrinth.com/foo.jar"] } ] }
            """.Replace("__SHA1__", "sha1", StringComparison.Ordinal)
                .Replace("\"x\"", "\"" + Sha1Hex(bytes) + "\"", StringComparison.Ordinal),
            []);

        var result = await NewInstaller(handler).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("{0} 个文件失败。", result.Failure!.Key);
        Assert.IsNotNull(result.Failure.Note);
        Assert.AreEqual("{0} 校验不通过：期望 {1}，实际 {2}", result.Failure.Note!.Key);
        Assert.IsFalse(File.Exists(Path.Combine(_gameDirectory, "mods", "foo.jar")));
    }

    [TestMethod]
    public async Task AFileWithoutADownloadAddressMustBeProvidedByTheOverrides()
    {
        var bytes = Encoding.UTF8.GetBytes("override content");

        var index = """
            { "formatVersion": 1, "game": "minecraft", "dependencies": {},
              "files": [ { "path": "config/pack.toml", "hashes": { "sha1": "aa", "sha512": "bb" }, "downloads": [] } ] }
            """;

        var withOverride = WriteMrpack(index, [("overrides/config/pack.toml", "override content")]);
        var ok = await NewInstaller(new StubHandler()).InstallAsync(new MrpackImportRequest(withOverride, _gameDirectory));

        Assert.IsTrue(ok.Succeeded, ok.Failure?.Key);
        Assert.AreEqual("override content", File.ReadAllText(Path.Combine(_gameDirectory, "config", "pack.toml")));
        Assert.AreEqual(Encoding.UTF8.GetByteCount("override content"), bytes.Length);

        var without = WriteMrpack(index, [("overrides/readme.txt", "nothing here")]);
        var missing = await NewInstaller(new StubHandler())
            .InstallAsync(new MrpackImportRequest(without, Path.Combine(_tempRoot, "instance2")));

        Assert.IsFalse(missing.Succeeded);
        Assert.AreEqual("清单里的 {0} 没有下载地址，覆盖层里也没有这个文件。", missing.Failure!.Key);
    }

    [TestMethod]
    public async Task AClientUnsupportedFileIsNotDownloadedAndDoesNotFailTheImport()
    {
        var bytes = Encoding.UTF8.GetBytes("client mod");
        var handler = new StubHandler().RespondBytesTo("cdn.modrinth.com/client.jar", bytes);

        var pack = WriteMrpack(
            """
            { "formatVersion": 1, "game": "minecraft", "dependencies": {},
              "files": [
                { "path": "mods/client.jar", "hashes": { "__SHA1__": "x", "sha512": "__SHA512__" }, "downloads": ["https://cdn.modrinth.com/client.jar"] },
                { "path": "mods/server.jar", "hashes": { "sha1": "aa", "sha512": "bb" },
                  "downloads": ["https://cdn.modrinth.com/server.jar"], "env": { "client": "unsupported", "server": "required" } }
              ] }
            """.Replace("__SHA1__", "sha1", StringComparison.Ordinal)
                .Replace("\"x\"", "\"" + Sha1Hex(bytes) + "\"", StringComparison.Ordinal)
                .Replace("__SHA512__", Sha512Hex(bytes), StringComparison.Ordinal),
            []);

        var result = await NewInstaller(handler).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.IsTrue(File.Exists(Path.Combine(_gameDirectory, "mods", "client.jar")));
        Assert.IsFalse(
            File.Exists(Path.Combine(_gameDirectory, "mods", "server.jar")),
            "服务端专用的文件不该出现在客户端实例里（规范原文）。");
        Assert.IsTrue(_diagnostics.Entries.Any(static entry => entry.MessageKey == "整合包里有 {0} 个文件标明客户端不装，已跳过。"));
    }

    [TestMethod]
    public async Task AStrayRootEntryIsIgnoredButCounted()
    {
        var bytes = Encoding.UTF8.GetBytes("mod");
        var handler = new StubHandler().RespondBytesTo("cdn.modrinth.com/foo.jar", bytes);

        var pack = WriteMrpack(
            """
            { "formatVersion": 1, "game": "minecraft", "dependencies": {},
              "files": [ { "path": "mods/foo.jar", "hashes": { "sha1": "__SHA1__", "sha512": "__SHA512__" }, "downloads": ["https://cdn.modrinth.com/foo.jar"] } ] }
            """.Replace("__SHA1__", Sha1Hex(bytes), StringComparison.Ordinal)
                .Replace("__SHA512__", Sha512Hex(bytes), StringComparison.Ordinal),
            [],
            extraEntries: [("README.md", "not a standard entry")]);

        var report = NewScanner().Scan(pack);

        Assert.AreEqual(0, report.Findings.Count);
        Assert.AreEqual(1, report.StrayRootEntries, "不在标准目录里的条目要**记账**（忽略但不能装作没看见）。");

        var result = await NewInstaller(handler).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.IsFalse(File.Exists(Path.Combine(_gameDirectory, "README.md")), "不在标准目录里的条目一律不落地。");
        Assert.IsTrue(_diagnostics.Entries.Any(static entry => entry.MessageKey == "整合包里有 {0} 个不在标准目录里的条目，已忽略。"));
    }

    [TestMethod]
    public async Task APackFullOfFindingsIsRefusedBeforeAnythingIsWritten()
    {
        var pack = WriteMrpack(Index("mods/foo.jar", "https://evil.example.com/foo.jar"), [("overrides/a.txt", "x")]);

        var result = await NewInstaller(new StubHandler()).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("整合包没有通过安全扫描（{0} 项）。", result.Failure!.Key);
        Assert.IsNotNull(result.Failure.Note);
        Assert.AreEqual("清单里 {0} 的下载源不在允许的域名内（{1}）。", result.Failure.Note!.Key);
        Assert.AreEqual(0, Directory.GetFiles(_gameDirectory, "*", SearchOption.AllDirectories).Length, "扫描没过就一个字节都不该写。");
    }

    [TestMethod]
    public async Task AnArchiveWithoutAnIndexIsRefused()
    {
        var pack = Path.Combine(_tempRoot, "not-a-pack.mrpack");

        using (var archive = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        var result = await NewInstaller(new StubHandler()).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("这不是一个整合包：压缩包里没有 {0}。", result.Failure!.Key);
        CollectionAssert.AreEqual(new[] { MrpackReader.IndexEntryName }, result.Failure.Parameters.ToArray());
    }

    [TestMethod]
    public async Task ThePackNameBecomesTheTaskSubject()
    {
        var handler = new StubHandler();
        var pack = WriteMrpack("""
            { "formatVersion": 1, "game": "minecraft", "name": "Better MC [FORGE] 1.20.4", "dependencies": { "minecraft": "1.20.4", "forge": "49.0.30" }, "files": [] }
            """, []);

        var result = await NewInstaller(handler).InstallAsync(new MrpackImportRequest(pack, _gameDirectory));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.AreEqual("Better MC [FORGE] 1.20.4", result.VersionId);
    }

    // ================================================================ 辅助 ===

    private static ArchiveSafetyScanner NewScanner() => new(HttpHostPolicy.Default);

    /// <summary>扫描一个包（连同它的清单：下载源白名单那一项只在给了清单时才查）。</summary>
    private static ArchiveSafetyReport ScanPack(string archivePath, string indexJson) =>
        NewScanner().Scan(archivePath, MrpackReader.Parse(indexJson));

    /// <summary>把一个值安全地放进 JSON 字符串（路径里可能带反斜杠 —— 那正是要测的输入）。</summary>
    private static string Esc(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>失败时把**参数**也打出来（只报键的话永远看不出是哪一步坏了）。</summary>
    private static string Why(InstallResult result) =>
        result.Failure is { } failure
            ? failure.Key + " :: " + string.Join(" | ", failure.Parameters)
            : "（没有失败原因）";

    private MrpackInstaller NewInstaller(StubHandler handler)
    {
        var factory = new FakeHttpClientFactory(handler);
        var downloader = new FileDownloader(
            factory,
            HttpHostPolicy.Default,
            new FixedClock(Now),
            _diagnostics,
            static (_, _) => Task.CompletedTask);

        return new MrpackInstaller(
            new ArchiveSafetyScanner(HttpHostPolicy.Default),
            downloader,
            // 磁盘预检用固定值：真实可用空间在测试运行期间会变。
            new DiskSpaceChecker(_diagnostics, static _ => long.MaxValue),
            _diagnostics);
    }

    /// <summary>只有一个文件的清单（路径与下载地址可指定）。</summary>
    private static string Index(string path, string download = "https://cdn.modrinth.com/x/foo.jar") =>
        $$"""
        { "formatVersion": 1, "game": "minecraft", "name": "Test Pack", "dependencies": { "minecraft": "1.20.4" },
          "files": [ { "path": "{{Esc(path)}}", "hashes": { "sha1": "aa", "sha512": "bb" }, "downloads": ["{{Esc(download)}}"] } ] }
        """;

    /// <summary>多个文件、每个带真实哈希的清单。</summary>
    private static string Index(params (string Path, string Url, byte[] Content)[] files)
    {
        var entries = files.Select(file => string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"path\":\"{Esc(file.Path)}\",\"hashes\":{{\"sha1\":\"{Sha1Hex(file.Content)}\",\"sha512\":\"{Sha512Hex(file.Content)}\"}},\"downloads\":[\"{Esc(file.Url)}\"],\"fileSize\":{file.Content.Length}}}"));

        return "{ \"formatVersion\": 1, \"game\": \"minecraft\", \"name\": \"Test Pack\", "
            + "\"dependencies\": { \"minecraft\": \"1.20.4\" }, \"files\": ["
            + string.Join(",", entries)
            + "] }";
    }

    /// <summary>把清单与覆盖层写成一个真的 <c>.mrpack</c>（zip）。</summary>
    private string WriteMrpack(
        string indexJson,
        (string Name, string Content)[] overrides,
        (string Name, string Content)[]? extraEntries = null)
    {
        var path = Path.Combine(_tempRoot, "pack-" + Guid.NewGuid().ToString("N")[..8] + ".mrpack");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        Write(archive, MrpackReader.IndexEntryName, indexJson);

        foreach (var (name, content) in overrides)
        {
            Write(archive, name, content);
        }

        foreach (var (name, content) in extraEntries ?? [])
        {
            Write(archive, name, content);
        }

        return path;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
#pragma warning disable CA5350 // SHA-1 是 Minecraft / Modrinth 协议规定的完整性字段，不是我们选的安全机制。
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private static string Sha512Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha1Hex(byte[] content)
    {
#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private static string Sha512Hex(byte[] content) =>
        Convert.ToHexString(SHA512.HashData(content)).ToLowerInvariant();
}
