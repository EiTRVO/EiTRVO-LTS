namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// <c>version_manifest_v2.json</c> 的形状（<c>piston-meta.mojang.com</c>）。
/// <para>
/// 端点必须是 <c>piston-meta</c>：Neo 用的是已弃用的 <c>launchermeta.mojang.com</c>，
/// 那条端点随时可能停服，且不在 <c>HttpHostPolicy</c> 的白名单里（M0 已刻意移除）。
/// </para>
/// </summary>
public sealed record VersionManifest
{
    /// <summary>最新正式版 / 快照。</summary>
    public LatestVersions? Latest { get; init; }

    /// <summary>全部版本条目。</summary>
    public IReadOnlyList<ManifestVersion> Versions { get; init; } = [];
}

/// <summary>清单里的「最新版本」两个指针。</summary>
public sealed record LatestVersions
{
    /// <summary>最新正式版 id。</summary>
    public string? Release { get; init; }

    /// <summary>最新快照 id。</summary>
    public string? Snapshot { get; init; }
}

/// <summary>清单里的一条版本记录。</summary>
public sealed record ManifestVersion
{
    /// <summary>版本 id，如 <c>1.20.4</c>。</summary>
    public string? Id { get; init; }

    /// <summary>类型：<c>release</c> / <c>snapshot</c> / <c>old_beta</c> / <c>old_alpha</c>。</summary>
    public string? Type { get; init; }

    /// <summary>该版本 JSON 的地址（在 <c>piston-meta.mojang.com</c> 上）。</summary>
    public string? Url { get; init; }

    /// <summary>该版本 JSON 的 SHA-1。</summary>
    public string? Sha1 { get; init; }

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>发布时间。</summary>
    public DateTimeOffset? ReleaseTime { get; init; }
}

/// <summary>清单记录的类型映射。</summary>
public static class VersionTypes
{
    /// <summary>正式版。</summary>
    public const string Release = "release";

    /// <summary>快照。</summary>
    public const string Snapshot = "snapshot";

    /// <summary>该类型是否属于界面上要展示的版本。</summary>
    /// <param name="type">清单里的 type 字段。</param>
    /// <returns>属于则返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 只收 <c>release</c> 与 <c>snapshot</c>：设计稿 p05 的计数是 917 = 305 正式版 + 612 快照，
    /// 恰好等于这两类之和 —— <c>old_beta</c> / <c>old_alpha</c>（2011 年前后的老版本）不在界面设计范围内，
    /// 混进去会让「共 N 个」这个数字与设计稿对不上。
    /// </remarks>
    public static bool IsDisplayed(string? type) =>
        string.Equals(type, Release, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, Snapshot, StringComparison.OrdinalIgnoreCase);

    /// <summary>映射到界面用的版本类型。</summary>
    /// <param name="type">清单里的 type 字段。</param>
    /// <returns>版本类型。</returns>
    public static VersionKind ToKind(string? type) =>
        string.Equals(type, Snapshot, StringComparison.OrdinalIgnoreCase)
            ? VersionKind.Snapshot
            : VersionKind.Release;
}
