using EiTRVO.Core.Launcher.Settings;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Downloads;

/// <summary>
/// 要下载的东西属于哪一类（用于数据源展示、诊断与统计）。
/// <para>
/// 2026-10-23 起所有下载都**只走官方地址**：类别不再参与"镜像地址拼装"，
/// 但保留它是因为"这类文件从哪来"仍然是有意义的事实（例如 Java 运行时来自 Mojang 的对象存储）。
/// 类别由**产生下载项的人**（安装器）如实标注，而不是靠 URL 猜。
/// </para>
/// </summary>
public enum DownloadKind
{
    /// <summary>版本清单。</summary>
    VersionManifest,

    /// <summary>某个版本的版本 JSON。</summary>
    VersionJson,

    /// <summary>客户端 JAR。</summary>
    ClientJar,

    /// <summary>库（Maven 坐标对应的 jar）。</summary>
    Library,

    /// <summary>资源索引 JSON。</summary>
    AssetIndex,

    /// <summary>资源对象（按 SHA-1 寻址）。</summary>
    Asset,

    /// <summary>日志配置等杂项。</summary>
    Logging,

    /// <summary>
    /// Mojang 官方 Java 运行时的文件（§12.7）。
    /// <para>
    /// 单独一类而不是复用 <see cref="Library"/> 或 <see cref="Asset"/>：运行时文件按 SHA-1 内容寻址
    /// （<c>piston-data.mojang.com/v1/objects/&lt;sha1&gt;/…</c>），地址与游戏资源对象不是同一套。
    /// </para>
    /// </summary>
    JavaRuntime,

    /// <summary>
    /// 从 Modrinth 下的内容文件（模组 jar，M7-d）。
    /// <para>
    /// 地址直接来自 Modrinth 的版本 JSON（<c>cdn.modrinth.com</c>，已在出站白名单里），
    /// 每个文件带官方给的 SHA-1 / SHA-512 —— 这一类只用官方地址。
    /// </para>
    /// </summary>
    Content,
}

/// <summary>一个待下载文件。</summary>
/// <param name="Kind">类别（用于数据源展示与诊断）。</param>
/// <param name="VersionId">所属游戏版本；与版本无关的项可为空串。</param>
/// <param name="Url">官方地址。</param>
/// <param name="Sha1">期望的 SHA-1；空串表示该来源没有提供校验值。</param>
/// <param name="Size">期望字节数；未知时为 0。</param>
/// <param name="TargetPath">落地路径（绝对路径）。</param>
/// <param name="Sha512">
/// 期望的 SHA-512；空串表示该来源没有提供（M7-e）。
/// <para>
/// 为什么要有它：<c>.mrpack</c> 规范要求 <c>hashes</c> 里 **sha1 与 sha512 都在**，
/// 而 sha1 早已被证明可以构造碰撞 —— 只校验 sha1 等于把"内容对不对"押在一个已知可攻击的摘要上。
/// 两个都在时**同一次读盘**把它们一起算出来（见 <c>FileDownloader</c>），所以不额外多读一遍文件。
/// </para>
/// </param>
public sealed record DownloadItem(
    DownloadKind Kind,
    string VersionId,
    string Url,
    string Sha1,
    long Size,
    string TargetPath,
    string Sha512 = "")
{
    /// <summary>给界面/日志看的短名字（文件名）。</summary>
    public string DisplayName => Path.GetFileName(TargetPath);

    /// <summary>是否有可校验的 SHA-1。</summary>
    public bool IsVerifiable => !string.IsNullOrWhiteSpace(Sha1);

    /// <summary>是否有可校验的 SHA-512。</summary>
    public bool HasSha512 => !string.IsNullOrWhiteSpace(Sha512);

    /// <summary>是否需要做内容校验（两个摘要里有一个就行）。</summary>
    public bool NeedsVerification => IsVerifiable || HasSha512;
}

/// <summary>一个文件的失败记录。</summary>
/// <param name="Item">失败的文件。</param>
/// <param name="Reason">失败原因（已翻译成人能读的说明）。</param>
/// <param name="ChecksumMismatch">是否为校验不通过（与网络失败区分，界面提示不同）。</param>
public sealed record DownloadFailure(DownloadItem Item, TextTemplate Reason, bool ChecksumMismatch);

/// <summary>一次批量下载的结果。</summary>
/// <param name="TotalFiles">需要处理的文件数（不含已存在的）。</param>
/// <param name="CompletedFiles">成功下载的文件数。</param>
/// <param name="SkippedFiles">本地已存在且校验通过、因此跳过的文件数。</param>
/// <param name="DownloadedBytes">本次实际写入的字节数。</param>
/// <param name="Failures">失败清单。</param>
public sealed record DownloadOutcome(
    int TotalFiles,
    int CompletedFiles,
    int SkippedFiles,
    long DownloadedBytes,
    IReadOnlyList<DownloadFailure> Failures)
{
    /// <summary>是否全部成功。</summary>
    public bool IsComplete => Failures.Count == 0;

    /// <summary>失败文件数。</summary>
    public int FailedFiles => Failures.Count;
}

/// <summary>
/// 下载进度（**数字**，不含界面文案）。
/// <para>
/// 与本命名空间里那个同名的 <see cref="DownloadProgress"/> 是两件事：那个是**界面用的**
/// （已经格式化好的「256 MB / 412 MB · 8.4 MB/s」），由 <c>IDownloadCatalog</c> 提供给 p06；
/// 这个是**内核用的**原始数字，格式化是 ViewModel 的事（AGENTS §5.3 的同一原则）。
/// 名字里带 Transfer 就是为了避免两个同义类型在同一个命名空间里互相误导。
/// </para>
/// </summary>
/// <param name="CompletedFiles">已完成文件数（含已存在而跳过的）。</param>
/// <param name="TotalFiles">总文件数。</param>
/// <param name="CompletedBytes">已完成字节数（含已存在而跳过的）。</param>
/// <param name="TotalBytes">总字节数；未知项按 0 计。</param>
/// <param name="CurrentFile">正在下载的文件名。</param>
/// <param name="BytesPerSecond">平均速度（字节/秒）。</param>
/// <param name="Remaining">预计剩余时间；无法估算时为 <see langword="null"/>。</param>
public sealed record FileTransferProgress(
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string CurrentFile,
    double BytesPerSecond,
    TimeSpan? Remaining)
{
    /// <summary>
    /// 本次进度是否"没有总量可算"（例如 Forge / NeoForge / OptiFine 的官方安装器在子进程里
    /// 自己下载库，我们拿不到它的字节数）。界面据此显示**不确定进度条**，而不是一根永远停在 0% 的条。
    /// </summary>
    public bool IsIndeterminate { get; init; }

    /// <summary>
    /// 当前实际下载所在的主机（如 <c>maven.fabricmc.net</c> / <c>piston-data.mojang.com</c>）。
    /// <para>
    /// 进度条旁边那格「数据源」读它：一次安装会跨多个主机（资源、库、加载器），
    /// 只按"下载源设置"猜一个主机名会把 Forge 安装显示成 piston-data。
    /// 空串表示还没有开始任何文件传输（此时回落到任务种类的默认源）。
    /// </para>
    /// </summary>
    public string SourceHost { get; init; } = string.Empty;

    /// <summary>
    /// 当前正在做的事的**键 + 参数**（例如「正在运行官方安装器」）；没有时回落到队列行的初始说明。
    /// </summary>
    public TextTemplate? Detail { get; init; }
}

/// <summary>下载选项。</summary>
public sealed record DownloadOptions
{
    /// <summary>默认选项。</summary>
    public static DownloadOptions Default { get; } = new();

    /// <summary>下载源策略（与「Java 与设置」页的「下载源」同义）。</summary>
    public DownloadSourcePolicy Source { get; init; } = DownloadSourcePolicy.Official;

    /// <summary>并发下载数（来自设置的「并发下载数」）。</summary>
    public int Concurrency { get; init; } = 8;

    /// <summary>
    /// 单个文件的**文件级**重试次数。
    /// <para>
    /// 与管道上的 <see cref="Net.RetryingHandler"/> 是两层：管道重试针对"同一个请求的瞬时故障"，
    /// 这里针对"整个文件重取"（校验不通过、镜像也失败、连接中途断）。默认 2 次，
    /// 与管道重试叠加后最坏情况是 2×3 次请求 —— 再多就只是让用户等更久。
    /// </para>
    /// </summary>
    public int MaxAttemptsPerFile { get; init; } = 2;
}
