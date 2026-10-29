using System.Globalization;
using System.Text.Json;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>加载器 profile：Fabric / Quilt 的 REST 接口返回的一份**继承了某个游戏版本的版本 JSON**。</summary>
/// <param name="Id">版本 id（两家都会给，如 <c>fabric-loader-0.15.7-1.20.4</c>）。</param>
/// <param name="GameVersion">它继承的游戏版本（<c>inheritsFrom</c>）。</param>
/// <param name="Version">版本 JSON 本体（已把 <c>Id</c> 归一化）。</param>
public sealed record LoaderProfile(string Id, string GameVersion, VersionJson Version);

/// <summary>
/// 取加载器 profile（B-5 / B-6，M4-f）。
/// <para>
/// Fabric 与 Quilt 的安装形态**完全一样**（一个 REST 接口直接给出版本 JSON），
/// 差别只有主机名与路径版本号，所以两种加载器共用这一个客户端 —— 抄两份的后果是
/// "改了 Fabric 的地址、忘了 Quilt"。
/// </para>
/// <para>
/// 这里只负责"取到并把形状核对好"，落盘与下载在
/// <see cref="EiTRVO.Core.Launcher.Installer.LoaderInstaller"/>。
/// </para>
/// </summary>
public sealed class LoaderProfileClient
{
    /// <summary>Fabric 的 profile 地址模板（<c>{0}</c> = 游戏版本，<c>{1}</c> = 加载器版本）。</summary>
    public const string FabricProfileUrlFormat =
        "https://meta.fabricmc.net/v2/versions/loader/{0}/{1}/profile/json";

    /// <summary>Quilt 的 profile 地址模板。</summary>
    public const string QuiltProfileUrlFormat =
        "https://meta.quiltmc.org/v3/versions/loader/{0}/{1}/profile/json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造 profile 客户端。</summary>
    /// <param name="httpClientFactory">命名客户端工厂（白名单与重试在管道上）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public LoaderProfileClient(IHttpClientFactory httpClientFactory, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _httpClientFactory = httpClientFactory;
        _diagnostics = diagnostics;
    }

    /// <summary>算出一个加载器的 profile 地址（纯函数，便于逐条断言 URL）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loaderVersion">加载器版本。</param>
    /// <returns>完整地址。</returns>
    /// <exception cref="ArgumentException">版本串为空或含会影响 URL 结构的字符。</exception>
    /// <exception cref="ArgumentOutOfRangeException">不是 profile 型加载器。</exception>
    public static string UrlFor(LoaderKind kind, string gameVersion, string loaderVersion)
    {
        var format = kind switch
        {
            LoaderKind.Fabric => FabricProfileUrlFormat,
            LoaderKind.Quilt => QuiltProfileUrlFormat,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "只有 Fabric / Quilt 有纯 JSON profile；Forge / NeoForge 走 Maven 与安装器（M4-g）。"),
        };

        return string.Format(
            CultureInfo.InvariantCulture,
            format,
            EnsureUrlSegment(gameVersion, nameof(gameVersion)),
            EnsureUrlSegment(loaderVersion, nameof(loaderVersion)));
    }

    /// <summary>取一个加载器的 profile。</summary>
    /// <param name="kind">加载器种类（Fabric / Quilt）。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loaderVersion">加载器版本。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>profile。</returns>
    /// <exception cref="VersionDataException">请求失败、响应不是合法 JSON，或形状不符合约定。</exception>
    public async Task<LoaderProfile> FetchAsync(
        LoaderKind kind,
        string gameVersion,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        var url = UrlFor(kind, gameVersion, loaderVersion);
        var client = _httpClientFactory.CreateClient(HttpClientNames.Core);

        using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.ModLoader,
                "获取加载器 profile 失败：{0} 返回了 {1}",
                [url, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)]);

            throw new VersionDataException(
                $"获取 {kind} 的安装信息失败：{url} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        VersionJson? version;
        try
        {
            version = LauncherJson.Deserialize<VersionJson>(json, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new VersionDataException($"{kind} 的 profile 不是合法 JSON：{url}。", ex);
        }

        if (version is null)
        {
            throw new VersionDataException($"{kind} 的 profile 解析结果为空：{url}。");
        }

        var parent = version.InheritsFrom?.Trim();

        // profile 必须继承**我们请求的那个**游戏版本：不核对的话，远端改一次行为就会
        // 装出一个"名字写着 1.20.4、实际基于别的版本"的实例。
        if (!string.Equals(parent, gameVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new VersionDataException(
                $"{kind} 的 profile 声明的父版本是「{version.InheritsFrom ?? "（没有）"}」，而不是请求的「{gameVersion.Trim()}」。");
        }

        // 两家都会给 id；万一没给就按本地命名规则补一个（安装器还要拿它与规则核对一次）。
        var id = string.IsNullOrWhiteSpace(version.Id)
            ? LoaderNaming.VersionId(kind, loaderVersion, gameVersion)
            : version.Id.Trim();

        return new LoaderProfile(id, parent!, version with { Id = id });
    }

    /// <summary>
    /// 版本串会被拼进请求地址，因此不允许任何能改变 URL 结构的字符（分隔符、查询串、片段、空白）。
    /// 它来自远端清单与用户选择，属于不可信输入。
    /// </summary>
    private static string EnsureUrlSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("版本串不能为空。", parameterName);
        }

        var text = value.Trim();

        if (text.IndexOfAny(['/', '\\', '?', '#', '&', '=']) >= 0 || text.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"版本串不能用于拼请求地址：'{value}'。", parameterName);
        }

        return text;
    }
}
