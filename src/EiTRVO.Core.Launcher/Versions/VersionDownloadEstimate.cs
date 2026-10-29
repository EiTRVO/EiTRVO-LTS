namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 一个版本"安装大概要下载多少"的估算（只统计版本 JSON 里**明确写了 size** 的项目）。
/// </summary>
/// <remarks>
/// 这是给向导第 2 步的体积提示用的：设计稿写死了一个 412 MB，但 1.7.10 与 1.21 的实际大小差很多，
/// 固定数字会误导用户。这里只算「版本 JSON 能确定的部分」：客户端 JAR、库（含 Windows 分类器）、
/// 资源对象总量、natives 分类器 JAR；加载器自己的库不在其中（安装器阶段才知道）。
/// <para>
/// 宁可**少算**也不猜：拿不到体积的项按 0 计，界面会写明"这是估算"。
/// </para>
/// </remarks>
/// <param name="ClientBytes">客户端 JAR。</param>
/// <param name="LibraryBytes">库的主构件（不含 natives 分类器）。</param>
/// <param name="AssetBytes">资源对象总量（版本 JSON 的 <c>assetIndex.totalSize</c>）。</param>
/// <param name="NativeBytes">natives 分类器 JAR。</param>
public sealed record VersionDownloadEstimate(
    long ClientBytes,
    long LibraryBytes,
    long AssetBytes,
    long NativeBytes)
{
    /// <summary>下载总量。</summary>
    public long TotalBytes => ClientBytes + LibraryBytes + AssetBytes + NativeBytes;

    /// <summary>
    /// 估算时目标盘（游戏目录所在卷）的剩余字节数；拿不到时为 0（界面据此**不显示**空间判断，
    /// 而不是显示"空间充足"）。
    /// </summary>
    public long FreeDiskBytes { get; init; }
}

/// <summary>
/// 从版本 JSON 计算 <see cref="VersionDownloadEstimate"/> 的纯函数。
/// </summary>
public static class VersionDownloadEstimator
{
    /// <summary>按目标平台（Windows x64）估算一份版本 JSON 的下载量。</summary>
    /// <param name="json">版本 JSON（原版；加载器增量 JSON 的库也可能被算进去）。</param>
    /// <returns>估算。</returns>
    public static VersionDownloadEstimate Estimate(VersionJson json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var client = json.Downloads?.Client?.Size ?? 0;
        var libraries = 0L;
        var natives = 0L;

        foreach (var library in json.Libraries ?? [])
        {
            if (!LibraryFilter.Applies(library, GamePlatform.WindowsX64))
            {
                continue;
            }

            libraries += library.Downloads?.Artifact?.Size ?? library.Size;

            if (library.Natives is null
                || library.Downloads?.Classifiers is null
                || !library.Natives.TryGetValue(GamePlatform.WindowsX64.OsName, out var classifier)
                || string.IsNullOrWhiteSpace(classifier)
                || !library.Downloads.Classifiers.TryGetValue(classifier, out var native))
            {
                continue;
            }

            natives += native.Size;
        }

        return new VersionDownloadEstimate(
            ClientBytes: client,
            LibraryBytes: libraries,
            AssetBytes: json.AssetIndex?.TotalSize ?? 0,
            NativeBytes: natives);
    }
}
