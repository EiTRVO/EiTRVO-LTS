using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M7-d：从 Modrinth 下内容（依赖递归、校验、更新替换）。
/// <para>
/// 这里钉住的全是"看不见但会骗人"的东西：**依赖只认 required**、
/// **已经有这个项目就不再下一份**、**环不能无限递归**、**解析不出来时一个字节都不下**、
/// 以及**更新要把旧的移走**（只下不删 = mods 目录里同一个模组两份）。
/// 网络与磁盘是桩和临时目录，但文件是真的 —— 这一层的价值恰恰在于"落盘的东西对不对"。
/// </para>
/// </summary>
[TestClass]
public sealed class ContentInstallerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 21, 30, 0, TimeSpan.FromHours(8));

    private const string InstanceName = "1.20.4-Forge";

    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FileInstanceCatalog _instances = null!;
    private string _modsDirectory = string.Empty;

    /// <summary>每个用例一套全新的目录与一个真实的隔离实例。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-content-dl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(Path.Combine(_tempRoot, "data"));
        _diagnostics = new RecordingDiagnosticsLog();

        var settings = new FileSettingsCatalog(_paths, _diagnostics);
        settings.SaveGlobal(SettingsDefaults.CreateGlobal() with { GameDirectory = Path.Combine(_tempRoot, "game") });

        _instances = new FileInstanceCatalog(_paths, settings, new FixedClock(Now), _diagnostics);
        _instances.CreateInstance(new InstanceCreationRequest(InstanceName, "1.20.4", LoaderKind.Forge, "49.0.30"));

        _modsDirectory = Path.Combine(_instances.GetLaunchInfo(InstanceName).InstancePath, "mods");
        Directory.CreateDirectory(_modsDirectory);
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

    // ------------------------------------------------------------ 依赖递归 ---

    [TestMethod]
    public async Task TheRequiredDependencyIsDownloadedTooAndTheOptionalOneIsNot()
    {
        var root = Encoding.UTF8.GetBytes("root mod");
        var dependency = Encoding.UTF8.GetBytes("required lib");
        var optional = Encoding.UTF8.GetBytes("optional extra");

        var handler = new StubHandler()
            .RespondTo(
                "/v2/versions",
                HttpStatusCode.OK,
                VersionsJson(
                    Version("V1", "ROOT", "root-1.0.jar", root, "1.0.0", Required("V2", "LIB"), Optional("V3", "EXTRA")),
                    Version("V2", "LIB", "lib-2.0.jar", dependency, "2.0.0"),
                    Version("V3", "EXTRA", "extra-3.0.jar", optional, "3.0.0")))
            .RespondBytesTo("cdn.modrinth.com/data/PROJ/versions/V1/root-1.0.jar", root)
            .RespondBytesTo("cdn.modrinth.com/data/PROJ/versions/V2/lib-2.0.jar", dependency)
            .RespondBytesTo("cdn.modrinth.com/data/PROJ/versions/V3/extra-3.0.jar", optional);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "ROOT")));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.AreEqual(2, result.FilesDownloaded, "根目标 + 一个必需依赖。");
        Assert.IsTrue(File.Exists(Path.Combine(_modsDirectory, "root-1.0.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(_modsDirectory, "lib-2.0.jar")));
        Assert.IsFalse(
            File.Exists(Path.Combine(_modsDirectory, "extra-3.0.jar")),
            "optional 依赖是「可选增强」，替用户决定装不装是错的。");

        // 内容文件走官方 CDN（内容这一类没有镜像路线）。
        Assert.IsTrue(handler.Requests.Any(static request =>
            request.RequestUri!.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task AFileWhoseSha512DoesNotMatchIsRejected()
    {
        // 2026-10-21「修复依赖」的交叉验证：下载器要同时核对 SHA-1 与 SHA-512；
        // SHA-1 对、SHA-512 错时也不能收（两个摘要都由官方给出，任何一个不符都说明文件不对）。
        var content = Encoding.UTF8.GetBytes("mod with a bad sha512");

        var handler = new StubHandler()
            .RespondTo(
                "/v2/versions",
                HttpStatusCode.OK,
                VersionsJson(VersionWithSha512(
                    "V1",
                    "ROOT",
                    "root-1.0.jar",
                    content,
                    "1.0.0",
                    sha512: new string('0', 128))))
            .RespondBytesTo("cdn.modrinth.com/data/PROJ/versions/V1/root-1.0.jar", content);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "ROOT")));

        Assert.IsFalse(result.Succeeded, "SHA-512 不符时整个任务必须失败。");
        Assert.IsFalse(
            File.Exists(Path.Combine(_modsDirectory, "root-1.0.jar")),
            "校验不通过的文件不能留在 mods 目录里。");
    }

    [TestMethod]
    public async Task ADependencyCycleDoesNotRecurseForever()
    {
        var first = Encoding.UTF8.GetBytes("A");
        var second = Encoding.UTF8.GetBytes("B");

        var handler = new StubHandler()
            .RespondTo(
                "/v2/versions",
                HttpStatusCode.OK,
                VersionsJson(
                    Version("V1", "A", "a-1.0.jar", first, "1.0.0", Required("V2", "B")),
                    Version("V2", "B", "b-1.0.jar", second, "1.0.0", Required("V1", "A"))))
            .RespondBytesTo("versions/V1/a-1.0.jar", first)
            .RespondBytesTo("versions/V2/b-1.0.jar", second);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "A")));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.AreEqual(2, result.FilesDownloaded);

        // 两层就到底：真递归下去这里会是一长串请求（甚至永远）。
        Assert.AreEqual(2, handler.Requests.Count(static request => request.RequestUri!.AbsolutePath == "/v2/versions"));
    }

    [TestMethod]
    public async Task TwoVersionsOfTheSameDependencyProjectAreNotBothDownloaded()
    {
        var root = Encoding.UTF8.GetBytes("root");
        var library = Encoding.UTF8.GetBytes("lib");

        var handler = new StubHandler()
            .RespondTo(
                "/v2/versions",
                HttpStatusCode.OK,
                VersionsJson(
                    Version("V1", "ROOT", "root-1.0.jar", root, "1.0.0", Required("V2", "LIB"), Required("V3", "LIB")),
                    Version("V2", "LIB", "lib-2.0.jar", library, "2.0.0"),
                    Version("V3", "LIB", "lib-3.0.jar", library, "3.0.0")))
            .RespondBytesTo("versions/V1/root-1.0.jar", root)
            .RespondBytesTo("versions/V2/lib-2.0.jar", library)
            .RespondBytesTo("versions/V3/lib-3.0.jar", library);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "ROOT")));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.IsTrue(File.Exists(Path.Combine(_modsDirectory, "lib-2.0.jar")));
        Assert.IsFalse(
            File.Exists(Path.Combine(_modsDirectory, "lib-3.0.jar")),
            "同一个项目的两个版本都下下来 = 一个模组两份。");
    }

    [TestMethod]
    public async Task ADependencyThatIsAlreadyInstalledIsNotDownloaded()
    {
        var root = Encoding.UTF8.GetBytes("root");

        var handler = new StubHandler()
            .RespondTo(
                "/v2/versions",
                HttpStatusCode.OK,
                VersionsJson(
                    Version("V1", "ROOT", "root-1.0.jar", root, "1.0.0", Required("V2", "LIB")),
                    Version("V2", "LIB", "lib-2.0.jar", Encoding.UTF8.GetBytes("lib"), "2.0.0")))
            .RespondBytesTo("versions/V1/root-1.0.jar", root);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "ROOT"), installed: ["LIB"]));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.AreEqual(1, result.FilesDownloaded);
        Assert.IsTrue(File.Exists(Path.Combine(_modsDirectory, "root-1.0.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(_modsDirectory, "lib-2.0.jar")));
    }

    // -------------------------------------------------------- 解析即失败 ---

    [TestMethod]
    public async Task AnUnknownVersionFailsBeforeAnythingIsDownloaded()
    {
        var handler = new StubHandler().RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson());

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V9", "ROOT")));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Modrinth 上没有找到要装的那个版本（{0}）。", result.Failure!.Key);
        CollectionAssert.AreEqual(new[] { "V9" }, result.Failure.Parameters.ToArray());
        Assert.AreEqual(0, CountFiles(_modsDirectory), "解析失败时 mods 目录一个字节都不该动。");
        Assert.IsFalse(handler.Requests.Any(static request =>
            request.RequestUri!.Host.Contains("cdn", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AFileNameThatEscapesTheModsDirectoryIsRefused()
    {
        var bytes = Encoding.UTF8.GetBytes("evil");
        const string EvilName = @"..\..\evil.jar";

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(Version("V1", "EVIL", EvilName, bytes, "1.0.0")))
            .RespondBytesTo("evil.jar", bytes);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "EVIL")));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Modrinth 给的文件名不能用（{0}）。", result.Failure!.Key);
        Assert.AreEqual(0, CountFiles(_modsDirectory));
        Assert.IsFalse(
            File.Exists(Path.Combine(_tempRoot, "evil.jar")),
            "穿越出去的文件一个都不该出现（远端给的文件名是不可信输入）。");
    }

    [TestMethod]
    public async Task AVersionWithoutAnyDownloadableFileFailsWithItsId()
    {
        var handler = new StubHandler().RespondTo(
            "/v2/versions",
            HttpStatusCode.OK,
            VersionsJson("""
                {"id":"V1","project_id":"ROOT","version_number":"1.0.0","name":"1.0.0","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"],"files":[],"dependencies":[]}
                """));

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "ROOT")));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Modrinth 上的这个版本没有可下载的文件（{0}）。", result.Failure!.Key);
    }

    [TestMethod]
    public async Task ThePrimaryFileWins()
    {
        var sources = Encoding.UTF8.GetBytes("sources");
        var main = Encoding.UTF8.GetBytes("main jar");

        var json = """
            {"id":"V1","project_id":"PROJ","version_number":"1.0.0","name":"1.0.0","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"],"files":[
            __SOURCES__,
            __MAIN__
            ],"dependencies":[]}
            """
            .Replace("__SOURCES__", FileJson("V1", "root-1.0-sources.jar", sources, primary: false), StringComparison.Ordinal)
            .Replace("__MAIN__", FileJson("V1", "root-1.0.jar", main, primary: true), StringComparison.Ordinal);

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(json))
            .RespondBytesTo("versions/V1/root-1.0-sources.jar", sources)
            .RespondBytesTo("versions/V1/root-1.0.jar", main);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "ROOT")));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.IsTrue(File.Exists(Path.Combine(_modsDirectory, "root-1.0.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(_modsDirectory, "root-1.0-sources.jar")));
    }

    // ---------------------------------------------------------------- 校验 ---

    [TestMethod]
    public async Task BytesThatDoNotMatchTheOfficialSha1AreRejected()
    {
        var expected = Encoding.UTF8.GetBytes("expected bytes");
        var served = Encoding.UTF8.GetBytes("tampered bytes");

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(Version("V1", "ROOT", "root-1.0.jar", expected, "1.0.0")))
            .RespondBytesTo("versions/V1/root-1.0.jar", served);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V1", "ROOT")));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("{0} 个文件失败。", result.Failure!.Key);
        Assert.IsNotNull(result.Failure.Note);
        Assert.AreEqual(0, CountFiles(_modsDirectory), "校验不通过的文件不许落盘。");
    }

    // ------------------------------------------------------------ 更新替换 ---

    [TestMethod]
    public async Task UpdatingMovesTheOldFileIntoTheBackupsDirectory()
    {
        var old = Path.Combine(_modsDirectory, "sodium-0.5.8.jar");
        File.WriteAllText(old, "old version", new UTF8Encoding(false));

        var bytes = Encoding.UTF8.GetBytes("new version");

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(Version("V2", "SODIUM", "sodium-0.6.5.jar", bytes, "0.6.5")))
            .RespondBytesTo("versions/V2/sodium-0.6.5.jar", bytes);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V2", "SODIUM", replace: old)));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.IsTrue(File.Exists(Path.Combine(_modsDirectory, "sodium-0.6.5.jar")));
        Assert.IsFalse(File.Exists(old), "旧版本必须移走，否则 mods 目录里是同一个模组的两份。");

        var backups = Path.Combine(_instances.GetLaunchInfo(InstanceName).InstancePath, "backups", "mods");
        var moved = Directory.GetFiles(backups, "*.jar", SearchOption.AllDirectories);

        Assert.AreEqual(1, moved.Length, "旧文件是**移走**不是删掉（用户随时能拿回来）。");
        Assert.AreEqual("old version", File.ReadAllText(moved[0]));
    }

    [TestMethod]
    public async Task UpdatingADisabledModKeepsTheNewFileDisabled()
    {
        var old = Path.Combine(_modsDirectory, "sodium-0.5.8.jar.disabled");
        File.WriteAllText(old, "old disabled", new UTF8Encoding(false));

        var bytes = Encoding.UTF8.GetBytes("new disabled");

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(Version("V2", "SODIUM", "sodium-0.6.5.jar", bytes, "0.6.5")))
            .RespondBytesTo("versions/V2/sodium-0.6.5.jar", bytes);

        var result = await NewInstaller(handler).InstallAsync(
            Request(Target("V2", "SODIUM", replace: old, keepDisabled: true)));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.IsTrue(
            File.Exists(Path.Combine(_modsDirectory, "sodium-0.6.5.jar.disabled")),
            "更新被禁用的模组时，新文件必须继续以 .disabled 落盘。");
        Assert.IsFalse(
            File.Exists(Path.Combine(_modsDirectory, "sodium-0.6.5.jar")),
            "不能因为更新就把用户关掉的模组顺手启用。");
        Assert.IsFalse(File.Exists(old), "旧版本仍要移进备份目录。");
    }

    [TestMethod]
    public async Task WhenTheNewFileHasTheSameNameTheOldOneIsSimplyOverwritten()
    {
        var old = Path.Combine(_modsDirectory, "sodium.jar");
        File.WriteAllText(old, "old version", new UTF8Encoding(false));

        var bytes = Encoding.UTF8.GetBytes("new version");

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(Version("V2", "SODIUM", "sodium.jar", bytes, "0.6.5")))
            .RespondBytesTo("versions/V2/sodium.jar", bytes);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V2", "SODIUM", replace: old)));

        Assert.IsTrue(result.Succeeded, result.Failure?.Key);
        Assert.AreEqual("new version", File.ReadAllText(old));
        Assert.IsFalse(
            Directory.Exists(Path.Combine(_instances.GetLaunchInfo(InstanceName).InstancePath, "backups")),
            "同名更新就是覆盖，没有「旧文件」要备份。");
    }

    [TestMethod]
    public async Task AFailedDownloadLeavesTheOldFileAlone()
    {
        var old = Path.Combine(_modsDirectory, "sodium-0.5.8.jar");
        File.WriteAllText(old, "old version", new UTF8Encoding(false));

        var expected = Encoding.UTF8.GetBytes("expected");
        var served = Encoding.UTF8.GetBytes("tampered");

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(Version("V2", "SODIUM", "sodium-0.6.5.jar", expected, "0.6.5")))
            .RespondBytesTo("versions/V2/sodium-0.6.5.jar", served);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V2", "SODIUM", replace: old)));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            "old version",
            File.ReadAllText(old),
            "下载失败时旧版本必须原样留着 —— 先删后下就是把用户的模组弄丢。");
        Assert.IsFalse(Directory.Exists(Path.Combine(_instances.GetLaunchInfo(InstanceName).InstancePath, "backups")));
    }

    [TestMethod]
    public async Task AReplacePathOutsideTheModsDirectoryIsRefusedAndLeftAlone()
    {
        var outside = Path.Combine(_tempRoot, "somewhere-else.jar");
        File.WriteAllText(outside, "not ours", new UTF8Encoding(false));

        var bytes = Encoding.UTF8.GetBytes("new version");

        var handler = new StubHandler()
            .RespondTo("/v2/versions", HttpStatusCode.OK, VersionsJson(Version("V2", "SODIUM", "sodium-0.6.5.jar", bytes, "0.6.5")))
            .RespondBytesTo("versions/V2/sodium-0.6.5.jar", bytes);

        var result = await NewInstaller(handler).InstallAsync(Request(Target("V2", "SODIUM", replace: outside)));

        Assert.IsFalse(result.Succeeded, "新版本下好了却没能移走旧的 ⇒ 必须如实失败（mods 里会有两份）。");
        Assert.AreEqual("要替换的旧版本不在这个实例的 mods 目录里（{0}），这次没有动它。", result.Failure!.Key);
        Assert.IsTrue(File.Exists(outside), "mods 目录之外的文件一个都不该动。");
    }

    [TestMethod]
    public async Task AMissingInstanceFailsWithAReadableReason()
    {
        var handler = new StubHandler();

        var result = await NewInstaller(handler).InstallAsync(new ContentDownloadRequest(
            "早就删掉的实例",
            [Target("V1", "ROOT")],
            []));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("实例 {0} 已经不在了，这次没有下载任何模组。", result.Failure!.Key);
        Assert.AreEqual(0, handler.Requests.Count, "实例都没有，联网是白花的。");
    }

    [TestMethod]
    public async Task NoTargetsIsANoOp()
    {
        var handler = new StubHandler();

        var result = await NewInstaller(handler).InstallAsync(Request());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, result.FilesDownloaded);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    // ---------------------------------------------------------------- 辅助 ---

    private ContentInstaller NewInstaller(StubHandler handler)
    {
        var factory = new FakeHttpClientFactory(handler);

        return new ContentInstaller(
            new ModrinthClient(factory, _diagnostics),
            new FileDownloader(factory, HttpHostPolicy.Default, new FixedClock(Now), _diagnostics, static (_, _) => Task.CompletedTask),
            // 磁盘预检用固定值：真实可用空间在测试运行期间会变（同一条命令里的构建会删临时文件）。
            new DiskSpaceChecker(_diagnostics, static _ => long.MaxValue),
            _instances,
            new FixedClock(Now),
            _diagnostics);
    }

    private static ContentDownloadRequest Request(params ContentDownloadTarget[] targets) =>
        new(InstanceName, targets, []);

    private static ContentDownloadRequest Request(ContentDownloadTarget target, IReadOnlyList<string> installed) =>
        new(InstanceName, [target], installed);

    private static ContentDownloadTarget Target(
        string versionId,
        string projectId,
        string? replace = null,
        bool keepDisabled = false) =>
        new(projectId, versionId, projectId, replace, keepDisabled);

    private static int CountFiles(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length : 0;

    private static string VersionsJson(params string[] versions) => "[" + string.Join(",", versions) + "]";

    /// <summary>版本 JSON，但主文件的 SHA-512 用一个指定值（用来测"两个摘要都对才收"）。</summary>
    private static string VersionWithSha512(
        string id,
        string projectId,
        string fileName,
        byte[] content,
        string versionNumber,
        string sha512) =>
        "{\"id\":\"" + Esc(id) + "\",\"project_id\":\"" + Esc(projectId) + "\",\"version_number\":\"" + Esc(versionNumber)
        + "\",\"name\":\"" + Esc(versionNumber) + "\",\"date_published\":\"2026-01-01T00:00:00Z\","
        + "\"game_versions\":[\"1.20.4\"],\"loaders\":[\"forge\"],"
        + "\"files\":[" + FileJson(id, fileName, content, primary: true, sha512: sha512) + "],"
        + "\"dependencies\":[]}";

    private static string Version(
        string id,
        string projectId,
        string fileName,
        byte[] content,
        string versionNumber,
        params string[] dependencies) =>
        "{\"id\":\"" + Esc(id) + "\",\"project_id\":\"" + Esc(projectId) + "\",\"version_number\":\"" + Esc(versionNumber)
        + "\",\"name\":\"" + Esc(versionNumber) + "\",\"date_published\":\"2026-01-01T00:00:00Z\","
        + "\"game_versions\":[\"1.20.4\"],\"loaders\":[\"forge\"],"
        + "\"files\":[" + FileJson(id, fileName, content, primary: true) + "],"
        + "\"dependencies\":[" + string.Join(",", dependencies) + "]}";

    private static string FileJson(string versionId, string fileName, byte[] content, bool primary, string? sha512 = null) =>
        "{\"url\":\"https://cdn.modrinth.com/data/PROJ/versions/" + Esc(versionId) + "/" + Esc(fileName)
        + "\",\"filename\":\"" + Esc(fileName) + "\",\"size\":" + content.Length.ToString(CultureInfo.InvariantCulture)
        + ",\"primary\":" + (primary ? "true" : "false")
        + ",\"hashes\":{\"sha1\":\"" + Sha1Hex(content) + "\",\"sha512\":\"" + (sha512 ?? Sha512Hex(content)) + "\"}}";

    private static string Required(string versionId, string projectId) =>
        Dependency(versionId, projectId, "required");

    private static string Optional(string versionId, string projectId) =>
        Dependency(versionId, projectId, "optional");

    private static string Dependency(string versionId, string projectId, string type) =>
        "{\"version_id\":\"" + Esc(versionId) + "\",\"project_id\":\"" + Esc(projectId)
        + "\",\"dependency_type\":\"" + type + "\"}";

    /// <summary>把一个值安全地放进 JSON 字符串（文件名里可能带反斜杠 —— 那正是要测的输入）。</summary>
    private static string Esc(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>SHA-512（Modrinth 的第二个官方摘要，缺失依赖的交叉验证用它）。</summary>
    private static string Sha512Hex(byte[] content) =>
        Convert.ToHexString(SHA512.HashData(content)).ToLowerInvariant();

    /// <summary>SHA-1 是 Modrinth 协议的内容寻址方式（不是我们选的安全机制）。</summary>
    private static string Sha1Hex(byte[] content)
    {
#pragma warning disable CA5350
        var hash = SHA1.HashData(content);
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
