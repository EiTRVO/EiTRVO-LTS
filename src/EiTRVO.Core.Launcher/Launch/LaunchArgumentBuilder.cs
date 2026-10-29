using System.Globalization;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 启动参数构建（C-1…C-5）：把"合并后的版本 JSON + 账户 + 设置"算成一条完整的命令行。
/// <para>
/// **纯函数**：不读设置、不查磁盘、不碰网络、不改任何状态。文件是否存在由
/// <see cref="LaunchArgumentRequest.FileExists"/> 注入，平台是数据（<see cref="GamePlatform"/>）。
/// 这是风险登记 R1 的对策 —— M4 里最容易出错的就是参数构建（Neo 的 <c>BuildLaunchArgs</c>
/// 混在 1177 行的编排里，只能靠"起个进程看看能不能进主菜单"来验），
/// 拆成纯函数之后，完整命令行可以在单测里逐参数断言。
/// </para>
/// <para>
/// 顺序（JVM 参数 → 主类 → 游戏参数）：
/// </para>
/// <list type="number">
/// <item>启动器的默认参数：<c>-Xmx</c>、<c>-Djava.library.path</c>、品牌、<c>-Dlog4j2.formatMsgNoLookups</c>。</item>
/// <item>日志配置（C-5；Java 8 跳过）。</item>
/// <item>版本 JSON 的 <c>arguments.jvm</c>（按 Java 版本裁剪、按安全策略过滤、按去重键去掉与我们重复的）。</item>
/// <item>设置里的附加 JVM 参数（放在最后且**不参与去重**，用户要能覆盖 <c>-Xmx</c>）。</item>
/// <item>Java 8 的额外参数。</item>
/// <item>Forge / NeoForge 的 <c>--add-opens</c> 修正（C-3）。</item>
/// <item><c>-cp</c> + 库列表算出的类路径。</item>
/// <item>主类。</item>
/// <item>游戏参数（1.13+ 用 <c>arguments.game</c>，1.12- 用 <c>minecraftArguments</c>）。</item>
/// <item>设置里的附加游戏参数。</item>
/// <item>窗口分辨率（没被版本 JSON 的规则覆盖时才补）、<c>--userProperties</c> 兜底（C-2）。</item>
/// </list>
/// </summary>
public static class LaunchArgumentBuilder
{
    /// <summary>版本 JSON 没声明主类时的兜底（原版客户端一直用的那个）。</summary>
    private const string DefaultMainClass = "net.minecraft.client.main.Main";

    /// <summary>注入类参数的拒绝理由。</summary>
    private const string UnsafeArgumentReason =
        "参数属于注入类（-javaagent / -agentlib / -agentpath / -XX:On*），已拒绝。";

    /// <summary>构造一次启动的命令行。</summary>
    /// <param name="request">全部输入。</param>
    /// <returns>完整命令行与过程中发现的问题。</returns>
    /// <exception cref="ArgumentException">版本 id 不能用作目录名，或目录为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">内存或 Java 主版本不是正数。</exception>
    public static LaunchCommandLine Build(LaunchArgumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Version);
        ArgumentNullException.ThrowIfNull(request.Identity);
        ArgumentNullException.ThrowIfNull(request.Platform);
        ArgumentNullException.ThrowIfNull(request.FileExists);
        ArgumentNullException.ThrowIfNull(request.Branding);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NativesDirectory);

        if (!PathSafety.IsSafePathSegment(request.VersionId))
        {
            throw new ArgumentException($"版本 id 不能用作目录名：'{request.VersionId}'。", nameof(request));
        }

        if (request.MemoryMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MemoryMb, "内存分配必须是正数。");
        }

        if (request.JavaMajorVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.JavaMajorVersion, "Java 主版本必须是正数。");
        }

        var merged = request.Version.Merged;
        var gameDirectory = Path.GetFullPath(request.GameDirectory);
        var instanceDirectory = Path.GetFullPath(request.InstanceDirectory);
        var nativesDirectory = Path.GetFullPath(request.NativesDirectory);

        // 特性（features）由**本次启动**推导，不采信调用方传进来的那份：
        // 版本 JSON 的规则就是按这些开关决定某个参数要不要出现，让调用方自己记着填
        // 迟早会出现"用户在设置里改了分辨率，但 has_custom_resolution 还是 false"。
        var platform = request.Platform with
        {
            Features = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["is_demo_user"] = request.IsDemoUser,
                ["has_custom_resolution"] = request.Width is > 0 && request.Height is > 0,
                ["has_quick_plays_support"] = false,
                ["is_quick_play_singleplayer"] = false,
                ["is_quick_play_multiplayer"] = false,
                ["is_quick_play_realms"] = false,
            },
        };

        List<LaunchProblem> problems = [];

        // ---- 类路径 ----
        var modulePathArtifacts = JvmArgumentPolicy.ModulePathArtifacts(merged.Arguments?.Jvm, platform);
        var classpathResult = LaunchClasspath.Build(
            request.Version, gameDirectory, platform, request.FileExists, modulePathArtifacts);

        problems.AddRange(classpathResult.Problems);

        List<string> classpath = [.. classpathResult.Entries];
        var clientJar = string.IsNullOrWhiteSpace(request.ClientJarPath)
            ? PathSafety.CombineWithin(gameDirectory, "versions", request.VersionId, request.VersionId + ".jar")
            : Path.GetFullPath(request.ClientJarPath);

        if (request.FileExists(clientJar))
        {
            classpath.Add(clientJar);
        }
        else
        {
            problems.Add(new LaunchProblem(
                LaunchProblemKind.MissingFile,
                "客户端 JAR",
                $"客户端 JAR 不存在：{clientJar}"));
        }

        var classpathText = string.Join(Path.PathSeparator, classpath);

        // ---- 占位符表 ----
        var assetIndexId = string.IsNullOrWhiteSpace(merged.Assets) ? request.VersionId : merged.Assets;
        var values = PlaceholderValues(request, merged, gameDirectory, instanceDirectory, nativesDirectory, assetIndexId, classpathText);

        // ---- JVM 参数 ----
        List<string> jvmArguments = [];
        HashSet<string> emittedKeys = new(StringComparer.OrdinalIgnoreCase);

        // 只有第一个同键参数会被收下 —— 于是我们先放启动器的默认值，
        // 版本 JSON 里那些 -Djava.library.path / -Dminecraft.launcher.brand 就自然被去掉，
        // 而不是变成"同一个参数出现两次"（JVM 取最后一次，看着能用但日志里很乱）。
        bool Emit(string argument, string? key = null)
        {
            var effectiveKey = key ?? JvmArgumentPolicy.FlagKey(argument);
            if (!emittedKeys.Add(effectiveKey))
            {
                return false;
            }

            jvmArguments.Add(argument);
            return true;
        }

        Emit(string.Create(CultureInfo.InvariantCulture, $"-Xmx{request.MemoryMb}M"));
        Emit($"-Djava.library.path={values["natives_directory"]}");
        Emit($"-Dminecraft.launcher.brand={request.Branding.Name}");
        Emit($"-Dminecraft.launcher.version={request.Branding.Version}");
        Emit("-Dlog4j2.formatMsgNoLookups=true");

        // 输出编码（C-13 的另一半）：启动器要**读**游戏的标准输出并显示在 p14 上，
        // 而 java 的 System.out 在重定向时用 file.encoding（Java ≤17 = 系统代码页，中文机器上是 GBK），
        // Java 18+ 起 stdout.encoding 默认仍是控制台代码页（JEP 400 明确把它排除在"UTF-8 化"之外）。
        // 不统一的话中文日志在界面上就是乱码；两端都钉成 UTF-8 才能确定地解码。
        // 这三个参数在 Java 8~17 上只有 file.encoding 起效（另两个是未知属性，JVM 忽略），
        // 18+ 三个都认 —— 对每个版本都安全，也与 Java 18+ 自己的默认取向一致。
        Emit("-Dfile.encoding=UTF-8");
        Emit("-Dstdout.encoding=UTF-8");
        Emit("-Dstderr.encoding=UTF-8");

        // 日志配置（C-5）：Java 8 不加 —— 老版本的 log4j 配置在 Java 8 下用默认值即可，
        // 而多个 -Dlog4j.configurationFile 会让日志变成谁都读不懂的样子。
        var loggingClient = merged.Logging?.Client;
        var loggingPath = LoggingConfigLayout.TryGetPath(merged, gameDirectory);

        if (request.JavaMajorVersion > 8 && loggingClient?.Argument is not null && loggingPath is not null)
        {
            if (request.FileExists(loggingPath))
            {
                var logArgument = JvmArgumentPolicy.NormalizeValue(
                    loggingClient.Argument.Replace(
                        "${" + LaunchPlaceholders.LoggingPath + "}",
                        loggingPath,
                        StringComparison.Ordinal));

                if (JvmArgumentPolicy.IsSafe(logArgument))
                {
                    Emit(logArgument);
                }
                else
                {
                    problems.Add(new LaunchProblem(
                        LaunchProblemKind.RejectedArgument,
                        logArgument,
                        UnsafeArgumentReason));
                }
            }
            else
            {
                problems.Add(new LaunchProblem(
                    LaunchProblemKind.MissingFile,
                    "日志配置",
                    $"日志配置不存在：{loggingPath}")
                {
                    // 不拦启动：log4j 找不到配置文件只影响日志格式，游戏照常跑。
                    BlocksLaunch = false,
                });
            }
        }

        AppendVersionJvmArguments(request, merged, platform, values, problems, jvmArguments, Emit);

        // 设置里的附加 JVM 参数：**不参与去重**（JVM 对 -Xmx 取最后一次，用户必须能覆盖默认值），
        // 也不做安全过滤 —— 它来自本机设置文件，不是远端数据。
        if (!string.IsNullOrWhiteSpace(request.ExtraJvmArguments))
        {
            jvmArguments.AddRange(
                LaunchPlaceholders.SplitLegacy(request.ExtraJvmArguments, values, out _));
        }

        if (request.JavaMajorVersion <= 8)
        {
            Emit("-Xss1M");
            Emit("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
        }

        var mainClass = string.IsNullOrWhiteSpace(merged.MainClass) ? DefaultMainClass : merged.MainClass;
        List<string> jvmFixed = [.. JvmArgumentPolicy.ApplyBootstrapperOpens(jvmArguments, mainClass)];
        jvmFixed.Add("-cp");
        jvmFixed.Add(classpathText);

        // ---- 游戏参数 ----
        var gameArguments = BuildGameArguments(request, merged, platform, values, problems);

        if (!string.IsNullOrWhiteSpace(request.ExtraGameArguments))
        {
            gameArguments.AddRange(
                LaunchPlaceholders.SplitLegacy(request.ExtraGameArguments, values, out _));
        }

        AppendResolution(request, gameArguments);
        EnsureUserProperties(gameArguments, DeclaresUserProperties(merged));

        List<string> arguments = [.. jvmFixed];
        var mainClassIndex = arguments.Count;
        arguments.Add(mainClass);
        arguments.AddRange(gameArguments);

        return new LaunchCommandLine(arguments, mainClassIndex, classpath, ScrubProblems(problems, request));
    }

    /// <summary>把请求里已知的敏感值从问题文本里抹掉（C-15 的第三层，见 <see cref="LaunchRedaction"/>）。</summary>
    /// <remarks>
    /// 场景是真实的：版本 JSON 是远端数据，一份恶意（或只是被改坏）的 JSON 可以写进
    /// <c>-javaagent:${auth_access_token}</c> —— 它会被安全策略拒掉，然后作为"被拒参数"出现在事件日志里，
    /// 令牌跟着一起被写出来。与其指望每个记录日志的地方都记得脱敏，
    /// 不如在这一层（唯一同时知道凭据与问题文本的地方）先抹干净。
    /// </remarks>
    private static List<LaunchProblem> ScrubProblems(List<LaunchProblem> problems, LaunchArgumentRequest request)
    {
        List<string> secrets =
        [
            request.Identity.AccessToken,
            request.Identity.Uuid,

            // 命令行里用的是无连字符形式，而问题文本里的令牌/标识可能来自任意一种写法 —— 两种都抹。
            request.Identity.UuidWithoutDashes,
        ];

        if (request.Identity.Xuid is not null)
        {
            secrets.Add(request.Identity.Xuid);
        }

        List<LaunchProblem> scrubbed = [];

        foreach (var problem in problems)
        {
            scrubbed.Add(problem with
            {
                Subject = LaunchRedaction.RedactText(problem.Subject, secrets),
                Reason = LaunchRedaction.RedactText(problem.Reason, secrets),
            });
        }

        return scrubbed;
    }

    /// <summary>版本 JSON 声明过的 <c>${user_properties}</c>（C-2 的判定，与规则是否适用无关）。</summary>
    private static bool DeclaresUserProperties(VersionJson merged)
    {
        if (LaunchPlaceholders.References(merged.MinecraftArguments, LaunchPlaceholders.UserProperties))
        {
            return true;
        }

        foreach (var entry in merged.Arguments?.Game ?? [])
        {
            foreach (var template in entry.Values)
            {
                if (LaunchPlaceholders.References(template, LaunchPlaceholders.UserProperties))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>把版本 JSON 的 <c>arguments.jvm</c> 按平台、Java 版本与安全策略筛过之后收下。</summary>
    private static void AppendVersionJvmArguments(
        LaunchArgumentRequest request,
        VersionJson merged,
        GamePlatform platform,
        IReadOnlyDictionary<string, string> values,
        List<LaunchProblem> problems,
        List<string> jvmArguments,
        Func<string, string?, bool> emit)
    {
        // 先摊平成一个 token 序列，再按"选项 + 值"的语法走一遍 ——
        // 版本 JSON 把 `-cp` 与它的值写成两项，把 `["-cp","${classpath}"]` 写成一项，
        // 只按项遍历会让这两种写法必须走两条分支。
        List<string> tokens = [];

        foreach (var entry in merged.Arguments?.Jvm ?? [])
        {
            if (!LibraryFilter.IsAllowed(entry.Rules, platform))
            {
                continue;
            }

            foreach (var template in entry.Values)
            {
                var text = LaunchPlaceholders.Substitute(template, values, out var unresolved);
                AddUnresolvedPlaceholders(problems, unresolved);
                tokens.Add(text);
            }
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            // 版本 JSON 自己声明的类路径整对丢掉：类路径由库列表重建（Neo 也是这么做的）。
            if (JvmArgumentPolicy.IsClasspathOption(token))
            {
                index++;
                continue;
            }

            string? value = null;
            if (JvmArgumentPolicy.HasSeparateValue(token))
            {
                if (index + 1 >= tokens.Count)
                {
                    problems.Add(new LaunchProblem(
                        LaunchProblemKind.RejectedArgument,
                        token,
                        "该选项需要一个值，但版本 JSON 里没有给它值。"));

                    continue;
                }

                value = tokens[++index];
            }

            var normalized = JvmArgumentPolicy.NormalizeValue(token);

            if (!JvmArgumentPolicy.IsSafe(normalized)
                || (value is not null && !JvmArgumentPolicy.IsSafe(value)))
            {
                problems.Add(new LaunchProblem(LaunchProblemKind.RejectedArgument, normalized, UnsafeArgumentReason));
                continue;
            }

            if (!JvmArgumentPolicy.IsCompatibleWithJava(normalized, request.JavaMajorVersion))
            {
                problems.Add(new LaunchProblem(
                    LaunchProblemKind.RejectedArgument,
                    normalized,
                    $"该选项需要 Java 9 及以上，当前实例用的是 Java {request.JavaMajorVersion}。"));

                continue;
            }

            // 自带值的选项：去重键必须把值算进去，否则第二个 --add-opens 会被当成重复项。
            var key = value is null
                ? JvmArgumentPolicy.FlagKey(normalized)
                : normalized + " " + value;

            if (!emit(normalized, key))
            {
                continue;
            }

            if (value is not null)
            {
                jvmArguments.Add(value);
            }
        }
    }

    /// <summary>版本 JSON 的游戏参数：1.13+ 走结构化列表，1.12- 走整串。</summary>
    private static List<string> BuildGameArguments(
        LaunchArgumentRequest request,
        VersionJson merged,
        GamePlatform platform,
        IReadOnlyDictionary<string, string> values,
        List<LaunchProblem> problems)
    {
        List<string> gameArguments = [];

        if (merged.Arguments?.Game is { Count: > 0 } structured)
        {
            foreach (var entry in structured)
            {
                if (!LibraryFilter.IsAllowed(entry.Rules, platform))
                {
                    continue;
                }

                foreach (var template in entry.Values)
                {
                    var text = LaunchPlaceholders.Substitute(template, values, out var unresolved);
                    AddUnresolvedPlaceholders(problems, unresolved);

                    // 结构化参数的每一项就是**一个实参**：值里有空格也不切（见 LaunchPlaceholders 的注释）。
                    gameArguments.Add(text);
                }
            }

            return gameArguments;
        }

        if (!string.IsNullOrWhiteSpace(merged.MinecraftArguments))
        {
            gameArguments.AddRange(
                LaunchPlaceholders.SplitLegacy(merged.MinecraftArguments, values, out var unresolved));

            AddUnresolvedPlaceholders(problems, unresolved);
        }

        return gameArguments;
    }

    /// <summary>窗口分辨率：版本 JSON 的规则没有产生 <c>--width</c> 时才自己补。</summary>
    private static void AppendResolution(LaunchArgumentRequest request, List<string> gameArguments)
    {
        if (request.Width is not > 0 || request.Height is not > 0)
        {
            return;
        }

        if (gameArguments.Contains("--width", StringComparer.Ordinal))
        {
            return;
        }

        gameArguments.Add("--width");
        gameArguments.Add(request.Width.Value.ToString(CultureInfo.InvariantCulture));
        gameArguments.Add("--height");
        gameArguments.Add(request.Height.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 保证 <c>--userProperties</c> 存在且带值（C-2）。
    /// </summary>
    /// <param name="gameArguments">游戏参数（就地修改）。</param>
    /// <param name="declared">版本 JSON 是否声明过 <c>${user_properties}</c>。</param>
    /// <remarks>
    /// 1.7.6 ~ 1.12.x 的 <c>Main</c> 用 joptsimple 把 <c>--userProperties</c> 声明为**必需项**，
    /// 缺了它会直接以 <c>Missing required option(s) ['userProperties']</c> 退出。
    /// 被裁剪过或从别的启动器导入的版本 JSON 正好可能出现"声明了但没给值"。
    /// <para>
    /// 两种情形分开处理：键在但没值 → 就地补 <c>{}</c>；键完全不在、而版本**声明过**这个占位符
    /// → 补整个键值对。后者不能无条件做：1.5.2 及更早的 <c>Main</c> 不认这个选项，
    /// 给它注入反而会抛 <c>UnrecognizedOptionException</c>。
    /// </para>
    /// </remarks>
    private static void EnsureUserProperties(List<string> gameArguments, bool declared)
    {
        var index = gameArguments.LastIndexOf("--userProperties");

        if (index >= 0)
        {
            if (index + 1 >= gameArguments.Count || gameArguments[index + 1].StartsWith('-'))
            {
                gameArguments.Insert(index + 1, "{}");
            }

            return;
        }

        if (declared)
        {
            gameArguments.Add("--userProperties");
            gameArguments.Add("{}");
        }
    }

    /// <summary>把未解析的占位符记成问题（同一个占位符只记一次）。</summary>
    private static void AddUnresolvedPlaceholders(List<LaunchProblem> problems, IReadOnlyList<string> unresolved)
    {
        foreach (var name in unresolved)
        {
            var subject = "${" + name + "}";

            if (problems.Any(problem =>
                    problem.Kind == LaunchProblemKind.UnresolvedPlaceholder
                    && string.Equals(problem.Subject, subject, StringComparison.Ordinal)))
            {
                continue;
            }

            problems.Add(new LaunchProblem(
                LaunchProblemKind.UnresolvedPlaceholder,
                subject,
                "版本 JSON 引用了本启动器不认识的占位符，这一类参数无法安全构造。"));
        }
    }

    /// <summary>按请求填占位符表。</summary>
    private static Dictionary<string, string> PlaceholderValues(
        LaunchArgumentRequest request,
        VersionJson merged,
        string gameDirectory,
        string instanceDirectory,
        string nativesDirectory,
        string assetIndexId,
        string classpathText) =>
        new(StringComparer.Ordinal)
        {
            ["auth_player_name"] = request.Identity.PlayerName,
            ["version_name"] = request.VersionId,
            ["game_directory"] = instanceDirectory,
            ["assets_root"] = Path.Combine(gameDirectory, "assets"),
            // pre-1.6 的 virtual 资源目录（1.5.2 及更早的 ${game_assets}）。
            ["game_assets"] = string.IsNullOrWhiteSpace(request.GameAssetsDirectory)
                ? Path.Combine(gameDirectory, "assets")
                : Path.GetFullPath(request.GameAssetsDirectory),
            ["assets_index_name"] = assetIndexId,
            ["auth_uuid"] = request.Identity.UuidWithoutDashes,
            ["auth_access_token"] = request.Identity.AccessToken,
            // 空 ClientId 用 0 顶替：离线启动根本不需要它（Microsoft 登录才需要，见 AGENTS.md §6），
            // 而一个空串会在部分版本的 joptsimple 上被当成"这个选项没有值"。
            ["clientid"] = string.IsNullOrWhiteSpace(request.ClientId) ? "0" : request.ClientId,
            ["auth_xuid"] = request.Identity.Xuid ?? "0",
            ["user_type"] = request.Identity.UserType,
            ["version_type"] = string.IsNullOrWhiteSpace(merged.Type) ? "release" : merged.Type,
            ["natives_directory"] = nativesDirectory,

            // 品牌来自请求（AGENTS.md §5.1：不许把 Neo 的品牌带进来）。
            ["launcher_name"] = request.Branding.Name,
            ["launcher_version"] = request.Branding.Version,

            ["classpath"] = classpathText,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Path.Combine(gameDirectory, "libraries"),
            ["user_properties"] = "{}",
            ["auth_session"] = "0",
            ["profile_name"] = request.Identity.PlayerName,

            // 分辨率与快速游玩：这两组占位符只有对应的 feature 打开时才会出现在参数里。
            // 快速游玩我们一律不支持（feature 恒为 false），但仍然给出定义 ——
            // 否则一旦哪个版本 JSON 用了它们，参数会被当成"未知占位符"丢掉。
            ["resolution_width"] = request.Width is > 0
                ? request.Width.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            ["resolution_height"] = request.Height is > 0
                ? request.Height.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            ["quickPlayPath"] = string.Empty,
            ["quickPlaySingleplayer"] = string.Empty,
            ["quickPlayMultiplayer"] = string.Empty,
            ["quickPlayRealms"] = string.Empty,
        };
}
