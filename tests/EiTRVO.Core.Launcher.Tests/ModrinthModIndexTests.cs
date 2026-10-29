using System.Net;
using System.Security.Cryptography;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M7-c：本地 jar 的 SHA-1 索引与 Modrinth 对账（来源 / 可更新）。
/// <para>
/// 两件事在这里被钉住，它们都是"看不见但会骗人"的：**哈希缓存不能白读一遍磁盘**，
/// 以及**"查过但没查到"与"没查过"必须分开**（前者才敢说「不在 Modrinth 收录」）。
/// </para>
/// </summary>
[TestClass]
public sealed class ModrinthModIndexTests
{
    private static readonly DateTimeOffset StartupTime = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(8));

    private const string Hash = "3f786850e387550fdab836ed7e6dc881de23001b";

    private static readonly string ExactJson =
        """{"__HASH__":{"id":"V1","project_id":"PROJ","version_number":"0.5.1f","name":"Create 0.5.1f","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
            .Replace("__HASH__", Hash, StringComparison.Ordinal);

    private static readonly string UpdateJson =
        """{"__HASH__":{"id":"V2","project_id":"PROJ","version_number":"0.6.0","name":"Create 0.6.0","date_published":"2026-02-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
            .Replace("__HASH__", Hash, StringComparison.Ordinal);

    /// <summary>官方对"已经是最新的"也会返回它自己那个版本 —— 这不是"有更新"。</summary>
    private static readonly string UpToDateJson =
        """{"__HASH__":{"id":"V1","project_id":"PROJ","version_number":"0.5.1f","name":"Create 0.5.1f","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
            .Replace("__HASH__", Hash, StringComparison.Ordinal);

    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FixedClock _clock = null!;

    /// <summary>每个用例一个全新的数据目录与固定时钟。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-modhash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
        _diagnostics = new RecordingDiagnosticsLog();
        _clock = new FixedClock(StartupTime);
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

    private ModHashIndex NewHashIndex() => new(_paths, _diagnostics);

    /// <summary>对账用的桩：**先注册更具体的 update 规则**（`/version_files` 是它的前缀）。</summary>
    private static StubHandler HappyPath(string? updateJson = null) => new StubHandler()
        .RespondTo("/version_files/update", HttpStatusCode.OK, updateJson ?? UpdateJson)
        .RespondTo("/version_files", HttpStatusCode.OK, ExactJson);

    private static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);

        // 测试里也一样：SHA-1 是 Modrinth 协议的内容寻址方式，不是我们选的安全机制。
#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    // ------------------------------------------------------------ 哈希索引 ---

    [TestMethod]
    public void TheHashIsTheRealSha1OfTheFile()
    {
        var path = Path.Combine(_tempRoot, "create.jar");
        File.WriteAllText(path, "这是 create 的字节");

        Assert.AreEqual(Sha1Of(path), NewHashIndex().Sha1Of(path), "小写十六进制的真 SHA-1。");
    }

    [TestMethod]
    public void ASecondCallDoesNotReadTheFileAgain()
    {
        var path = Path.Combine(_tempRoot, "cached.jar");
        File.WriteAllText(path, "AAAA");
        var index = NewHashIndex();

        var first = index.Sha1Of(path);

        // 内容换了但**长度与最后写入时间都没变**（模拟"缓存判据能骗过"的极端情况）：
        // 结果必须还是缓存里那个值 —— 那就证明第二次没有真的去读文件。
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "BBBB");
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.AreEqual(first, index.Sha1Of(path));
        Assert.AreNotEqual(Sha1Of(path), index.Sha1Of(path), "文件内容确实变了（只是判据看不出来）。");
    }

    [TestMethod]
    public void TheHashIndexSurvivesARestart()
    {
        var path = Path.Combine(_tempRoot, "restart.jar");
        File.WriteAllText(path, "AAAA");

        var first = NewHashIndex();
        var sha1 = first.Sha1Of(path);
        first.Save();

        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "BBBB");
        File.SetLastWriteTimeUtc(path, stamp);

        // 新实例 = 重启启动器：应当从磁盘索引里读到上次的哈希，而不是把几百兆再读一遍。
        Assert.IsTrue(NewHashIndex().TryGetSha1(path, out var restored));
        Assert.AreEqual(sha1, restored);
    }

    [TestMethod]
    public void SavePrunesEntriesWhoseFileIsGone()
    {
        var kept = Path.Combine(_tempRoot, "kept.jar");
        var removed = Path.Combine(_tempRoot, "removed.jar");
        File.WriteAllText(kept, "AAAA");
        File.WriteAllText(removed, "BBBB");

        var index = NewHashIndex();
        index.Sha1Of(kept);
        index.Sha1Of(removed);
        File.Delete(removed);
        index.Save();

        Assert.AreEqual(1, NewHashIndex().Count, "已经不在的文件不该永远留在索引里。");
    }

    [TestMethod]
    public void ACorruptHashIndexIsIgnoredAndNotDeleted()
    {
        Directory.CreateDirectory(_paths.ContentCacheDirectory);
        var path = Path.Combine(_paths.ContentCacheDirectory, ModHashIndex.FileName);
        File.WriteAllText(path, "{ 这不是 JSON");

        var index = NewHashIndex();

        Assert.AreEqual(0, index.Count);
        Assert.IsTrue(File.Exists(path), "读不出来**不删文件**（下一次成功会原子覆盖它）。");
        Assert.AreEqual(
            1,
            _diagnostics.Entries.Count(static entry => entry.MessageKey.StartsWith("模组哈希缓存不可用", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- 对账 ---

    [TestMethod]
    public async Task TheExactLookupRecordsBothWhatWasFoundAndWhatWasLookedUp()
    {
        var (index, handler, _) = NewIndexAndHandler(HappyPath());

        Assert.IsTrue(await index.RefreshAsync([Hash, "ffffffffffffffffffffffffffffffffffffffff"], "1.20.4", "forge"));

        // ① 查到了：这是一份 Modrinth 上的文件。
        Assert.IsTrue(index.WasLookedUp(Hash));

        var match = index.Find(Hash);

        Assert.IsNotNull(match);
        Assert.AreEqual("PROJ", match.ProjectId);
        Assert.AreEqual("V1", match.VersionId);
        Assert.AreEqual("0.5.1f", match.VersionNumber);
        Assert.IsTrue(match.HasUpdate);
        Assert.AreEqual("0.6.0", match.LatestVersionNumber);
        Assert.AreEqual("V2", match.LatestVersionId);

        // ② 查过但没查到：**这**才敢让界面写「不在 Modrinth 收录」。
        const string Unknown = "ffffffffffffffffffffffffffffffffffffffff";

        Assert.IsTrue(index.WasLookedUp(Unknown));
        Assert.IsNull(index.Find(Unknown));

        Assert.AreEqual(2, handler.Requests.Count, "一次精确查 + 一次查更新。");
    }

    [TestMethod]
    public async Task TheUpdateLookupCarriesTheInstanceFacetsInTheRequestBody()
    {
        var (index, handler, _) = NewIndexAndHandler(HappyPath());

        await index.RefreshAsync([Hash], "1.20.4", "forge");

        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual("https://api.modrinth.com/v2/version_files", handler.UriAt(0).AbsoluteUri);
        Assert.AreEqual("https://api.modrinth.com/v2/version_files/update", handler.UriAt(1).AbsoluteUri);

        var exact = handler.Bodies[0];
        var update = handler.Bodies[1];

        StringAssert.Contains(exact, "\"algorithm\":\"sha1\"");
        StringAssert.Contains(exact, Hash);
        Assert.IsFalse(exact.Contains("game_versions", StringComparison.Ordinal), "精确查不带版本条件。");

        // 「有没有更新」必须带**当前实例**的游戏版本与加载器：不带的话，一个给 1.16 做的版本
        // 也会被算成"你的 1.20.4 有更新"。
        StringAssert.Contains(update, "\"game_versions\":[\"1.20.4\"]");
        StringAssert.Contains(update, "\"loaders\":[\"forge\"]");
    }

    [TestMethod]
    public async Task AModThatIsAlreadyUpToDateIsNotMarkedUpdatable()
    {
        var (index, _, _) = NewIndexAndHandler(HappyPath(UpToDateJson));

        await index.RefreshAsync([Hash], "1.20.4", "forge");

        var match = index.Find(Hash);

        Assert.IsNotNull(match);
        Assert.AreEqual("0.5.1f", match.LatestVersionNumber);
        Assert.IsFalse(match.HasUpdate, "官方对最新的模组也返回它自己那个版本 —— 版本号相同就不是更新。");
    }

    [TestMethod]
    public async Task ASecondRefreshWithTheSameFacetsDoesNotTalkToTheNetwork()
    {
        var (index, handler, _) = NewIndexAndHandler(HappyPath());

        await index.RefreshAsync([Hash], "1.20.4", "forge");
        var requests = handler.Requests.Count;

        Assert.IsTrue(await index.RefreshAsync([Hash], "1.20.4", "forge"));
        Assert.AreEqual(requests, handler.Requests.Count, "12 小时窗口内、同一批哈希、同一条件 —— 不该再问一遍。");

        // 换一个游戏版本就要重新问：那一半结果是按条件算的。
        await index.RefreshAsync([Hash], "1.21.1", "forge");
        Assert.AreEqual(requests + 2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AFailedLookupKeepsThePreviousResult()
    {
        var (index, _, _) = NewIndexAndHandler(HappyPath());

        await index.RefreshAsync([Hash], "1.20.4", "forge");

        // 换条件（绕开新鲜度窗口）但这次远端 500：结果必须**原样保留**，不算"没查到"。
        var failing = new ModrinthModIndex(
            new ModrinthClient(
                new FakeHttpClientFactory(new StubHandler().RespondTo("/version_files", HttpStatusCode.InternalServerError)),
                _diagnostics),
            _paths,
            _clock,
            _diagnostics);

        Assert.IsTrue(failing.WasLookedUp(Hash), "重启后从磁盘恢复的账还在。");
        Assert.IsFalse(await failing.RefreshAsync([Hash], "1.21.1", "forge"), "失败要如实返回 false。");
        Assert.AreEqual("PROJ", failing.Find(Hash)?.ProjectId, "失败时保留上一次的完整结果。");

        var events = _diagnostics.Entries.Count(static entry => entry.MessageKey == "获取 Modrinth 数据失败（{0}）：{1}");
        Assert.AreEqual(1, events);
    }

    [TestMethod]
    public async Task TheReconciliationSurvivesARestart()
    {
        var (index, _, _) = NewIndexAndHandler(HappyPath());

        await index.RefreshAsync([Hash], "1.20.4", "forge");

        // 新实例 = 重启启动器：结果从磁盘来，不需要再联网（桩里故意没有准备任何响应，
        // 真去请求就会抛"桩没有为这次请求准备响应"）。
        var restarted = NewIndexAndHandler(new StubHandler()).Index;

        Assert.IsTrue(restarted.WasLookedUp(Hash));
        Assert.AreEqual("0.6.0", restarted.Find(Hash)?.LatestVersionNumber);
        Assert.AreEqual(StartupTime, restarted.FetchedAt);
    }

    [TestMethod]
    public async Task AnEmptyHashListIsANoOp()
    {
        var (index, handler, _) = NewIndexAndHandler(HappyPath());

        Assert.IsFalse(await index.RefreshAsync([], "1.20.4", "forge"));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task A404DependencyIsCachedAsKnownAbsent()
    {
        var handler = new StubHandler().RespondTo(
            "/v2/project/ghost-dep",
            HttpStatusCode.NotFound,
            """{"error":"not_found"}""");

        var (index, _, _) = NewIndexAndHandler(handler);

        Assert.IsTrue(await index.RefreshMissingDependenciesAsync(["ghost-dep"]));
        Assert.IsTrue(index.IsDependencyKnownAbsent("ghost-dep"));
        HttpAssert.Url(handler, 0, "https://api.modrinth.com/v2/project/ghost-dep");

        // 重启后仍记得“明确不存在”，不需要再发一次请求。
        var restarted = NewIndexAndHandler(new StubHandler()).Index;
        Assert.IsTrue(restarted.IsDependencyKnownAbsent("ghost-dep"));
    }

    [TestMethod]
    public async Task AnExistingDependencyIsNotMarkedAbsent()
    {
        var handler = new StubHandler().RespondTo(
            "/v2/project/real-dep",
            HttpStatusCode.OK,
            """{"id":"real-dep"}""");

        var (index, _, _) = NewIndexAndHandler(handler);

        Assert.IsFalse(await index.RefreshMissingDependenciesAsync(["real-dep"]));
        Assert.IsFalse(index.IsDependencyKnownAbsent("real-dep"));
    }

    [TestMethod]
    public async Task AFailedDependencyLookupDoesNotMarkItAbsent()
    {
        var handler = new StubHandler().RespondTo(
            "/v2/project/offline-dep",
            HttpStatusCode.InternalServerError,
            "boom");

        var (index, _, _) = NewIndexAndHandler(handler);

        Assert.IsFalse(await index.RefreshMissingDependenciesAsync(["offline-dep"]));
        Assert.IsFalse(index.IsDependencyKnownAbsent("offline-dep"));
    }

    private (ModrinthModIndex Index, StubHandler Handler, FakeHttpClientFactory Factory) NewIndexAndHandler(StubHandler handler)
    {
        var factory = new FakeHttpClientFactory(handler);
        var index = new ModrinthModIndex(
            new ModrinthClient(factory, _diagnostics),
            _paths,
            _clock,
            _diagnostics);

        return (index, handler, factory);
    }
}
