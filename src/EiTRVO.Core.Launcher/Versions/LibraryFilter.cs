namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 库 / 参数的适用规则求值。
/// <para>
/// 官方语义（迁移自 Neo 的 <c>JvmArgHelper.IsRuleAllowed</c>，并补上它漏掉的两处）：
/// 没有规则 = 适用；有规则时**从前往后逐条判断，最后一条匹配的规则说了算**；
/// 一条都不匹配 = 不适用。
/// </para>
/// <para>
/// 补的两处缺口：
/// </para>
/// <list type="number">
/// <item><b><c>features</c> 之前被完全忽略</b>：Neo 只看 <c>os</c>，于是一条
/// <c>{"action":"allow","features":{"is_demo_user":true}}</c> 的规则会被当成"匹配"，
/// 把只为演示模式准备的库也装进普通环境。现在"没有显式启用的特性"就是**不匹配**。</item>
/// <item><b><c>os.version</c> 之前被完全忽略</b>：带来的是"装了一个只适用于 Win7 的库"这类问题。
/// 现在按正则判断，并给正则加超时（表达式来自远端清单）。</item>
/// </list>
/// </summary>
public static class LibraryFilter
{
    /// <summary>判断一组规则是否适用于该平台。</summary>
    /// <param name="rules">规则；为空表示无条件适用。</param>
    /// <param name="platform">当前平台。</param>
    /// <returns>适用返回 <see langword="true"/>。</returns>
    public static bool IsAllowed(IReadOnlyList<VersionRule>? rules, GamePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;

        foreach (var rule in rules)
        {
            if (Matches(rule, platform))
            {
                allowed = rule.IsAllow;
            }
        }

        return allowed;
    }

    /// <summary>判断单个库是否适用于该平台。</summary>
    /// <param name="library">库。</param>
    /// <param name="platform">当前平台。</param>
    /// <returns>适用返回 <see langword="true"/>。</returns>
    public static bool Applies(Library library, GamePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(library);
        return IsAllowed(library.Rules, platform);
    }

    /// <summary>单条规则是否匹配该平台。</summary>
    /// <param name="rule">规则。</param>
    /// <param name="platform">当前平台。</param>
    /// <returns>匹配返回 <see langword="true"/>。</returns>
    public static bool Matches(VersionRule rule, GamePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(platform);

        if (rule.Os is not null
            && (!platform.MatchesOsName(rule.Os.Name)
                || !platform.MatchesArch(rule.Os.Arch)
                || !platform.MatchesOsVersion(rule.Os.Version)))
        {
            return false;
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var feature in rule.Features)
            {
                // 规则里写的是"该特性必须为这个值"；没有启用的特性一律按 false 看。
                var enabled = platform.Features.TryGetValue(feature.Key, out var value) && value;
                if (enabled != feature.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
