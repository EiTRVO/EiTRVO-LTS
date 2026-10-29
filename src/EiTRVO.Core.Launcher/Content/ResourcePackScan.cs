using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content;

/// <summary>
/// 资源包目录的读取：<c>resourcepacks\</c> 下的 zip 与文件夹，
/// 启用状态与优先级来自游戏目录里的 <c>options.txt</c>。
/// </summary>
/// <remarks>
/// 两件事必须来自同一个地方，否则界面会自相矛盾：
/// <list type="number">
/// <item><b>谁被启用、谁排在前面，只有 <c>options.txt</c> 知道</b> ——
/// 游戏把 <c>resourcePacks:["vanilla","file/Faithful 32x.zip"]</c> 按**数组顺序**当优先级，
/// 我们另存一份"优先级"就是第二条真相，换个启动器就会与游戏对不上；</item>
/// <item><b>分辨率与格式来自包自己</b>：分辨率从包名里的 <c>32x</c> 这类写法取（取不到就不编），
/// 格式号（<c>pack_format</c>）从包里的 <c>pack.mcmeta</c> 读。</item>
/// </list>
/// </remarks>
public static partial class ResourcePackScan
{
    /// <summary>游戏设置文件里的键（资源包清单）。</summary>
    private const string ResourcePacksKey = "resourcePacks";

    /// <summary><c>pack.mcmeta</c> 最多读多少字符（它总是很小）。</summary>
    private const int MaxMetadataChars = 64 * 1024;

    /// <summary>
    /// 读 <c>options.txt</c> 里已启用的资源包 id（顺序就是优先级顺序）。
    /// </summary>
    /// <param name="gameDirectory">游戏目录。</param>
    /// <returns>id 列表（如 <c>file/Faithful 32x.zip</c>）；读不到时为空。</returns>
    public static IReadOnlyList<string> EnabledIds(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var path = Path.Combine(gameDirectory, "options.txt");
        if (!File.Exists(path))
        {
            return [];
        }

        string? value;
        try
        {
            value = OptionsValue(File.ReadAllLines(path), ResourcePacksKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<string> ids = [];
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    ids.Add(item.GetString()!.Trim());
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            // 用户手工改坏了 options.txt：当成"一个都没启用"。**绝不去改那个文件**。
            return [];
        }
    }

    /// <summary>在 <c>options.txt</c> 的行里找一个键的值。</summary>
    /// <param name="lines">文件的全部行。</param>
    /// <param name="key">键。</param>
    /// <returns>值（冒号之后的部分）；没找到为 <see langword="null"/>。</returns>
    public static string? OptionsValue(IReadOnlyList<string> lines, string key)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var prefix = key + ":";
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>读一个资源包目录。</summary>
    /// <param name="resourcePacksDirectory">资源包目录。</param>
    /// <param name="enabledIds"><c>options.txt</c> 里已启用的 id（**顺序即优先级，靠后的优先**）。</param>
    /// <returns>资源包项（显示顺序 = 游戏里从高到低：已启用的按优先级在前，未启用的按名字在后）。</returns>
    /// <remarks>
    /// <para>
    /// **优先级 1 = 最高的那一个**，而 <c>options.txt</c> 的数组是**从低到高**存的（游戏把
    /// <c>["vanilla","file/A.zip","file/B.zip"]</c> 里的**最后一个**当成最高优先级 —— 默认包在最前，
    /// 用户新加的包追加在后、覆盖前面的）。所以这里的显示顺序是数组的**倒序**：
    /// 数组最后一个 → 优先级 1（列表第一行），与游戏「选择资源包」界面里从上到下一致。
    /// </para>
    /// <para>
    /// M7-a 的第一版把数组顺序直接当显示顺序（第一个 = 优先级 1），在**多于一个已启用包**时是反的 ——
    /// 一个包会显示成"优先级 1"，而游戏实际让它最先加载（最低优先级）。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ResourcePackEntry> Read(
        string resourcePacksDirectory,
        IReadOnlyList<string> enabledIds)
    {
        ArgumentNullException.ThrowIfNull(enabledIds);

        List<PackCandidate> candidates = ListCandidates(resourcePacksDirectory);
        if (candidates.Count == 0)
        {
            return [];
        }

        List<ResourcePackEntry> entries = [];

        // 数组是低→高，显示要高→低：从最后一个开始。
        for (var index = enabledIds.Count - 1; index >= 0; index--)
        {
            var name = IdToName(enabledIds[index]);
            if (name.Length == 0)
            {
                // `vanilla` 与 `programmer_art` 是游戏内置的，不在 resourcepacks 目录里。
                continue;
            }

            var match = candidates.FindIndex(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

            if (match < 0)
            {
                // options.txt 里写着、但文件已经不在了（用户删了包）：不编造一行。
                continue;
            }

            entries.Add(Build(candidates[match], entries.Count + 1, isEnabled: true));
        }

        var enabledNames = enabledIds.Select(IdToName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates
                     .Where(candidate => !enabledNames.Contains(candidate.Name))
                     .OrderBy(static candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(Build(candidate, entries.Count + 1, isEnabled: false));
        }

        return entries;
    }

    /// <summary><c>file/Faithful 32x.zip</c> → <c>Faithful 32x</c>（去前缀与扩展名）。</summary>
    /// <param name="id">options.txt 里的资源包 id。</param>
    /// <returns>包名；内置包返回空串。</returns>
    public static string IdToName(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var text = id.Trim();
        if (text.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
            || text.Equals("programmer_art", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (text.StartsWith("file/", StringComparison.OrdinalIgnoreCase))
        {
            text = text["file/".Length..];
        }

        return text.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(text)
            : Path.GetFileName(text);
    }

    /// <summary>从包名里取分辨率倍率（<c>Faithful 32x</c> → 32）；取不到时返回 <see langword="null"/>。</summary>
    /// <param name="name">包名。</param>
    /// <returns>倍率。</returns>
    /// <remarks>
    /// 只在名字里明确写着 <c>32x</c> / <c>32X</c> / <c>32×</c> 时才认；
    /// 认不出来就显示 <c>pack_format</c>，**不猜**（猜出来的"32×"是假数据）。
    /// </remarks>
    public static int? ResolutionOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = ResolutionPattern().Match(name);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(
            match.Groups["value"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    /// <summary>读包里的 <c>pack.mcmeta</c> 的 <c>pack_format</c>；读不到时返回 <see langword="null"/>。</summary>
    /// <param name="packPath">包文件或文件夹。</param>
    /// <param name="isDirectory">是不是文件夹形态的包。</param>
    /// <returns>格式号。</returns>
    public static int? PackFormatOf(string packPath, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);

        try
        {
            var json = isDirectory ? ReadFromDirectory(packPath) : ReadFromZip(packPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("pack", out var pack)
                   && pack.TryGetProperty("pack_format", out var format)
                   && format.ValueKind == JsonValueKind.Number
                   && format.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadFromDirectory(string directory)
    {
        var path = Path.Combine(directory, "pack.mcmeta");
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return ReadBounded(stream);
    }

    private static string? ReadFromZip(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        using var stream = archive.GetEntry("pack.mcmeta")?.Open();
        return stream is null ? null : ReadBounded(stream);
    }

    private static string ReadBounded(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxMetadataChars];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return read <= 0 ? string.Empty : new string(buffer, 0, read);
    }

    private static List<PackCandidate> ListCandidates(string resourcePacksDirectory)
    {
        List<PackCandidate> candidates = [];

        if (!Directory.Exists(resourcePacksDirectory))
        {
            return candidates;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(resourcePacksDirectory, "*.zip"))
            {
                candidates.Add(new PackCandidate(
                    Path.GetFileNameWithoutExtension(file),
                    Path.GetFileName(file),
                    file,
                    IsDirectory: false));
            }

            foreach (var directory in Directory.EnumerateDirectories(resourcePacksDirectory))
            {
                var name = Path.GetFileName(directory);
                candidates.Add(new PackCandidate(name, name, directory, IsDirectory: true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return candidates;
    }

    private static ResourcePackEntry Build(PackCandidate candidate, int priority, bool isEnabled)
    {
        var size = candidate.IsDirectory
            ? DirectorySize.Measure(candidate.Path)
            : new FileInfo(candidate.Path).Length;

        var sizeText = InstanceText.FormatSize(size);
        var resolution = ResolutionOf(candidate.Name);
        var format = resolution is null ? PackFormatOf(candidate.Path, candidate.IsDirectory) : null;
        var priorityText = priority.ToString(CultureInfo.InvariantCulture);

        // 三种形状对应三种**真实可得**的数据：有倍率用倍率、没有倍率有格式号用格式号、
        // 两者都没有（很少见）就只报大小与优先级 —— 编一个"32×"比不写更糟。
        var detail = resolution is not null
            ? TextTemplate.Of(
                "{0}× · {1} · 优先级 {2}",
                resolution.Value.ToString(CultureInfo.InvariantCulture),
                sizeText,
                priorityText)
            : format is not null
                ? TextTemplate.Of(
                    "格式 {0} · {1} · 优先级 {2}",
                    format.Value.ToString(CultureInfo.InvariantCulture),
                    sizeText,
                    priorityText)
                : TextTemplate.Of("{0} · 优先级 {1}", sizeText, priorityText);

        return new ResourcePackEntry(
            candidate.Name,
            candidate.FileName,
            detail,
            priority,
            isEnabled,
            IsActive: priority == 1);
    }

    /// <summary>
    /// <c>32x</c> / <c>32X</c> / <c>32×</c>；两侧都得是"词的边界"才算。
    /// </summary>
    /// <remarks>
    /// 两侧的 <c>[^\d.]</c> 是必需的：包名里常带版本号（<c>Better Leaves 1.20.4x</c>），
    /// 只挡数字的话 <c>4x</c> 会被当成长边 4 —— 那一格就会显示一个荒谬的"4×"。
    /// </remarks>
    [GeneratedRegex(@"(?<![\d.])(?<value>\d{1,4})\s*[xX×](?![\d.])")]
    private static partial Regex ResolutionPattern();

    /// <summary>目录里的一个候选资源包。</summary>
    /// <param name="Name">包名（显示用：zip 去扩展名、文件夹用目录名）。</param>
    /// <param name="FileName">磁盘上的文件名（zip 带 <c>.zip</c>，文件夹就是目录名；options.txt 的 id 用它）。</param>
    /// <param name="Path">文件或目录路径。</param>
    /// <param name="IsDirectory">是不是文件夹形态。</param>
    private sealed record PackCandidate(string Name, string FileName, string Path, bool IsDirectory);
}
