namespace EiTRVO.Core.Launcher.Security;

/// <summary>路径包容性判定的结果。</summary>
public enum PathContainment
{
    /// <summary>目标路径确实落在基准目录内，且路径上没有重解析点。</summary>
    Contained,

    /// <summary>目标路径在基准目录之外（路径穿越 / Zip Slip）。</summary>
    Outside,

    /// <summary>目标路径名义上在基准目录内，但路径上有 NTFS 重解析点（Junction / 符号链接）。</summary>
    ReparsePoint,
}

/// <summary>
/// 路径安全工具（F-7）：集中式路径穿越与 NTFS 重解析点防护。
/// <para>
/// **为什么需要它**：实例名、模组文件名、整合包里的归档条目都是用户可影响的输入，
/// 而它们最终会被拼成真实路径。仅靠 <see cref="Path.GetFullPath(string)"/> 做前缀比较
/// 挡不住两类攻击：
/// </para>
/// <list type="number">
/// <item><c>..\..\Windows\System32</c> 这类显式穿越；</item>
/// <item>沙箱内先放一个指向外部的 Junction，再往「沙箱内」的路径写文件 ——
/// 前缀比较会通过，但文件实际落到了沙箱外。<see cref="Path.GetFullPath(string)"/>
/// 只做字符串规范化，**不会**解析重解析点，所以必须自己逐段检查。</item>
/// </list>
/// <para>
/// 来源：借鉴 Neo 的 <c>PathSafetyHelper</c>（内核逻辑，AGENTS.md §5.1 允许），
/// 但按本仓库约定重写：判定结果用枚举区分「越界」与「重解析点」（Neo 把两者都压成一个
/// <see langword="false"/>，调用方无法分辨该报哪一种错）；异常消息不泄露绝对路径；
/// 命名与 <c>ILauncherPaths</c> 一致。
/// </para>
/// </summary>
public static class PathSafety
{
    /// <summary>名称无法消毒时使用的兜底名。</summary>
    public const string FallbackName = "unnamed";

    /// <summary>
    /// 消毒单级路径组件（文件名 / 目录名）：剥离目录分隔符与 <c>..</c> / <c>.</c> 序列。
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFileName(string)"/> 会剥掉所有目录组件，对 <c>/</c> 与 <c>\</c> 都成立，
    /// 因此这一句同时挡住了 <c>../../../evil</c> 与 <c>..\..\System32</c>。
    /// </remarks>
    /// <param name="name">待消毒的名称。</param>
    /// <returns>只剩最后一级名称；空输入返回 <see cref="FallbackName"/>。</returns>
    public static string SanitizeNameComponent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return FallbackName;
        }

        var sanitized = Path.GetFileName(name.Trim());

        // 输入是纯分隔符时 Path.GetFileName 会返回空串。
        return string.IsNullOrEmpty(sanitized) ? FallbackName : sanitized;
    }

    /// <summary>
    /// 判定目标路径相对基准目录的位置。
    /// </summary>
    /// <param name="baseDirectory">基准目录。</param>
    /// <param name="candidatePath">待判定路径（可以尚不存在）。</param>
    /// <returns>判定结果。</returns>
    public static PathContainment Classify(string baseDirectory, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var fullBase = TrimSeparator(Path.GetFullPath(baseDirectory));
        var fullCandidate = Path.GetFullPath(candidatePath);

        if (!IsPrefixedBy(fullCandidate, fullBase))
        {
            return PathContainment.Outside;
        }

        return HasReparsePoint(fullBase, fullCandidate)
            ? PathContainment.ReparsePoint
            : PathContainment.Contained;
    }

    /// <summary>目标路径是否安全地落在基准目录内（不抛异常的版本）。</summary>
    /// <param name="baseDirectory">基准目录。</param>
    /// <param name="candidatePath">待判定路径。</param>
    /// <returns>安全时为 <see langword="true"/>。</returns>
    public static bool IsContained(string baseDirectory, string candidatePath) =>
        Classify(baseDirectory, candidatePath) == PathContainment.Contained;

    /// <summary>验证目标路径落在基准目录内，否则抛出异常。</summary>
    /// <param name="baseDirectory">基准目录。</param>
    /// <param name="candidatePath">待验证路径。</param>
    /// <exception cref="InvalidDataException">路径越界或经过重解析点。</exception>
    public static void ValidateContained(string baseDirectory, string candidatePath)
    {
        var containment = Classify(baseDirectory, candidatePath);
        if (containment == PathContainment.Contained)
        {
            return;
        }

        // 消息里只出现消毒后的**最后一级名称**，不出现解析后的绝对路径 ——
        // 这条日志可能被用户贴到公开的 issue 里，不该带上他机器上的目录结构。
        var leaf = SanitizeNameComponent(candidatePath);
        throw new InvalidDataException(containment == PathContainment.ReparsePoint
            ? $"路径包含符号链接或 Junction，已拒绝：{leaf}"
            : $"路径穿越检测：目标逃逸了基准目录，已拒绝：{leaf}");
    }

    /// <summary>
    /// 把若干级相对片段拼到基准目录下，并验证结果没有越界。
    /// </summary>
    /// <param name="baseDirectory">基准目录。</param>
    /// <param name="relativeParts">相对片段（如归档条目的分段）。</param>
    /// <returns>拼好并验证过的绝对路径。</returns>
    /// <exception cref="InvalidDataException">拼接结果越界或经过重解析点。</exception>
    public static string CombineWithin(string baseDirectory, params string[] relativeParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(relativeParts);

        var combined = baseDirectory;
        foreach (var part in relativeParts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            combined = Path.Combine(combined, part);
        }

        ValidateContained(baseDirectory, combined);
        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// 从基准目录到目标路径之间是否存在 NTFS 重解析点。
    /// <para>
    /// 只检查**已存在**的路径段 —— 还没创建的路径不构成威胁，而且
    /// <see cref="File.GetAttributes(string)"/> 对不存在的路径会抛异常。
    /// </para>
    /// </summary>
    /// <param name="fullBaseDirectory">已规范化的基准目录（不含结尾分隔符）。</param>
    /// <param name="fullCandidatePath">已规范化的目标路径。</param>
    /// <returns>存在重解析点时为 <see langword="true"/>。</returns>
    public static bool HasReparsePoint(string fullBaseDirectory, string fullCandidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullCandidatePath);

        var fullBase = TrimSeparator(Path.GetFullPath(fullBaseDirectory));
        var fullCandidate = Path.GetFullPath(fullCandidatePath);

        if (!IsPrefixedBy(fullCandidate, fullBase))
        {
            // 越界由调用方按「越界」处理，这里只回答重解析点问题。
            return false;
        }

        var relative = fullCandidate[fullBase.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return false;
        }

        var current = fullBase;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 连属性都读不到的路径：保守按「不是重解析点」处理，
                // 因为攻击者通常也没有权限在那种目录里放 Junction。
            }
        }

        return false;
    }

    /// <summary>
    /// 某个名称是否可以安全地当作**单段路径名**（目录名或文件名）。
    /// <para>
    /// 这条规则有两个调用方，都是来自远端的用户不可控数据：实例名（用户输入）与版本 id（清单里的远端数据）。
    /// 规则只有一份，避免两处各写一版、日后改了一处漏另一处。
    /// </para>
    /// </summary>
    /// <param name="name">待检查的名称。</param>
    /// <param name="maxLength">长度上限（默认 64）。</param>
    /// <returns>可用则返回 <see langword="true"/>。</returns>
    public static bool IsSafePathSegment(string? name, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(name)) { return false; }
        if (name.Length > maxLength) { return false; }
        if (name is "." or "..") { return false; }

        // 首尾空格与结尾点号在 Windows 上会被悄悄裁掉，于是 "a " 与 "a" 变成同一个目录。
        if (name[0] == ' ' || name[^1] == ' ' || name[^1] == '.') { return false; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { return false; }

        var stem = name;
        var dot = stem.IndexOf('.');
        if (dot >= 0) { stem = stem[..dot]; }

        return !IsReservedDeviceName(stem);
    }

    /// <summary>Windows 保留设备名：CON / PRN / AUX / NUL / COM1… / LPT1…。</summary>
    private static bool IsReservedDeviceName(string stem) =>
        stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9');

    /// <summary>
    /// 目标是否等于基准或以「基准 + 分隔符」开头。
    /// <para>
    /// 必须带上分隔符比较：否则 <c>C:\Temp\SafeDirMalicious</c> 会被误认为在
    /// <c>C:\Temp\SafeDir</c> 之内。
    /// </para>
    /// </summary>
    private static bool IsPrefixedBy(string candidate, string basePrefix)
    {
        if (string.Equals(candidate, basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(basePrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(basePrefix + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(path);
}
