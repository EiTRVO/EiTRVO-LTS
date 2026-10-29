using System.Text.RegularExpressions;
using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 一个可安装的 OptiFine 构建（B-9，M4-i）。
/// <para>
/// 2026-10-23 起不再走任何镜像：版本列表来自 <b>optifine.net 官方 downloads 页</b>，
/// 和 Neo 的做法一致（官方没有 API，只能抓页面上的 <c>adloadx</c> 链接）。
/// 版本段仍然从文件名取（官方安装器写出的目录名就是它），而不是从页面文字拼。
/// </para>
/// </summary>
/// <param name="GameVersion">要挂载的游戏版本（我们这边的版本 id）。</param>
/// <param name="Edition">OptiFine 版本段，如 <c>HD_U_I6</c> / <c>HD_U_I8_pre4</c>。</param>
/// <param name="FileName">官方文件名，如 <c>OptiFine_1.20.1_HD_U_I6.jar</c>。</param>
/// <param name="DownloadUrl">官方 <c>adloadx</c> 页面地址（安装时再解析出 <c>downloadx</c> 一次性地址）。</param>
/// <param name="ForgeVersion">官方下载表里的 Forge 兼容列；当前解析不到时为 <see langword="null"/>。</param>
/// <param name="IsPreview">是否为预览版。</param>
public sealed record OptiFineBuild(
    string GameVersion,
    string Edition,
    string FileName,
    string DownloadUrl,
    string? ForgeVersion,
    bool IsPreview)
{
    /// <summary>装完之后会出现的版本 id（与官方安装器写出的目录名一致）。</summary>
    public string VersionId => LoaderNaming.VersionId(LoaderKind.OptiFine, Edition, GameVersion);

    /// <summary>界面上的显示名：<c>OptiFine HD U I6</c>。</summary>
    public string DisplayName => "OptiFine " + Edition.Replace('_', ' ');

    /// <summary>这个构建能不能作为 **Forge 模组**使用。</summary>
    public bool SupportsForge => !string.IsNullOrWhiteSpace(ForgeVersion);
}

/// <summary>
/// OptiFine 的官方清单规则（纯函数）：官方 downloads 页、adloadx 地址、版本段解析、排序。
/// </summary>
public static class OptiFineCatalog
{
    /// <summary>官方下载页（版本列表与 adloadx 链接都在这一页）。</summary>
    public const string OfficialDownloadsPage = "https://optifine.net/downloads";

    /// <summary>官方文件名的固定词头。</summary>
    private const string FilePrefix = "OptiFine_";

    private static readonly Regex AdloadLink = new(
        @"href=[""']https?://optifine\.net/adloadx\?f=(OptiFine_[^""']+?\.jar|preview_OptiFine_[^""']+?\.jar)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 官方文件名里 1.8 / 1.9 写成 1.8.0 / 1.9.0；其余版本原样。
    /// </summary>
    /// <param name="gameVersion">游戏版本 id。</param>
    /// <returns>官方文件名里的游戏版本段。</returns>
    /// <exception cref="ArgumentException">版本串为空。</exception>
    public static string LookupVersion(string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        return gameVersion.Trim() switch
        {
            "1.8" => "1.8.0",
            "1.9" => "1.9.0",
            var other => other,
        };
    }

    /// <summary>列表地址：官方 downloads 页（与具体游戏版本无关）。</summary>
    /// <param name="gameVersion">游戏版本 id（仅为保留调用形状）。</param>
    /// <returns>下载页地址。</returns>
    public static string BuildsUrl(string gameVersion) => OfficialDownloadsPage;

    /// <summary>某个官方文件名的 <c>adloadx</c> 页面地址。</summary>
    /// <param name="fileName">官方文件名。</param>
    /// <returns>完整地址。</returns>
    public static string AdloadUrl(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return "https://optifine.net/adloadx?f=" + Uri.EscapeDataString(fileName.Trim());
    }

    /// <summary>
    /// 从官方文件名里取出 OptiFine 版本段：<c>OptiFine_1.20.1_HD_U_I6.jar</c> → <c>HD_U_I6</c>；
    /// <c>preview_OptiFine_1.20.4_HD_U_I8_pre4.jar</c> → <c>HD_U_I8_pre4</c>。
    /// </summary>
    /// <param name="fileName">文件名。</param>
    /// <returns>版本段；文件名不含 <c>OptiFine_&lt;游戏版本&gt;_</c> 形状时为 <see langword="null"/>。</returns>
    public static string? EditionOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = fileName.Trim();
        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var index = name.IndexOf(FilePrefix, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var rest = name[(index + FilePrefix.Length)..];
        var separator = rest.IndexOf('_', StringComparison.Ordinal);

        if (separator <= 0 || separator == rest.Length - 1)
        {
            return null;
        }

        return rest[(separator + 1)..];
    }

    /// <summary>
    /// 解析官方 downloads 页：只保留当前游戏版本的稳定版与预览版，稳定版在前、各自从新到旧。
    /// </summary>
    /// <param name="html">官方 downloads 页的 HTML。</param>
    /// <param name="gameVersion">请求的游戏版本 id。</param>
    /// <returns>按界面顺序排好的构建列表。</returns>
    /// <exception cref="VersionDataException">响应不是可解析的页面。</exception>
    public static IReadOnlyList<OptiFineBuild> Parse(string html, string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new VersionDataException($"OptiFine 下载页为空（游戏版本 {gameVersion.Trim()}）。");
        }

        var lookup = LookupVersion(gameVersion);
        var stablePrefix = FilePrefix + lookup + "_";
        var previewPrefix = "preview_" + FilePrefix + lookup + "_";

        List<OptiFineBuild> stable = [];
        List<OptiFineBuild> preview = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AdloadLink.Matches(html))
        {
            var fileName = match.Groups[1].Value;

            var isPreview = fileName.StartsWith(previewPrefix, StringComparison.OrdinalIgnoreCase)
                            || fileName.StartsWith("preview_" + FilePrefix, StringComparison.OrdinalIgnoreCase)
                               && fileName.Contains("_" + lookup + "_", StringComparison.OrdinalIgnoreCase);

            var isStable = fileName.StartsWith(stablePrefix, StringComparison.OrdinalIgnoreCase);

            if (!isStable && !isPreview)
            {
                continue;
            }

            var edition = EditionOf(fileName);
            if (edition is null || !seen.Add(fileName))
            {
                continue;
            }

            var build = new OptiFineBuild(
                gameVersion.Trim(),
                edition,
                fileName,
                AdloadUrl(fileName),
                ForgeVersion: null,
                IsPreview: isPreview);

            (isPreview ? preview : stable).Add(build);
        }

        stable.Sort(static (left, right) => MavenVersion.Compare(right.Edition, left.Edition));
        preview.Sort(static (left, right) => MavenVersion.Compare(right.Edition, left.Edition));
        stable.AddRange(preview);

        return stable;
    }
}
