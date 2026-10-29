using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M7-a 内容目录：模组 / 资源包 / 存档都来自实例的游戏目录。
/// <para>
/// 这一组用例全部在临时目录上跑**真实文件**：真的写 zip、真的写 gzip 过的 NBT、
/// 真的写 <c>options.txt</c>。用 mock 文件系统测它就等于什么都没测 ——
/// 而这一步的价值恰恰在于"元数据是从 jar 里读出来的、存档信息是从 level.dat 里读出来的"。
/// </para>
/// </summary>
[TestClass]
public sealed class ContentCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 16, 1, 2, TimeSpan.FromHours(8));

    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;
    private FileInstanceCatalog _instances = null!;

    /// <summary>每个用例一个全新的数据目录与固定时钟。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-content", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
        _diagnostics = new RecordingDiagnosticsLog();
        var settings = new FileSettingsCatalog(_paths, _diagnostics);
        settings.SaveGlobal(SettingsDefaults.CreateGlobal() with
        {
            GameDirectory = Path.Combine(_tempRoot, "game"),
        });
        _diagnostics.Entries.Clear();
        _instances = new FileInstanceCatalog(
            _paths,
            settings,
            new FixedClock(Now),
            _diagnostics);
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

    /// <summary>
    /// 内容目录（模组 / 资源包 / 存档）。
    /// </summary>
    /// <param name="modrinth">
    /// Modrinth 侧的桩；不传就是"没有网络、也没有缓存"（桩不为任何请求准备响应 ⇒ 真去联网就会抛，
    /// 等于顺带钉住"读本地内容不该联网"）。
    /// </param>
    /// <remarks>
    /// M7-b 起 <see cref="FileContentCatalog"/> 还要一个 <c>ModrinthCatalog</c>（p08 的浏览列表），
    /// M7-c 起还要哈希索引与对账索引（p07 的来源 / 可更新）。
    /// </remarks>
    private FileContentCatalog NewCatalog(StubHandler? modrinth = null)
    {
        var handler = modrinth ?? new StubHandler();

        return new FileContentCatalog(
            _instances,
            new FixedClock(Now),
            _diagnostics,
            new ModrinthCatalog(
                new ModrinthClient(new FakeHttpClientFactory(handler), _diagnostics),
                new ModrinthCache(_paths, _diagnostics),
                new FixedClock(Now),
                _diagnostics),
            new ModHashIndex(_paths, _diagnostics),
            new ModrinthModIndex(
                new ModrinthClient(new FakeHttpClientFactory(handler), _diagnostics),
                _paths,
                new FixedClock(Now),
                _diagnostics));
    }

    private string CreateInstance(string name, string gameVersion, LoaderKind loader, string? loaderVersion = null)
    {
        _instances.CreateInstance(new InstanceCreationRequest(name, gameVersion, loader, loaderVersion));
        return _instances.GetLaunchInfo(name).InstancePath;
    }

    // ------------------------------------------------------------------ 零实例 ---

    [TestMethod]
    public void NoInstanceContextIsNotAnError()
    {
        var catalog = NewCatalog();

        // 首次运行没有实例是**正常状态**：p07/p09 要给空表，而不是抛异常（绑定里抛就是崩溃）。
        Assert.AreEqual(0, catalog.GetMods(string.Empty).Count);
        Assert.AreEqual(0, catalog.GetMods("不存在的实例").Count);
        Assert.AreEqual(0, catalog.GetResourcePacks(string.Empty).Count);
        Assert.AreEqual(0, catalog.GetWorldSaves("不存在的实例").Count);

        var summary = catalog.GetModSummary(string.Empty);

        Assert.AreEqual(0, summary.Enabled);
        Assert.AreEqual(0, summary.Disabled);
        Assert.AreEqual("原版", summary.LoaderMatch.Key);
        Assert.AreEqual("—", summary.GameVersionMatch.Key, "没有实例时游戏版本是纯数据键「—」，不是空串。");
        Assert.IsNull(summary.MissingDependencyDetail);
    }

    [TestMethod]
    public void AnEmptyInstanceReportsZerosInsteadOfFailing()
    {
        CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var catalog = NewCatalog();

        var summary = catalog.GetModSummary("1.20.4-Forge");

        Assert.AreEqual(0, summary.Enabled);
        Assert.AreEqual(0, summary.Updatable, "「可更新」要问 Modrinth（P1-4），现在如实为 0。");
        CollectionAssert.AreEqual(new[] { "Forge", "49.0.30" }, summary.LoaderMatch.Parameters.ToArray());
        CollectionAssert.AreEqual(new[] { "1.20.4" }, summary.GameVersionMatch.Parameters.ToArray());
        Assert.AreEqual(0, catalog.GetResourcePacks("1.20.4-Forge").Count);
        Assert.AreEqual(0, catalog.GetWorldSaves("1.20.4-Forge").Count);
    }

    // ------------------------------------------------------------------ 模组 ---

    [TestMethod]
    public void ModsComeFromTheJarsNotFromTheirFileNames()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        WriteJar(
            Path.Combine(directory, "mods", "create-1.20.4-0.5.1f.jar"),
            ("fabric.mod.json", """{"schemaVersion":1,"id":"create","version":"0.5.1f","name":"Create","depends":{"minecraft":">=1.20.4"}}"""));
        WriteJar(
            Path.Combine(directory, "mods", "jei-1.20.4.jar"),
            ("META-INF/mods.toml", "[[mods]]\nmodId=\"jei\"\nversion=\"17.3.0.49\"\ndisplayName=\"JEI (Just Enough Items)\"\n"));

        var mods = NewCatalog().GetMods("1.20.4-Forge");

        // 名字与版本来自 jar 里的元数据，不是文件名 —— 文件名只是兜底。
        Assert.AreEqual("Create", mods[0].Name);
        Assert.AreEqual("0.5.1f", mods[0].Version);
        Assert.AreEqual("JEI (Just Enough Items)", mods[1].Name);
        Assert.AreEqual("17.3.0.49", mods[1].Version);
        Assert.AreEqual("1.20.4", mods[0].GameVersion, "「游戏版本」这一列是**实例**的版本（模组装在这个实例里）。");
        Assert.AreEqual(ContentSource.LocalFile, mods[0].Source);
        Assert.AreEqual(ModEnablement.Enabled, mods[0].Enablement);
        Assert.AreEqual(ModState.Normal, mods[0].State);
        Assert.IsNull(mods[0].Note, "没有备注时内核给 null（不是空串）。");
        Assert.IsTrue(mods[0].IsActive, "设计稿把第一行画成高亮行。");
        Assert.IsFalse(mods[1].IsActive);
    }

    [TestMethod]
    public void ADeclaredButAbsentDependencyShowsUpInTheRowAndTheBanner()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        WriteJar(
            Path.Combine(directory, "mods", "create-0.5.1f.jar"),
            ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create","depends":{"minecraft":">=1.20.4","flywheel":">=0.6.10"}}"""));
        WriteJar(
            Path.Combine(directory, "mods", "sodium-0.5.8.jar.disabled"),
            ("fabric.mod.json", """{"id":"sodium","version":"0.5.8","name":"Sodium"}"""));

        var catalog = NewCatalog();
        var mods = catalog.GetMods("1.20.4-Forge");

        Assert.AreEqual(ModState.MissingDependency, mods.Single(static m => m.Name == "Create").State);
        Assert.AreEqual("需要 {0}", mods.Single(static m => m.Name == "Create").Note!.Key);
        CollectionAssert.AreEqual(
            new[] { "flywheel" },
            mods.Single(static m => m.Name == "Create").Note!.Parameters.ToArray());

        // 禁用是**按后缀**认的，文件名照原样留着。
        var disabled = mods.Single(static m => m.Name == "Sodium");

        Assert.AreEqual(ModEnablement.Disabled, disabled.Enablement);
        Assert.IsNull(disabled.Note, "它自己不缺依赖。");

        var summary = catalog.GetModSummary("1.20.4-Forge");

        Assert.AreEqual(1, summary.Enabled);
        Assert.AreEqual(1, summary.Disabled);
        Assert.AreEqual(1, summary.MissingDependency);
        Assert.AreEqual("{0} 需要 {1}，当前未安装", summary.MissingDependencyDetail!.Key);
        CollectionAssert.AreEqual(
            new[] { "create-0.5.1f.jar", "flywheel" },
            summary.MissingDependencyDetail.Parameters.ToArray(),
            "横幅要报**磁盘上的文件名**与缺失的依赖 id。");
    }

    [TestMethod]
    public async Task MissingDependenciesThatDoNotExistOnModrinthAreNotReported()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Fabric, "0.15.7");
        WriteJar(
            Path.Combine(directory, "mods", "sodium.jar"),
            ("fabric.mod.json", """{"id":"sodium","version":"0.8.12","name":"Sodium","depends":{"ghost-dep":"*"}}"""));

        // 哈希对账的请求故意不准备响应；随后查 /project/ghost-dep 得到 404。
        var handler = new StubHandler().RespondTo(
            "/v2/project/ghost-dep",
            HttpStatusCode.NotFound,
            """{"error":"not_found"}""");

        var catalog = NewCatalog(handler);
        await catalog.RefreshModsAsync("1.20.4-Forge");

        var sodium = catalog.GetMods("1.20.4-Forge").Single(static mod => mod.Name == "Sodium");

        Assert.AreEqual(ModState.Normal, sodium.State);
        Assert.IsNull(sodium.Note, "Modrinth 上不存在的依赖 id 不报缺失。");
    }

    [TestMethod]
    public void NestedFabricApiModulesSatisfyDependencies()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Fabric, "0.15.7");
        var fabricApi = Path.Combine(directory, "mods", "fabric-api-0.116.17+1.jar");
        WriteJar(fabricApi, ("fabric.mod.json", """{"id":"fabric-api","version":"0.116.17+1","name":"Fabric API"}"""));

        using (var nestedBuffer = new MemoryStream())
        {
            using (var nested = new ZipArchive(nestedBuffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(
                    nested.CreateEntry("fabric.mod.json").Open(),
                    new UTF8Encoding(false));
                writer.Write("""{"id":"fabric-block-view-api-v2","version":"1.0.31","name":"Fabric Block View API v2"}""");
            }

            using var outer = ZipFile.Open(fabricApi, ZipArchiveMode.Update);
            using var target = outer.CreateEntry("META-INF/jars/fabric-block-view-api-v2.jar").Open();
            target.Write(nestedBuffer.ToArray(), 0, (int)nestedBuffer.Length);
        }

        WriteJar(
            Path.Combine(directory, "mods", "sodium-fabric-0.8.12.jar"),
            ("fabric.mod.json", """{"id":"sodium","version":"0.8.12","name":"Sodium","depends":{"fabric-block-view-api-v2":"*"}}"""));

        var sodium = NewCatalog().GetMods("1.20.4-Forge").Single(static mod => mod.Name == "Sodium");

        Assert.AreEqual(ModState.Normal, sodium.State, "嵌套模块提供的 id 也要算作已安装。");
        Assert.IsNull(sodium.Note);
    }

    [TestMethod]
    public void ADisabledDependencyDoesNotCountAsPresent()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        WriteJar(
            Path.Combine(directory, "mods", "create.jar"),
            ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create","depends":{"flywheel":">=0.6.10"}}"""));
        WriteJar(
            Path.Combine(directory, "mods", "flywheel.jar.disabled"),
            ("fabric.mod.json", """{"id":"flywheel","version":"0.6.10","name":"Flywheel"}"""));

        // 被禁用的依赖不会加载，依赖它的模组照样起不来 —— 所以"在不在"只看已启用的那些。
        Assert.AreEqual(
            ModState.MissingDependency,
            NewCatalog().GetMods("1.20.4-Forge").Single(static m => m.Name == "Create").State);
    }

    [TestMethod]
    public async Task TheModrinthReconciliationFillsSourceStateAndNote()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var modsDirectory = Path.Combine(directory, "mods");
        var createJar = Path.Combine(modsDirectory, "create.jar");

        WriteJar(
            createJar,
            ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create"}"""));
        WriteJar(
            Path.Combine(modsDirectory, "local.jar"),
            ("fabric.mod.json", """{"id":"local","version":"1.0","name":"Local Mod"}"""));

        // 对账**之前**：来源未知 —— 沿用 M7-a 的显示（"没查过"不说成"不在 Modrinth 收录"）。
        var before = NewCatalog().GetMods("1.20.4-Forge");

        Assert.AreEqual("Create", before[0].Name);
        Assert.AreEqual(ContentSource.LocalFile, before[0].Source);
        Assert.AreEqual(ModEnablement.Enabled, before[0].Enablement);
        Assert.IsNull(before[0].Note);

        // 桩按**这个 jar 真的 SHA-1** 作答（另一个 jar 的哈希不在任何响应里）。
        var hash = Sha1OfFile(createJar);
        var handler = new StubHandler()
            .RespondTo(
                "/version_files/update",
                HttpStatusCode.OK,
                """{"__HASH__":{"id":"V2","project_id":"PROJ","version_number":"0.6.0","name":"Create 0.6.0","date_published":"2026-02-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
                    .Replace("__HASH__", hash, StringComparison.Ordinal))
            .RespondTo(
                "/version_files",
                HttpStatusCode.OK,
                """{"__HASH__":{"id":"V1","project_id":"PROJ","version_number":"0.5.1f","name":"Create 0.5.1f","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
                    .Replace("__HASH__", hash, StringComparison.Ordinal));

        var catalog = NewCatalog(handler);
        await catalog.RefreshModsAsync("1.20.4-Forge");

        var after = catalog.GetMods("1.20.4-Forge");
        var create = after.Single(static mod => mod.Name == "Create");

        Assert.AreEqual(ContentSource.Modrinth, create.Source, "查到了 ⇒ 来源是 Modrinth。");
        Assert.AreEqual(ModState.Updatable, create.State);
        Assert.AreEqual("最新 {0}", create.Note!.Key);
        CollectionAssert.AreEqual(new[] { "0.6.0" }, create.Note.Parameters.ToArray());

        var local = after.Single(static mod => mod.Name == "Local Mod");

        Assert.AreEqual(ContentSource.LocalFile, local.Source);
        Assert.AreEqual(ModEnablement.Unverified, local.Enablement, "启用了、但来源查不到 ⇒ 「未验证来源」。");
        Assert.AreEqual("不在 Modrinth 收录", local.Note!.Key);

        Assert.AreEqual(1, catalog.GetModSummary("1.20.4-Forge").Updatable, "「可更新」这个数字现在是真的。");

        // 对账跑完要落盘：下次启动不必把几百兆重读一遍，也不必重新问一遍。
        Assert.IsTrue(File.Exists(Path.Combine(_paths.ContentCacheDirectory, ModHashIndex.FileName)));
        Assert.IsTrue(File.Exists(Path.Combine(_paths.ContentCacheDirectory, ModrinthModIndex.FileName)));
    }

    [TestMethod]
    public async Task ReconcilingWithoutAnInstanceIsANoOp()
    {
        // 首次运行没有实例：不该抛、也不该联网（桩没有准备任何响应）。
        var catalog = NewCatalog();

        await catalog.RefreshModsAsync(string.Empty);
        await catalog.RefreshModsAsync("不存在的实例");

        Assert.AreEqual(0, catalog.GetMods(string.Empty).Count);
    }

    // ------------------------------------------------ M7-d：更新请求 ---

    [TestMethod]
    public async Task TheUpdateRequestCarriesTheLatestVersionAndTheLocalJarToReplace()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var modsDirectory = Path.Combine(directory, "mods");
        var createJar = Path.Combine(modsDirectory, "create-0.5.1f.jar");
        var localJar = Path.Combine(modsDirectory, "local.jar");

        WriteJar(createJar, ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create"}"""));
        WriteJar(localJar, ("fabric.mod.json", """{"id":"local","version":"1.0","name":"Local Mod"}"""));

        var hash = Sha1OfFile(createJar);
        var handler = new StubHandler()
            .RespondTo(
                "/version_files/update",
                HttpStatusCode.OK,
                """{"__HASH__":{"id":"V2","project_id":"PROJ","version_number":"0.6.0","name":"Create 0.6.0","date_published":"2026-02-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
                    .Replace("__HASH__", hash, StringComparison.Ordinal))
            .RespondTo(
                "/version_files",
                HttpStatusCode.OK,
                """{"__HASH__":{"id":"V1","project_id":"PROJ","version_number":"0.5.1f","name":"Create 0.5.1f","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
                    .Replace("__HASH__", hash, StringComparison.Ordinal));

        var catalog = NewCatalog(handler);

        // 对账**之前**：还不知道有没有更新 ⇒ 没有请求（"还没查过"不等于"可以更新"）。
        Assert.IsNull(catalog.CreateModUpdateRequest("1.20.4-Forge"));

        await catalog.RefreshModsAsync("1.20.4-Forge");

        var request = catalog.CreateModUpdateRequest("1.20.4-Forge");

        Assert.IsNotNull(request);
        Assert.AreEqual("1.20.4-Forge", request.InstanceName);

        // 一个目标：本地那个真的 jar + Modrinth 上那个**最新版本**的 id。
        var target = request.Targets.Single();

        Assert.AreEqual("Create", target.DisplayName);
        Assert.AreEqual("V2", target.VersionId);
        Assert.AreEqual("PROJ", target.ProjectId);
        Assert.AreEqual(createJar, target.ReplacePath, "替换的是本地那个 jar（路径来自扫描，界面不拼路径）。");

        // 本机已装的项目要一并带上：依赖解析靠它避免"拿另一个版本覆盖已有模组"。
        CollectionAssert.Contains(request.InstalledProjectIds.ToArray(), "PROJ");
    }

    [TestMethod]
    public async Task ADisabledOutdatedModIsUpdatedButStaysDisabled()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var disabledJar = Path.Combine(directory, "mods", "create-0.5.1f.jar.disabled");

        WriteJar(disabledJar, ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create"}"""));

        var hash = Sha1OfFile(disabledJar);
        var handler = new StubHandler()
            .RespondTo(
                "/version_files/update",
                HttpStatusCode.OK,
                """{"__HASH__":{"id":"V2","project_id":"PROJ","version_number":"0.6.0","name":"Create 0.6.0","date_published":"2026-02-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
                    .Replace("__HASH__", hash, StringComparison.Ordinal))
            .RespondTo(
                "/version_files",
                HttpStatusCode.OK,
                """{"__HASH__":{"id":"V1","project_id":"PROJ","version_number":"0.5.1f","name":"Create 0.5.1f","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}"""
                    .Replace("__HASH__", hash, StringComparison.Ordinal));

        var catalog = NewCatalog(handler);
        await catalog.RefreshModsAsync("1.20.4-Forge");

        Assert.AreEqual(ModState.Updatable, catalog.GetMods("1.20.4-Forge").Single().State, "这一行确实有更新。");

        var request = catalog.CreateModUpdateRequest("1.20.4-Forge");
        Assert.IsNotNull(request, "一键更新要覆盖所有非最新模组，包括被禁用的。");

        var target = request.Targets.Single();
        Assert.AreEqual("V2", target.VersionId);
        Assert.IsTrue(target.KeepDisabled, "禁用的模组升级后仍要保持 `.disabled`，不能顺手启用。");
    }

    [TestMethod]
    public void WithoutAnInstanceThereIsNoUpdateRequest()
    {
        var catalog = NewCatalog();

        Assert.IsNull(catalog.CreateModUpdateRequest(string.Empty));
        Assert.IsNull(catalog.CreateModUpdateRequest("不存在的实例"));
    }

    [TestMethod]
    public void LoaderAndGameDependenciesAreNotReportedAsMissingMods()
    {
        var directory = CreateInstance("1.20.1-Fabric", "1.20.1", LoaderKind.Fabric, "0.15.7");
        WriteJar(
            Path.Combine(directory, "mods", "sodium.jar"),
            ("fabric.mod.json", """{"id":"sodium","version":"0.5.8","name":"Sodium","depends":{"minecraft":">=1.20.1","fabricloader":">=0.14.0","java":">=17"}}"""));

        var summary = NewCatalog().GetModSummary("1.20.1-Fabric");

        Assert.AreEqual(0, summary.MissingDependency, "`minecraft` / `fabricloader` / `java` 不是装在 mods 里的模组。");
        Assert.IsNull(summary.MissingDependencyDetail);
    }

    [TestMethod]
    public void TheScanIsReusedUntilTheFolderChanges()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var modsDirectory = Path.Combine(directory, "mods");
        WriteJar(
            Path.Combine(modsDirectory, "create.jar"),
            ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create"}"""));

        var catalog = NewCatalog();
        var first = catalog.GetMods("1.20.4-Forge")[0];

        // 同一个内容戳下复用同一份扫描结果（绑定刷新会反复读它），但**目录一变必须立刻看得见**：
        // 用户往 mods 里拖一个 jar 再回到这一页，看见的就该是新的那一行。
        Assert.AreSame(first, catalog.GetMods("1.20.4-Forge")[0]);

        WriteJar(
            Path.Combine(modsDirectory, "flywheel.jar"),
            ("fabric.mod.json", """{"id":"flywheel","version":"0.6.10","name":"Flywheel"}"""));

        Assert.AreEqual(2, catalog.GetMods("1.20.4-Forge").Count);
    }

    [TestMethod]
    public void ABrokenJarFallsBackToItsFileNameInsteadOfBreakingTheTable()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var modsDirectory = Path.Combine(directory, "mods");
        Directory.CreateDirectory(modsDirectory);
        File.WriteAllText(Path.Combine(modsDirectory, "truncated.jar"), "这不是一个 zip");

        var mods = NewCatalog().GetMods("1.20.4-Forge");

        Assert.AreEqual(1, mods.Count);
        Assert.AreEqual("truncated", mods[0].Name);
        Assert.AreEqual("—", mods[0].Version, "读不出内容时写纯数据键「—」，绝不编一个版本号。");
        Assert.AreEqual(ModState.Normal, mods[0].State);
    }

    // ------------------------------------------------- 模组动作（M7-g-2）---

    [TestMethod]
    public void DisablingAndEnablingAModRenamesTheFileInPlace()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var jar = Path.Combine(directory, "mods", "create-1.20.4-0.5.1f.jar");
        WriteJar(jar, ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create"}"""));

        var catalog = NewCatalog();

        var disabled = catalog.SetModEnabled("1.20.4-Forge", "create-1.20.4-0.5.1f.jar", enabled: false);

        Assert.IsTrue(disabled.Succeeded, "禁用是改名，不该失败。");
        Assert.IsNull(disabled.Message, "成功且无需交代时不给句子（界面不会多出一行）。");
        Assert.IsFalse(File.Exists(jar), "原来的 .jar 不在了。");
        Assert.IsTrue(File.Exists(jar + ".disabled"), "文件还在，只是多了 .disabled 后缀（禁用永远可逆）。");

        var row = catalog.GetMods("1.20.4-Forge").Single();

        Assert.AreEqual(ModEnablement.Disabled, row.Enablement);
        Assert.AreEqual(
            "create-1.20.4-0.5.1f.jar.disabled",
            row.FileName,
            "文件名照原样留着，只在末尾加后缀（界面的下一次动作按它点名）。");

        var enabled = catalog.SetModEnabled("1.20.4-Forge", row.FileName, enabled: true);

        Assert.IsTrue(enabled.Succeeded);
        Assert.IsTrue(File.Exists(jar), "启用 = 去掉后缀，文件回到原来的名字。");
        Assert.IsFalse(File.Exists(jar + ".disabled"));
        Assert.AreEqual(ModEnablement.Enabled, catalog.GetMods("1.20.4-Forge").Single().Enablement);

        // 幂等：已经是目标状态时不碰文件（按钮本来就该是灰的，这里是第二道保险）。
        var again = catalog.SetModEnabled("1.20.4-Forge", "create-1.20.4-0.5.1f.jar", enabled: true);

        Assert.IsTrue(again.Succeeded);
        Assert.IsTrue(File.Exists(jar));
    }

    [TestMethod]
    public void ModActionsRefuseToOverwriteAnExistingFile()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var enabledJar = Path.Combine(directory, "mods", "create.jar");
        var disabledJar = Path.Combine(directory, "mods", "create.jar.disabled");
        WriteJar(enabledJar, ("fabric.mod.json", """{"id":"create","version":"1","name":"Create"}"""));
        WriteJar(disabledJar, ("fabric.mod.json", """{"id":"create","version":"1","name":"Create"}"""));

        var result = NewCatalog().SetModEnabled("1.20.4-Forge", "create.jar.disabled", enabled: true);

        Assert.IsFalse(result.Succeeded, "两个文件同时在时不能挑一个覆盖 —— 那是用户的文件。");
        Assert.IsNotNull(result.Message);
        StringAssert.Contains(result.Message.Key, "同时存在");
        Assert.AreEqual(2, result.Message.Parameters.Count);
        Assert.IsTrue(File.Exists(enabledJar), "两个文件都没有被碰过。");
        Assert.IsTrue(File.Exists(disabledJar));
    }

    [TestMethod]
    public void DeletingAModMovesItIntoTheBackupDirectory()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var jar = Path.Combine(directory, "mods", "create.jar");
        WriteJar(jar, ("fabric.mod.json", """{"id":"create","version":"1","name":"Create"}"""));

        var catalog = NewCatalog();
        var result = catalog.DeleteMod("1.20.4-Forge", "create.jar");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Message, "删掉的模组要告诉用户被移到了哪里。");
        Assert.IsFalse(File.Exists(jar), "mods 目录里不再有它。");

        var moved = Directory
            .EnumerateFiles(Path.Combine(directory, "backups", "mods"), "create.jar", SearchOption.AllDirectories)
            .ToArray();

        Assert.AreEqual(1, moved.Length, "文件被移进 backups\\mods\\<时间戳>\\，用户随时能拿回来。");
        Assert.AreEqual(0, catalog.GetMods("1.20.4-Forge").Count);
    }

    [TestMethod]
    public void ModActionsRefusePathsThatEscapeTheModsFolder()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        WriteJar(
            Path.Combine(directory, "mods", "create.jar"),
            ("fabric.mod.json", """{"id":"create","version":"1","name":"Create"}"""));
        var outside = Path.Combine(directory, "escape.jar");
        File.WriteAllText(outside, "not a jar");

        var catalog = NewCatalog();

        foreach (var name in new[] { @"..\escape.jar", "sub/escape.jar", @"C:\Windows\system32\evil.jar" })
        {
            var renamed = catalog.SetModEnabled("1.20.4-Forge", name, enabled: false);
            var deleted = catalog.DeleteMod("1.20.4-Forge", name);

            Assert.IsFalse(renamed.Succeeded, $"文件名「{name}」必须被拒绝（它是跨层传回来的不可信输入）。");
            Assert.IsFalse(deleted.Succeeded);
            Assert.IsNotNull(renamed.Message);
            Assert.IsNotNull(deleted.Message);
        }

        Assert.IsTrue(File.Exists(outside), "越界的文件一个字节都没动。");
        Assert.IsTrue(File.Exists(Path.Combine(directory, "mods", "create.jar")), "正常的模组也没被误伤。");
    }

    [TestMethod]
    public void AMissingModFileIsReportedInsteadOfPretendingSuccess()
    {
        CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var catalog = NewCatalog();

        var result = catalog.DeleteMod("1.20.4-Forge", "它已经不在了.jar");

        Assert.IsFalse(result.Succeeded, "用户在资源管理器里删掉它之后，这里不能报成功。");
        Assert.IsNotNull(result.Message);
        StringAssert.Contains(result.Message.Key, "找不到");
    }

    [TestMethod]
    public async Task RenamingAModKeepsItsModrinthSource()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var jar = Path.Combine(directory, "mods", "create.jar");
        WriteJar(jar, ("fabric.mod.json", """{"id":"create","version":"0.5.1f","name":"Create"}"""));

        const string Response = """{"__HASH__":{"id":"V1","project_id":"PROJ","version_number":"0.5.1f","name":"Create 0.5.1f","date_published":"2026-01-01T00:00:00Z","game_versions":["1.20.4"],"loaders":["forge"]}}""";

        var hash = Sha1OfFile(jar);
        var handler = new StubHandler()
            .RespondTo("/version_files/update", HttpStatusCode.OK, Response.Replace("__HASH__", hash, StringComparison.Ordinal))
            .RespondTo("/version_files", HttpStatusCode.OK, Response.Replace("__HASH__", hash, StringComparison.Ordinal));

        var catalog = NewCatalog(handler);
        await catalog.RefreshModsAsync("1.20.4-Forge");

        Assert.AreEqual(ContentSource.Modrinth, catalog.GetMods("1.20.4-Forge").Single().Source);

        var result = catalog.SetModEnabled("1.20.4-Forge", "create.jar", enabled: false);

        Assert.IsTrue(result.Succeeded);

        var disabled = catalog.GetMods("1.20.4-Forge").Single();

        Assert.AreEqual(ModEnablement.Disabled, disabled.Enablement);
        Assert.AreEqual(
            ContentSource.Modrinth,
            disabled.Source,
            "改名不改内容：哈希索引跟着搬，否则禁用一次就退回「本地文件」（要等下一次对账才恢复）。");
    }

    // ---------------------------------------------------------------- 资源包 ---

    [TestMethod]
    public void ResourcePackOrderAndEnablementComeFromOptionsTxt()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);

        WriteJar(
            Path.Combine(packs, "Faithful 32x.zip"),
            ("pack.mcmeta", """{"pack":{"pack_format":15,"description":"x"}}"""));
        WriteJar(Path.Combine(packs, "Stay True.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));
        File.WriteAllText(
            Path.Combine(directory, "options.txt"),
            "version:3465\nresourcePacks:[\"vanilla\",\"file/Faithful 32x.zip\"]\nincompatibleResourcePacks:[]\n");

        var entries = NewCatalog().GetResourcePacks("1.20.4-Forge");

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("Faithful 32x", entries[0].Name);
        Assert.IsTrue(entries[0].IsEnabled);
        Assert.AreEqual(1, entries[0].Priority);
        Assert.IsTrue(entries[0].IsActive);

        // 名字里有 32x 才敢写 32×；名字里没有倍率的那个用包自己的 pack_format（第 ⑩ 条：数据就是数据）。
        Assert.AreEqual("{0}× · {1} · 优先级 {2}", entries[0].Detail.Key);
        Assert.AreEqual("32", entries[0].Detail.Parameters[0]);
        Assert.AreEqual("1", entries[0].Detail.Parameters[2]);
        Assert.AreEqual("格式 {0} · {1} · 优先级 {2}", entries[1].Detail.Key, "认不出倍率就报包自己的格式号，不猜。");
        Assert.AreEqual("15", entries[1].Detail.Parameters[0]);
        Assert.AreEqual("2", entries[1].Detail.Parameters[2]);

        // 没写进 options.txt 的包是**未启用**（游戏里新放进去的包默认不开）。
        Assert.IsFalse(entries[1].IsEnabled);
        Assert.AreEqual(2, entries[1].Priority, "未启用的排在已启用之后。");
    }

    [TestMethod]
    public void ResourcePackHelpersReadTheGamesOwnNotation()
    {
        Assert.AreEqual("Faithful 32x", ResourcePackScan.IdToName("file/Faithful 32x.zip"));
        Assert.AreEqual(string.Empty, ResourcePackScan.IdToName("vanilla"), "内置包不在 resourcepacks 目录里。");
        Assert.AreEqual(32, ResourcePackScan.ResolutionOf("Faithful 32x"));
        Assert.AreEqual(64, ResourcePackScan.ResolutionOf("ShaderPack_64X"));
        Assert.IsNull(ResourcePackScan.ResolutionOf("Stay True"), "认不出来就不猜。");
        Assert.IsNull(ResourcePackScan.ResolutionOf("Better Leaves 1.20.4x"), "版本号里的 x 不是倍率。");
        Assert.AreEqual(
            "[\"file/A.zip\"]",
            ResourcePackScan.OptionsValue(["fov:0.0", "resourcePacks:[\"file/A.zip\"]"], "resourcePacks"));
        Assert.IsNull(ResourcePackScan.OptionsValue(["fov:0.0"], "resourcePacks"));
    }

    // ------------------------------------------------- 资源包动作（M7-g-2b）---

    [TestMethod]
    public void EnablingAResourcePackAppendsItAtTheLowestUserPriority()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "Faithful 32x.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));
        WriteJar(Path.Combine(packs, "Stay True.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        var optionsPath = Path.Combine(directory, "options.txt");
        File.WriteAllText(optionsPath, "version:3465\r\nresourcePacks:[\"vanilla\"]\r\nfov:0.5\r\n");

        var catalog = NewCatalog();
        var enabled = catalog.SetResourcePackEnabled("1.20.4-Forge", "Faithful 32x.zip", enabled: true);

        Assert.IsTrue(enabled.Succeeded);
        Assert.IsNull(enabled.Message);

        var text = File.ReadAllText(optionsPath);

        // 新启用的包插在**内置包之后** —— 用户区的最低优先级，不覆盖他排好的其它包。
        StringAssert.Contains(text, "resourcePacks:[\"vanilla\",\"file/Faithful 32x.zip\"]", "启用的 id 要落进 options.txt。");
        StringAssert.Contains(text, "version:3465", "其它设置行必须原样保留。");
        StringAssert.Contains(text, "fov:0.5");
        StringAssert.Contains(text, "\r\n", "原文件的 CRLF 不能被换成 LF。");

        var entries = catalog.GetResourcePacks("1.20.4-Forge");

        Assert.AreEqual(1, entries.Count(static entry => entry.IsEnabled));
        Assert.AreEqual(1, entries.Single(static entry => entry.IsEnabled).Priority);

        // 幂等：再启用一次不写第二个 id。
        Assert.IsTrue(catalog.SetResourcePackEnabled("1.20.4-Forge", "Faithful 32x.zip", enabled: true).Succeeded);
        Assert.AreEqual(
            1,
            Regex.Count(File.ReadAllText(optionsPath), "file/Faithful 32x.zip"));
    }

    [TestMethod]
    public void EnablingAnotherPackKeepsTheExistingOneAboveIt()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "Faithful 32x.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));
        WriteJar(Path.Combine(packs, "Stay True.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        var optionsPath = Path.Combine(directory, "options.txt");
        File.WriteAllText(optionsPath, "resourcePacks:[\"vanilla\",\"file/Faithful 32x.zip\"]\n");

        Assert.IsTrue(
            NewCatalog().SetResourcePackEnabled("1.20.4-Forge", "Stay True.zip", enabled: true).Succeeded);

        // 低 → 高：新包排在旧包**前面** = 优先级更低；旧的 Faithful 仍然压在上面。
        Assert.AreEqual(
            "resourcePacks:[\"vanilla\",\"file/Stay True.zip\",\"file/Faithful 32x.zip\"]",
            File.ReadAllText(optionsPath).Trim());
    }

    [TestMethod]
    public void DisablingRemovesOnlyThatPackFromTheList()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "Faithful 32x.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));
        WriteJar(Path.Combine(packs, "Stay True.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        var optionsPath = Path.Combine(directory, "options.txt");
        File.WriteAllText(
            optionsPath,
            "resourcePacks:[\"vanilla\",\"file/Stay True.zip\",\"file/Faithful 32x.zip\"]\r\n");

        var catalog = NewCatalog();

        Assert.IsTrue(catalog.SetResourcePackEnabled("1.20.4-Forge", "Stay True.zip", enabled: false).Succeeded);

        StringAssert.Contains(
            File.ReadAllText(optionsPath),
            "resourcePacks:[\"vanilla\",\"file/Faithful 32x.zip\"]",
            "只去掉这一个，另一个的启用状态不能被顺手改掉。");
        Assert.AreEqual(
            1,
            catalog.GetResourcePacks("1.20.4-Forge").Count(static entry => entry.IsEnabled));
    }

    [TestMethod]
    public void DisplayOrderIsTheReverseOfTheOptionsArray()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "Faithful 32x.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));
        WriteJar(Path.Combine(packs, "Stay True.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        // 低 → 高：Faithful 先加载，Stay True 覆盖它 —— 游戏里 Stay True 在上面。
        File.WriteAllText(
            Path.Combine(directory, "options.txt"),
            "resourcePacks:[\"vanilla\",\"file/Faithful 32x.zip\",\"file/Stay True.zip\"]\r\n");

        var entries = NewCatalog().GetResourcePacks("1.20.4-Forge");

        Assert.AreEqual("Stay True", entries[0].Name, "列表第一行 = 游戏里最上面 = 最高优先级。");
        Assert.AreEqual(1, entries[0].Priority);
        Assert.AreEqual("Faithful 32x", entries[1].Name);
        Assert.AreEqual(2, entries[1].Priority);

        // 单测 M7-a 那一版是反的（第一个 = 优先级 1），这里把方向钉死。
        Assert.AreEqual("Stay True.zip", entries[0].FileName, "行上的文件名是磁盘事实（zip 带扩展名），动作按它点名。");
    }

    [TestMethod]
    public void MovingAPackUpRaisesItsPriorityAndDownLowersIt()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "A.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));
        WriteJar(Path.Combine(packs, "B.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        var optionsPath = Path.Combine(directory, "options.txt");
        File.WriteAllText(optionsPath, "resourcePacks:[\"vanilla\",\"file/A.zip\",\"file/B.zip\"]\r\n");

        var catalog = NewCatalog();

        // A 原本是优先级 2（低），上移到优先级 1（高）。
        Assert.IsTrue(catalog.MoveResourcePack("1.20.4-Forge", "A.zip", direction: -1).Succeeded);
        Assert.AreEqual(
            "resourcePacks:[\"vanilla\",\"file/B.zip\",\"file/A.zip\"]",
            File.ReadAllText(optionsPath).Trim());
        Assert.AreEqual("A", catalog.GetResourcePacks("1.20.4-Forge")[0].Name);

        // 再下移回来。
        Assert.IsTrue(catalog.MoveResourcePack("1.20.4-Forge", "A.zip", direction: 1).Succeeded);
        Assert.AreEqual(
            "resourcePacks:[\"vanilla\",\"file/A.zip\",\"file/B.zip\"]",
            File.ReadAllText(optionsPath).Trim());

        // 到顶 / 到底都是幂等成功（界面按钮是灰的，这里是第二道保险），且**不写文件**。
        var before = File.ReadAllText(optionsPath);
        Assert.IsTrue(catalog.MoveResourcePack("1.20.4-Forge", "B.zip", direction: -1).Succeeded);
        Assert.AreEqual(before, File.ReadAllText(optionsPath), "已经在最高优先级：上移不该改任何东西。");
        Assert.IsTrue(catalog.MoveResourcePack("1.20.4-Forge", "A.zip", direction: 1).Succeeded);
        Assert.AreEqual(before, File.ReadAllText(optionsPath), "已经最低（内置包之上）：下移不该把它挪到 vanilla 下面。");
    }

    [TestMethod]
    public void MovingADisabledPackIsRefused()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "A.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        File.WriteAllText(Path.Combine(directory, "options.txt"), "resourcePacks:[\"vanilla\"]\r\n");

        var result = NewCatalog().MoveResourcePack("1.20.4-Forge", "A.zip", direction: -1);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Message);
        StringAssert.Contains(result.Message.Key, "没有启用");
    }

    [TestMethod]
    public void ABrokenOptionsFileIsNeverOverwritten()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "A.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        var optionsPath = Path.Combine(directory, "options.txt");
        const string Broken = "resourcePacks:[\"vanilla\",\r\nfov:0.5\r\n";
        File.WriteAllText(optionsPath, Broken);

        var catalog = NewCatalog();

        var result = catalog.SetResourcePackEnabled("1.20.4-Forge", "A.zip", enabled: true);

        Assert.IsFalse(result.Succeeded, "读不懂的 options.txt 只能如实报告，不能顺手清空。");
        Assert.IsNotNull(result.Message);
        StringAssert.Contains(result.Message.Key, "不是合法的字符串数组");
        Assert.AreEqual(Broken, File.ReadAllText(optionsPath), "原文件一个字节都没动。");
    }

    [TestMethod]
    public void DeletingAResourcePackMovesItIntoTheBackupDirectoryAndDropsItsId()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        var zip = Path.Combine(packs, "Faithful 32x.zip");
        WriteJar(zip, ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));

        var optionsPath = Path.Combine(directory, "options.txt");
        File.WriteAllText(optionsPath, "resourcePacks:[\"vanilla\",\"file/Faithful 32x.zip\"]\r\n");

        var catalog = NewCatalog();
        var result = catalog.DeleteResourcePack("1.20.4-Forge", "Faithful 32x.zip");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Message, "删掉的资源包要告诉用户被移到了哪里。");
        Assert.IsFalse(File.Exists(zip), "resourcepacks 目录里不再有它。");

        var moved = Directory
            .EnumerateFiles(Path.Combine(directory, "backups", "resourcepacks"), "Faithful 32x.zip", SearchOption.AllDirectories)
            .ToArray();

        Assert.AreEqual(1, moved.Length, "文件被移进 backups\\resourcepacks\\<时间戳>\\，可以从那里拿回来。");
        Assert.AreEqual(
            "resourcePacks:[\"vanilla\"]",
            File.ReadAllText(optionsPath).Trim(),
            "删掉的包不能留在启用列表里（否则游戏会留着一条指向不存在文件的 id）。");
        Assert.AreEqual(0, catalog.GetResourcePacks("1.20.4-Forge").Count);
    }

    [TestMethod]
    public void ResourcePackActionsRefusePathsThatEscapeThePackFolder()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var packs = Path.Combine(directory, "resourcepacks");
        Directory.CreateDirectory(packs);
        WriteJar(Path.Combine(packs, "A.zip"), ("pack.mcmeta", """{"pack":{"pack_format":15}}"""));
        var outside = Path.Combine(directory, "escape.zip");
        File.WriteAllText(outside, "not a pack");

        var catalog = NewCatalog();

        foreach (var name in new[] { @"..\escape.zip", "sub/escape.zip", @"C:\Windows\system32\evil.zip" })
        {
            Assert.IsFalse(catalog.SetResourcePackEnabled("1.20.4-Forge", name, enabled: true).Succeeded, $"「{name}」必须被拒绝。");
            Assert.IsFalse(catalog.MoveResourcePack("1.20.4-Forge", name, direction: -1).Succeeded);
            Assert.IsFalse(catalog.DeleteResourcePack("1.20.4-Forge", name).Succeeded);
        }

        Assert.IsTrue(File.Exists(outside), "越界的文件一个字节都没动。");
        Assert.IsTrue(File.Exists(Path.Combine(packs, "A.zip")));
    }

    // ------------------------------------------------------------------ 存档 ---

    [TestMethod]
    public void WorldSavesComeFromLevelDat()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var saves = Path.Combine(directory, "saves");
        var played = Now.AddHours(-1);

        WriteLevelDat(
            Path.Combine(saves, "New World", "level.dat"),
            levelName: "新世界",
            gameType: 1,
            lastPlayed: played,
            versionName: "1.20.4");

        Directory.CreateDirectory(Path.Combine(saves, "坏存档"));
        File.WriteAllBytes(Path.Combine(saves, "坏存档", "level.dat"), [1, 2, 3, 4, 5]);
        Directory.CreateDirectory(Path.Combine(saves, "没有 level.dat"));
        Directory.CreateDirectory(Path.Combine(directory, "backups"));

        // 备份文件名只认**存档目录名**（M7-f 的写入端就是这个约定），不是 level.dat 里的显示名。
        File.WriteAllBytes(Path.Combine(directory, "backups", "New World.zip"), [0]);

        // 同名**目录**不算备份：恢复时换下来的旧存档就放在 backups\saves\... 里（M7-f），
        // 多认一种形状的话，一个名叫 saves 的存档会因为那个回收目录被报成"已备份"。
        Directory.CreateDirectory(Path.Combine(directory, "backups", "坏存档"));

        var entries = NewCatalog().GetWorldSaves("1.20.4-Forge");

        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual(1, entries.Count(static entry => entry.IsActive), "设计稿把第一行存档画成高亮行。");

        var world = entries.Single(static entry => entry.Name == "新世界");

        Assert.AreEqual("New World", world.Folder, "目录名是磁盘上的事实（备份与恢复都按它定）。");
        Assert.AreEqual("今天 {0}", world.LastPlayed.Key);
        CollectionAssert.AreEqual(
            new[] { played.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) },
            world.LastPlayed.Parameters.ToArray());
        Assert.AreEqual("创造", world.GameMode.Key);
        Assert.AreEqual("1.20.4", world.GameVersion);
        Assert.IsTrue(world.IsBackedUp, "<游戏目录>\\backups\\<存档目录名>.zip 才算已备份。");

        // 坏掉的 level.dat 不是"整页失败"：这一行按"信息未知"显示（纯数据键「—」）。
        var broken = entries.Single(static entry => entry.Name == "坏存档");

        Assert.AreEqual("—", broken.GameMode.Key);
        Assert.AreEqual("—", broken.GameVersion);
        Assert.IsFalse(broken.IsBackedUp, "backups 下有同名目录不算备份。");

        // 目录名不是显示名、但读不出 level.dat 时它就是唯一可用的名字。
        Assert.AreEqual("没有 level.dat", entries.Single(static entry => entry.Name == "没有 level.dat").Name);
    }

    [TestMethod]
    public void DeletingASaveMovesTheWholeDirectoryIntoBackupsSaves()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var saves = Path.Combine(directory, "saves");
        var save = Path.Combine(saves, "New World");

        WriteLevelDat(Path.Combine(save, "level.dat"), levelName: "新世界", gameType: 0, lastPlayed: Now, versionName: "1.20.4");
        File.WriteAllText(Path.Combine(save, "session.lock"), "keep me");

        var catalog = NewCatalog();
        var result = catalog.DeleteWorldSave("1.20.4-Forge", "New World");

        Assert.IsTrue(result.Succeeded, result.Message?.Key);
        Assert.IsFalse(Directory.Exists(save), "删除后源目录不该还在。");

        // 整个目录连同里面每个文件都被移走，而不是只删 level.dat。
        var moved = Directory
            .EnumerateDirectories(Path.Combine(directory, "backups", "saves"))
            .SelectMany(Directory.EnumerateDirectories)
            .Single(path => Path.GetFileName(path) == "New World");

        Assert.IsTrue(File.Exists(Path.Combine(moved, "session.lock")));
        Assert.IsTrue(File.Exists(Path.Combine(moved, "level.dat")));

        // 去向要写进给用户看的那句话（参数里是相对游戏目录的路径）。
        StringAssert.Contains(Uri.UnescapeDataString(result.Message!.Parameters[0]), @"backups\saves\");
        Assert.AreEqual(0, catalog.GetWorldSaves("1.20.4-Forge").Count, "列表按磁盘现算，删掉之后就不该再有这一行。");
    }

    [TestMethod]
    public void DeletingAnUnknownOrUnsafeSaveNameFailsWithoutTouchingAnything()
    {
        var directory = CreateInstance("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30");
        var outside = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");

        var catalog = NewCatalog();

        // 目录名是跨层传回来的不可信输入：不能穿出 saves\。
        Assert.IsFalse(catalog.DeleteWorldSave("1.20.4-Forge", "..\\outside").Succeeded);
        Assert.IsFalse(catalog.DeleteWorldSave("1.20.4-Forge", "Missing World").Succeeded);
        Assert.IsFalse(catalog.DeleteWorldSave("不存在的实例", "New World").Succeeded);

        Assert.IsTrue(File.Exists(Path.Combine(outside, "keep.txt")), "越界的目录一个字节都没动。");
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "backups")), "失败不该顺手建回收目录。");
    }

    // ------------------------------------------------------------ 导出实例 ---

    [TestMethod]
    public void ExportingAnInstanceWritesAMrpackWithOverridesAndDependencies()
    {
        var directory = CreateInstance("Pack", "1.20.4", LoaderKind.Forge, "49.0.30");
        Directory.CreateDirectory(Path.Combine(directory, "mods"));
        File.WriteAllText(Path.Combine(directory, "mods", "sodium.jar"), "jar");
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        File.WriteAllText(Path.Combine(directory, "config", "options.txt"), "config");
        File.WriteAllText(Path.Combine(directory, "options.txt"), "options");
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        File.WriteAllText(Path.Combine(directory, "logs", "latest.log"), "noise");
        Directory.CreateDirectory(Path.Combine(directory, "backups"));
        File.WriteAllText(Path.Combine(directory, "backups", "x.zip"), "noise");

        var destination = Path.Combine(_tempRoot, "export.mrpack");
        var result = NewCatalog().ExportInstance("Pack", destination);

        Assert.IsTrue(result.Succeeded, result.Message?.Key);
        Assert.IsTrue(File.Exists(destination));

        using var zip = ZipFile.OpenRead(destination);
        var names = zip.Entries.Select(static entry => entry.FullName).ToArray();
        CollectionAssert.Contains(names, "modrinth.index.json");
        CollectionAssert.Contains(names, "overrides/mods/sodium.jar");
        CollectionAssert.Contains(names, "overrides/config/options.txt");
        CollectionAssert.Contains(names, "overrides/options.txt");
        Assert.IsFalse(names.Any(static name => name.Contains("logs/", StringComparison.Ordinal)), "日志不是实例内容。");
        Assert.IsFalse(names.Any(static name => name.Contains("backups/", StringComparison.Ordinal)), "备份不是实例内容。");

        var indexEntry = zip.GetEntry("modrinth.index.json")!;
        using var reader = new StreamReader(indexEntry.Open());
        var index = reader.ReadToEnd();
        StringAssert.Contains(index, "\"minecraft\": \"1.20.4\"");
        StringAssert.Contains(index, "\"forge\": \"49.0.30\"");
        StringAssert.Contains(index, "\"formatVersion\": 1");
    }

    [TestMethod]
    public void ExportingIntoTheInstanceDirectoryIsRejected()
    {
        var directory = CreateInstance("Pack", "1.20.4", LoaderKind.Vanilla);
        var result = NewCatalog().ExportInstance("Pack", Path.Combine(directory, "inside.mrpack"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "inside.mrpack")));
    }

    [TestMethod]
    public void ExportingAnInstanceWithoutAGameVersionIsRejected()
    {
        var directory = CreateInstance("Pack", string.Empty, LoaderKind.Vanilla);
        var result = NewCatalog().ExportInstance("Pack", Path.Combine(_tempRoot, "no-version.mrpack"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(Directory.Exists(directory) && File.Exists(Path.Combine(_tempRoot, "no-version.mrpack")));
    }
    // -------------------------------------------------------------- NBT 读取 ---

    [TestMethod]
    public void BrokenNbtIsRejectedInsteadOfThrowing()
    {
        Assert.IsNull(NbtReader.Read([0x0A, 0x00, 0x00, 0x08, 0x00]), "声明了一个字符串却没有任何数据。");
        Assert.IsNull(NbtReader.Read([0x01, 0x00, 0x00]), "根标签不是复合标签。");

        var path = Path.Combine(_tempRoot, "level.dat");
        File.WriteAllText(path, "not gzip at all");
        Assert.IsNull(NbtReader.ReadGzipFile(path));
        Assert.IsNull(NbtReader.ReadGzipFile(Path.Combine(_tempRoot, "不存在.dat")));

        // 声称很长的集合：不该去分配它（这条钉住长度检查真的在）。
        Assert.IsNull(NbtReader.Read([0x0A, 0x00, 0x00, 0x09, 0x00, 0x01, 0x78, 0x08, 0x7F, 0xFF, 0xFF, 0xFF, 0x00]));
    }

    [TestMethod]
    public void NbtReadsTheTagsThatLevelDatUses()
    {
        var path = Path.Combine(_tempRoot, "level.dat");
        WriteLevelDat(path, levelName: "新世界", gameType: 0, lastPlayed: Now, versionName: "1.20.4");

        var root = NbtReader.ReadGzipFile(path);

        Assert.IsNotNull(root);
        Assert.AreEqual("新世界", root.Text("LevelName"));
        Assert.AreEqual(0, root.Number("GameType"));
        Assert.AreEqual("1.20.4", root.Compound("Version")!.Text("Name"));
        Assert.IsNull(root.Text("不存在"));
    }

    // ---------------------------------------------------------------- jar 元数据 ---

    [TestMethod]
    public void JarMetadataCoversTheFourEcosystems()
    {
        var directory = Path.Combine(_tempRoot, "jars");
        Directory.CreateDirectory(directory);

        var fabric = Path.Combine(directory, "fabric.jar");
        WriteJar(
            fabric,
            ("fabric.mod.json", """{"id":"sodium","version":"0.5.8","name":"Sodium","depends":{"fabric-api":"*"}}"""));

        var quilt = Path.Combine(directory, "quilt.jar");
        WriteJar(
            quilt,
            ("quilt.mod.json", """{"quilt_loader":{"id":"iris","version":"1.7.6","metadata":{"name":"Iris Shaders"},"depends":[{"id":"sodium","versions":">=0.5"}]}}"""));

        var forge = Path.Combine(directory, "forge.jar");
        WriteJar(forge, ("META-INF/mods.toml", ForgeToml));

        var legacy = Path.Combine(directory, "legacy.jar");
        WriteJar(
            legacy,
            ("mcmod.info", """[{"modid":"optifine","name":"OptiFine","version":"HD U E2","mcversion":"1.12.2","requiredMods":["flywheel@[0.6,)"]}]"""));

        var manifest = Path.Combine(directory, "manifest.jar");
        WriteJar(manifest, ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Title: Some Mod\nImplementation-Version: 1.2.3\n"));

        var broken = Path.Combine(directory, "broken.jar");
        File.WriteAllText(broken, "not a zip");

        var fabricMeta = JarMetadataReader.Read(fabric);

        Assert.AreEqual("Sodium", fabricMeta.DisplayName);
        Assert.AreEqual("0.5.8", fabricMeta.Version);
        Assert.AreEqual("sodium", fabricMeta.ModId);
        Assert.AreEqual("fabric-api", fabricMeta.Dependencies.Single().ModId);
        Assert.IsTrue(fabricMeta.Dependencies.Single().Mandatory);

        var quiltMeta = JarMetadataReader.Read(quilt);

        Assert.AreEqual("Iris Shaders", quiltMeta.DisplayName, "Quilt 的显示名在 quilt_loader.metadata.name 里。");
        Assert.AreEqual("iris", quiltMeta.ModId);
        Assert.AreEqual("sodium", quiltMeta.Dependencies.Single().ModId);

        var forgeMeta = JarMetadataReader.Read(forge);

        Assert.AreEqual("Create", forgeMeta.DisplayName);
        Assert.AreEqual("0.5.1f", forgeMeta.Version);
        Assert.AreEqual("create", forgeMeta.ModId);
        Assert.AreEqual(2, forgeMeta.Dependencies.Count);
        Assert.AreEqual("flywheel", forgeMeta.Dependencies[1].ModId);
        Assert.AreEqual("[0.6.10,)", forgeMeta.Dependencies[1].VersionRange);
        Assert.IsTrue(forgeMeta.Dependencies[1].Mandatory);

        var legacyMeta = JarMetadataReader.Read(legacy);

        Assert.AreEqual("OptiFine", legacyMeta.DisplayName);
        Assert.AreEqual("flywheel", legacyMeta.Dependencies.Single().ModId);
        Assert.AreEqual("[0.6,)", legacyMeta.Dependencies.Single().VersionRange);

        var manifestMeta = JarMetadataReader.Read(manifest);

        Assert.AreEqual("Some Mod", manifestMeta.DisplayName);
        Assert.AreEqual("1.2.3", manifestMeta.Version);

        // 损坏的 jar 退回"没有元数据"，而不是抛异常、也不是编一个名字。
        // （刻意逐字段断言：`Dependencies` 是列表，record 的自动相等对它用的是**引用相等**，
        //  拿整条记录去比会得到一个看起来通过、其实没比内容的断言。）
        var brokenMeta = JarMetadataReader.Read(broken);

        Assert.IsNull(brokenMeta.DisplayName);
        Assert.IsNull(brokenMeta.Version);
        Assert.IsNull(brokenMeta.ModId);
        Assert.AreEqual(0, brokenMeta.Dependencies.Count);
    }

    [TestMethod]
    public void MiniTomlKeepsMultiLineStringsOutOfTheKeyValuePairs()
    {
        var sections = MiniToml.Parse(ForgeToml);

        // 多行描述里故意放了 `=` 与看起来像表头的东西：它们必须整体被读掉，
        // 否则里面的假键值对会覆盖真键、假表头会凭空多出一张表。
        Assert.AreEqual(1, sections.Count(static section => section.Header == "mods"), "描述里的假表头不该多造一张表。");

        var mods = sections.Single(static section => section.Header == "mods");

        CollectionAssert.AreEquivalent(
            new[] { "modId", "version", "displayName", "description" },
            mods.Values.Keys.ToArray(),
            "描述里的 `=` 不该变成一个键。");
        StringAssert.Contains(mods.Values["description"], "多行描述里有一个 = 号");
        Assert.AreEqual("create", MiniToml.Value(sections, "mods", "modId"));
        Assert.AreEqual("Create", MiniToml.Value(sections, "mods", "displayName"));
        Assert.AreEqual("0.5.1f", MiniToml.Value(sections, "mods", "version"));

        // 同一张表头可以出现多次（Forge 的依赖就是这么写的），顺序必须保留。
        var dependencies = sections.Where(static section => section.Header == "dependencies.create").ToList();

        Assert.AreEqual(2, dependencies.Count);
        Assert.AreEqual("minecraft", dependencies[0].Values["modId"]);
        Assert.AreEqual("[1.20.4,1.21)", dependencies[0].Values["versionRange"]);
        Assert.AreEqual("flywheel", dependencies[1].Values["modId"]);
        Assert.AreEqual("[0.6.10,)", dependencies[1].Values["versionRange"]);
        Assert.AreEqual("[1.20.4,1.21)", MiniToml.Value(sections, "dependencies.create", "versionRange"), "取的是第一次出现的那张表。");
    }

    // ------------------------------------------------------------------ 工具 ---

    /// <summary>Forge 1.20.4 的 <c>mods.toml</c> 形状（含注释、多行字符串与两张表）。</summary>
    private const string ForgeToml = """
        modLoader="javafml"
        loaderVersion="[47,)"
        license="MIT"

        # 这一行是注释
        [[mods]]
        modId="create"
        version="0.5.1f"
        displayName="Create"
        description='''
        多行描述里有一个 = 号
        还有一行看起来像表头 [[mods]]
        '''
        [[dependencies.create]]
        modId="minecraft"
        mandatory=true
        versionRange="[1.20.4,1.21)"
        [[dependencies.create]]
        modId="flywheel"
        mandatory=true
        versionRange="[0.6.10,)"
        """;

    /// <summary>一个 jar 的真实 SHA-1（测试里也走同一条协议：SHA-1 是 Modrinth 的内容寻址方式）。</summary>
    private static string Sha1OfFile(string path)
    {
        using var stream = File.OpenRead(path);

#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private static void WriteJar(string path, params (string Name, string Content)[] entries)    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    /// <summary>写一个 gzip 压缩的 <c>level.dat</c>（字段为 <see langword="null"/> 时整个标签不写）。</summary>
    private static void WriteLevelDat(
        string path,
        string? levelName,
        int gameType,
        DateTimeOffset? lastPlayed,
        string? versionName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        List<byte> bytes = [];
        bytes.Add(10);
        AddName(bytes, string.Empty);

        if (levelName is not null)
        {
            AddString(bytes, "LevelName", levelName);
        }

        AddInt(bytes, "GameType", gameType);

        if (lastPlayed is not null)
        {
            AddLong(bytes, "LastPlayed", lastPlayed.Value.ToUnixTimeMilliseconds());
        }

        if (versionName is not null)
        {
            bytes.Add(10);
            AddName(bytes, "Version");
            AddString(bytes, "Name", versionName);
            bytes.Add(0);
        }

        bytes.Add(0);

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write([.. bytes]);
    }

    private static void AddName(List<byte> bytes, string name)
    {
        var encoded = Encoding.UTF8.GetBytes(name);
        bytes.Add((byte)(encoded.Length >> 8));
        bytes.Add((byte)(encoded.Length & 0xFF));
        bytes.AddRange(encoded);
    }

    private static void AddString(List<byte> bytes, string name, string value)
    {
        bytes.Add(8);
        AddName(bytes, name);
        AddName(bytes, value);
    }

    private static void AddInt(List<byte> bytes, string name, int value)
    {
        bytes.Add(3);
        AddName(bytes, name);
        bytes.AddRange(BigEndian(value, 4));
    }

    private static void AddLong(List<byte> bytes, string name, long value)
    {
        bytes.Add(4);
        AddName(bytes, name);
        bytes.AddRange(BigEndian(value, 8));
    }

    private static byte[] BigEndian(long value, int length)
    {
        var result = new byte[length];
        for (var index = 0; index < length; index++)
        {
            result[length - 1 - index] = (byte)(value >> (8 * index));
        }

        return result;
    }
}
