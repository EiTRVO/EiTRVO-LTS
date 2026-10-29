using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// <c>inheritsFrom</c> 链无法解析（父版本缺失、成环、层级过深、或版本 JSON 自身不合法）。
/// </summary>
public sealed class VersionResolutionException : Exception
{
    /// <summary>构造解析异常。</summary>
    /// <param name="reason">失败原因（键 + 参数）。</param>
    /// <param name="chain">已解析到的版本链（从子到父）。</param>
    /// <param name="missingVersionId">缺失的父版本 id（非缺失类失败为 <see langword="null"/>）。</param>
    public VersionResolutionException(
        TextTemplate reason,
        IReadOnlyList<string> chain,
        string? missingVersionId = null)
        : base((reason ?? throw new ArgumentNullException(nameof(reason))).Key)
    {
        Reason = reason;
        Chain = chain;
        MissingVersionId = missingVersionId;
    }

    /// <summary>失败原因（键 + 参数）。</summary>
    public TextTemplate Reason { get; }

    /// <summary>已解析到的版本链（从子到父）。</summary>
    public IReadOnlyList<string> Chain { get; }

    /// <summary>缺失的父版本 id。</summary>
    public string? MissingVersionId { get; }
}

/// <summary>解析完成的版本（合并后的完整版本 JSON + 它从哪来）。</summary>
/// <param name="Merged">合并后的版本 JSON（<c>InheritsFrom</c> 已清空）。</param>
/// <param name="BaseVersionId">链根的原版版本 id（原版版本即自身）。</param>
/// <param name="Chain">版本链，从子到父，如 <c>["1.20.4-forge-49.0.30","1.20.4"]</c>。</param>
public sealed record ResolvedVersion(VersionJson Merged, string BaseVersionId, IReadOnlyList<string> Chain);

/// <summary>
/// <c>inheritsFrom</c> 继承链的解析与合并。
/// <para>
/// 合并规则（与官方启动器的语义一致，**父版本先、子版本后**）：
/// </para>
/// <list type="bullet">
/// <item><b>库</b>：子在前、父在后，按 Maven 坐标去重（同坐标以子为准）—— 顺序决定 classpath 优先级。</item>
/// <item><b>参数</b>：<c>jvm</c> 与 <c>game</c> 都是「父 + 子」按序拼接（父版本的 <c>-Xmx</c>、<c>--username</c> 必须在前）。</item>
/// <item><b>标量字段</b>：子有值就用子的，否则继承父的（<c>mainClass</c> / <c>assets</c> / <c>assetIndex</c> /
/// 客户端 JAR / <c>logging</c> / <c>javaVersion</c> / <c>minecraftArguments</c>）。</item>
/// </list>
/// <para>
/// 与 Neo 的两处有意差异（Neo 的 <c>ModLoaderService.MergeVersionJson</c> 是这次迁移的来源）：
/// </para>
/// <list type="number">
/// <item>Neo **强制**用父版本的 <c>assets</c> / <c>assetIndex</c> / <c>downloads</c>，
/// 子版本自己声明了也会被丢弃。这里改成"**逐字段**：子有则子，无则父"——
/// 既保住"加载器 JSON 通常没有这些字段"的常见情况，也不至于把自定义资源索引的整合包版本悄悄改回原版资源。</item>
/// <item>Neo 只合并**一层**：父版本自己还有 <c>inheritsFrom</c> 时（Quilt→Fabric→原版、部分 NeoForge 链），
/// 合并结果会把 <c>inheritsFrom</c> 清成 <c>null</c>，链上剩下的部分就永远丢了。
/// 这里按官方语义**递归解析整条链**，并检测成环与层级上限。</item>
/// </list>
/// </summary>
public static class VersionInheritanceResolver
{
    /// <summary>继承链的最大深度。真实加载器链最多 2–3 层，超过这个数只可能是坏数据或成环。</summary>
    public const int MaxDepth = 8;

    /// <summary>解析一条版本链。</summary>
    /// <param name="version">子版本（可能是加载器版本）。</param>
    /// <param name="loadParent">按 id 取父版本；取不到返回 <see langword="null"/>。</param>
    /// <returns>合并后的版本、链根 id 与整条链。</returns>
    /// <exception cref="VersionResolutionException">父版本缺失、成环或层级过深。</exception>
    public static ResolvedVersion Resolve(VersionJson version, Func<string, VersionJson?> loadParent)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(loadParent);

        var selfId = string.IsNullOrWhiteSpace(version.Id) ? "?" : version.Id;
        List<string> chain = [selfId];
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selfId };

        var current = version;

        // 注意：这里必须以**父版本自己的** InheritsFrom 作为下一跳。
        // Merge 的语义是把 InheritsFrom 清成 null（合并完就不需要继承了），
        // 所以若用 current.InheritsFrom 当循环条件，链就会在第一层之后停住 ——
        // Quilt → Fabric → 原版 这种两层链会静默丢掉 Fabric 之外的部分。
        var nextParentId = version.InheritsFrom;

        while (!string.IsNullOrWhiteSpace(nextParentId))
        {
            var parentId = nextParentId.Trim();

            if (chain.Count >= MaxDepth)
            {
                throw new VersionResolutionException(
                    TextTemplate.Of(
                        "inheritsFrom 链超过 {0} 层，已停止解析（链：{1}）。",
                        MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        string.Join(" → ", chain)),
                    chain);
            }

            if (!visited.Add(parentId))
            {
                throw new VersionResolutionException(
                    TextTemplate.Of(
                        "inheritsFrom 成环：{0} 已经出现过（链：{1} → {2}）。",
                        parentId,
                        string.Join(" → ", chain),
                        parentId),
                    chain);
            }

            var parent = loadParent(parentId)
                ?? throw new VersionResolutionException(
                    TextTemplate.Of(
                        "缺少父版本「{0}」：{1} 声明了 inheritsFrom，但本地没有它的版本 JSON。",
                        parentId,
                        selfId),
                    chain,
                    parentId);

            chain.Add(parentId);
            nextParentId = parent.InheritsFrom;
            current = Merge(current, parent);
        }

        return new ResolvedVersion(current, chain[^1], chain);
    }

    /// <summary>合并子版本与父版本（父先子后，见类型注释里的规则）。</summary>
    /// <param name="child">子版本（加载器或整合包版本）。</param>
    /// <param name="parent">父版本（原版，或链上的上一层）。</param>
    /// <returns>合并后的版本。</returns>
    public static VersionJson Merge(VersionJson child, VersionJson parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);

        return child with
        {
            // 合并之后这条链已经走完，不再需要继承标记。
            InheritsFrom = null,
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            Assets = child.Assets ?? parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Downloads = MergeDownloads(child.Downloads, parent.Downloads),
            Logging = HasLogging(child.Logging) ? child.Logging : parent.Logging,
            JavaVersion = child.JavaVersion?.MajorVersion is null ? parent.JavaVersion : child.JavaVersion,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Libraries = MergeLibraries(child.Libraries, parent.Libraries),
            Arguments = MergeArguments(child.Arguments, parent.Arguments),
        };
    }

    /// <summary>按「子在前、父在后」合并库列表，同 Maven 坐标以子为准。</summary>
    /// <param name="child">子版本的库。</param>
    /// <param name="parent">父版本的库。</param>
    /// <returns>合并后的库列表。</returns>
    /// <remarks>
    /// <b>同名条目要合并，不能只留第一条</b>：旧版官方清单（1.13–1.14，例如 1.14.4）把同一个
    /// Maven 坐标写了**两遍** —— 一条只有主构件，另一条带 <c>natives</c> 与 <c>classifiers</c>。
    /// 按名字去重的第一版留下了前一条，于是加载器安装出来的版本 JSON 里一个 natives 都没有；
    /// 启动表现是 <c>UnsatisfiedLinkError: Failed to locate library: lwjgl.dll</c>（退出码 -1），
    /// 而原版单独安装（不过继承链合并）反而正常 —— 这正是 1.14.4 + Fabric 起不来的根因。
    /// 合并时**以先出现的那条为准**（子版本优先），但把后一条独有的 natives / classifiers /
    /// 解压规则补上：它们是同一个构件的不同侧面，不是"另一个版本"。
    /// </remarks>
    public static IReadOnlyList<Library> MergeLibraries(
        IReadOnlyList<Library>? child,
        IReadOnlyList<Library>? parent)
    {
        List<Library> merged = [];
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(Library library)
        {
            if (string.IsNullOrWhiteSpace(library.Name))
            {
                return;
            }

            if (index.TryGetValue(library.Name, out var existing))
            {
                merged[existing] = CombineLibraries(merged[existing], library);
                return;
            }

            index[library.Name] = merged.Count;
            merged.Add(library);
        }

        foreach (var library in child ?? [])
        {
            Add(library);
        }

        foreach (var library in parent ?? [])
        {
            Add(library);
        }

        return merged;
    }

    /// <summary>合并同一个 Maven 坐标的两条库记录（主构件保留先出现的那条）。</summary>
    /// <param name="first">先出现的库（子版本或清单里靠前的那条）。</param>
    /// <param name="second">后出现的库（父版本或清单里靠后的那条）。</param>
    /// <returns>合并后的库。</returns>
    /// <remarks>
    /// 只补 <c>natives</c> / <c>classifiers</c> / <c>extract</c> —— 主构件的地址、校验值、规则、
    /// 旧式仓库基址都以先出现的为准（"子 override 父"的语义不变）。
    /// </remarks>
    private static Library CombineLibraries(Library first, Library second) =>
        first with
        {
            Natives = MergeStrings(first.Natives, second.Natives),
            Downloads = MergeLibraryDownloads(first.Downloads, second.Downloads),
            Extract = first.Extract ?? second.Extract,
        };

    /// <summary>合并两份"平台 → 分类器"映射（先出现的优先，后出现的只补缺）。</summary>
    private static IReadOnlyDictionary<string, string>? MergeStrings(
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string>? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null || second.Count == 0)
        {
            return first;
        }

        var merged = new Dictionary<string, string>(first, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in second)
        {
            merged.TryAdd(key, value);
        }

        return merged;
    }

    /// <summary>合并库的下载信息：主构件先出现者优先，分类器两侧互补。</summary>
    private static LibraryDownloads? MergeLibraryDownloads(LibraryDownloads? first, LibraryDownloads? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return new LibraryDownloads
        {
            Artifact = first.Artifact ?? second.Artifact,
            Classifiers = MergeDownloadInfos(first.Classifiers, second.Classifiers),
        };
    }

    /// <summary>合并两份分类器表（先出现的优先，后出现的只补缺）。</summary>
    private static IReadOnlyDictionary<string, DownloadInfo>? MergeDownloadInfos(
        IReadOnlyDictionary<string, DownloadInfo>? first,
        IReadOnlyDictionary<string, DownloadInfo>? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null || second.Count == 0)
        {
            return first;
        }

        var merged = new Dictionary<string, DownloadInfo>(first, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in second)
        {
            merged.TryAdd(key, value);
        }

        return merged;
    }

    /// <summary>合并参数：父在前、子在后（顺序即命令行顺序）。</summary>
    /// <param name="child">子版本参数。</param>
    /// <param name="parent">父版本参数。</param>
    /// <returns>合并后的参数。</returns>
    public static VersionArguments? MergeArguments(VersionArguments? child, VersionArguments? parent)
    {
        if (child is null && parent is null)
        {
            return null;
        }

        var jvm = Concat(parent?.Jvm, child?.Jvm);
        var game = Concat(parent?.Game, child?.Game);

        return new VersionArguments
        {
            Jvm = jvm.Count > 0 ? jvm : null,
            Game = game.Count > 0 ? game : null,
        };
    }

    private static List<VersionArgument> Concat(
        IReadOnlyList<VersionArgument>? first,
        IReadOnlyList<VersionArgument>? second)
    {
        List<VersionArgument> merged = [];
        merged.AddRange(first ?? []);
        merged.AddRange(second ?? []);
        return merged;
    }

    private static VersionDownloads? MergeDownloads(VersionDownloads? child, VersionDownloads? parent)
    {
        var client = child?.Client?.Url is not null ? child.Client : parent?.Client;
        var server = child?.Server?.Url is not null ? child.Server : parent?.Server;

        if (client is null && server is null)
        {
            return null;
        }

        return new VersionDownloads { Client = client, Server = server };
    }

    private static bool HasLogging(VersionLogging? logging) => logging?.Client?.File?.Url is not null;
}
