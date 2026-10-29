using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 版本 JSON 里 <c>arguments.jvm</c> 的接受/拒绝规则，以及几处必须修正的参数。
/// <para>
/// 这些规则都在处理同一件事：<c>arguments.jvm</c> 是**远端数据**，而它会变成
/// <c>java.exe</c> 的命令行 —— 一份被篡改的版本 JSON 足以在用户机器上执行任意代码。
/// 所以这里既管"能不能用"（Java 版本兼容），也管"准不准用"（注入类参数）。
/// </para>
/// </summary>
public static class JvmArgumentPolicy
{
    /// <summary>Java 8 上不存在的模块化选项（C-1 的兼容裁剪）。</summary>
    private static readonly string[] Java8BlockedPrefixes =
    [
        "--add-opens",
        "--add-exports",
        "--add-modules",
        "--add-reads",
        "--patch-module",
        "--illegal-access",
    ];

    /// <summary>
    /// 注入类参数前缀：命中即拒绝。
    /// <para>
    /// 迁移自 Neo，但判定从 <c>Contains</c> 收紧成 <c>StartsWith</c>：
    /// <c>-Dfoo=-javaagent:bar</c> 只是一个值里提到这个词的**普通系统属性**，
    /// 用 <c>Contains</c> 会把它一起毙掉（表现为"某些整合包莫名其妙起不来"）。
    /// JVM 只解析以 <c>-</c> 开头的参数，所以按前缀判断才是对的。
    /// </para>
    /// </summary>
    private static readonly string[] DangerousPrefixes =
    [
        "-javaagent:",
        "-agentlib:",
        "-agentpath:",
        "-XX:OnOutOfMemoryError",
        "-XX:OnError",
    ];

    /// <summary>需要 Forge/NeoForge 的 <c>ALL-UNNAMED</c> 修正的主类（C-3）。</summary>
    private static readonly string[] BootstrapperMainClasses =
    [
        "cpw.mods.bootstraplauncher.BootstrapLauncher",
        "net.minecraftforge.bootstrap.ForgeBootstrap",
    ];

    /// <summary>自身带一个独立值的选项（去重键必须把值也算进去，否则第二个会被当重复项丢掉）。</summary>
    private static readonly string[] OptionsWithSeparateValue =
    [
        "--add-opens",
        "--add-exports",
        "--add-reads",
        "--add-modules",
        "--patch-module",
        "-p",
        "--module-path",
        "-cp",
        "-classpath",
    ];

    /// <summary>Forge/NeoForge 启动器需要的三组 <c>ALL-UNNAMED</c> 开放（Neo 的原表，未改动）。</summary>
    private static readonly string[] FixedOpens =
    [
        "--add-opens",
        "java.base/java.lang.invoke=ALL-UNNAMED",
        "--add-opens",
        "java.base/java.util.jar=ALL-UNNAMED",
        "--add-exports",
        "java.base/sun.security.util=ALL-UNNAMED",
    ];

    /// <summary>该参数在指定 Java 版本上是否可用。</summary>
    /// <param name="argument">参数。</param>
    /// <param name="javaMajorVersion">Java 主版本。</param>
    /// <returns>可用返回 <see langword="true"/>。</returns>
    public static bool IsCompatibleWithJava(string argument, int javaMajorVersion)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (javaMajorVersion > 8)
        {
            return true;
        }

        foreach (var prefix in Java8BlockedPrefixes)
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>该参数是否是注入类危险参数。</summary>
    /// <param name="argument">参数。</param>
    /// <returns>安全返回 <see langword="true"/>。</returns>
    public static bool IsSafe(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        foreach (var prefix in DangerousPrefixes)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>是否是版本 JSON 自己声明的类路径选项（它的值由库列表重建，整对丢弃）。</summary>
    /// <param name="argument">参数。</param>
    /// <returns>是返回 <see langword="true"/>。</returns>
    public static bool IsClasspathOption(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return argument is "-cp" or "-classpath";
    }

    /// <summary>是否是 module-path 选项（C-4 要从里面收集构件名）。</summary>
    /// <param name="argument">参数。</param>
    /// <returns>是返回 <see langword="true"/>。</returns>
    public static bool IsModulePathOption(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return argument is "-p" or "--module-path";
    }

    /// <summary>该选项是否自带一个独立的值。</summary>
    /// <param name="argument">参数。</param>
    /// <returns>是返回 <see langword="true"/>。</returns>
    public static bool HasSeparateValue(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return Array.IndexOf(OptionsWithSeparateValue, argument) >= 0;
    }

    /// <summary>
    /// 去重键：同一个键只保留**第一个**出现的参数。
    /// </summary>
    /// <param name="argument">参数。</param>
    /// <returns>键。</returns>
    /// <remarks>
    /// 规则：<c>-Dfoo=a</c> 与 <c>-Dfoo=b</c> 是同一个键（键是 <c>-Dfoo</c>）；
    /// <c>-Xmx1G</c> / <c>-Xms1G</c> / <c>-Xss1M</c> 按前四个字符归一；
    /// 其余参数用自身当键（<c>-XX:+UseG1GC</c> 这类不同参数本来就不同）。
    /// <para>
    /// 这条规则存在的意义是让**启动器的默认值优先**：版本 JSON 里
    /// <c>-Djava.library.path=${natives_directory}</c>、<c>-Dminecraft.launcher.brand=${launcher_name}</c>
    /// 与我们先放进去的那几条是同键的，因此会被丢掉，而不是变成"同一个参数出现两次"
    /// （Neo 就会重复传，只不过 JVM 取最后一次、看着"能用"）。
    /// </para>
    /// </remarks>
    public static string FlagKey(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.StartsWith("-D", StringComparison.Ordinal))
        {
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            return separator > 0 ? argument[..separator] : argument;
        }

        if (argument.Length >= 4
            && (argument.StartsWith("-Xmx", StringComparison.Ordinal)
                || argument.StartsWith("-Xms", StringComparison.Ordinal)
                || argument.StartsWith("-Xss", StringComparison.Ordinal)))
        {
            return argument[..4];
        }

        return argument;
    }

    /// <summary>
    /// 规范化 <c>key=value</c> 形式参数的值部分：去掉首尾空白，并剥掉一层包裹引号。
    /// </summary>
    /// <param name="argument">参数。</param>
    /// <returns>规范化后的参数。</returns>
    /// <remarks>
    /// 迁移自 Neo 的 <c>StripEmbeddedQuotes</c>，来源是 Fabric API 的真实写法
    /// <c>-DFabricMcEmu= net.minecraft.client.main.Main </c>：值前面多一个空格、后面多一个空格，
    /// 而 Fabric 自己会拿这个系统属性跟无空格的类名比较 —— 不修就永远不相等，
    /// 表现是"Fabric 装上了但游戏起不来"。没有 <c>=</c> 的参数原样返回。
    /// </remarks>
    public static string NormalizeValue(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var separator = argument.IndexOf('=', StringComparison.Ordinal);
        if (separator < 0)
        {
            return argument;
        }

        var prefix = argument[..(separator + 1)];
        var value = argument[(separator + 1)..].Trim();

        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            value = value[1..^1];
        }

        return prefix + value;
    }

    /// <summary>该主类是否需要 Forge/NeoForge 的 <c>ALL-UNNAMED</c> 修正。</summary>
    /// <param name="mainClass">主类。</param>
    /// <returns>需要返回 <see langword="true"/>。</returns>
    public static bool NeedsBootstrapperOpens(string? mainClass) =>
        mainClass is not null && Array.IndexOf(BootstrapperMainClasses, mainClass) >= 0;

    /// <summary>
    /// 修正 Forge / NeoForge 的 <c>--add-opens</c>（C-3）。
    /// </summary>
    /// <param name="jvmArguments">已收集的 JVM 参数。</param>
    /// <param name="mainClass">主类。</param>
    /// <returns>修正后的 JVM 参数（不需要修正时原样返回）。</returns>
    /// <remarks>
    /// 安装器生成的版本 JSON 可能只把包开放给**具名**模块，而 bootstraplauncher 是从 classpath
    /// （无名模块）加载的 —— 于是它需要的包没被开放，游戏在启动阶段就崩。做法是：先删掉所有
    /// "开放给 <c>ALL-UNNAMED</c>" 的 <c>--add-opens</c>/<c>--add-exports</c>，再补上固定的三组。
    /// <para>
    /// 与 Neo 的差异：Neo 只处理"分开写"的形式（<c>--add-opens</c> + 值两个参数），
    /// 合并写的形式（<c>--add-opens=java.base/java.lang=ALL-UNNAMED</c>，NeoForge 的 JSON 里就有）
    /// 会漏掉，于是同一个包被开放两次、而缺的那个仍然缺着。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ApplyBootstrapperOpens(IReadOnlyList<string> jvmArguments, string? mainClass)
    {
        ArgumentNullException.ThrowIfNull(jvmArguments);

        if (!NeedsBootstrapperOpens(mainClass))
        {
            return jvmArguments;
        }

        List<string> fixedArguments = [];

        for (var index = 0; index < jvmArguments.Count; index++)
        {
            var argument = jvmArguments[index];

            // 分开写：--add-opens <值>
            if ((argument is "--add-opens" or "--add-exports")
                && index + 1 < jvmArguments.Count
                && jvmArguments[index + 1].EndsWith("=ALL-UNNAMED", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            // 合并写：--add-opens=<值>
            if ((argument.StartsWith("--add-opens=", StringComparison.Ordinal)
                    || argument.StartsWith("--add-exports=", StringComparison.Ordinal))
                && argument.EndsWith("=ALL-UNNAMED", StringComparison.Ordinal))
            {
                continue;
            }

            fixedArguments.Add(argument);
        }

        fixedArguments.AddRange(FixedOpens);
        return fixedArguments;
    }

    /// <summary>
    /// 从版本 JSON 的 <c>arguments.jvm</c> 里收集 module-path 上的构件名（C-4）。
    /// </summary>
    /// <param name="jvmArguments">版本 JSON 的 JVM 参数项。</param>
    /// <param name="platform">平台（规则求值要用）。</param>
    /// <returns>构件名集合。</returns>
    /// <remarks>
    /// 值里的分隔符按 Windows 的 <c>;</c> 切（本产品只支持 win-x64）。
    /// 这里**不做占位符替换**：只取路径最后一段的文件名，占位符出现在路径前缀部分不影响结果。
    /// </remarks>
    public static IReadOnlySet<string> ModulePathArtifacts(
        IReadOnlyList<VersionArgument>? jvmArguments,
        GamePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        HashSet<string> artifacts = new(StringComparer.OrdinalIgnoreCase);

        // 先把适用的项摊平成一个 token 序列：`["-p","<值>"]` 与 `"-p"` + `"<值>"` 两种写法都存在，
        // 只按项遍历会漏掉后一种（那正是 Forge 的 JSON 的写法）。
        List<string> tokens = [];

        foreach (var entry in jvmArguments ?? [])
        {
            if (LibraryFilter.IsAllowed(entry.Rules, platform))
            {
                tokens.AddRange(entry.Values);
            }
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            if (!IsModulePathOption(tokens[index]) || index + 1 >= tokens.Count)
            {
                continue;
            }

            foreach (var segment in tokens[index + 1].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                artifacts.Add(MavenArtifactIdentity.FromFileName(segment).Artifact);
            }

            index++;
        }

        return artifacts;
    }
}
