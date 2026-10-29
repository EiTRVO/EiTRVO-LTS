using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EiTRVO.Core.Launcher.Storage;

/// <summary>
/// 落盘用的 JSON 选项。全仓库只在这里定义一次，避免每处各写一套（写出来的文件风格还不一致）。
/// <para>
/// 三件事是刻意选择的：
/// </para>
/// <list type="number">
/// <item><b>只做最小转义</b>：<c>System.Text.Json</c> 默认把中文写成 <c>\u65E7</c>，
/// 设置文件与事件日志是给人和排障用的，必须直接可读。更麻烦的是默认编码器连
/// <c>+</c> 都会写成 <c>\u002B</c> —— 而 JVM 参数（<c>-XX:+UseG1GC</c>）里全是加号，
/// 写出来就没法看了。
/// <para>
/// 用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 是**刻意的**：它的
/// "unsafe" 指的是"不要把它拼进 HTML / &lt;script&gt; 里"，而这里写的是独立文件、
/// 由 <c>System.Text.Json</c> 自己读回。若将来要把这段 JSON 内嵌到网页里，
/// 必须换回 <see cref="JavaScriptEncoder.Default"/>。
/// </para></item>
/// <item><b>允许注释与尾逗号</b>：用户会手改 <c>settings.json</c>；
/// 因为一个尾逗号就整个文件回退默认值太粗暴。</item>
/// <item><b>枚举写成字符串</b>：<c>"Mirror"</c> 比 <c>1</c> 能看懂，
/// 也避免将来枚举成员顺序变化导致旧文件被解释成别的值。</item>
/// </list>
/// </summary>
public static class LauncherJson
{
    /// <summary>给人看的文件（settings.json / instance.json / overrides.json）：缩进对齐。</summary>
    public static JsonSerializerOptions Indented { get; } = Create(writeIndented: true);

    /// <summary>逐行追加的文件（events.jsonl）：一行一个对象，不缩进。</summary>
    public static JsonSerializerOptions Compact { get; } = Create(writeIndented: false);

    /// <summary>序列化。</summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="value">值。</param>
    /// <param name="options">选项。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize<T>(T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Serialize(value, options);
    }

    /// <summary>反序列化；文本不是合法 JSON 时抛 <see cref="JsonException"/>（由调用方决定如何回退）。</summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="json">JSON 文本。</param>
    /// <param name="options">选项。</param>
    /// <returns>反序列化结果；文本为 <c>null</c> 字面量时返回 <see langword="null"/>。</returns>
    public static T? Deserialize<T>(string json, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Deserialize<T>(json, options);
    }

    private static JsonSerializerOptions Create(bool writeIndented) => new()
    {
        WriteIndented = writeIndented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
