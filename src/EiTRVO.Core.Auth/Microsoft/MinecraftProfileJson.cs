using System.Globalization;
using System.Text.Json;
using EiTRVO.Core.Launcher.Net;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Microsoft;

/// <summary>
/// 官方**玩家档案**（<c>GET /minecraft/profile</c>）的读法。
/// <para>
/// 抽成一个类型而不是留在 <see cref="MinecraftAuthChain"/> 里：同一个档案响应现在有三处会拿到它 ——
/// 登录链的最后一步、皮肤上传的响应（官方把改完之后的档案原样回给你）、
/// 以及恢复默认皮肤之后的重新读取。**三处的规则必须一模一样**：
/// </para>
/// <list type="bullet">
/// <item>32 位无连字符的 UUID 要换成标准形式（命令行与存档只认一种形状）；</item>
/// <item>皮肤地址官方给的是 <c>http://textures.minecraft.net/…</c>，要升到 https，越界的要丢掉
/// （M5-1 的真机发现 —— 丢掉的话每个真实账户的皮肤都会显示"未获取"）；</item>
/// <item>生效的那一条是 <c>state=ACTIVE</c>，都没有时退回第一条。</item>
/// </list>
/// <para>
/// 复制第三份实现是最容易发生的事故：改了其中一处的规则，另外两处会安静地按旧规则走。
/// </para>
/// </summary>
internal static class MinecraftProfileJson
{
    /// <summary>读一个玩家档案。</summary>
    /// <param name="body">响应正文。</param>
    /// <param name="hostPolicy">出站域名策略（皮肤地址要过它）。</param>
    /// <returns>档案。</returns>
    /// <exception cref="AuthException">正文不是合法 JSON、或缺少 id / name。</exception>
    public static MinecraftProfile Parse(string body, HttpHostPolicy hostPolicy)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(hostPolicy);

        ProfileResponse? raw;

        try
        {
            raw = LauncherJson.Deserialize<ProfileResponse>(body, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new AuthException(TextTemplate.Of("玩家档案返回的不是合法 JSON。"), ex);
        }

        if (raw is null)
        {
            throw new AuthException(TextTemplate.Of("玩家档案的响应是空的。"));
        }

        if (string.IsNullOrWhiteSpace(raw.Id) || string.IsNullOrWhiteSpace(raw.Name))
        {
            throw new AuthException(TextTemplate.Of("玩家档案里没有 id 或 name。"));
        }

        var skin = ActiveSkin(raw.Skins);

        return new MinecraftProfile(
            Uuid: WithDashes(raw.Id),
            Name: raw.Name,
            SkinUrl: NormalizeSkinUrl(skin?.Url, hostPolicy),
            SkinVariant: skin?.Variant,
            HasCape: raw.Capes is { Count: > 0 });
    }

    /// <summary>把 32 位无连字符的 UUID 写成标准形式（命令行与存档都用这一种）。</summary>
    /// <param name="id">官方给的 id。</param>
    /// <returns>带连字符的 UUID；形状不对时原样返回。</returns>
    public static string WithDashes(string id) =>
        id.Length != 32
            ? id
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..]}");

    /// <summary>
    /// 规整皮肤地址：允许的主机 + 升到 https；不允许就返回 <see langword="null"/>。
    /// </summary>
    /// <param name="url">官方给的地址。</param>
    /// <param name="hostPolicy">出站域名策略。</param>
    /// <returns>可用地址；不可用时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 不因为一个反常的皮肤地址让整次登录失败（那是"因为头像挂了所以登不上"），
    /// 但也不把不允许的地址留下来当"可用地址" —— 它迟早会被拿去下载。
    /// </remarks>
    public static string? NormalizeSkinUrl(string? url, HttpHostPolicy hostPolicy)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        if (hostPolicy.IsAllowed(parsed))
        {
            return parsed.ToString();
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var upgraded = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

        return hostPolicy.IsAllowed(upgraded) ? upgraded.ToString() : null;
    }

    /// <summary>档案里**生效的那一条**皮肤（<c>state=ACTIVE</c>；都没有就退回第一条）。</summary>
    private static SkinEntry? ActiveSkin(IReadOnlyList<SkinEntry>? skins)
    {
        if (skins is null || skins.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < skins.Count; i++)
        {
            if (string.Equals(skins[i].State, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return skins[i];
            }
        }

        return skins[0];
    }

    /// <summary>玩家档案响应。</summary>
    private sealed record ProfileResponse
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public IReadOnlyList<SkinEntry>? Skins { get; init; }

        public IReadOnlyList<CapeEntry>? Capes { get; init; }
    }

    /// <summary>档案里的一条皮肤。</summary>
    private sealed record SkinEntry
    {
        public string? State { get; init; }

        public string? Url { get; init; }

        public string? Variant { get; init; }
    }

    /// <summary>档案里的一条披风。</summary>
    private sealed record CapeEntry
    {
        public string? Id { get; init; }
    }
}
