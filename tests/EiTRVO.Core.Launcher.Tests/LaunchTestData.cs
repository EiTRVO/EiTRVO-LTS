using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// 启动参数相关测试共用的**真实清单参考数据**（1.20.4 的 <c>arguments</c> 与库摘录、1.12.2 形状的库、
/// 1.7.10 与 1.5.2 的旧版参数串）。
/// <para>
/// 之所以做成一个基类而不是各写一份：这些工厂是"从 piston-meta 逐字段抄下来的**真实输入**"，
/// 参数构建（M4-a）与参数脱敏（M4-b）必须面对同一批输入。各抄一份的话，
/// 一边按新抄的真实数据改了、另一边还在测旧形状，而**两边的测试都是绿的**。
/// </para>
/// <para>
/// 所有工厂都是内存数据 + <c>FileExists</c> 谓词，不碰磁盘、不联网。
/// </para>
/// </summary>
public abstract class LaunchTestData
{
    /// <summary>游戏根目录（<c>assets</c> / <c>libraries</c> / <c>versions</c> 的基准）。</summary>
    protected const string GameDirectory = @"C:\mc\.minecraft";

    /// <summary>游戏目录（<c>${game_directory}</c>）。</summary>
    protected const string InstanceDirectory = @"C:\mc\.minecraft\instances\fabric-1.20.4";

    /// <summary>natives 解压目录。</summary>
    protected const string NativesDirectory = @"C:\mc\.minecraft\versions\1.20.4\natives";

    /// <summary>库目录。</summary>
    protected const string LibrariesDirectory = @"C:\mc\.minecraft\libraries";

    /// <summary>资源根目录。</summary>
    protected const string AssetsDirectory = @"C:\mc\.minecraft\assets";

    /// <summary>日志配置落盘位置（与安装器共用同一份规则）。</summary>
    protected const string LoggingConfigPath = @"C:\mc\.minecraft\assets\log_configs\client-1.12.xml";

    /// <summary>配置里的 Client ID（真实值；脱敏测试用它验证"刻意不脱敏"的那一类）。</summary>
    protected const string ClientId = "5a0b94a6-2810-4a43-a722-ba15271955b4";

    /// <summary>离线 UUID（Java 算出来的真实值，见 <c>OfflineUuidTests</c>）。</summary>
    protected const string OfflineUuid = "b50ad385-829d-3141-a216-7e7d7539ba7f";

    /// <summary>玩家名。</summary>
    protected const string PlayerName = "Player_123456789";

    /// <summary>
    /// 一个 JWT 形状的假访问令牌（够长，能触发"值清扫"；不含空格与引号）。
    /// <para>
    /// 刻意写成一眼假的样子：它是日志脱敏测试要"搜不到"的那个值，
    /// 若长得像真令牌，review 时会把"某个测试文件里有一个令牌"当成事故。
    /// </para>
    /// </summary>
    protected const string TestAccessToken =
        "eyJhbGciOiJIUzI1NiJ9.eyJmYWtlIjp0cnVlfQ.SAMPLE-SIGNATURE-DO-NOT-LOG-ME";

    /// <summary>1.20.4 的 Windows 规则给的那个参数（真实清单里就有）。</summary>
    protected const string HeapDumpArgument =
        "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump";

    /// <summary>离线账户的形状（D-6：令牌是 <c>0</c>、用户类型 <c>mojang</c>）。</summary>
    protected static LaunchIdentity OfflineIdentity { get; } = new(PlayerName, OfflineUuid, "0", "mojang");

    /// <summary>正版账户的形状（长令牌 + 带连字符的 UUID + xuid）—— 脱敏测试用它。</summary>
    protected static LaunchIdentity MicrosoftIdentity { get; } =
        new(PlayerName, OfflineUuid, TestAccessToken, "msa", "2535429452678874");

    /// <summary>把命令行拼回一行（**未脱敏**，只用于断言原始实参）。</summary>
    protected static string Describe(LaunchCommandLine launch) => string.Join(' ', launch.Arguments);

    /// <summary>逐参数比对（失败时指出第几个参数不符，比 <c>CollectionAssert</c> 好定位）。</summary>
    protected static void AssertSequence(string[] expected, IReadOnlyList<string> actual, string what)
    {
        Assert.AreEqual(expected.Length, actual.Count, $"{what}：个数不符。实际：{string.Join(' ', actual)}");

        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"{what}：第 {index + 1} 个参数不符。");
        }
    }

    /// <summary>构造一次启动的请求（默认用 1.20.4 的真实清单 + 离线账户）。</summary>
    protected static LaunchArgumentRequest Request(
        VersionJson? version = null,
        string versionId = "1.20.4",
        GamePlatform? platform = null,
        Func<string, bool>? fileExists = null,
        int javaMajor = 17,
        int memoryMb = 4096,
        LaunchIdentity? identity = null)
    {
        var json = version ?? Real1204Json();

        return new LaunchArgumentRequest(
            Version: new ResolvedVersion(json, versionId, [versionId]),
            VersionId: versionId,
            GameDirectory: GameDirectory,
            InstanceDirectory: InstanceDirectory,
            NativesDirectory: Path.Combine(GameDirectory, "versions", versionId, "natives"),
            Identity: identity ?? OfflineIdentity,
            MemoryMb: memoryMb,
            JavaMajorVersion: javaMajor,
            ClientId: ClientId,
            Platform: platform ?? GamePlatform.WindowsX64,
            FileExists: fileExists ?? (_ => true));
    }

    /// <summary>1.20.4 的 <c>arguments.jvm</c>（照抄真实清单，含三条规则项）。</summary>
    protected static IReadOnlyList<VersionArgument> Real1204JvmArguments() =>
    [
        VersionArgument.Conditional(["-XstartOnFirstThread"], [AllowOs("osx")]),
        VersionArgument.Conditional([HeapDumpArgument], [AllowOs("windows")]),
        VersionArgument.Conditional(["-Xss1M"], [AllowArch("x86")]),
        VersionArgument.Plain("-Djava.library.path=${natives_directory}"),
        VersionArgument.Plain("-Djna.tmpdir=${natives_directory}"),
        VersionArgument.Plain("-Dorg.lwjgl.system.SharedLibraryExtractPath=${natives_directory}"),
        VersionArgument.Plain("-Dio.netty.native.workdir=${natives_directory}"),
        VersionArgument.Plain("-Dminecraft.launcher.brand=${launcher_name}"),
        VersionArgument.Plain("-Dminecraft.launcher.version=${launcher_version}"),
        VersionArgument.Plain("-cp"),
        VersionArgument.Plain("${classpath}"),
    ];

    /// <summary>1.20.4 的 <c>arguments.game</c>（照抄真实清单，含六个特性规则项）。</summary>
    protected static IReadOnlyList<VersionArgument> Real1204GameArguments() =>
    [
        VersionArgument.Plain("--username"),
        VersionArgument.Plain("${auth_player_name}"),
        VersionArgument.Plain("--version"),
        VersionArgument.Plain("${version_name}"),
        VersionArgument.Plain("--gameDir"),
        VersionArgument.Plain("${game_directory}"),
        VersionArgument.Plain("--assetsDir"),
        VersionArgument.Plain("${assets_root}"),
        VersionArgument.Plain("--assetIndex"),
        VersionArgument.Plain("${assets_index_name}"),
        VersionArgument.Plain("--uuid"),
        VersionArgument.Plain("${auth_uuid}"),
        VersionArgument.Plain("--accessToken"),
        VersionArgument.Plain("${auth_access_token}"),
        VersionArgument.Plain("--clientId"),
        VersionArgument.Plain("${clientid}"),
        VersionArgument.Plain("--xuid"),
        VersionArgument.Plain("${auth_xuid}"),
        VersionArgument.Plain("--userType"),
        VersionArgument.Plain("${user_type}"),
        VersionArgument.Plain("--versionType"),
        VersionArgument.Plain("${version_type}"),
        VersionArgument.Conditional(["--demo"], [AllowFeature("is_demo_user", true)]),
        VersionArgument.Conditional(
            ["--width", "${resolution_width}", "--height", "${resolution_height}"],
            [AllowFeature("has_custom_resolution", true)]),
        VersionArgument.Conditional(["--quickPlayPath", "${quickPlayPath}"], [AllowFeature("has_quick_plays_support", true)]),
        VersionArgument.Conditional(
            ["--quickPlaySingleplayer", "${quickPlaySingleplayer}"],
            [AllowFeature("is_quick_play_singleplayer", true)]),
        VersionArgument.Conditional(
            ["--quickPlayMultiplayer", "${quickPlayMultiplayer}"],
            [AllowFeature("is_quick_play_multiplayer", true)]),
        VersionArgument.Conditional(["--quickPlayRealms", "${quickPlayRealms}"], [AllowFeature("is_quick_play_realms", true)]),
    ];

    /// <summary>1.20.4 的版本 JSON（正文照抄真实清单；库列表可替换）。</summary>
    protected static VersionJson Real1204Json(IReadOnlyList<Library>? libraries = null) => new()
    {
        Id = "1.20.4",
        Type = "release",
        Assets = "12",
        MainClass = "net.minecraft.client.main.Main",
        JavaVersion = new JavaVersionRequirement { Component = "java-runtime-gamma", MajorVersion = 17 },
        Logging = new VersionLogging
        {
            Client = new VersionLoggingClient
            {
                Argument = "-Dlog4j.configurationFile=${path}",
                Type = "log4j2-xml",
                File = new DownloadInfo
                {
                    Id = "client-1.12.xml",
                    Sha1 = "bd65e7d2e3c237be76cfbef4c2405033d7f91521",
                    Size = 888,
                    Url = "https://piston-data.mojang.com/v1/objects/bd65e7d2e3c237be76cfbef4c2405033d7f91521/client-1.12.xml",
                },
            },
        },
        Arguments = new VersionArguments { Jvm = Real1204JvmArguments(), Game = Real1204GameArguments() },
        Libraries = libraries ?? Real1204Libraries(),
    };

    /// <summary>1.20.4 库列表的摘录（真实路径与坐标，含一个 Windows 专属的 natives 分类器与一个 Linux 专属项）。</summary>
    /// <remarks>
    /// 1.19 起 natives 是**普通库**（分类器写在坐标与文件名里），而清单只按 <c>os.name</c> 过滤 ——
    /// 于是 Windows 的 x64 / arm64 / x86 三种 natives 会**都**进类路径（LWJGL 运行时按当前架构自己挑），
    /// 这与官方启动器的行为一致，不是"过度包含"。用真实的 1.20.4 清单核过：
    /// 88 个库 → 43 个无条件库 + 7 个 LWJGL 构件 × 3 个 Windows 变体 + 客户端 JAR = 65 条，一条不多一条不少。
    /// </remarks>
    protected static IReadOnlyList<Library> Real1204Libraries() =>
    [
        MavenLibrary("com.github.oshi:oshi-core:6.4.5", "com/github/oshi/oshi-core/6.4.5/oshi-core-6.4.5.jar"),
        MavenLibrary("com.google.code.gson:gson:2.10.1", "com/google/code/gson/gson/2.10.1/gson-2.10.1.jar"),
        MavenLibrary("com.mojang:authlib:6.0.52", "com/mojang/authlib/6.0.52/authlib-6.0.52.jar"),
        MavenLibrary("org.lwjgl:lwjgl:3.3.2", "org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2.jar"),
        PlatformLibrary(
            "org.lwjgl:lwjgl:3.3.2:natives-windows",
            "org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-windows.jar",
            "windows"),
        PlatformLibrary(
            "org.lwjgl:lwjgl:3.3.2:natives-linux",
            "org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-linux.jar",
            "linux"),
    ];

    /// <summary>1.7.10 的 <c>minecraftArguments</c>（真实整串：它**声明了** <c>${user_properties}</c>）。</summary>
    protected const string Legacy1710Arguments =
        "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} "
        + "--assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} "
        + "--accessToken ${auth_access_token} --userProperties ${user_properties} --userType ${user_type}";

    /// <summary>1.7.10 的版本 JSON（旧版整串参数）。</summary>
    protected static VersionJson Legacy1710Json() => new()
    {
        Id = "1.7.10",
        Type = "release",
        Assets = "1.7.10",
        MainClass = "net.minecraft.client.main.Main",
        JavaVersion = new JavaVersionRequirement { Component = "jre-legacy", MajorVersion = 8 },
        MinecraftArguments = Legacy1710Arguments,
        Libraries = [MavenLibrary("com.mojang:authlib:1.5.21", "com/mojang/authlib/1.5.21/authlib-1.5.21.jar")],
    };

    /// <summary>1.5.2（真实模板：玩家名是第一个位置参数、资源用 <c>${game_assets}</c>、资源索引是 <c>pre-1.6</c>）。</summary>
    protected static VersionJson Legacy152Json() => new()
    {
        Id = "1.5.2",
        Type = "release",
        Assets = "pre-1.6",
        MainClass = "net.minecraft.launchwrapper.Launch",
        JavaVersion = new JavaVersionRequirement { Component = "jre-legacy", MajorVersion = 8 },
        MinecraftArguments =
            "${auth_player_name} ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}",
    };

    /// <summary>一个带主构件的库。</summary>
    protected static Library MavenLibrary(string name, string path) => new()
    {
        Name = name,
        Downloads = new LibraryDownloads
        {
            Artifact = new DownloadInfo
            {
                Path = path,
                Sha1 = "0000000000000000000000000000000000000000",
                Size = 1,
                Url = "https://libraries.minecraft.net/" + path,
            },
        },
    };

    /// <summary>一个只在某个系统上生效的库。</summary>
    protected static Library PlatformLibrary(string name, string path, string osName) =>
        MavenLibrary(name, path) with { Rules = [AllowOs(osName)] };

    /// <summary>"只有分类器"的库（1.12.2 及以前的 LWJGL 2 里有这种形状）：没有主构件，也就没有主 JAR。</summary>
    protected static Library NativesOnlyLibrary(string name, string nativesPath) => new()
    {
        Name = name,
        Natives = new Dictionary<string, string>(StringComparer.Ordinal) { ["windows"] = "natives-windows" },
        Downloads = new LibraryDownloads
        {
            Classifiers = new Dictionary<string, DownloadInfo>(StringComparer.Ordinal)
            {
                ["natives-windows"] = new DownloadInfo
                {
                    Path = nativesPath,
                    Sha1 = "0000000000000000000000000000000000000000",
                    Size = 1,
                    Url = "https://libraries.minecraft.net/" + nativesPath,
                },
            },
        },
    };

    /// <summary>按系统名生效的规则。</summary>
    protected static VersionRule AllowOs(string name) => new("allow", new VersionRuleOs(name, null, null), null);

    /// <summary>按架构生效的规则。</summary>
    protected static VersionRule AllowArch(string arch) => new("allow", new VersionRuleOs(null, arch, null), null);

    /// <summary>按特性生效的规则。</summary>
    protected static VersionRule AllowFeature(string name, bool value) =>
        new("allow", null, new Dictionary<string, bool>(StringComparer.Ordinal) { [name] = value });
}
