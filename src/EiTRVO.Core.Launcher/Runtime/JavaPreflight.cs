namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>启动／安装前的 Java 检查结论。</summary>
public enum JavaCheckStatus
{
    /// <summary>还没检测过 —— 什么都不断言（不许说"缺少 Java"）。</summary>
    Unknown,

    /// <summary>有可用的运行时。</summary>
    Satisfied,

    /// <summary>已经检测过，但没有能跑这个版本的运行时。</summary>
    Missing,

    /// <summary>用户指定的运行时不在检测结果里（路径失效、被卸载、或指到了非 Java 目录）。</summary>
    OverrideInvalid,
}

/// <summary>用户这个 Java 指定是从哪一层来的（Q14：全局值只是默认值，失效时不许把实例卡死）。</summary>
public enum JavaOverrideScope
{
    /// <summary>没有任何覆盖（自动按版本匹配）。</summary>
    None,

    /// <summary>实例自己的覆盖（用户对这一台实例的明确选择）。</summary>
    Instance,

    /// <summary>全局默认覆盖（所有实例继承；失效时回落到自动匹配并说明）。</summary>
    Global,
}

/// <summary>某个游戏版本的 Java 检查结果。</summary>
/// <param name="Status">结论。</param>
/// <param name="RequiredMajor">该版本要求的 Java 下限。</param>
/// <param name="MaxUsableMajor">该版本能用的 Java 上限（老版本封在 8）。</param>
/// <param name="Selected">选中的运行时；没选出来时为 <see langword="null"/>。</param>
/// <param name="FellBackFromOverride">
/// 是否**因为全局默认覆盖不可用而回落**到自动匹配（只有 <see cref="JavaOverrideScope.Global"/> 会置真）。
/// 界面要如实说明这一条，否则用户会以为全局默认的那个 Java 正在生效。
/// </param>
public sealed record JavaCheck(
    JavaCheckStatus Status,
    int RequiredMajor,
    int MaxUsableMajor,
    JavaInstallation? Selected,
    bool FellBackFromOverride = false)
{
    /// <summary>是否可以直接启动／继续。</summary>
    public bool IsSatisfied => Status == JavaCheckStatus.Satisfied;
}

/// <summary>
/// 启动／安装前的 Java 前置校验（迁移评估 B-12）。
/// <para>
/// 刻意做成**纯函数**：输入是（游戏版本、用户覆盖、检测结果、有没有检测过），输出是结论。
/// 这样它能被 M4 的启动编排、p04/p17 的提示文案、以及将来的加载器安装器三处共用，
/// 而"到底该选哪一个 Java"这条规则只需要被单测钉一次。
/// </para>
/// <para>
/// 与 Neo 的差别：Neo 有 <c>GetMinecraftRequiredJavaVersion</c>，但**安装路径从不调用它**，
/// 于是"缺 Java"要等到游戏进程起不来才暴露。这里把它做成一个必须显式调用的结论，
/// 并且区分「没查过」与「查过没有」—— 前者不许报故障。
/// </para>
/// </summary>
public static class JavaPreflight
{
    /// <summary>检查某个游戏版本能不能拿到可用的 Java。</summary>
    /// <param name="gameVersion">游戏版本号。</param>
    /// <param name="javaOverride">用户指定的运行时根目录或可执行文件路径；为空表示自动匹配。</param>
    /// <param name="installations">检测到的运行时。</param>
    /// <param name="hasDetectionResult">是否已经检测过（见 <c>IJavaRuntimeCatalog.HasDetectionResult</c>）。</param>
    /// <param name="declaredMajor">
    /// 本地版本 JSON 声明的 Java 主版本（<c>javaVersion.majorVersion</c>）；为空则用规则表。
    /// <para>
    /// C-9 的落点：声明值是 Mojang 自己写的答案，规则表只在没有本地 JSON 时兜底。
    /// 启动时拿到的是**已合并的版本 JSON**，所以这里必须能传进来 ——
    /// 否则会出现"p05 说需要 Java 16，而启动时按规则表去挑 Java 8"这种界面与行为不一致。
    /// </para>
    /// </param>
    /// <returns>检查结论。</returns>
    /// <param name="overrideScope">
    /// 这个覆盖来自哪一层（默认按实例覆盖处理）。**全局默认值失效时必须回落**（Q14）：
    /// 全局覆盖只是"所有实例的默认值"，它失效不该让一个本来能自动匹配的实例变成启动失败。
    /// </param>
    public static JavaCheck Check(
        string gameVersion,
        string? javaOverride,
        IReadOnlyList<JavaInstallation> installations,
        bool hasDetectionResult,
        int? declaredMajor = null,
        JavaOverrideScope overrideScope = JavaOverrideScope.Instance)
    {
        ArgumentNullException.ThrowIfNull(installations);

        var required = declaredMajor is > 0 ? declaredMajor.Value : JavaRequirement.RequiredMajor(gameVersion);

        // 上限仍由规则表给（1.12.2 及以前封在 8），但不能低于声明的下限 ——
        // 否则一个"声明要 17"的版本会因为规则表把它当老版本而被判成「没有可用的 Java」。
        var maximum = declaredMajor is > 0
            ? Math.Max(required, JavaRequirement.MaxUsableMajor(gameVersion))
            : JavaRequirement.MaxUsableMajor(gameVersion);

        // 没检测过就什么都不说：把"还没查"说成"缺 Java"会让用户去装一个他其实已经有的东西。
        if (!hasDetectionResult)
        {
            return new JavaCheck(JavaCheckStatus.Unknown, required, maximum, null);
        }

        if (!string.IsNullOrWhiteSpace(javaOverride))
        {
            var overridden = FindByPath(installations, javaOverride);

            if (overridden is not null
                && JavaRequirement.IsWithin(overridden.MajorVersion, required, maximum))
            {
                return new JavaCheck(JavaCheckStatus.Satisfied, required, maximum, overridden);
            }

            if (overrideScope == JavaOverrideScope.Global)
            {
                // 全局默认值失效（路径没了 / 版本不合适）：**回落**到自动匹配，并标出"这是回落"。
                var fallback = Select(installations, required, maximum);

                return fallback is null
                    ? new JavaCheck(JavaCheckStatus.Missing, required, maximum, null, FellBackFromOverride: true)
                    : new JavaCheck(JavaCheckStatus.Satisfied, required, maximum, fallback, FellBackFromOverride: true);
            }

            return overridden is null
                ? new JavaCheck(JavaCheckStatus.OverrideInvalid, required, maximum, null)
                : new JavaCheck(JavaCheckStatus.Missing, required, maximum, overridden);
        }

        var selected = Select(installations, required, maximum);
        return selected is null
            ? new JavaCheck(JavaCheckStatus.Missing, required, maximum, null)
            : new JavaCheck(JavaCheckStatus.Satisfied, required, maximum, selected);
    }

    /// <summary>
    /// 自动挑一个运行时：优先**主版本正好等于要求**的那个，其次满足区间里最小的那个。
    /// <para>
    /// 顺序是有理由的：等于要求的那一个就是 Mojang 声明的版本，最稳；
    /// 没有正好相等的时候向上取最小的（而不是取最新的）—— 拿 Java 25 去跑 1.20.4 通常也能跑，
    /// 但那是"用最新版碰运气"，而 21 是这一代明确支持的版本。
    /// </para>
    /// </summary>
    /// <param name="installations">检测到的运行时。</param>
    /// <param name="gameVersion">游戏版本号。</param>
    /// <returns>选中的运行时；没有可用的则为 <see langword="null"/>。</returns>
    public static JavaInstallation? Select(IReadOnlyList<JavaInstallation> installations, string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);

        return Select(
            installations,
            JavaRequirement.RequiredMajor(gameVersion),
            JavaRequirement.MaxUsableMajor(gameVersion));
    }

    /// <summary>按显式区间挑一个运行时（<c>declaredMajor</c> 给出的下限/上限）。</summary>
    /// <param name="installations">检测到的运行时。</param>
    /// <param name="requiredMajor">下限。</param>
    /// <param name="maxUsableMajor">上限。</param>
    /// <returns>选中的运行时；没有可用的则为 <see langword="null"/>。</returns>
    public static JavaInstallation? Select(
        IReadOnlyList<JavaInstallation> installations,
        int requiredMajor,
        int maxUsableMajor)
    {
        ArgumentNullException.ThrowIfNull(installations);

        JavaInstallation? best = null;
        foreach (var installation in installations)
        {
            if (!JavaRequirement.IsWithin(installation.MajorVersion, requiredMajor, maxUsableMajor))
            {
                continue;
            }

            if (installation.MajorVersion == requiredMajor)
            {
                return installation;
            }

            if (best is null || installation.MajorVersion < best.MajorVersion)
            {
                best = installation;
            }
        }

        return best;
    }

    /// <summary>按用户指定的路径找运行时（根目录或可执行文件路径都认）。</summary>
    /// <param name="installations">检测到的运行时。</param>
    /// <param name="path">用户指定值。</param>
    /// <returns>找到的运行时；没有则为 <see langword="null"/>。</returns>
    public static JavaInstallation? FindByPath(IReadOnlyList<JavaInstallation> installations, string? path)
    {
        ArgumentNullException.ThrowIfNull(installations);

        var wanted = Normalize(path);
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var installation in installations)
        {
            if (string.Equals(Normalize(installation.HomeDirectory), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(installation.ExecutablePath), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return installation;
            }
        }

        return null;
    }

    /// <summary>比较路径前先统一：去空白、去结尾的分隔符。</summary>
    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().Trim('"');
        return trimmed.Length > 1
            ? trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : trimmed;
    }
}
