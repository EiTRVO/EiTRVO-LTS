using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Content.Modrinth;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// 加载器安装完成后的可选组件（对齐 Neo 的共存组件）：
/// <list type="bullet">
/// <item>Forge 实例可选安装 OptiFine：把 OptiFine 的 JAR 放进实例 <c>mods\</c>，作为 Forge 模组共存；</item>
/// <item>Fabric 实例可选安装 Fabric API：从 Modrinth 取当前游戏版本/加载器兼容的最新版本，放进 <c>mods\</c>。</item>
/// </list>
/// </summary>
/// <remarks>
/// 可选组件失败**不把整个安装判失败**：游戏与加载器已经装好了，组件失败只记一条警告事件。
/// 这与 Neo 的“可选组件下载失败只提示、不中断”一致。
/// </remarks>
public sealed class OptionalComponentInstaller
{
    private readonly IInstanceCatalog _instances;
    private readonly ModrinthCatalog _modrinth;
    private readonly ContentInstaller _contentInstaller;
    private readonly OptiFineCatalogClient _optiFineCatalog;
    private readonly FileDownloader _downloader;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造可选组件安装器。</summary>
    public OptionalComponentInstaller(
        IInstanceCatalog instances,
        ModrinthCatalog modrinth,
        ContentInstaller contentInstaller,
        OptiFineCatalogClient optiFineCatalog,
        FileDownloader downloader,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(modrinth);
        ArgumentNullException.ThrowIfNull(contentInstaller);
        ArgumentNullException.ThrowIfNull(optiFineCatalog);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _instances = instances;
        _modrinth = modrinth;
        _contentInstaller = contentInstaller;
        _optiFineCatalog = optiFineCatalog;
        _downloader = downloader;
        _diagnostics = diagnostics;
    }

    /// <summary>安装请求里勾选的可选组件。</summary>
    public async Task InstallAsync(
        InstallRequest request,
        GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(request.InstanceName))
        {
            return;
        }

        var instanceName = request.InstanceName;

        if (request.WithOptiFine && request.Loader == LoaderKind.Forge)
        {
            await InstallOptiFineAsModAsync(request, instanceName, progress, cancellationToken).ConfigureAwait(false);
        }

        if (request.WithFabricApi && request.Loader == LoaderKind.Fabric)
        {
            await InstallFabricApiAsync(request, instanceName, settings, progress, stages, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task InstallOptiFineAsModAsync(
        InstallRequest request,
        string instanceName,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var builds = await _optiFineCatalog.FetchAsync(request.VersionId, cancellationToken).ConfigureAwait(false);
            var build = builds.FirstOrDefault(static candidate => !candidate.IsPreview);

            if (build is null)
            {
                Warn(instanceName, "OptiFine", "没有找到稳定版");
                return;
            }

            var launch = _instances.GetLaunchInfo(instanceName);
            var modsDirectory = PathSafety.CombineWithin(launch.InstancePath, "mods");
            Directory.CreateDirectory(modsDirectory);

            var target = PathSafety.CombineWithin(modsDirectory, build.FileName);
            if (File.Exists(target))
            {
                return;
            }

            var url = await _optiFineCatalog.ResolveInstallerUrlAsync(build.FileName, cancellationToken)
                .ConfigureAwait(false);

            var outcome = await _downloader
                .DownloadAsync(
                    [new DownloadItem(
                        DownloadKind.Content,
                        request.VersionId,
                        url,
                        Sha1: string.Empty,
                        Size: 0,
                        TargetPath: target)],
                    new DownloadOptions { Concurrency = 1 },
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.IsComplete)
            {
                Warn(instanceName, "OptiFine", "下载未完成");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(instanceName, "OptiFine", exception.Message);
        }
    }

    private async Task InstallFabricApiAsync(
        InstallRequest request,
        string instanceName,
        GlobalSettings settings,
        IProgress<FileTransferProgress>? progress,
        IProgress<InstallStageChange>? stages,
        CancellationToken cancellationToken)
    {
        try
        {
            var launch = _instances.GetLaunchInfo(instanceName);
            var prepared = await _modrinth
                .PrepareContentAsync(
                    ModrinthContentKind.Mod,
                    "fabric-api",
                    "Fabric API",
                    launch.GameVersion,
                    "fabric",
                    requireBothHashes: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!prepared.Succeeded || prepared.Target is null)
            {
                Warn(instanceName, "Fabric API", "没有找到兼容版本");
                return;
            }

            var result = await _contentInstaller
                .InstallAsync(
                    new ContentDownloadRequest(instanceName, [prepared.Target], []),
                    settings.DownloadSource,
                    settings.ConcurrentDownloads,
                    progress,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                Warn(instanceName, "Fabric API", "下载未完成");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(instanceName, "Fabric API", exception.Message);
        }
    }

    private void Warn(string instanceName, string component, string reason) =>
        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.ModLoader,
            "可选组件 {0} 安装失败（{1}）：{2}",
            [component, instanceName, reason],
            instanceName);
}
