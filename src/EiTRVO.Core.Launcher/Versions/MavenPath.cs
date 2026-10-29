namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// Maven 坐标 → 仓库内的相对路径。
/// <para>
/// 迁移自 Neo 的 <c>ModLoaderService.MavenNameToPath</c>，并补上 Neo 没处理的两件事：
/// **分类器**（<c>group:artifact:version:classifier</c>，natives 与 sources 用）与
/// **扩展名**（<c>group:artifact:version@zip</c>）。这两者都是真实清单里出现过的写法，
/// 只按三段式切分会把它们拼成一条 404 路径 —— 而 404 之后的表现只是"某个库没下下来"。
/// </para>
/// </summary>
public static class MavenPath
{
    /// <summary>把 Maven 坐标转成相对路径。</summary>
    /// <param name="name">坐标，如 <c>org.apache.commons:commons-lang3:3.3.2</c>。</param>
    /// <returns>相对路径，如 <c>org/apache/commons/commons-lang3/3.3.2/commons-lang3-3.3.2.jar</c>。</returns>
    /// <exception cref="ArgumentException">坐标不合法。</exception>
    public static string FromName(string? name)
    {
        if (!TryFromName(name, out var path))
        {
            throw new ArgumentException($"不是合法的 Maven 坐标：'{name}'。", nameof(name));
        }

        return path;
    }

    /// <summary>尝试把 Maven 坐标转成相对路径。</summary>
    /// <param name="name">坐标。</param>
    /// <param name="path">转换结果；失败时为空串。</param>
    /// <returns>成功返回 <see langword="true"/>。</returns>
    public static bool TryFromName(string? name, out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // 扩展名写在最后一段上：group:artifact:version[:classifier][@ext]
        var text = name.Trim();
        var extension = "jar";
        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            extension = text[(at + 1)..];
            text = text[..at];

            if (extension.Length == 0 || extension.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }

        var parts = text.Split(':');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length == 4 ? parts[3] : null;

        if (group.Length == 0 || artifact.Length == 0 || version.Length == 0)
        {
            return false;
        }

        var fileName = classifier is null or ""
            ? artifact + "-" + version + "." + extension
            : artifact + "-" + version + "-" + classifier + "." + extension;

        path = group.Replace('.', '/') + "/" + artifact + "/" + version + "/" + fileName;
        return true;
    }
}
