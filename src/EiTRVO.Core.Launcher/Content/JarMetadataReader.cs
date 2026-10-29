using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EiTRVO.Core.Launcher.Content;

/// <summary>模组声明的一条依赖。</summary>
/// <param name="ModId">被依赖的模组 id（例如 <c>flywheel</c>）。</param>
/// <param name="Mandatory">是否必需（<c>optional</c> / <c>recommends</c> 都算不必需）。</param>
/// <param name="VersionRange">版本区间（原样保留，例如 <c>&gt;=0.6.10</c>）；没写为 <see langword="null"/>。</param>
public sealed record ModDependency(string ModId, bool Mandatory, string? VersionRange);

/// <summary>从一个模组 jar 里读出来的元数据（读不到的一律为 <see langword="null"/>）。</summary>
/// <param name="DisplayName">显示名（<c>name</c> / <c>displayName</c>）。</param>
/// <param name="Version">模组自己的版本。</param>
/// <param name="ModId">模组 id（依赖对账靠它）。</param>
/// <param name="Dependencies">声明的依赖。</param>
public sealed record ModFileMetadata(
    string? DisplayName,
    string? Version,
    string? ModId,
    IReadOnlyList<ModDependency> Dependencies)
{
    /// <summary>
    /// 这个 jar 额外提供的模组 id。Fabric API 这类“伞形模组”把子模块放在
    /// <c>META-INF/jars/</c> 里，子模块的 id / provides 也要算作“已安装”。
    /// </summary>
    public IReadOnlyList<string> ProvidedIds { get; init; } = [];

    /// <summary>什么都没读出来。</summary>
    public static ModFileMetadata Empty { get; } = new(null, null, null, []);
}

/// <summary>
/// 从模组 jar 的元数据文件里读「这个模组叫什么、什么版本、依赖谁」。
/// </summary>
/// <remarks>
/// 这是模组表格能显示真名与真版本值的**唯一**来源：文件名（<c>sodium-fabric-0.5.8+mc1.20.4.jar</c>）
/// 既不是名字也不是版本，靠它猜出来的表格是假的。
/// <para>
/// 按加载器生态的四种写法依次尝试：Fabric（<c>fabric.mod.json</c>）、Quilt（<c>quilt.mod.json</c>）、
/// Forge / NeoForge（<c>META-INF/mods.toml</c>、<c>META-INF/neoforge.mods.toml</c>）、
/// 1.12 时代的 <c>mcmod.info</c>，最后兜底读 <c>META-INF/MANIFEST.MF</c>。
/// </para>
/// <para>
/// <b>只读不写、只解压不落盘</b>：zip 条目只读前若干 KB（元数据文件都很小，
/// 而一个声称 10 GB 的条目不该让我们去分配 10 GB）。
/// </para>
/// </remarks>
public static class JarMetadataReader
{
    /// <summary>单个元数据文件最多读多少字符（256 KiB，远大于任何真实元数据）。</summary>
    private const int MaxMetadataChars = 256 * 1024;

    /// <summary>读一个 jar。</summary>
    /// <param name="jarPath">jar 路径。</param>
    /// <returns>元数据；不是 zip、读不到或没有元数据文件时返回 <see cref="ModFileMetadata.Empty"/>。</returns>
    public static ModFileMetadata Read(string jarPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);

        if (!File.Exists(jarPath))
        {
            return ModFileMetadata.Empty;
        }

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                entries[entry.FullName] = entry;
            }

            return Read(entries);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            // 损坏的 jar 很常见（下载没完成、用户手工改过名）：这一行退回按文件名显示，
            // 但**不**因此让整个模组列表失败（AGENTS.md §6 的精神：坏数据不得扩散）。
            return ModFileMetadata.Empty;
        }
    }

    private static ModFileMetadata Read(Dictionary<string, ZipArchiveEntry> entries)
    {
        foreach (var name in MetadataFilePriority)
        {
            if (!entries.TryGetValue(name, out var entry))
            {
                continue;
            }

            var text = ReadEntry(entry);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var metadata = name switch
            {
                "fabric.mod.json" => ParseFabric(text),
                "quilt.mod.json" => ParseQuilt(text),
                "META-INF/neoforge.mods.toml" or "META-INF/mods.toml" => ParseForge(text),
                "mcmod.info" => ParseLegacy(text),
                _ => Manifest(text),
            };

            if (!string.IsNullOrWhiteSpace(metadata.DisplayName)
                || !string.IsNullOrWhiteSpace(metadata.ModId)
                || !string.IsNullOrWhiteSpace(metadata.Version))
            {
                var nested = ReadNestedProvidedIds(entries);
                return nested.Count == 0
                    ? metadata
                    : metadata with { ProvidedIds = [.. metadata.ProvidedIds, .. nested] };
            }
        }

        return ModFileMetadata.Empty;
    }

    /// <summary>尝试顺序：越靠前越具体（先现代格式，最后才是通用的 MANIFEST）。</summary>
    private static readonly string[] MetadataFilePriority =
    [
        "fabric.mod.json",
        "quilt.mod.json",
        "META-INF/neoforge.mods.toml",
        "META-INF/mods.toml",
        "mcmod.info",
        "META-INF/MANIFEST.MF",
    ];

    /// <summary>依赖对账时要忽略的 id：它们是加载器/游戏/运行时，不是"装在 mods 里的模组"。</summary>
    private static readonly HashSet<string> LoaderOrGameIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft",
        "forge",
        "neoforge",
        "fabricloader",
        "quilt_loader",
        "java",
    };

    /// <summary>该依赖 id 是否代表"加载器/游戏"（这类依赖不参与"是否缺失"的判定）。</summary>
    /// <param name="modId">依赖 id。</param>
    /// <returns>是返回 <see langword="true"/>。</returns>
    public static bool IsLoaderOrGame(string modId) => LoaderOrGameIds.Contains(modId);

    // -------------------------------------------------------------- Fabric ---

    private static ModFileMetadata ParseFabric(string json)
    {
        var root = ParseJson(json);
        if (root is null)
        {
            return ModFileMetadata.Empty;
        }

        var dependencies = new List<ModDependency>();
        if (root.Value.TryGetProperty("depends", out var depends))
        {
            CollectDependencies(depends, mandatory: true, dependencies);
        }

        var provided = new List<string>();
        if (root.Value.TryGetProperty("provides", out var provides))
        {
            CollectProvides(provides, provided);
        }

        return new ModFileMetadata(
            DisplayName: Text(root.Value, "name"),
            Version: Text(root.Value, "version"),
            ModId: Text(root.Value, "id"),
            Dependencies: dependencies)
        {
            ProvidedIds = provided,
        };
    }

    // -------------------------------------------------------------- Quilt ---

    private static ModFileMetadata ParseQuilt(string json)
    {
        var root = ParseJson(json);
        if (root is null || !root.Value.TryGetProperty("quilt_loader", out var loader))
        {
            return ModFileMetadata.Empty;
        }

        var dependencies = new List<ModDependency>();
        if (loader.TryGetProperty("depends", out var depends))
        {
            CollectDependencies(depends, mandatory: true, dependencies);
        }

        var provided = new List<string>();
        if (loader.TryGetProperty("provides", out var provides))
        {
            CollectProvides(provides, provided);
        }

        // Quilt 把显示名放在 `quilt_loader.metadata.name`（没有 `name`），失败时退回 id。
        string? displayName = null;
        if (loader.TryGetProperty("metadata", out var metadata))
        {
            displayName = Text(metadata, "name");
        }

        var modId = Text(loader, "id");

        return new ModFileMetadata(
            DisplayName: displayName ?? modId,
            Version: Text(loader, "version"),
            ModId: modId,
            Dependencies: dependencies)
        {
            ProvidedIds = provided,
        };
    }

    /// <summary>把 fabric / quilt 的 <c>depends</c> 收成依赖列表（两种写法的形状不同）。</summary>
    private static void CollectDependencies(JsonElement element, bool mandatory, List<ModDependency> target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    target.Add(new ModDependency(
                        property.Name,
                        // 对象形态里 <c>optional: true</c> 表示不必需（quilt 的写法）。
                        mandatory && !IsOptional(property.Value),
                        RangeOf(property.Value)));
                }

                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var id = item.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            target.Add(new ModDependency(id.Trim(), mandatory, null));
                        }

                        continue;
                    }

                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = Text(item, "id") ?? Text(item, "modId");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string? range = null;
                    if (item.TryGetProperty("versions", out var versions))
                    {
                        range = RangeOf(versions);
                    }

                    target.Add(new ModDependency(name, mandatory && !IsOptional(item), range));
                }

                return;

            default:
                return;
        }
    }

    /// <summary>收集 <c>provides</c>（Fabric / Quilt 的伞形模组会用它声明额外模块 id）。</summary>
    private static void CollectProvides(JsonElement element, List<string> target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var single = element.GetString();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    target.Add(single.Trim());
                }

                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        target.Add(item.GetString()!.Trim());
                    }
                }

                return;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(property.Name))
                    {
                        target.Add(property.Name);
                    }
                }

                return;

            default:
                return;
        }
    }

    private static bool IsOptional(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("optional", out var optional)
        && optional.ValueKind == JsonValueKind.True;

    private static string? RangeOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Trim(),
        JsonValueKind.Array => string.Join(
            ", ",
            element.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty)
                .Where(static item => item.Length > 0)),
        JsonValueKind.Object => Text(element, "version") ?? Text(element, "versions"),
        _ => null,
    };

    // ------------------------------------------------------- Forge / NeoForge ---

    private static ModFileMetadata ParseForge(string toml)
    {
        var sections = MiniToml.Parse(toml);

        string? modId = null;
        string? version = null;
        string? displayName = null;

        foreach (var section in sections)
        {
            if (!string.Equals(section.Header, "mods", StringComparison.Ordinal))
            {
                continue;
            }

            modId ??= Value(section, "modId");
            version ??= Value(section, "version");
            displayName ??= Value(section, "displayName");
        }

        var dependencies = new List<ModDependency>();
        foreach (var section in sections)
        {
            if (!section.Header.StartsWith("dependencies.", StringComparison.Ordinal))
            {
                continue;
            }

            var dependencyId = Value(section, "modId");
            if (string.IsNullOrWhiteSpace(dependencyId))
            {
                continue;
            }

            // 1.20.1 的 NeoForge 与 Forge 写 `mandatory=true`；1.20.5+ 的 NeoForge 写 `type="required"`。
            var type = Value(section, "type");
            var mandatory = type is not null
                ? string.Equals(type, "required", StringComparison.OrdinalIgnoreCase)
                : !string.Equals(Value(section, "mandatory"), "false", StringComparison.OrdinalIgnoreCase);

            dependencies.Add(new ModDependency(dependencyId, mandatory, Value(section, "versionRange")));
        }

        return new ModFileMetadata(displayName ?? modId, version, modId, dependencies);
    }

    private static string? Value(TomlSection section, string key) =>
        section.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    // -------------------------------------------------------- 1.12 的 mcmod.info ---

    private static ModFileMetadata ParseLegacy(string json)
    {
        var root = ParseJson(json);
        if (root is null)
        {
            return ModFileMetadata.Empty;
        }

        var element = root.Value;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("modList", out var list))
        {
            element = list;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return ModFileMetadata.Empty;
        }

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Text(entry, "modid") ?? Text(entry, "modId");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(Text(entry, "name")))
            {
                continue;
            }

            List<ModDependency> dependencies = [];
            if (entry.TryGetProperty("requiredMods", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    // 写法是 `modid@[1.0,)`；没有 @ 就是只有 id。
                    var text = item.GetString() ?? string.Empty;
                    var at = text.IndexOf('@', StringComparison.Ordinal);
                    var dependencyId = (at < 0 ? text : text[..at]).Trim();
                    if (dependencyId.Length > 0)
                    {
                        dependencies.Add(new ModDependency(
                            dependencyId,
                            Mandatory: true,
                            at < 0 ? null : text[(at + 1)..].Trim()));
                    }
                }
            }

            return new ModFileMetadata(
                DisplayName: Text(entry, "name") ?? id,
                Version: Text(entry, "version"),
                ModId: id,
                Dependencies: dependencies);
        }

        return ModFileMetadata.Empty;
    }

    // ------------------------------------------------------------ MANIFEST.MF ---

    private static ModFileMetadata Manifest(string text)
    {
        string? title = null;
        string? version = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Equals("Implementation-Title", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Specification-Title", StringComparison.OrdinalIgnoreCase))
            {
                title ??= value;
            }
            else if (key.Equals("Implementation-Version", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("Specification-Version", StringComparison.OrdinalIgnoreCase))
            {
                version ??= value;
            }
        }

        return new ModFileMetadata(title, version, null, []);
    }

    // ------------------------------------------------------------------ 工具 ---

    private static JsonElement? ParseJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>
    /// 读 Fabric / Quilt 伞形模组里的嵌套模块 id（<c>META-INF/jars/*.jar</c>）。
    /// </summary>
    /// <remarks>
    /// Fabric API 的外层 <c>fabric.mod.json</c> 只写 <c>fabric-api</c>，子模块
    /// （<c>fabric-block-view-api-v2</c> 等）都在嵌套 jar 里。只读外层元数据会把
    /// “Fabric API 明明装了、Sodium 仍缺 block-view-api”这种假缺失报给用户。
    /// </remarks>
    private static List<string> ReadNestedProvidedIds(Dictionary<string, ZipArchiveEntry> entries)
    {
        const int MaxNestedJars = 256;
        const long MaxNestedJarBytes = 64L * 1024 * 1024;

        List<string> ids = [];
        var inspected = 0;

        foreach (var entry in entries.Values)
        {
            if (inspected >= MaxNestedJars)
            {
                break;
            }

            if (!entry.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase)
                || !entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || entry.Length > MaxNestedJarBytes)
            {
                continue;
            }

            inspected++;

            try
            {
                using var buffer = new MemoryStream();
                using (var source = entry.Open())
                {
                    source.CopyTo(buffer);
                }

                buffer.Position = 0;

                using var nested = new ZipArchive(buffer, ZipArchiveMode.Read);
                var metadataEntry = nested.Entries.FirstOrDefault(static candidate =>
                    candidate.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase)
                    || candidate.FullName.Equals("quilt.mod.json", StringComparison.OrdinalIgnoreCase));

                if (metadataEntry is null)
                {
                    continue;
                }

                var text = ReadEntry(metadataEntry);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var metadata = metadataEntry.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase)
                    ? ParseFabric(text)
                    : ParseQuilt(text);

                if (!string.IsNullOrWhiteSpace(metadata.ModId))
                {
                    ids.Add(metadata.ModId);
                }

                ids.AddRange(metadata.ProvidedIds);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                // 某个嵌套 jar 坏了：不影响外层模组的名字/版本，更不该让整张模组表失败。
            }
        }

        return ids;
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxMetadataChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return read <= 0 ? string.Empty : new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return string.Empty;
        }
    }
}
