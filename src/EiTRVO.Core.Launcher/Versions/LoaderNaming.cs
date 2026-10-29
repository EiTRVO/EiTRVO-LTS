using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 加载器版本的 **id 命名规则**（M4-f）。
/// <para>
/// 这条规则必须只有一份：安装器把远端 profile 写成
/// <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>，而启动时要从实例元数据
/// （游戏版本 + 加载器 + 加载器版本）反推出**同一个** id。
/// 两处各写一遍的后果是"装好了，点启动却说这个版本不存在" —— 而且两边看起来都对。
/// </para>
/// <para>
/// 规则取自两家 profile 自己给出的 id 形状：<c>fabric-loader-&lt;加载器版本&gt;-&lt;游戏版本&gt;</c>、
/// <c>quilt-loader-&lt;加载器版本&gt;-&lt;游戏版本&gt;</c>。
/// 安装器会拿远端的 <c>id</c> 与这里算出来的值**核对**，不一致就如实失败（见
/// <see cref="EiTRVO.Core.Launcher.Installer.LoaderInstaller"/>）。
/// </para>
/// </summary>
public static class LoaderNaming
{
    /// <summary>是不是"纯 JSON profile"型的加载器（Fabric / Quilt）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>Fabric / Quilt 为真。</returns>
    public static bool IsProfileLoader(LoaderKind kind) => kind is LoaderKind.Fabric or LoaderKind.Quilt;

    /// <summary>是不是"官方安装器"型的加载器（Forge / NeoForge，M4-g）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>Forge / NeoForge 为真。</returns>
    public static bool IsInstallerLoader(LoaderKind kind) => kind is LoaderKind.Forge or LoaderKind.NeoForge;

    /// <summary>
    /// 是不是 OptiFine（B-9 / M4-i）：它既不是纯 JSON profile，也不是 Forge 那套安装器 ——
    /// 它的版本 id 由**官方安装器写出**，形状见 <see cref="VersionId"/>。
    /// </summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>OptiFine 为真。</returns>
    public static bool IsOptiFine(LoaderKind kind) => kind == LoaderKind.OptiFine;

    /// <summary>
    /// NeoForge 在 1.20.1 及以前用的是**旧的 <c>net/neoforged/forge</c> 体系**：
    /// 版本号形如 <c>1.20.1-47.1.106</c>、装出来的版本 id 也还是 Forge 那个形状（B-8）。
    /// </summary>
    /// <param name="gameVersion">游戏版本。</param>
    /// <returns>1.20.1 为真。</returns>
    public static bool IsLegacyNeoForge(string gameVersion) =>
        string.Equals(gameVersion?.Trim(), "1.20.1", StringComparison.Ordinal);

    /// <summary>版本 id 的加载器前缀（只有 Fabric / Quilt 用带前缀的形状）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <returns>前缀，如 <c>fabric-loader</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">不是 profile 型加载器。</exception>
    public static string PrefixFor(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "fabric-loader",
        LoaderKind.Quilt => "quilt-loader",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "只有 Fabric / Quilt 的版本 id 带加载器前缀；Forge / NeoForge 的形状见 VersionId。"),
    };

    /// <summary>由「加载器 + 加载器版本 + 游戏版本」算出版本 id。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="loaderVersion">加载器版本，如 <c>0.15.7</c> / <c>49.0.30</c>。</param>
    /// <param name="gameVersion">游戏版本，如 <c>1.20.4</c>。</param>
    /// <returns>版本 id，如 <c>fabric-loader-0.15.7-1.20.4</c> / <c>1.20.4-forge-49.0.30</c> / <c>neoforge-20.4.237</c>。</returns>
    /// <remarks>
    /// Forge 与 NeoForge 的形状是**由官方安装器实际写出来的目录名**定的
    /// （2026-10-04 用真安装器核过：1.20.4 → <c>1.20.4-forge-49.0.30</c>、1.12.2 → <c>1.12.2-forge-14.23.5.2860</c>、
    /// NeoForge 20.4.237 → <c>neoforge-20.4.237</c>）。写错这一条的表现是"装完了、点启动说这个版本不存在"。
    /// OptiFine 同理：官方安装器写的是 <c>&lt;游戏版本&gt;-OptiFine_&lt;版本段&gt;</c>
    /// （真机核过 1.20.1 → <c>1.20.1-OptiFine_HD_U_I6</c>），而"版本段"取自官方文件名
    /// （见 <see cref="OptiFineCatalog.EditionOf"/>）—— 两边必须是同一个串。
    /// </remarks>
    /// <exception cref="ArgumentException">版本串为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">不是受支持的加载器（原版走别的路径）。</exception>
    public static string VersionId(LoaderKind kind, string? loaderVersion, string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        var version = loaderVersion.Trim();
        var game = gameVersion.Trim();

        return kind switch
        {
            LoaderKind.Fabric or LoaderKind.Quilt =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{PrefixFor(kind)}-{version}-{game}"),

            // Forge：游戏版本在前。旧体系的 NeoForge（1.20.1）装出来就是这个形状。
            LoaderKind.Forge => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{game}-forge-{version}"),
            LoaderKind.NeoForge when IsLegacyNeoForge(game) =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{game}-forge-{version}"),

            // 1.20.2 起 NeoForge 换了体系：版本号里不含游戏版本，id 也不再带游戏版本。
            LoaderKind.NeoForge => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"neoforge-{version}"),

            // OptiFine：与官方安装器写出的目录名逐字符一致。
            LoaderKind.OptiFine =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{game}-OptiFine_{version}"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "原版没有加载器版本 id。"),
        };
    }
}
