using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace EiTRVO.Core.App.Localization;

/// <summary>
/// 界面多语言的唯一实现。
/// <para>
/// 资源用标准 <c>.resx</c>（<c>Resources/Strings.resx</c> 为简体中文，
/// <c>Strings.en.resx</c> 为英文），读取用 BCL 的 <see cref="ResourceManager"/> ——
/// 不引任何第三方本地化库，满足 AGENTS.md §3。
/// </para>
/// <para>
/// <b>键就是简体中文原文</b>。这样做的好处是「不可能漏译」：
/// 转换脚本按原文建键，翻译文件只要少一条，键集一致性单测立刻失败；
/// 而且代码里读到的仍然是原文，不需要在脑中做「键 → 文案」的二次映射。
/// 代价是改动中文原文等于改键，仅适用于本仓库这种「文案稳定、由开发直接维护两种语言」的场景。
/// </para>
/// <para>
/// 实现了 <see cref="INotifyPropertyChanged"/>：语言切换时对索引器发出
/// <c>"Item[]"</c> 通知，所有 <c>{loc:Text …}</c> 绑定会立刻重新求值，
/// 不需要重启窗口。
/// </para>
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    /// <summary>中文（默认语言）。</summary>
    public const string Chinese = "zh-Hans";

    /// <summary>英文。</summary>
    public const string English = "en";

    private static readonly ResourceManager Manager =
        new("EiTRVO.Core.App.Resources.Strings", typeof(LocalizationService).Assembly);

    private static readonly LanguageInfo[] AllLanguages =
    [
        // 语言名一律用**它自己的语言**书写（简体中文 / English），不随当前语言变化 ——
        // 否则界面切成英文之后，用户就找不到自己刚才是从哪一项切过来的了。
        new(Chinese, "简体中文"),
        new(English, "English"),
    ];

    private CultureInfo _culture = CultureInfo.GetCultureInfo(Chinese);

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public string CurrentLanguage => _culture.Name;

    /// <inheritdoc />
    public CultureInfo CurrentCulture => _culture;

    /// <inheritdoc />
    public IReadOnlyList<LanguageInfo> Languages => AllLanguages;

    /// <inheritdoc />
    public string this[string text] => Translate(text);

    /// <inheritdoc />
    /// <remarks>
    /// 空值是**"没有语言可切"**而不是"切换语言"：WPF 的双向绑定（<c>SelectedValue</c> / <c>SelectedItem</c>）
    /// 会在选项重建时写 <see langword="null"/>，而早先 <see cref="Normalize"/> 会把 null 归一到中文 ——
    /// 结果是界面被**悄悄切回中文**（用户选了 English 又点一下别处就变回来）。这里直接忽略空值。
    /// </remarks>
    public void SetLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        var normalized = Normalize(language);
        if (string.Equals(_culture.Name, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _culture = CultureInfo.GetCultureInfo(normalized);

        // CultureInfo.CurrentUICulture 也一起改：{loc:Text} 之外的零星查找（比如异常里的
        // 格式化）会跟着走，而且将来接真实内核时后端也能拿到同一个值。
        CultureInfo.CurrentUICulture = _culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));

        // 索引器绑定靠这个名字刷新；WPF 认 "Item[]" 为「所有索引器取值都变了」。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <inheritdoc />
    public string Translate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 查不到就原样返回：中文模式下这本来就是正确结果；
        // 英文模式下会露出中文原文，一眼就能看出漏译了哪一条。
        return Manager.GetString(text, _culture) ?? text;
    }

    /// <inheritdoc />
    public string Translate(string format, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var template = Translate(format);
        return arguments.Length == 0
            ? template
            : string.Format(_culture, template, arguments);
    }

    /// <summary>资源里已登记的键（中英各一份，键集必须一致）。</summary>
    /// <param name="language">语言标签。</param>
    /// <returns>该语言的全部键。</returns>
    public static IReadOnlyList<string> KeysOf(string language)
    {
        var set = Manager.GetResourceSet(CultureInfo.GetCultureInfo(Normalize(language)), true, false);
        if (set is null)
        {
            return [];
        }

        List<string> keys = [];
        foreach (DictionaryEntry entry in set)
        {
            if (entry.Key is string key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>该语言标签是否受支持（用于启动语言解析，避免把垃圾值当成有效选择）。</summary>
    /// <param name="language">语言标签。</param>
    /// <returns>受支持返回 <see langword="true"/>。</returns>
    public static bool IsSupported(string? language) =>
        language is not null
        && AllLanguages.Any(info => string.Equals(info.Tag, language, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string language) =>
        string.IsNullOrWhiteSpace(language) || language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;
}