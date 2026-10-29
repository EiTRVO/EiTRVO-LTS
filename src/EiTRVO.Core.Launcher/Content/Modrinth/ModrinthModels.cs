using System.Globalization;
using System.Text.Json.Serialization;

namespace EiTRVO.Core.Launcher.Content.Modrinth;

/// <summary>
/// Modrinth 数据获取失败：HTTP 状态异常、网络失败、或响应不是合法 JSON。
/// </summary>
/// <remarks>
/// 与 <c>VersionDataException</c> / <c>JavaRuntimeDataException</c> 并列的第三个"远端数据读不出来"：
/// 调用方要据此给出"Modrinth 现在取不到，稍后再试"这类结论，而不是漏出一个 <c>HttpRequestException</c>。
/// </remarks>
public sealed class ModrinthException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="message">消息。</param>
    /// <param name="innerException">底层异常。</param>
    public ModrinthException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary><c>/v2/search</c> 的一次响应（只取界面要用的字段）。</summary>
/// <param name="TotalHits">匹配总数（搜索接口给的总量，不是本页条数）。</param>
/// <param name="Hits">本页条目（远端可以给 <c>null</c>，由客户端归一成空列表）。</param>
public sealed record ModrinthSearchPage(
    [property: JsonPropertyName("total_hits")] int TotalHits,
    [property: JsonPropertyName("hits")] IReadOnlyList<ModrinthSearchHit>? Hits);

/// <summary>搜索结果里的一条项目（整合包 / 模组都走同一个形状）。</summary>
/// <param name="ProjectId">项目 id（后续取版本、下载都用它）。</param>
/// <param name="Slug">可读的短名（URL 里的那一段）。</param>
/// <param name="Title">项目名。</param>
/// <param name="Description">一句话简介（**原样显示**：Modrinth 上的文字多为英文，属于远端数据、不翻译）。</param>
/// <param name="Downloads">累计下载量。</param>
/// <param name="LatestVersionId">最新版本的**id**（搜索接口不给版本号，要另外批量查一次）。</param>
public sealed record ModrinthSearchHit(
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("downloads")] long Downloads,
    [property: JsonPropertyName("latest_version")] string? LatestVersionId);

/// <summary>一个 Modrinth 版本。</summary>
/// <param name="Id">版本 id。</param>
/// <param name="ProjectId">所属项目 id。</param>
/// <param name="VersionNumber">版本号（如 <c>15.0.0</c> / <c>15.0.0-beta.1</c>）。</param>
/// <param name="Name">版本名（作者可以写成任意句子）。</param>
/// <param name="DatePublished">发布时间。</param>
/// <param name="GameVersions">支持的 Minecraft 版本。</param>
/// <param name="Loaders">支持的加载器（<c>fabric</c> / <c>forge</c> / <c>neoforge</c> / <c>quilt</c>）。</param>
/// <param name="Files">
/// 这个版本的可下载文件（M7-d 起要下它）。
/// <para>
/// 一个版本**可以挂多个文件**（例如主 jar 加一个 sources jar），所以"要下哪一个"不能靠"第一个"猜 ——
/// 官方用 <see cref="ModrinthVersionFile.Primary"/> 明确标出主文件。
/// </para>
/// </param>
/// <param name="Dependencies">
/// 这个版本声明的依赖（M7-d 的递归起点，见 <see cref="ModrinthVersionDependency"/>）。
/// </param>
public sealed record ModrinthVersion(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("version_number")] string? VersionNumber,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("date_published")] DateTimeOffset? DatePublished,
    [property: JsonPropertyName("game_versions")] IReadOnlyList<string>? GameVersions,
    [property: JsonPropertyName("loaders")] IReadOnlyList<string>? Loaders,
    [property: JsonPropertyName("files")] IReadOnlyList<ModrinthVersionFile>? Files = null,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<ModrinthVersionDependency>? Dependencies = null);

/// <summary>一个版本挂的文件。</summary>
/// <param name="Url">下载地址（<c>cdn.modrinth.com</c>，已在出站白名单里）。</param>
/// <param name="FileName">文件名（**来自远端**，落盘前必须当不可信输入校验，见 <c>ContentInstaller</c>）。</param>
/// <param name="Size">字节数（未知时为 0）。</param>
/// <param name="Primary">是不是这个版本的主文件。</param>
/// <param name="Hashes">官方给的校验值（<c>sha1</c> / <c>sha512</c>）。</param>
public sealed record ModrinthVersionFile(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("filename")] string? FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("primary")] bool Primary,
    [property: JsonPropertyName("hashes")] ModrinthFileHashes? Hashes);

/// <summary>官方给的校验值。</summary>
/// <param name="Sha1">SHA-1（下载器按它校验，与 Mirai/清单协议同一套）。</param>
/// <param name="Sha512">SHA-512（目前不用，只用于排障时核对）。</param>
public sealed record ModrinthFileHashes(
    [property: JsonPropertyName("sha1")] string? Sha1,
    [property: JsonPropertyName("sha512")] string? Sha512);

/// <summary>
/// 一个版本声明的依赖。
/// </summary>
/// <param name="VersionId">被依赖的那个**确切版本** id（Modrinth 直接给答案，不需要我们再解一次"该装哪个版本"）。</param>
/// <param name="ProjectId">被依赖项目的 id（用来判断"这个项目已经装了"）。</param>
/// <param name="DependencyType">
/// 依赖类型：<c>required</c> / <c>optional</c> / <c>incompatible</c> / <c>embedded</c>。
/// <para>
/// **只有 <c>required</c> 会被递归下载**：<c>optional</c> 是"可选增强"（替用户做决定是错的）、
/// <c>embedded</c> 表示已经打进这个 jar 里了、<c>incompatible</c> 是"别装"。
/// 而且官方有时给 <c>required</c> 的依赖**不带 version_id**（只给 project_id），
/// 那种只能在浏览页让用户自己挑，不该在这里替它选一个版本。
/// </para>
/// </param>
public sealed record ModrinthVersionDependency(
    [property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("dependency_type")] string? DependencyType);

/// <summary>Modrinth 数据在界面上的数值写法。</summary>
public static class ModrinthText
{
    /// <summary>下载量的简写（<c>18632180</c> → <c>18.6M</c>）—— 与设计稿 p08 的「下载量」列同形。</summary>
    /// <param name="downloads">下载量。</param>
    /// <returns>文本。</returns>
    /// <remarks>
    /// **纯数据**（数字 + 国际单位词头），两种语言逐字相同，所以不走资源表；
    /// 用不变文化保证小数点永远是 <c>.</c>（设计稿里就是 <c>12.4M</c>）。
    /// </remarks>
    public static string Downloads(long downloads)
    {
        if (downloads < 0)
        {
            return "0";
        }

        if (downloads >= 1_000_000)
        {
            return (downloads / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (downloads >= 1_000)
        {
            return (downloads / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        return downloads.ToString(CultureInfo.InvariantCulture);
    }
}
