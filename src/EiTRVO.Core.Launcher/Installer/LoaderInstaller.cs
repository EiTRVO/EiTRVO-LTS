using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Settings;
using EiTRVO.Core.Launcher.Text;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Installer;

/// <summary>
/// Fabric / Quilt 安装（B-5 / B-6，M4-f）：取 profile → 与原版合并 → 走**同一条**下载/落盘流水线。
/// <para>
/// 两家的安装形态完全一样，所以是一份实现；Forge / NeoForge（M4-g）的差别在"怎么拿到版本 JSON"
/// （要跑 Java 安装器），而不是在"拿到之后怎么装"，因此它们将来也复用这里下沉到
/// <see cref="VanillaInstaller.InstallResolvedAsync"/> 的那一段。
/// </para>
/// <para>
/// 三条刻意的地方：
/// </para>
/// <list type="number">
/// <item><b>不做"先装原版再装加载器"</b>：profile 与它继承的原版合并之后是一份**自足**的版本
/// （库、客户端 JAR、资源全在里面），一次流水线就装完。分两步会把进度条走成"100% → 从 0 开始"，
/// 而且中途失败会留下"原版装好了、加载器没装"的半状态。</item>
/// <item><b>远端 id 必须与本地命名规则一致</b>：装到 <c>versions/&lt;id&gt;</c> 下的目录名就是启动时
/// 反查的名字，不一致会出现"装完了、启动说版本不存在"。核对不过就如实失败，不硬装。</item>
/// <item><b>仍然最后才写版本 JSON</b>：它与原版安装共用同一条流水线，
/// "下载全部成功才落 JSON"这条规则不会因为换了个加载器就失效。</item>
/// </list>
/// </summary>
public sealed class LoaderInstaller
{
    private readonly LoaderProfileClient _profiles;
    private readonly VanillaInstaller _vanilla;
    private readonly VersionJsonStore _versions;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造加载器安装器。</summary>
    /// <param name="profiles">profile 客户端（Fabric / Quilt 的 REST）。</param>
    /// <param name="vanilla">原版安装器（合并后的下载与落盘都复用它）。</param>
    /// <param name="versions">版本 JSON 存储（读父版本、解析继承链）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public LoaderInstaller(
        LoaderProfileClient profiles,
        VanillaInstaller vanilla,
        VersionJsonStore versions,
        IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(vanilla);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _profiles = profiles;
        _vanilla = vanilla;
        _versions = versions;
        _diagnostics = diagnostics;
    }

    /// <summary>安装一个加载器版本。</summary>
    /// <param name="kind">加载器种类（Fabric / Quilt）。</param>
    /// <param name="gameVersion">它要挂载的游戏版本。</param>
    /// <param name="loaderVersion">加载器版本。</param>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="platform">平台。</param>
    /// <param name="source">下载源策略。</param>
    /// <param name="concurrency">并发下载数。</param>
    /// <param name="progress">进度接收者。</param>
    /// <param name="stages">阶段变化接收者（与安装队列共用同一张阶段表）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>安装结果（<see cref="InstallResult.VersionId"/> 是加载器版本的 id）。</returns>
    public async Task<InstallResult> InstallAsync(
        LoaderKind kind,
        string gameVersion,
        string? loaderVersion,
        string gameDirectory,
        GamePlatform platform,
        DownloadSourcePolicy source = DownloadSourcePolicy.Official,
        int concurrency = 8,
        IProgress<FileTransferProgress>? progress = null,
        IProgress<InstallStageChange>? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(platform);

        // Forge / NeoForge / OptiFine 不走这里。走到这里说明路由写错了，
        // 但那也不该变成一句异常类型名 —— 如实说清它属于哪个里程碑。
        if (!LoaderNaming.IsProfileLoader(kind))
        {
            return new InstallResult(
                gameVersion,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("{0} 的安装不是纯 JSON profile：Forge / NeoForge 属于 M4-g，OptiFine 属于 M4-i。现在可以先用原版或 Fabric / Quilt 启动。", kind.ToString()));
        }

        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            return new InstallResult(
                gameVersion,
                false,
                0,
                0,
                0,
                0,
                TextTemplate.Of("这个实例没有记下 {0} 的版本号，无法确定要装哪一个。请在向导或「实例设置」里重新选一次。", kind.ToString()));
        }

        var versionId = LoaderNaming.VersionId(kind, loaderVersion, gameVersion);

        ReportStage(stages, InstallStageKind.ResolveVersion, completed: false);

        LoaderProfile profile;
        try
        {
            profile = await _profiles
                .FetchAsync(kind, gameVersion, loaderVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VersionDataException ex)
        {
            return Failed(versionId, TextTemplate.Of("获取 {0} 的安装信息失败：{1}", kind.ToString(), ex.Message));
        }

        if (!string.Equals(profile.Id, versionId, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                versionId,
                TextTemplate.Of(
                    "{0} 给回的 profile id 是「{1}」，与本地命名规则算出的「{2}」不一致 —— 装到盘上启动时也找不到它。已停止安装（对方可能改了命名约定）。",
                    kind.ToString(),
                    profile.Id,
                    versionId));
        }

        // 父版本（原版）的**版本 JSON**：本地没有就按 id 去清单里取回来。
        // 注意这里要的是"数据"，不是"原版已经装好" —— 它的库与客户端 JAR 会由本次安装一起下。
        ResolvedVersion parent;
        try
        {
            parent = await _vanilla
                .ResolveVersionAsync(profile.GameVersion, gameDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VersionResolutionException or VersionDataException)
        {
            return Failed(versionId, TextTemplate.Of("需要先能读到原版 {0} 的版本 JSON，但：{1}", profile.GameVersion, ex.Message));
        }

        ResolvedVersion merged;
        try
        {
            merged = VersionInheritanceResolver.Resolve(
                profile.Version,
                id => string.Equals(id, profile.GameVersion, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(id, parent.BaseVersionId, StringComparison.OrdinalIgnoreCase)
                    ? parent.Merged
                    : _versions.Load(gameDirectory, id));
        }
        catch (VersionResolutionException ex)
        {
            return Failed(versionId, ex.Reason);
        }

        ReportStage(stages, InstallStageKind.ResolveVersion, completed: true);

        var result = await _vanilla
            .InstallResolvedAsync(
                merged,
                gameDirectory,
                platform,
                source,
                concurrency,
                progress,
                stages,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            _diagnostics.Write(
                LauncherEventLevel.Information,
                DiagnosticsSources.ModLoader,
                "{0} {1} 安装完成（游戏 {2}，版本 {3}）",
                [kind.ToString(), loaderVersion.Trim(), gameVersion.Trim(), result.VersionId]);
        }

        return result;
    }

    private static void ReportStage(IProgress<InstallStageChange>? stages, InstallStageKind kind, bool completed) =>
        stages?.Report(new InstallStageChange(kind, completed));

    private static InstallResult Failed(string versionId, TextTemplate reason) =>
        new(versionId, false, 0, 0, 0, 0, reason);
}
