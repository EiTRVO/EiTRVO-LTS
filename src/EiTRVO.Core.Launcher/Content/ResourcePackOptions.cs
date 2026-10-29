using System.Text;
using System.Text.Json;
using EiTRVO.Core.Launcher.Storage;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Content;

/// <summary>
/// 游戏 <c>options.txt</c> 里 <c>resourcePacks</c> 那一行的读与写（M7-g-2b）。
/// </summary>
/// <remarks>
/// <para>
/// **这是唯一一份真相**：资源包的启用与优先级只有 <c>options.txt</c> 说了算（AGENTS.md §6）。
/// 我们另存一份"优先级"就是第二条真相，换个启动器就会与游戏对不上。
/// </para>
/// <para>
/// **顺序是低 → 高**：游戏把数组的**最后一个**当最高优先级（默认包 <c>vanilla</c> 在最前，
/// 用户新加的包追加在后、覆盖前面的）。界面显示时再倒过来（优先级 1 = 数组最后一个）。
/// </para>
/// <para>
/// **写文件的两条纪律**（与用户数据安全四条同源）：
/// ① 读不出来的 <c>options.txt</c>（JSON 不是数组、含非字符串元素）**一律不写** —— 用户手改坏的文件
/// 只能如实报告，不能"顺手修好"（那会丢掉他其它的设置行）；
/// ② 写入走 <see cref="AtomicFile"/>（临时文件 + 原子替换 + 上一版 <c>.bak</c>）。
/// 除了 <c>resourcePacks</c> 这一行，其余内容逐行原样保留。
/// </para>
/// </remarks>
public static class ResourcePackOptions
{
    /// <summary>游戏设置文件里的键（资源包清单）。</summary>
    public const string Key = "resourcePacks";

    /// <summary>设置文件名。</summary>
    public const string FileName = "options.txt";

    /// <summary>一个资源包在 <c>options.txt</c> 里的 id 前缀。</summary>
    private const string FileIdPrefix = "file/";

    /// <summary><c>options.txt</c> 的路径。</summary>
    /// <param name="gameDirectory">实例的游戏目录。</param>
    /// <returns>路径。</returns>
    public static string PathOf(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        return Path.Combine(gameDirectory, FileName);
    }

    /// <summary>
    /// 读已启用的资源包 id（**低 → 高**）。
    /// </summary>
    /// <param name="gameDirectory">实例的游戏目录。</param>
    /// <param name="ids">读到的 id 列表。</param>
    /// <param name="failure">读失败时的原因；成功时为 <see langword="null"/>。</param>
    /// <returns>可以安全地基于这个结果写回时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// **文件不存在 / 没有这一行 / 是空数组**都算"一个都没启用"，可以写（游戏中第一次设置资源包时也是这么产生的）；
    /// 只有"这一行存在但读不懂"才算失败。
    /// </remarks>
    public static bool TryRead(string gameDirectory, out List<string> ids, out ContentActionResult? failure)
    {
        ids = [];
        failure = null;

        var path = PathOf(gameDirectory);

        if (!File.Exists(path))
        {
            return true;
        }

        string text;

        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ContentActionResult.Failure(
                TextTemplate.Of("读不出 options.txt（{0}），这次没有改动。", ex.GetType().Name));
            return false;
        }

        var value = ValueOf(text);

        if (value is null || value.Length == 0)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(value);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                failure = ContentActionResult.Failure(NotAnArray());
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    failure = ContentActionResult.Failure(NotAnArray());
                    return false;
                }

                var id = item.GetString();

                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id.Trim());
                }
            }

            return true;
        }
        catch (JsonException)
        {
            failure = ContentActionResult.Failure(NotAnArray());
            return false;
        }
    }

    /// <summary>
    /// 把资源包 id 列表（**低 → 高**）写回 <c>options.txt</c>。
    /// </summary>
    /// <param name="gameDirectory">实例的游戏目录。</param>
    /// <param name="ids">新的 id 列表。</param>
    /// <returns>结果；失败时带原因（失败时**原文件一个字节都没动**）。</returns>
    public static ContentActionResult Write(string gameDirectory, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var path = PathOf(gameDirectory);
        string text;

        try
        {
            text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ContentActionResult.Failure(
                TextTemplate.Of("读不出 options.txt（{0}），这次没有改动。", ex.GetType().Name));
        }

        // 保留用户文件的换行风格：Windows 上的游戏写 CRLF，手改出来的可能是 LF。
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = text.EndsWith('\n');

        List<string> lines = [];

        if (text.Length > 0)
        {
            foreach (var raw in text.Split('\n'))
            {
                lines.Add(raw.TrimEnd('\r'));
            }

            // 末尾换行会切出一个空串，它不是"一行"。
            if (lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
        }

        var replacement = Key + ":" + JsonSerializer.Serialize(ids);
        var index = lines.FindIndex(static line => line.StartsWith(Key + ":", StringComparison.Ordinal));

        string output;

        if (index >= 0)
        {
            lines[index] = replacement;
            output = string.Join(eol, lines) + (endsWithNewline || text.Length == 0 ? eol : string.Empty);
        }
        else
        {
            // 没有这一行：追加。游戏能接受缺少这个键的文件（会用它自己的默认值）。
            lines.Add(replacement);
            output = string.Join(eol, lines) + eol;
        }

        try
        {
            AtomicFile.WriteAllText(path, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ContentActionResult.Failure(
                TextTemplate.Of("写入 options.txt 失败（{0}），这次没有改动。", ex.GetType().Name));
        }

        return ContentActionResult.Success;
    }

    /// <summary>一个资源包在 <c>options.txt</c> 里的 id（<c>file/&lt;文件名&gt;</c>）。</summary>
    /// <param name="fileName">磁盘上的文件名（zip 带扩展名，文件夹就是目录名）。</param>
    /// <returns>id。</returns>
    public static string IdOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return FileIdPrefix + fileName.Trim();
    }

    /// <summary>这个 id 是不是"用户装的资源包"（内置的 <c>vanilla</c> / <c>programmer_art</c> 不是）。</summary>
    /// <param name="id">id。</param>
    /// <returns>是用户资源包时为 <see langword="true"/>。</returns>
    public static bool IsFileId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Trim().StartsWith(FileIdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>取 <c>resourcePacks:</c> 之后的值（没有这一行时为 <see langword="null"/>）。</summary>
    /// <param name="text"><c>options.txt</c> 的全文。</param>
    /// <returns>值。</returns>
    private static string? ValueOf(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith(Key + ":", StringComparison.Ordinal))
            {
                return line[(Key.Length + 1)..].Trim();
            }
        }

        return null;
    }

    private static TextTemplate NotAnArray() =>
        TextTemplate.Of("options.txt 里的资源包清单不是合法的字符串数组，这次没有改动。");
}
