using System.Text.Json.Serialization;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content.Modpack;

/// <summary>
/// <c>.mrpack</c> 清单读不出来 / 不合法（M7-e）。
/// </summary>
/// <remarks>
/// 与 <c>ModrinthException</c> / <c>VersionDataException</c> 并列："这份远端数据不能用"。
/// <see cref="Reason"/> 是**给用户看的原因**（键 + 参数），因为这类失败的结论要显示在队列任务上
/// （"这个整合包是给哪个格式版本做的"是用户能自己处理的）。
/// </remarks>
public sealed class ModpackException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="reason">给用户看的原因（键 + 参数）。</param>
    /// <param name="innerException">底层异常。</param>
    public ModpackException(TextTemplate reason, Exception? innerException = null)
        : base(reason.Key, innerException) => Reason = reason;

    /// <summary>给用户看的原因。</summary>
    public TextTemplate Reason { get; }
}

/// <summary>
/// 一个 <c>.mrpack</c> 的清单（<c>modrinth.index.json</c>，M7-e）。
/// </summary>
/// <param name="FormatVersion">格式版本（当前只支持 1）。</param>
/// <param name="Game">游戏类型（只支持 <c>minecraft</c>）。</param>
/// <param name="VersionId">整合包这一版的标识。</param>
/// <param name="Name">整合包名字（新实例的名字从这里来）。</param>
/// <param name="Summary">一句话简介。</param>
/// <param name="Files">要下载的文件（**路径已校验**，见 <see cref="ModpackFile"/>）。</param>
/// <param name="Dependencies">清单声明的依赖（原样的键值对）。</param>
/// <param name="UnknownDependencies">
/// 我们不认识、因而**忽略**的依赖键（规范明说"随时可能加新的"，所以是容忍而不是失败）。
/// </param>
/// <param name="GameVersion">游戏版本（来自 <c>dependencies.minecraft</c>；没有时为空串）。</param>
/// <param name="Loader">加载器；一个都没声明时是 <see cref="LoaderKind.Vanilla"/>。</param>
/// <param name="LoaderVersion">加载器版本（原版为 <see langword="null"/>）。</param>
/// <remarks>
/// 解析放在读取这一步而不是安装那一步：**能不能装**（格式版本、game 类型、路径合法性）应当在
/// 动任何文件之前就有结论 —— 装到一半才发现清单不合法，用户得到的是一个半成品实例。
/// </remarks>
public sealed record ModpackIndex(
    int FormatVersion,
    string Game,
    string? VersionId,
    string? Name,
    string? Summary,
    IReadOnlyList<ModpackFile> Files,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<string> UnknownDependencies,
    string GameVersion,
    LoaderKind Loader,
    string? LoaderVersion);

/// <summary>清单里的一个文件。</summary>
/// <param name="Path">相对实例目录的路径（已归一化成 <c>/</c> 分隔、单段已校验）。</param>
/// <param name="Segments">路径分段（拼真实路径时用 <c>PathSafety.CombineWithin</c>，不自己拼字符串）。</param>
/// <param name="Sha1">期望的 SHA-1（规范要求必须有）。</param>
/// <param name="Sha512">期望的 SHA-512（规范要求必须有）。</param>
/// <param name="Size">字节数（清单里是可选字段，没有时为 0）。</param>
/// <param name="Downloads">可下载地址（可能为空：那表示这个文件应当由覆盖层提供）。</param>
/// <param name="ClientSupported">
/// 客户端能不能装它（<c>env.client != "unsupported"</c>；**没有 <c>env</c> 的文件照装**）。
/// </param>
public sealed record ModpackFile(
    string Path,
    IReadOnlyList<string> Segments,
    string Sha1,
    string Sha512,
    long Size,
    IReadOnlyList<string> Downloads,
    bool ClientSupported);

/// <summary>
/// <c>modrinth.index.json</c> 的读取与校验（M7-e）。
/// </summary>
/// <remarks>
/// 规范：<see href="https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack"/>。
/// 四条硬要求在这里落地：
/// <list type="number">
/// <item><b><c>formatVersion</c> 必须是 1</b>、<b><c>game</c> 必须是 <c>minecraft</c></b> ——
/// 不认识的格式宁可拒绝，也不猜着读（猜错会写出一个看着像装好了、其实缺一半的实例）；</item>
/// <item><b><c>files[].path</c> 必须落在实例目录内</b>（不能有 <c>..</c>、不能是绝对路径或带盘符）——
/// 这是规范原文里点名的那条攻击面；</item>
/// <item><b><c>hashes</c> 里 sha1 与 sha512 都要有</b>：只有这样才谈得上"校验过"；</item>
/// <item><b>未知的 <c>dependencies</c> 键要容忍</b>（规范明说随时可能加新的），收集起来交给界面说明。</item>
/// </list>
/// <para>
/// <c>env</c> 的语义：<c>client</c> 为 <c>unsupported</c> 的文件**不装**（规范：服务端专用的文件
/// 不该出现在客户端）；<c>required</c> / <c>optional</c> 都装（"可选"是给用户挑的，
/// 而没有对话框就问时"装上"比"悄悄少装一个模组"更接近作者的意图）；
/// <c>env</c> 整个缺失时**照装**。
/// </para>
/// </remarks>
public static class MrpackReader
{
    /// <summary>清单在 zip 里的位置。</summary>
    public const string IndexEntryName = "modrinth.index.json";

    /// <summary>当前支持的格式版本。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>清单的读取上限（它本身只有几 KB；超过就是坏东西，别让它把内存吃掉）。</summary>
    public const int MaxIndexBytes = 4 * 1024 * 1024;

    /// <summary>Minecraft 的依赖键。</summary>
    public const string MinecraftDependency = "minecraft";

    /// <summary>Fabric 的依赖键。</summary>
    public const string FabricDependency = "fabric-loader";

    /// <summary>Quilt 的依赖键。</summary>
    public const string QuiltDependency = "quilt-loader";

    /// <summary>Forge 的依赖键。</summary>
    public const string ForgeDependency = "forge";

    /// <summary>NeoForge 的依赖键。</summary>
    public const string NeoForgeDependency = "neoforge";

    /// <summary>解析清单文本。</summary>
    /// <param name="json">清单 JSON。</param>
    /// <returns>清单。</returns>
    /// <exception cref="ModpackException">JSON 非法、版本/游戏类型不支持、路径越界、缺校验值等。</exception>
    public static ModpackIndex Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        RawIndex? raw;

        try
        {
            raw = LauncherJson.Deserialize<RawIndex>(json, LauncherJson.Indented);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ModpackException(TextTemplate.Of("整合包清单不是合法的 JSON。"), ex);
        }

        if (raw is null)
        {
            throw new ModpackException(TextTemplate.Of("整合包清单是空的。"));
        }

        if (raw.FormatVersion != SupportedFormatVersion)
        {
            // 版本号是**数据**，进参数（英文界面里也不该翻）。
            throw new ModpackException(TextTemplate.Of(
                "这个整合包的格式版本是 {0}，本启动器只支持 {1}。",
                raw.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SupportedFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (!string.Equals(raw.Game, "minecraft", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModpackException(TextTemplate.Of("这个整合包是给 {0} 做的，不是 Minecraft。", raw.Game ?? string.Empty));
        }

        var dependencies = raw.Dependencies is { Count: > 0 }
            ? new Dictionary<string, string>(raw.Dependencies, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var (gameVersion, loader, loaderVersion, unknown) = ReadDependencies(dependencies);
        var files = ReadFiles(raw.Files);

        return new ModpackIndex(
            raw.FormatVersion,
            raw.Game ?? string.Empty,
            raw.VersionId,
            raw.Name,
            raw.Summary,
            files,
            dependencies,
            unknown,
            gameVersion,
            loader,
            loaderVersion);
    }

    /// <summary>从 zip 条目流里读清单（限制读取长度）。</summary>
    /// <param name="indexStream">清单条目的流。</param>
    /// <returns>清单。</returns>
    /// <exception cref="ModpackException">读不出来 / 太长 / 内容不合法。</exception>
    public static ModpackIndex Read(Stream indexStream)
    {
        ArgumentNullException.ThrowIfNull(indexStream);

        using var reader = new StreamReader(
            indexStream,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: true);

        var buffer = new char[MaxIndexBytes];
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = reader.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        if (read >= buffer.Length)
        {
            throw new ModpackException(TextTemplate.Of("整合包清单大得不像清单（超过 {0} KB），已拒绝。", (MaxIndexBytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return Parse(new string(buffer, 0, read));
    }

    /// <summary>把 <c>dependencies</c> 分成"认识的"与"忽略的"，并挑出游戏版本与加载器。</summary>
    private static (string GameVersion, LoaderKind Loader, string? LoaderVersion, IReadOnlyList<string> Unknown) ReadDependencies(
        Dictionary<string, string> dependencies)
    {
        var gameVersion = dependencies.TryGetValue(MinecraftDependency, out var minecraft) ? minecraft : string.Empty;
        var unknown = new List<string>();

        List<(string Key, LoaderKind Kind)> loaders = [];

        foreach (var (key, value) in dependencies)
        {
            switch (key.ToLowerInvariant())
            {
                case MinecraftDependency:
                    break;
                case FabricDependency:
                    loaders.Add((key, LoaderKind.Fabric));
                    break;
                case QuiltDependency:
                    loaders.Add((key, LoaderKind.Quilt));
                    break;
                case ForgeDependency:
                    loaders.Add((key, LoaderKind.Forge));
                    break;
                case NeoForgeDependency:
                    loaders.Add((key, LoaderKind.NeoForge));
                    break;
                default:
                    // 规范：随时可能加新的依赖键，**容忍并忽略**。原文记下来，界面上要能说实话。
                    unknown.Add(key + "=" + value);
                    break;
            }
        }

        if (loaders.Count > 1)
        {
            // 两个加载器同时声明是**清单自相矛盾**（Forge 与 Fabric 装不进同一个实例），宁可拒绝。
            throw new ModpackException(TextTemplate.Of(
                "这个整合包同时声明了多个加载器（{0}），无法安装。",
                string.Join(", ", loaders.Select(static pair => pair.Key))));
        }

        if (loaders.Count == 0)
        {
            return (gameVersion, LoaderKind.Vanilla, null, unknown);
        }

        var (loaderKey, kind) = loaders[0];
        var version = dependencies.TryGetValue(loaderKey, out var declared) && !string.IsNullOrWhiteSpace(declared)
            ? declared.Trim()
            : null;

        return (gameVersion, kind, version, unknown);
    }

    /// <summary>读 <c>files[]</c>（路径与校验值在这里就校验，安装阶段不再判一次）。</summary>
    private static List<ModpackFile> ReadFiles(IReadOnlyList<RawFile>? files)
    {
        if (files is not { Count: > 0 })
        {
            return [];
        }

        List<ModpackFile> result = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (!ModpackFilePaths.TryNormalize(file.Path, out var normalized, out var segments))
            {
                // **规范点名的攻击面**：path 必须落在实例目录内。
                throw new ModpackException(TextTemplate.Of("清单里的文件路径越出了实例目录（{0}）。", file.Path ?? string.Empty));
            }

            if (!paths.Add(normalized))
            {
                throw new ModpackException(TextTemplate.Of("清单里同一个路径出现了两次（{0}）。", normalized));
            }

            var sha1 = file.Hashes?.Sha1?.Trim();
            var sha512 = file.Hashes?.Sha512?.Trim();

            if (string.IsNullOrWhiteSpace(sha1) || string.IsNullOrWhiteSpace(sha512))
            {
                // 规范要求两个都在；缺了就**无法校验** —— 不校验的下载等于让远端决定我们写什么。
                throw new ModpackException(TextTemplate.Of(
                    "清单里的 {0} 缺少 SHA-1 或 SHA-512 校验值，无法校验下载内容。",
                    normalized));
            }

            result.Add(new ModpackFile(
                Path: normalized,
                Segments: segments,
                Sha1: sha1,
                Sha512: sha512,
                Size: file.FileSize,
                Downloads: [.. (file.Downloads ?? []).Where(static url => !string.IsNullOrWhiteSpace(url)).Select(static url => url.Trim())],
                ClientSupported: !string.Equals(file.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    /// <summary>清单的落盘形状（字段名按规范）。</summary>
    private sealed record RawIndex
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; init; }

        [JsonPropertyName("game")]
        public string? Game { get; init; }

        [JsonPropertyName("versionId")]
        public string? VersionId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("files")]
        public IReadOnlyList<RawFile>? Files { get; init; }

        [JsonPropertyName("dependencies")]
        public IReadOnlyDictionary<string, string>? Dependencies { get; init; }
    }

    /// <summary>清单里一个文件的落盘形状。</summary>
    private sealed record RawFile
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("hashes")]
        public RawHashes? Hashes { get; init; }

        [JsonPropertyName("downloads")]
        public IReadOnlyList<string>? Downloads { get; init; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; init; }

        [JsonPropertyName("env")]
        public RawEnvironment? Env { get; init; }
    }

    /// <summary>校验值的落盘形状。</summary>
    private sealed record RawHashes
    {
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; init; }

        [JsonPropertyName("sha512")]
        public string? Sha512 { get; init; }
    }

    /// <summary><c>env</c> 的落盘形状（我们只关心 <c>client</c>）。</summary>
    private sealed record RawEnvironment
    {
        [JsonPropertyName("client")]
        public string? Client { get; init; }

        [JsonPropertyName("server")]
        public string? Server { get; init; }
    }
}
