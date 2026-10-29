using System.Text.Json;
using System.Text.Json.Serialization;

namespace EiTRVO.Core.Launcher.Versions;

/// <summary>
/// 1.13 起的结构化参数（<c>arguments.game</c> / <c>arguments.jvm</c>）里的一项。
/// <para>
/// 一项要么是**无条件**的若干实参，要么带一组规则（按操作系统或特性决定是否生效）。
/// 把它建模成类型而不是 <see cref="JsonElement"/>（Neo 的做法）的理由：
/// 参数求值在 M4 要用到规则里的 <c>os.name</c> / <c>os.arch</c> / <c>features</c>，
/// 让 <c>System.Text.Json</c> 的类型渗进领域模型、到那时再手工解析 JSON，既难测又容易漏字段。
/// </para>
/// </summary>
/// <param name="Values">实参（一项可能展开成多个实参，如 <c>["-cp","${classpath}"]</c>）。</param>
/// <param name="Rules">适用规则；<see langword="null"/> 或空表示无条件生效。</param>
[JsonConverter(typeof(VersionArgumentConverter))]
public sealed record VersionArgument(IReadOnlyList<string> Values, IReadOnlyList<VersionRule>? Rules)
{
    /// <summary>是否需要按规则求值。无条件时可以直接展开。</summary>
    public bool IsConditional => Rules is { Count: > 0 };

    /// <summary>构造一个无条件的实参项。</summary>
    /// <param name="value">实参。</param>
    /// <returns>参数项。</returns>
    public static VersionArgument Plain(string value) => new([value], null);

    /// <summary>构造一个带规则的实参项。</summary>
    /// <param name="values">实参。</param>
    /// <param name="rules">规则。</param>
    /// <returns>参数项。</returns>
    public static VersionArgument Conditional(IEnumerable<string> values, IReadOnlyList<VersionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rules);

        return new VersionArgument([.. values], rules);
    }
}

/// <summary>结构化参数容器。</summary>
public sealed record VersionArguments
{
    /// <summary>游戏参数。</summary>
    public IReadOnlyList<VersionArgument>? Game { get; init; }

    /// <summary>JVM 参数。</summary>
    public IReadOnlyList<VersionArgument>? Jvm { get; init; }
}

/// <summary>一条适用规则。</summary>
/// <param name="Action"><c>allow</c> 或 <c>disallow</c>。</param>
/// <param name="Os">操作系统条件。</param>
/// <param name="Features">特性条件（如 <c>is_demo_user</c>、<c>has_custom_resolution</c>）。</param>
public sealed record VersionRule(
    string? Action,
    VersionRuleOs? Os,
    IReadOnlyDictionary<string, bool>? Features)
{
    /// <summary>是否为 allow 规则。</summary>
    public bool IsAllow => string.Equals(Action, "allow", StringComparison.OrdinalIgnoreCase);
}

/// <summary>规则里的操作系统条件。</summary>
/// <param name="Name">系统名（<c>windows</c> / <c>osx</c> / <c>linux</c>）。</param>
/// <param name="Arch">架构（<c>x86</c> / <c>x64</c> / <c>arm64</c>）。</param>
/// <param name="Version">系统版本号正则。</param>
public sealed record VersionRuleOs(string? Name, string? Arch, string? Version);

/// <summary>
/// <see cref="VersionArgument"/> 的读写：兼容「字符串 / 字符串数组 / 带规则对象」三种写法。
/// </summary>
/// <remarks>
/// 三种写法都是真实存在的：vanilla 1.20 的 <c>arguments.jvm</c> 里既有
/// <c>"-XX:+UnlockExperimentalVMOptions"</c> 这样的字符串，也有
/// <c>{"rules":[{"action":"allow","os":{"name":"osx"}}],"value":["-XstartOnFirstThread"]}</c> 这样的对象，
/// 而对象里的 <c>value</c> 又可能是 <c>"-Dfoo"</c> 或 <c>["-cp","${classpath}"]</c>。
/// 只处理其中一种，就会静默丢掉参数（表现为"游戏起不来但看起来一切正常"）。
/// </remarks>
public sealed class VersionArgumentConverter : JsonConverter<VersionArgument>
{
    /// <inheritdoc />
    public override VersionArgument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return VersionArgument.Plain(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // 上游直接把 value 数组摊平进了参数列表。
            return new VersionArgument(ReadStringArray(ref reader), null);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"参数项只能是字符串、数组或对象，实际是 {reader.TokenType}。");
        }

        List<VersionRule>? rules = null;
        List<string>? values = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            reader.Read();

            if (string.Equals(property, "rules", StringComparison.OrdinalIgnoreCase))
            {
                rules = ReadRules(ref reader);
                continue;
            }

            if (string.Equals(property, "value", StringComparison.OrdinalIgnoreCase))
            {
                values = reader.TokenType switch
                {
                    JsonTokenType.String => [reader.GetString() ?? string.Empty],
                    JsonTokenType.StartArray => ReadStringArray(ref reader),
                    _ => throw new JsonException($"参数项的 value 只能是字符串或数组，实际是 {reader.TokenType}。"),
                };

                continue;
            }

            reader.Skip();
        }

        return new VersionArgument(values ?? [], rules);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VersionArgument value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (!value.IsConditional && value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
            return;
        }

        writer.WriteStartObject();

        if (value.IsConditional)
        {
            writer.WritePropertyName("rules");
            writer.WriteStartArray();

            foreach (var rule in value.Rules!)
            {
                writer.WriteStartObject();
                writer.WriteString("action", rule.Action);

                if (rule.Os is not null)
                {
                    writer.WriteStartObject("os");
                    if (rule.Os.Name is not null)
                    {
                        writer.WriteString("name", rule.Os.Name);
                    }

                    if (rule.Os.Arch is not null)
                    {
                        writer.WriteString("arch", rule.Os.Arch);
                    }

                    if (rule.Os.Version is not null)
                    {
                        writer.WriteString("version", rule.Os.Version);
                    }

                    writer.WriteEndObject();
                }

                if (rule.Features is { Count: > 0 })
                {
                    writer.WriteStartObject("features");
                    foreach (var feature in rule.Features)
                    {
                        writer.WriteBoolean(feature.Key, feature.Value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WritePropertyName("value");
        if (value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var item in value.Values)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static List<string> ReadStringArray(ref Utf8JsonReader reader)
    {
        List<string> values = [];

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                values.Add(reader.GetString() ?? string.Empty);
                continue;
            }

            // 数组里嵌对象（少见但存在）：按嵌套项继续解析，取它的实参。
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var nested = new VersionArgumentConverter().Read(ref reader, typeof(VersionArgument), new JsonSerializerOptions());
                values.AddRange(nested.Values);
            }
        }

        return values;
    }

    private static List<VersionRule> ReadRules(ref Utf8JsonReader reader)
    {
        List<VersionRule> rules = [];

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return rules;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                continue;
            }

            string? action = null;
            VersionRuleOs? os = null;
            Dictionary<string, bool>? features = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var property = reader.GetString();
                reader.Read();

                switch (property?.ToUpperInvariant())
                {
                    case "ACTION":
                        action = reader.GetString();
                        break;

                    case "OS":
                        os = ReadOs(ref reader);
                        break;

                    case "FEATURES":
                        features = ReadFeatures(ref reader);
                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }

            rules.Add(new VersionRule(action, os, features));
        }

        return rules;
    }

    private static VersionRuleOs ReadOs(ref Utf8JsonReader reader)
    {
        string? name = null;
        string? arch = null;
        string? version = null;

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new VersionRuleOs(null, null, null);
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            reader.Read();

            switch (property?.ToUpperInvariant())
            {
                case "NAME":
                    name = reader.GetString();
                    break;

                case "ARCH":
                    arch = reader.GetString();
                    break;

                case "VERSION":
                    version = reader.GetString();
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return new VersionRuleOs(name, arch, version);
    }

    private static Dictionary<string, bool> ReadFeatures(ref Utf8JsonReader reader)
    {
        Dictionary<string, bool> features = [];

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return features;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            reader.Read();

            if (property is not null && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                features[property] = reader.GetBoolean();
                continue;
            }

            reader.Skip();
        }

        return features;
    }
}
