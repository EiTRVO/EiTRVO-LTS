namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 从 Maven 仓库路径里认出一个构件（构件名 / 版本 / 分类器）。
/// <para>
/// 两个用途需要的东西不同，所以 <see cref="Key"/> 与 <see cref="Artifact"/> 分开：
/// </para>
/// <list type="bullet">
/// <item><b>classpath 去重</b>（C-4）用 <see cref="Key"/>：<c>asm-9.5.jar</c> 与 <c>asm-9.6.jar</c>
/// 是同一个构件，而 <c>lwjgl-3.3.2.jar</c> 与 <c>lwjgl-3.3.2-natives-windows.jar</c> 是**两个**构件 ——
/// 按文件名的"去掉版本号"去重会把后者当成前者的旧版本丢掉，表现是游戏缺 natives。
/// 分类器因此必须留在 <see cref="Key"/> 里。</item>
/// <item><b>module-path 排除</b>用 <see cref="Artifact"/>：同一个构件如果已经在 <c>-p</c> 上，
/// 就不该再出现在 classpath 上（同一份类被两个模块加载器各加载一次会冲突）。
/// <c>-p</c> 的值是一串裸文件路径，认不出分类器，所以那里只比构件名。</item>
/// </list>
/// <para>
/// 认不出来时**不做去重**：<see cref="Key"/> 退化成文件名，宁可把两个同名不同版的库都留下，
/// 也不要因为猜错而少一个库（少库的表现是游戏起来就崩，且看不出原因）。
/// </para>
/// </summary>
/// <param name="Artifact">构件名（如 <c>asm</c>、<c>lwjgl</c>）。</param>
/// <param name="Version">版本号；形状不符时为 <see langword="null"/>。</param>
/// <param name="Classifier">分类器（如 <c>natives-windows</c>）；没有时为 <see langword="null"/>。</param>
/// <param name="Key">去重键：构件名 + 分类器；形状不符退化成文件名。</param>
public sealed record MavenArtifactIdentity(string Artifact, string? Version, string? Classifier, string Key)
{
    /// <summary>从仓库内的相对路径（如 <c>org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-windows.jar</c>）解析。</summary>
    /// <param name="relativePath">仓库内的相对路径（<c>/</c> 与 <c>\</c> 都接受）。</param>
    /// <returns>构件身份。</returns>
    public static MavenArtifactIdentity FromRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 3)
        {
            return FromFileName(segments.Length > 0 ? segments[^1] : relativePath);
        }

        var artifact = segments[^3];
        var version = segments[^2];
        var stem = Path.GetFileNameWithoutExtension(segments[^1]);

        if (string.IsNullOrEmpty(artifact) || string.IsNullOrEmpty(version) || string.IsNullOrEmpty(stem))
        {
            return FromFileName(segments[^1]);
        }

        var prefix = artifact + "-" + version;

        if (stem.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return new MavenArtifactIdentity(artifact, version, null, artifact);
        }

        if (stem.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
        {
            var classifier = stem[(prefix.Length + 1)..];
            return string.IsNullOrEmpty(classifier)
                ? new MavenArtifactIdentity(artifact, version, null, artifact)
                : new MavenArtifactIdentity(artifact, version, classifier, artifact + "-" + classifier);
        }

        // 文件名与"构件-版本"对不上（非标准布局）：不去重。
        return new MavenArtifactIdentity(artifact, null, null, stem);
    }

    /// <summary>
    /// 只从一个裸文件名里认出**构件名**（<c>-p</c> 的值是一串路径，没有版本目录可用）。
    /// </summary>
    /// <param name="fileName">文件名（可带目录）。</param>
    /// <returns>构件身份；<see cref="Version"/> 与 <see cref="Classifier"/> 一律为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 规则是"第一个**后面跟数字**的连字符之前就是构件名"：
    /// <c>lwjgl-3.3.2-natives-windows.jar</c> → <c>lwjgl</c>，<c>asm-9.10.1.jar</c> → <c>asm</c>。
    /// 分类器在这里认不出来也不影响 —— 排除 module-path 只需要构件名。
    /// </remarks>
    public static MavenArtifactIdentity FromFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var stem = Path.GetFileNameWithoutExtension(fileName);

        for (var index = 0; index < stem.Length - 1; index++)
        {
            if (stem[index] == '-' && char.IsAsciiDigit(stem[index + 1]))
            {
                return new MavenArtifactIdentity(stem[..index], null, null, stem);
            }
        }

        return new MavenArtifactIdentity(stem, null, null, stem);
    }
}
