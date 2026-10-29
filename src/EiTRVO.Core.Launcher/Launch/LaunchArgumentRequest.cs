using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 一次启动要用的账户凭据（命令行里的 <c>--username</c> / <c>--uuid</c> / <c>--accessToken</c> / <c>--userType</c>）。
/// <para>
/// 刻意是四个**普通字符串**，而不是认证层的 <c>LaunchCredential</c>：
/// 依赖方向是 Auth → Launcher（见 AGENTS.md §2.4、§2.5），内核不能反向引用认证层。
/// M4 的编排层负责把 <c>LaunchCredential</c> 映射成这里的值，那是纯数据搬运。
/// </para>
/// </summary>
/// <param name="PlayerName">玩家名（离线账户即用户输入的名字）。</param>
/// <param name="Uuid">账户 UUID；带不带连字符都接受。</param>
/// <param name="AccessToken">访问令牌；离线账户由认证层给出 <c>0</c>（D-6 的决策：空串容易被当成"没值"）。</param>
/// <param name="UserType">用户类型（正版 <c>msa</c>，离线 <c>mojang</c>）。</param>
/// <param name="Xuid">Xbox 用户 id；离线账户为 <see langword="null"/>，命令行里按 <c>0</c> 传。</param>
public sealed record LaunchIdentity(
    string PlayerName,
    string Uuid,
    string AccessToken,
    string UserType,
    string? Xuid = null)
{
    /// <summary>
    /// 命令行里的 UUID 形式：**不带连字符**。
    /// <para>
    /// 与官方启动器一致（官方日志里是 <c>069a79f444e94726a5befca90e38aaf5</c> 这种样子）：
    /// 客户端自己会把无连字符的形式补成标准 UUID，两种都认，
    /// 但"跟官方一样"能少一类与第三方服务端插件/工具的意外差异。
    /// </para>
    /// </summary>
    public string UuidWithoutDashes => Uuid.Replace("-", string.Empty, StringComparison.Ordinal);
}

/// <summary>
/// 启动器品牌：<c>-Dminecraft.launcher.brand</c> 与 <c>-Dminecraft.launcher.version</c>。
/// <para>
/// 单独一个类型，是为了**不把品牌写死在参数构建里**：Neo 把 <c>eitrvo-neo</c> / <c>26</c>
/// 硬编码在参数构建与占位符表两处（AGENTS.md §5.1 明确禁止把 Neo 的品牌带进 Core），
/// 这里由调用方从 <c>ILauncherInfo</c> 传进来。
/// </para>
/// </summary>
/// <param name="Name">品牌名（会原样进命令行，建议用不含空格的短名）。</param>
/// <param name="Version">品牌版本。</param>
public sealed record LauncherBranding(string Name, string Version)
{
    /// <summary>默认品牌 —— 只是让调用方不必每次都传，M4-d 会用 <c>ILauncherInfo</c> 的真实值覆盖。</summary>
    public static LauncherBranding Default { get; } = new("eitrvo-core", "1.0");
}

/// <summary>
/// 构建一次启动命令行所需的全部输入。
/// <para>
/// 每个字段都在这儿出现，是因为"参数构建"必须是**纯函数**（风险登记 R1 的对策）：
/// 它不读设置、不查磁盘、不碰网络，只按给定输入算出命令行 —— 于是完整的命令行可以在单测里
/// 逐参数断言，而不是等到"起了个进程看看能不能进主菜单"。
/// </para>
/// </summary>
/// <param name="Version">已合并继承链的版本。</param>
/// <param name="VersionId">
/// 版本 id，同时也是 <c>versions\&lt;id&gt;\</c> 的目录名与命令行里的 <c>${version_name}</c>。
/// </param>
/// <param name="GameDirectory">游戏根目录（<c>assets</c> / <c>libraries</c> / <c>versions</c> 的基准）。</param>
/// <param name="InstanceDirectory">游戏目录（命令行里的 <c>${game_directory}</c>；启用隔离目录时是实例目录）。</param>
/// <param name="NativesDirectory">natives 解压目录（<c>-Djava.library.path</c> 与 <c>${natives_directory}</c>）。</param>
/// <param name="Identity">账户凭据。</param>
/// <param name="MemoryMb">最大堆内存（MB）。</param>
/// <param name="JavaMajorVersion">本次要用的 Java 主版本（决定 Java 8 的兼容裁剪，C-1/C-5）。</param>
/// <param name="ClientId">
/// 微软应用注册的 Client ID（<c>${clientid}</c>）。**必须从配置读**，禁止硬编码（AGENTS.md §6）；
/// 空值会用 <c>0</c> 顶替（离线启动不需要它，而空串会被某些版本的 joptsimple 当成"没给值"）。
/// </param>
/// <param name="Platform">当前平台（<c>os.name</c> / <c>os.arch</c> / <c>os.version</c> 的求值依据）。</param>
/// <param name="FileExists">
/// 判断文件是否存在。做成参数而不是直接调 <see cref="File.Exists(string)"/>，是为了让"哪些库在磁盘上"
/// 变成测试的输入 —— 于是类路径去重、缺失文件上报都能在**不建任何文件**的情况下断言。
/// </param>
public sealed record LaunchArgumentRequest(
    ResolvedVersion Version,
    string VersionId,
    string GameDirectory,
    string InstanceDirectory,
    string NativesDirectory,
    LaunchIdentity Identity,
    int MemoryMb,
    int JavaMajorVersion,
    string ClientId,
    GamePlatform Platform,
    Func<string, bool> FileExists)
{
    /// <summary>品牌（默认值见 <see cref="LauncherBranding.Default"/>）。</summary>
    public LauncherBranding Branding { get; init; } = LauncherBranding.Default;

    /// <summary>
    /// 客户端 JAR 的绝对路径；<see langword="null"/> 时按 <c>versions\&lt;VersionId&gt;\&lt;VersionId&gt;.jar</c> 找。
    /// <para>
    /// 启动链需要这个覆盖：实例目录（<c>versions\&lt;实例名&gt;\&lt;实例名&gt;.jar</c>）与旧布局的
    /// <c>versions\&lt;版本 id&gt;\&lt;版本 id&gt;.jar</c> 都可能是正确的，而加载器版本还可能只有父版本的 JAR
    /// （Forge / NeoForge / OptiFine 的官方安装器不生成自己的客户端 JAR）—— 由调用方按磁盘事实挑一个，
    /// 参数构建继续保持纯函数。
    /// </para>
    /// </summary>
    public string? ClientJarPath { get; init; }

    /// <summary>游戏窗口宽；<see langword="null"/> 表示不改窗口大小。</summary>
    public int? Width { get; init; }

    /// <summary>游戏窗口高；<see langword="null"/> 表示不改窗口大小。</summary>
    public int? Height { get; init; }

    /// <summary>是否以演示模式启动（命令行里的 <c>--demo</c>）。</summary>
    public bool IsDemoUser { get; init; }

    /// <summary>设置里的附加 JVM 参数（原样按空格/引号切分后**追加在最后**，可以覆盖默认值）。</summary>
    public string ExtraJvmArguments { get; init; } = string.Empty;

    /// <summary>设置里的附加游戏参数。</summary>
    public string ExtraGameArguments { get; init; } = string.Empty;

    /// <summary>
    /// pre-1.6 的 virtual 资源目录（旧版参数串里的 <c>${game_assets}</c>）。
    /// <para>
    /// 只有 1.5.2 及更早用得到它：那些版本的资源不是"按哈希存放 + 索引"，而是铺在一个虚拟目录里
    /// （<c>assets\virtual\&lt;索引 id&gt;</c>，或更早的 <c>resources\</c>），
    /// 具体是哪个由**资源索引里的 <c>virtual</c> 标志**决定 —— 那是调用方读磁盘才知道的事，
    /// 所以值由调用方给。为 <see langword="null"/> 时退回 <c>assets</c> 根目录。
    /// </para>
    /// </summary>
    public string? GameAssetsDirectory { get; init; }
}
