using System.Net;
using System.Security.Cryptography;
using System.Text;
using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// M4-f 的共用测试数据：一份最小原版 1.20.4、一份 Fabric / Quilt 的 profile，以及配套的 HTTP 桩。
/// <para>
/// 所有校验值都由**真正下发的字节**算出来 —— 这样"落盘内容对不对"是能被断言的，
/// 而不是靠"文件存在"这种弱断言。
/// </para>
/// </summary>
internal static class LoaderFixture
{
    /// <summary>客户端 JAR 的字节。</summary>
    public const string ClientJar = "client jar bytes";

    /// <summary>原版库的字节。</summary>
    public const string AuthlibJar = "authlib jar bytes";

    /// <summary>加载器库的字节（<c>downloads/artifact</c> 形态）。</summary>
    public const string FabricLoaderJar = "fabric loader jar bytes";

    /// <summary>库对象自带 <c>sha1</c> 的那个库的字节（Fabric profile 的真实形态）。</summary>
    public const string UrlFormJar = "url form jar bytes";

    /// <summary>既没有路径也没有校验值的那个库的字节（Quilt profile 的真实形态）。</summary>
    public const string UnverifiableJar = "unverifiable jar bytes";

    /// <summary>Quilt 加载器库的字节。</summary>
    public const string QuiltLoaderJar = "quilt loader jar bytes";

    /// <summary>Fabric profile 的地址（逐字符断言用）。</summary>
    public const string FabricProfileUrl = "https://meta.fabricmc.net/v2/versions/loader/1.20.4/0.15.7/profile/json";

    /// <summary>Quilt profile 的地址。</summary>
    public const string QuiltProfileUrl = "https://meta.quiltmc.org/v3/versions/loader/1.20.4/0.23.1/profile/json";

    /// <summary>Fabric 安装后的版本 id。</summary>
    public const string FabricId = "fabric-loader-0.15.7-1.20.4";

    /// <summary>Quilt 安装后的版本 id。</summary>
    public const string QuiltId = "quilt-loader-0.23.1-1.20.4";

    /// <summary>最小原版 1.20.4（一个客户端 JAR + 一个库，没有 natives / 资源索引）。</summary>
    /// <param name="id">版本 id（换一个游戏版本装加载器时要跟着换）。</param>
    public static string VanillaJson(string id = "1.20.4") =>
        $$"""
        {
          "id": "{{id}}",
          "mainClass": "net.minecraft.client.main.Main",
          "downloads": {
            "client": {
              "url": "https://piston-data.mojang.com/v1/objects/cccc/client.jar",
              "sha1": "{{Sha1Hex(ClientJar)}}",
              "size": {{ClientJar.Length}}
            }
          },
          "libraries": [
            {
              "name": "com.mojang:authlib:6.0.54",
              "downloads": { "artifact": {
                "path": "com/mojang/authlib/6.0.54/authlib-6.0.54.jar",
                "url": "https://libraries.minecraft.net/com/mojang/authlib/6.0.54/authlib-6.0.54.jar",
                "sha1": "{{Sha1Hex(AuthlibJar)}}",
                "size": {{AuthlibJar.Length}}
              } }
            }
          ],
          "arguments": { "game": ["--username", "${auth_player_name}"] }
        }
        """;

    /// <summary>加载器 profile（形状与两家的真实返回值一致）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="id">profile 声明的版本 id。</param>
    /// <param name="inheritsFrom">它继承的游戏版本。</param>
    /// <param name="loaderJarSha1">加载器库的校验值；传错值用来验"下载失败不落 JSON"。</param>
    public static string ProfileJson(
        LoaderKind kind,
        string id,
        string inheritsFrom = "1.20.4",
        string? loaderJarSha1 = null)
    {
        var quilt = kind == LoaderKind.Quilt;
        var loaderVersion = quilt ? "0.23.1" : "0.15.7";
        var artifact = quilt ? "quilt-loader" : "fabric-loader";
        var body = quilt ? QuiltLoaderJar : FabricLoaderJar;
        var group = quilt ? "org/quiltmc" : "net/fabricmc";
        var host = quilt ? "maven.quiltmc.org/repository/release" : "maven.fabricmc.net";

        return $$"""
        {
          "id": "{{id}}",
          "inheritsFrom": "{{inheritsFrom}}",
          "mainClass": "{{(quilt ? "org.quiltmc.loader.impl.launch.knot.KnotClient" : "net.fabricmc.loader.impl.launch.knot.KnotClient")}}",
          "libraries": [
            {
              "name": "{{(quilt ? "org.quiltmc" : "net.fabricmc")}}:{{artifact}}:{{loaderVersion}}",
              "downloads": { "artifact": {
                "path": "{{group}}/{{artifact}}/{{loaderVersion}}/{{artifact}}-{{loaderVersion}}.jar",
                "url": "https://{{host}}/{{group}}/{{artifact}}/{{loaderVersion}}/{{artifact}}-{{loaderVersion}}.jar",
                "sha1": "{{loaderJarSha1 ?? Sha1Hex(body)}}",
                "size": {{body.Length}}
              } }
            }
          ]
        }
        """;
    }

    /// <summary>按下发的字节造一套响应（客户端 JAR、原版库、profile、加载器库）。</summary>
    /// <param name="kind">加载器种类。</param>
    /// <param name="id">profile 声明的版本 id。</param>
    /// <param name="inheritsFrom">profile 继承的游戏版本。</param>
    /// <param name="loaderJarSha1">加载器库的校验值（传错值验失败路径）。</param>
    /// <param name="vanillaJson">要按清单地址提供的原版版本 JSON；为 <see langword="null"/> 表示不发它。</param>
    public static StubHandler Stub(
        LoaderKind kind = LoaderKind.Fabric,
        string id = FabricId,
        string inheritsFrom = "1.20.4",
        string? loaderJarSha1 = null,
        string? vanillaJson = null)
    {
        var quilt = kind == LoaderKind.Quilt;
        var artifact = quilt ? "quilt-loader" : "fabric-loader";
        var version = quilt ? "0.23.1" : "0.15.7";
        var body = quilt ? QuiltLoaderJar : FabricLoaderJar;

        var stub = new StubHandler()
            .RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar)
            .RespondTo("authlib-6.0.54.jar", HttpStatusCode.OK, AuthlibJar)
            .RespondTo(
                quilt ? "meta.quiltmc.org" : "meta.fabricmc.net",
                HttpStatusCode.OK,
                ProfileJson(kind, id, inheritsFrom, loaderJarSha1))
            .RespondTo($"{artifact}-{version}.jar", HttpStatusCode.OK, body);

        if (vanillaJson is not null)
        {
            stub.RespondTo("/mc/game/version_manifest_v2.json", HttpStatusCode.OK, Manifest(vanillaJson));
            stub.RespondTo("/packages/bbbb/1.20.4.json", HttpStatusCode.OK, vanillaJson);
        }

        return stub;
    }

    /// <summary>
    /// 真实形状的 Fabric profile：库把仓库基址与校验值写在**库对象自身**
    /// （<c>name</c> + <c>url</c> + <c>sha1</c> + <c>size</c>，没有 <c>downloads/artifact</c>），
    /// 还有一类连校验值都没有（Quilt 的 profile 全部是这一种）。
    /// <para>
    /// 这份数据是照 2026-10-04 从 <c>meta.fabricmc.net</c> 真实取回的 profile 抄的：
    /// 手写的桩全都用 <c>downloads/artifact</c> 形态，会把"真实 profile 走的是另一条分支"这件事整个漏掉。
    /// </para>
    /// </summary>
    /// <param name="id">profile 声明的版本 id。</param>
    public static string UrlFormProfileJson(string id = FabricId) =>
        $$"""
        {
          "id": "{{id}}",
          "inheritsFrom": "1.20.4",
          "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
          "arguments": { "game": [], "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "] },
          "libraries": [
            {
              "name": "org.ow2.asm:asm:9.6",
              "url": "https://maven.fabricmc.net/",
              "sha1": "{{Sha1Hex(UrlFormJar)}}",
              "size": {{UrlFormJar.Length}}
            },
            {
              "name": "net.fabricmc:intermediary:1.20.4",
              "url": "https://maven.fabricmc.net/"
            }
          ]
        }
        """;

    /// <summary>真实形状 profile 的桩。</summary>
    /// <param name="tamperVerifiableLibrary">把带 sha1 的那个库下发成错的字节（验"真的在校验"）。</param>
    /// <param name="vanillaJson">要按清单地址提供的原版版本 JSON；为 <see langword="null"/> 表示不发它。</param>
    public static StubHandler UrlFormStub(bool tamperVerifiableLibrary = false, string? vanillaJson = null)
    {
        var stub = new StubHandler()
            .RespondTo("objects/cccc/client.jar", HttpStatusCode.OK, ClientJar)
            .RespondTo("authlib-6.0.54.jar", HttpStatusCode.OK, AuthlibJar)
            .RespondTo("meta.fabricmc.net", HttpStatusCode.OK, UrlFormProfileJson())
            .RespondTo(
                "org/ow2/asm/asm/9.6/asm-9.6.jar",
                HttpStatusCode.OK,
                tamperVerifiableLibrary ? "tampered bytes" : UrlFormJar)
            .RespondTo("intermediary-1.20.4.jar", HttpStatusCode.OK, UnverifiableJar);

        if (vanillaJson is not null)
        {
            stub.RespondTo("/mc/game/version_manifest_v2.json", HttpStatusCode.OK, Manifest(vanillaJson));
            stub.RespondTo("/packages/bbbb/1.20.4.json", HttpStatusCode.OK, vanillaJson);
        }

        return stub;
    }

    /// <summary>一份只列了一个版本的清单。</summary>
    /// <param name="versionJson">该版本的 JSON（用来算 sha1）。</param>
    /// <param name="id">清单里那个版本的 id（可与被测版本不同，用来验"清单里没有它"）。</param>
    public static string Manifest(string versionJson, string id = "1.20.4") =>
        "{\"latest\":{\"release\":\"" + id + "\",\"snapshot\":\"" + id + "\"},\"versions\":[{"
        + "\"id\":\"" + id + "\",\"type\":\"release\","
        + "\"url\":\"https://piston-meta.mojang.com/v1/packages/bbbb/" + id + ".json\","
        + "\"sha1\":\"" + Sha1Hex(versionJson) + "\","
        + "\"releaseTime\":\"2023-12-07T11:00:00+00:00\",\"time\":\"2023-12-07T12:00:00+00:00\"}]}";

    /// <summary>把原版版本 JSON 直接写进游戏目录（这样加载器安装不必先去取清单）。</summary>
    /// <param name="gameDirectory">游戏根目录。</param>
    /// <param name="id">版本 id。</param>
    public static void WriteVanillaVersionJson(string gameDirectory, string id = "1.20.4")
    {
        var directory = Path.Combine(gameDirectory, "versions", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, id + ".json"),
            VanillaJson(id),
            new UTF8Encoding(false));
    }

    /// <summary>SHA-1 十六进制。</summary>
    /// <param name="body">文本。</param>
    /// <returns>校验值。</returns>
    public static string Sha1Hex(string body) => Sha1Hex(Encoding.UTF8.GetBytes(body));

    private static string Sha1Hex(byte[] bytes)
    {
#pragma warning disable CA5350 // 与产品代码同理：SHA-1 是 Minecraft 清单协议规定的完整性字段。
        var hash = SHA1.HashData(bytes);
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
