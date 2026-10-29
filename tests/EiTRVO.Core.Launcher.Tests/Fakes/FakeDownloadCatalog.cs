using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// 下载队列的替身：只记录"入了什么队"，不做任何真实下载。
/// <para>
/// 需要它是因为启动编排现在会在「缺少 Java 且设置里允许自动获取」时排一个获取运行时的任务（§12.7）——
/// 那条路径不该在启动链的单测里去真的联网下 100 MB。
/// </para>
/// </summary>
internal sealed class FakeDownloadCatalog : IDownloadCatalog
{
    /// <summary>被入队的安装请求。</summary>
    public List<InstallRequest> Enqueued { get; } = [];

    /// <summary>被入队的 Java 运行时请求。</summary>
    public List<JavaRuntimeRequest> RuntimeRequests { get; } = [];

    /// <summary>被入队的内容下载请求（M7-d）。</summary>
    public List<ContentDownloadRequest> ContentRequests { get; } = [];

    /// <summary>被入队的整合包导入请求（M7-e-2）。</summary>
    public List<MrpackInstallRequest> MrpackRequests { get; } = [];

    /// <summary>被入队的存档备份请求（M7-f）。</summary>
    public List<WorldBackupRequest> WorldBackupRequests { get; } = [];

    /// <summary>被入队的存档恢复请求（M7-f）。</summary>
    public List<WorldRestoreRequest> WorldRestoreRequests { get; } = [];

    /// <summary>排进队列时抛的异常（用来验证"排不进去也不改变启动失败的原因"）。</summary>
    public Exception? ThrowOnEnqueue { get; set; }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>手动广播一次变更。</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public int PendingTaskCount =>
        Enqueued.Count + RuntimeRequests.Count + ContentRequests.Count
        + MrpackRequests.Count + WorldBackupRequests.Count + WorldRestoreRequests.Count;

    /// <inheritdoc />
    public DownloadProgress GetProgress() => new(
        CurrentTask: TextTemplate.Of("—"),
        Percent: 0,
        AmountText: string.Empty,
        SpeedText: string.Empty,
        FilesText: string.Empty,
        RemainingText: TextTemplate.Of("—"),
        ConnectionsText: string.Empty,
        DataSource: string.Empty);

    /// <inheritdoc />
    public IReadOnlyList<DownloadQueueItem> GetQueue() => [];

    /// <inheritdoc />
    public bool Cancel(string jobId) => false;

    /// <inheritdoc />
    public InstallJob? Retry(string jobId) => null;

    /// <inheritdoc />
    public int ClearFinished() => 0;

    /// <inheritdoc />
    public InstallJob EnqueueInstall(InstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ThrowOnEnqueue is not null)
        {
            throw ThrowOnEnqueue;
        }

        Enqueued.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("安装 {0}", request.VersionId),
            new GameInstallTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueJavaRuntime(JavaRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ThrowOnEnqueue is not null)
        {
            throw ThrowOnEnqueue;
        }

        RuntimeRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("获取 Java 运行时（适配 {0}）", request.GameVersion ?? string.Empty),
            new JavaRuntimeTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <summary>被入队的「从 Modrinth 下载整合包」请求（2026-10-21）。</summary>
    public List<(ModrinthModpackRequest Request, string Title)> ModrinthModpackRequests { get; } = [];

    /// <inheritdoc />
    public InstallJob EnqueueModrinthModpack(ModrinthModpackRequest request, string title)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModrinthModpackRequests.Add((request, title));

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("安装整合包 {0}", title),
            new ModrinthModpackTask(request, title),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public IReadOnlyList<InstallJob> GetJobs() => [];

    /// <inheritdoc />
    public InstallJob EnqueueContentDownload(ContentDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ThrowOnEnqueue is not null)
        {
            throw ThrowOnEnqueue;
        }

        ContentRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("更新 {0} 个模组 · {1}", request.Targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Subject),
            new ContentDownloadTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public Task WhenIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public InstallJob EnqueueMrpackImport(MrpackInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ThrowOnEnqueue is not null)
        {
            throw ThrowOnEnqueue;
        }

        MrpackRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("导入整合包 {0}", request.Subject),
            new MrpackInstallTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueWorldBackup(WorldBackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ThrowOnEnqueue is not null)
        {
            throw ThrowOnEnqueue;
        }

        WorldBackupRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("备份存档 {0}", request.Subject),
            new WorldBackupTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }

    /// <inheritdoc />
    public InstallJob EnqueueWorldRestore(WorldRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ThrowOnEnqueue is not null)
        {
            throw ThrowOnEnqueue;
        }

        WorldRestoreRequests.Add(request);

        return new InstallJob(
            Guid.NewGuid().ToString("N")[..8],
            TextTemplate.Of("恢复存档 {0}", request.Subject),
            new WorldRestoreTask(request),
            InstallJobState.Queued,
            TextTemplate.Of("等待中"),
            null);
    }
}
