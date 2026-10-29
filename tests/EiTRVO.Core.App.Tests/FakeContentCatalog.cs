using EiTRVO.Core.Launcher;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 内容目录的测试替身（形状固定的输入）。
/// </summary>
/// <remarks>
/// M7-a 把 <c>SampleContentCatalog</c> 从产品代码里删掉了（真实实现 = <c>FileContentCatalog</c>），
/// 但那一批数据对**界面测试**仍然有用：p07 的备注、p07 的统计、p09 的资源包与存档投影
/// 都需要"确定形状"的输入，才能钉住"英文界面下这几句真的是英文"
/// （AGENTS.md §5.3 第 ⑨ / ⑩ 条 —— 中文界面上"没拼、直接显示键"看起来完全正常）。
/// <para>
/// 演示数据源不该留在产品里（§7 自检项：接上真实实现就删掉演示注册），留在测试里正合适。
/// 内核侧的真实实现由 <c>ContentCatalogTests</c> 在临时目录上跑真实文件。
/// </para>
/// </remarks>
internal sealed class FakeContentCatalog : IContentCatalog
{
    private static TextTemplate NoteNeed(string dependency) => TextTemplate.Of("需要 {0}", dependency);

    private static TextTemplate NoteLatest(string version) => TextTemplate.Of("最新 {0}", version);

    private static TextTemplate NoteConflict(string other) => TextTemplate.Of("与 {0} 冲突", other);

    private static readonly TextTemplate NoteNotOnModrinth = TextTemplate.Of("不在 Modrinth 收录");

    private static TextTemplate Data(string value) => TextTemplate.Of("{0}", value);

    /// <summary>模组备注：缺前置依赖的那一行（与真实实现同一套键）。</summary>
    private static readonly ModEntry[] ForgeMods =
    [
        new("Create", "create-0.5.1f.jar", "0.5.1f", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.MissingDependency, NoteNeed("flywheel"), true),
        new("Sodium", "sodium-0.5.8.jar", "0.5.8", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.Updatable, NoteLatest("0.6.5"), false),
        new("JEI (Just Enough Items)", "jei-17.3.0.49.jar", "17.3.0.49", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.Normal, null, false),
        new("Flywheel", "flywheel-0.6.10.jar", "0.6.10", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.Normal, null, false),
        new("OptiFine（作为模组）", "optifine-1.20.4.jar.disabled", "HD U I8", ContentSource.LocalFile, "1.20.4", ModEnablement.Disabled, ModState.Normal, NoteConflict("Sodium"), false),
        new("未收录模组（3 个）", "unknown-1.jar", "—", ContentSource.LocalFile, "1.20.4", ModEnablement.Unverified, ModState.Updatable, NoteNotOnModrinth, false),
    ];

    /// <summary>
    /// 对过账（M7-c）之后的同一批行：来源变成 Modrinth、有一条可更新、有一条"不在 Modrinth 收录"。
    /// </summary>
    /// <remarks>
    /// 只有打开 <see cref="ReconcileOnRefresh"/> 的用例才拿得到它 —— 其余用例要的是"刷新的**接线**"，
    /// 不是"刷新的效果"。
    /// </remarks>
    private static readonly ModEntry[] ReconciledForgeMods =
    [
        new("Create", "create-0.5.1f.jar", "0.5.1f", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.MissingDependency, NoteNeed("flywheel"), true),
        new("Sodium", "sodium-0.5.8.jar", "0.5.8", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.Updatable, NoteLatest("0.6.5"), false),
        new("JEI (Just Enough Items)", "jei-17.3.0.49.jar", "17.3.0.49", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.Normal, null, false),
        new("Flywheel", "flywheel-0.6.10.jar", "0.6.10", ContentSource.Modrinth, "1.20.4", ModEnablement.Enabled, ModState.Normal, null, false),
        new("OptiFine（作为模组）", "optifine-1.20.4.jar.disabled", "HD U I8", ContentSource.LocalFile, "1.20.4", ModEnablement.Disabled, ModState.Normal, NoteNotOnModrinth, false),
        new("未收录模组（3 个）", "unknown-1.jar", "—", ContentSource.LocalFile, "1.20.4", ModEnablement.Unverified, ModState.Normal, NoteNotOnModrinth, false),
    ];

    /// <remarks>
    /// M7-g-2b：每一行带**磁盘文件名**（`FileName`，zip 带扩展名）—— 启用 / 停用 / 上下移 / 删除都按它点名，
    /// 而显示名与文件名不一定一样（zip 去扩展名、文件夹用目录名）。
    /// </remarks>
    private static readonly ResourcePackEntry[] ResourcePackRows =
    [
        new("Faithful 32x", "Faithful 32x.zip", PackDetail("32", "18.4 MB", 1), 1, true, true),
        new("Fresh Animations", "Fresh Animations.zip", PackDetailAnimated("4.2 MB", 2), 2, true, false),
        new("Stay True", "Stay True.zip", PackDetail("16", "6.1 MB", 3), 3, false, false),
        new("Bare Bones", "Bare Bones.zip", PackDetail("16", "3.8 MB", 4), 4, false, false),
    ];

    /// <remarks>
    /// M7-f：每一行都带**存档目录名**（<c>Folder</c>），它与显示名刻意不一样 ——
    /// 备份文件名与恢复目标只认目录名，而 p09 的表格显示的是显示名。
    /// 前两个已备份（设计稿把前两行画成「已备份」）。
    /// </remarks>
    private static readonly WorldSaveEntry[] WorldSaves =
    [
        new("新世界", "New World", "412 MB", Today("16:01"), Survival(), "1.20.4", true, true),
        new("机械工厂", "Factory", "1.2 GB", At("2026/9/28 22:40"), Creative(), "1.20.4", true, false),
        new("空岛生存", "SkyBlock", "88 MB", At("2026/9/25 19:12"), Survival(), "1.20.4", false, false),
        new("红石测试", "Redstone", "24 MB", At("2026/9/21 15:33"), Creative(), "1.20.4", false, false),
    ];

    private static readonly ModpackEntry[] Modpacks =
    [
        new("Better MC [FORGE] 1.20.4", "v34", 216, "12.4 GB", "1.20.4-Forge", TextTemplate.Of("已同步"), Tone.Ok, true),
        new("Fabulously Optimized 1.20.1", "v5.9", 42, "6.1 GB", "1.20.1-Fabric", TextTemplate.Of("3 个模组可更新"), Tone.Warn, false),
    ];

    private static readonly ModrinthEntry[] ModrinthCatalog =
    [
        new("Better MC [FORGE]", "老牌整合包，任务线 + 性能优化", "12.4M", "v34", "better-mc", "v34"),
        new("Prominence II RPG", "RPG 向整合包，含自定义维度", "6.1M", "v2.8.4", "prominence", "v2.8.4"),
    ];

    /// <summary>
    /// 当前这一份模组列表。
    /// </summary>
    /// <remarks>
    /// M7-g-2 起是**可变的**：启用 / 禁用 / 删除的接线用例要看到"动作之后那一行真的变了"，
    /// 而输入形状本身仍来自上面那两份静态数组（多数用例只关心它们）。
    /// </remarks>
    private List<ModEntry>? _mods;

    private List<ModEntry> Mods => _mods ??= [.. (_reconciled ? ReconciledForgeMods : ForgeMods)];

    /// <inheritdoc />
    public IReadOnlyList<ModEntry> GetMods(string instanceName) =>
        string.Equals(instanceName, "1.20.4-Forge", StringComparison.Ordinal) ? Mods : [];

    /// <summary>「启用所选」交给内核的文件名（按调用顺序）。</summary>
    public List<string> EnabledFileNames { get; } = [];

    /// <summary>「禁用所选」交给内核的文件名。</summary>
    public List<string> DisabledFileNames { get; } = [];

    /// <summary>「删除所选」交给内核的文件名。</summary>
    public List<string> DeletedFileNames { get; } = [];

    /// <summary>下一次 <see cref="SetModEnabled"/> 的返回值；<see langword="null"/> 表示成功（默认）。</summary>
    public ContentActionResult? SetModEnabledResult { get; set; }

    /// <summary>下一次 <see cref="DeleteMod"/> 的返回值；<see langword="null"/> 表示成功（默认）。</summary>
    public ContentActionResult? DeleteModResult { get; set; }

    /// <inheritdoc />
    public ContentActionResult SetModEnabled(string instanceName, string fileName, bool enabled)
    {
        (enabled ? EnabledFileNames : DisabledFileNames).Add(fileName);

        if (SetModEnabledResult is { } configured)
        {
            return configured;
        }

        var mods = Mods;
        var index = mods.FindIndex(mod => string.Equals(mod.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            mods[index] = mods[index] with
            {
                Enablement = enabled ? ModEnablement.Enabled : ModEnablement.Disabled,
            };
        }

        return ContentActionResult.Success;
    }

    /// <inheritdoc />
    public ContentActionResult DeleteMod(string instanceName, string fileName)
    {
        DeletedFileNames.Add(fileName);

        if (DeleteModResult is { } configured)
        {
            return configured;
        }

        Mods.RemoveAll(mod => string.Equals(mod.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        // 成功也要交代去向（真实实现给的是 <游戏目录>\backups\mods\<时间戳>）。
        return ContentActionResult.Done(
            TextTemplate.Of("已把它移进 {0}，可以从那里拿回来。", @"backups\mods\20261017-160102"));
    }

    /// <summary>对账刷新被调用了几次（p07 被导航到时就该刷一次）。</summary>
    public int ModsRefreshCount { get; private set; }

    /// <summary>
    /// 刷新时"真的改了行"。
    /// </summary>
    /// <remarks>
    /// 默认关：多数用例只想钉住"导航会触发刷新"这件事，而"行到底变没变"由 p07 的对账用例单独钉
    /// （打开它之后 <see cref="GetMods"/> 换成对过账的那一份）。
    /// </remarks>
    public bool ReconcileOnRefresh { get; init; }

    private bool _reconciled;

    /// <inheritdoc />
    public Task RefreshModsAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        ModsRefreshCount++;
        _reconciled = ReconcileOnRefresh;

        // 刷新会换一份行（对过账的那一份）—— 之前动作改过的副本作废。
        _mods = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 当前这一份资源包列表。
    /// </summary>
    /// <remarks>
    /// M7-g-2b 起是**可变的**：启用 / 停用 / 上下移 / 删除的接线用例要看到"动作之后列表真的变了"，
    /// 而输入形状本身仍来自上面的静态数组。
    /// </remarks>
    private List<ResourcePackEntry>? _packs;

    private List<ResourcePackEntry> Packs => _packs ??= [.. ResourcePackRows];

    /// <summary>把列表按"已启用的在前（按优先级）、未启用的按名字"重新编号。</summary>
    private void ReindexPacks()
    {
        var enabled = Packs.Where(static pack => pack.IsEnabled).OrderBy(static pack => pack.Priority).ToList();
        var disabled = Packs.Where(static pack => !pack.IsEnabled)
            .OrderBy(static pack => pack.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _packs = [.. enabled.Concat(disabled)
            .Select(static (pack, index) => pack with { Priority = index + 1, IsActive = index == 0 })];
    }

    /// <summary>「启用资源包」收到的文件名（按调用顺序）。</summary>
    public List<string> EnabledResourcePacks { get; } = [];

    /// <summary>「停用资源包」收到的文件名。</summary>
    public List<string> DisabledResourcePacks { get; } = [];

    /// <summary>「上移 / 下移优先级」收到的文件名与方向（<c>-1</c> 上移，<c>1</c> 下移）。</summary>
    public List<(string FileName, int Direction)> MovedResourcePacks { get; } = [];

    /// <summary>「删除资源包」收到的文件名。</summary>
    public List<string> DeletedResourcePacks { get; } = [];

    /// <summary>下一次资源包动作的返回值；<see langword="null"/> 表示成功（默认）。</summary>
    public ContentActionResult? ResourcePackActionResult { get; set; }

    /// <inheritdoc />
    public ContentActionResult SetResourcePackEnabled(string instanceName, string fileName, bool enabled)
    {
        (enabled ? EnabledResourcePacks : DisabledResourcePacks).Add(fileName);

        if (ResourcePackActionResult is { } configured)
        {
            return configured;
        }

        var packs = Packs;
        var index = packs.FindIndex(pack => string.Equals(pack.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            packs[index] = packs[index] with { IsEnabled = enabled };
            ReindexPacks();
        }

        return ContentActionResult.Success;
    }

    /// <inheritdoc />
    public ContentActionResult MoveResourcePack(string instanceName, string fileName, int direction)
    {
        MovedResourcePacks.Add((fileName, direction));

        if (ResourcePackActionResult is { } configured)
        {
            return configured;
        }

        var packs = Packs;
        var index = packs.FindIndex(pack => string.Equals(pack.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || !packs[index].IsEnabled)
        {
            return ContentActionResult.Failure(TextTemplate.Of("这个资源包当前没有启用，不能调整优先级。"));
        }

        // 列表顺序就是显示顺序（已启用的在前，按优先级）：交换两行的**优先级**再重新编号。
        var target = direction < 0 ? index - 1 : index + 1;

        if (target < 0 || target >= packs.Count || !packs[target].IsEnabled)
        {
            return ContentActionResult.Success;
        }

        (packs[index], packs[target]) = (
            packs[index] with { Priority = packs[target].Priority },
            packs[target] with { Priority = packs[index].Priority });
        ReindexPacks();
        return ContentActionResult.Success;
    }

    /// <inheritdoc />
    public ContentActionResult DeleteResourcePack(string instanceName, string fileName)
    {
        DeletedResourcePacks.Add(fileName);

        if (ResourcePackActionResult is { } configured)
        {
            return configured;
        }

        Packs.RemoveAll(pack => string.Equals(pack.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        ReindexPacks();

        return ContentActionResult.Done(
            TextTemplate.Of("已把它移进 {0}，可以从那里拿回来。", @"backups\resourcepacks\20261017-214000"));
    }

    /// <inheritdoc />
    public IReadOnlyList<ResourcePackEntry> GetResourcePacks(string instanceName) => Packs;

    /// <summary>记录每次「删除存档」的（实例，目录名）—— p09 的「删除所选」必须传目录名而不是显示名。</summary>
    public List<(string InstanceName, string Folder)> DeletedWorldSaves { get; } = [];

    /// <summary>让下一次删除存档失败（界面应显示原因，而不是假装删掉了）。</summary>
    public ContentActionResult? WorldSaveDeleteResult { get; set; }

    /// <inheritdoc />
    public ContentActionResult DeleteWorldSave(string instanceName, string directoryName)
    {
        DeletedWorldSaves.Add((instanceName, directoryName));

        return WorldSaveDeleteResult
            ?? ContentActionResult.Done(TextTemplate.Of("已把它移进 {0}，可以从那里拿回来。", @"backups\saves\20261020-180000"));
    }
    /// <inheritdoc />
    public IReadOnlyList<WorldSaveEntry> GetWorldSaves(string instanceName) => WorldSaves;

    /// <inheritdoc />
    public IReadOnlyList<ModpackEntry> GetModpacks() => Modpacks;

    /// <inheritdoc />
    public IReadOnlyList<ModrinthEntry> GetModrinthCatalog() => ModrinthEmpty ? [] : ModrinthCatalog;

    /// <summary>让用例造出"Modrinth 返回了 0 条"这一态（界面上的空状态分两句，见 <c>ModpacksViewModel</c>）。</summary>
    public bool ModrinthEmpty { get; init; }

    /// <summary>"取到过没有"这一态；默认取到过（与演示数据一致）。</summary>
    public bool HasModrinthCatalog { get; init; } = true;

    /// <summary>记录每次「搜索 / 筛选」查询（S9-e：p08 的搜索框与两个下拉真的传下去了）。</summary>
    public List<ModrinthSearchQuery> ModrinthSearches { get; } = [];

    /// <summary>被请求的 Modrinth 模组 / 资源包搜索（实例、种类、关键词）。</summary>
    public List<(string InstanceName, ModrinthContentKind Kind, string? Query)> ModrinthContentSearches { get; } = [];

    /// <summary>让下一次「准备 Modrinth 内容」返回指定结果（失败用例注入原因）。</summary>
    public ModrinthContentResult? ModrinthContentPrepareResult { get; set; }

    /// <summary>让「修复依赖」返回指定结果。</summary>
    public ModDependencyFixResult? DependencyFixResult { get; set; }

    /// <summary>让下一次筛选查询失败（返回 null，界面应保留上一次结果并说明）。</summary>
    public bool ModrinthSearchFails { get; set; }

    /// <summary>记录每次「导出当前实例」的目标路径。</summary>
    public List<(string InstanceName, string DestinationPath)> ExportedInstances { get; } = [];

    /// <summary>让下一次导出返回指定结果（失败用例注入原因）。</summary>
    public ContentActionResult? ExportInstanceResult { get; set; }

    /// <inheritdoc />
    public ContentActionResult ExportInstance(string instanceName, string destinationPath)
    {
        ExportedInstances.Add((instanceName, destinationPath));

        return ExportInstanceResult
            ?? ContentActionResult.Done(TextTemplate.Of("已导出到 {0}。", destinationPath));
    }
    /// <summary>记录每次「从 Modrinth 直接安装」的准备请求。</summary>
    public List<ModrinthModpackRequest> ModrinthModpackRequests { get; } = [];

    /// <summary>让下一次准备返回指定结果（失败用例注入原因）。</summary>
    public ModrinthModpackResult? ModrinthModpackResult { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<ModrinthEntry>?> SearchModrinthContentAsync(
        string instanceName,
        ModrinthContentKind kind,
        string? query,
        CancellationToken cancellationToken = default)
    {
        ModrinthContentSearches.Add((instanceName, kind, query));
        return Task.FromResult<IReadOnlyList<ModrinthEntry>?>(ModrinthCatalog);
    }

    /// <inheritdoc />
    public Task<ModDependencyFixResult> PrepareDependencyFixAsync(
        string instanceName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(DependencyFixResult ?? new ModDependencyFixResult(
            Succeeded: false,
            Request: null,
            Failure: TextTemplate.Of("当前实例没有缺失依赖。")));

    /// <inheritdoc />
    public Task<ModrinthContentResult> PrepareModrinthContentAsync(
        string instanceName,
        ModrinthContentKind kind,
        string projectId,
        string displayName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ModrinthContentPrepareResult ?? new ModrinthContentResult(
            Succeeded: true,
            Target: new ContentDownloadTarget(
                ProjectId: projectId,
                VersionId: "version-" + projectId,
                DisplayName: displayName,
                ReplacePath: null),
            Failure: null));

    /// <inheritdoc />
    public Task<ModrinthModpackResult> PrepareModrinthModpackAsync(
        ModrinthModpackRequest request,
        CancellationToken cancellationToken = default)
    {
        ModrinthModpackRequests.Add(request);

        return Task.FromResult(ModrinthModpackResult
            ?? new ModrinthModpackResult(
                Succeeded: true,
                Archive: new ModrinthModpackArchive(@"C:\cache\content\modpacks\modpack-better-mc.mrpack", "Better MC 34"),
                Failure: null));
    }
    /// <inheritdoc />
    public Task<IReadOnlyList<ModrinthEntry>?> SearchModrinthAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ModrinthSearches.Add(query);

        if (ModrinthSearchFails)
        {
            return Task.FromResult<IReadOnlyList<ModrinthEntry>?>(null);
        }

        IEnumerable<ModrinthEntry> rows = ModrinthEmpty ? [] : ModrinthCatalog;

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            rows = rows.Where(row => row.Name.Contains(query.Query.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyList<ModrinthEntry>?>(rows.ToArray());
    }
    /// <summary>刷新被调用了几次（外壳导航到本页时应当刷新一次）。</summary>
    public int ModrinthRefreshCount { get; private set; }

    /// <inheritdoc />
    public Task RefreshModrinthAsync(CancellationToken cancellationToken = default)
    {
        ModrinthRefreshCount++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 「更新所选」的请求（M7-d）。
    /// </summary>
    /// <remarks>
    /// 默认 <see langword="null"/>（"没有可更新的模组"）：多数用例只关心页面本身，
    /// 而"按钮可不可按"必须由用例显式给出可更新数才有意义。可写是为了模拟"对账之后才知道有更新"。
    /// </remarks>
    public ContentDownloadRequest? UpdateRequest { get; set; }

    /// <summary><see cref="CreateModUpdateRequest"/> 被问了几次（命令的可执行性也会问它）。</summary>
    public int UpdateRequestQueries { get; private set; }

    /// <inheritdoc />
    public ContentDownloadRequest? CreateModUpdateRequest(string instanceName)
    {
        UpdateRequestQueries++;

        return string.Equals(instanceName, "1.20.4-Forge", StringComparison.Ordinal) ? UpdateRequest : null;
    }

    /// <inheritdoc />
    public ModSummary GetModSummary(string instanceName) => instanceName switch
    {
        "1.20.1-Fabric" => new(42, 0, 0, 1, InstanceNaming.DisplayWithVersion(LoaderKind.Fabric, "0.15.7"), Data("1.20.1"), null),
        "1.21.1-NeoForge" => new(61, 2, 0, 2, InstanceNaming.DisplayWithVersion(LoaderKind.NeoForge, "21.1.252"), Data("1.21.1"), null),
        "1.21.4-Vanilla" => new(0, 0, 0, 0, InstanceNaming.DisplayWithVersion(LoaderKind.Vanilla, null), Data("1.21.4"), null),
        _ => new(
            Enabled: 84,
            Disabled: 3,
            MissingDependency: 1,
            Updatable: 5,
            LoaderMatch: InstanceNaming.DisplayWithVersion(LoaderKind.Forge, "49.0.30"),
            GameVersionMatch: Data("1.20.4"),
            MissingDependencyDetail: TextTemplate.Of(
                "{0} 需要 {1}，当前未安装",
                "create-0.5.1f.jar",
                "flywheel-0.6.10")),
    };

    private static TextTemplate PackDetail(string resolution, string size, int priority) =>
        TextTemplate.Of(
            "{0}× · {1} · 优先级 {2}",
            resolution,
            size,
            priority.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static TextTemplate PackDetailAnimated(string size, int priority) =>
        TextTemplate.Of(
            "实体动画 · {0} · 优先级 {1}",
            size,
            priority.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static TextTemplate Survival() => TextTemplate.Of("生存");

    private static TextTemplate Creative() => TextTemplate.Of("创造");

    private static TextTemplate Today(string time) => TextTemplate.Of("今天 {0}", time);

    private static TextTemplate At(string time) => TextTemplate.Of("{0}", time);
}
