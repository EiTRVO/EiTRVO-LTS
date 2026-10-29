using System.IO;
using System.Text.RegularExpressions;
using EiTRVO.Core.App.Controls;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 界面多语言。
/// <para>
/// 这里钉住三件事：① 中英两份资源**键集完全一致**（少一条就失败，防止漏译）；
/// ② 英文里不允许残留中文（语言名「简体中文」除外）；③ 切语言之后界面文案真的会变，
/// 而且是**实时**的（对索引器发 <c>"Item[]"</c> 通知，页面与左导航跟着刷新）。
/// </para>
/// <para>
/// 注意：<see cref="Loc.Service"/> 是全进程单例，所以每个用例前后都把语言复位成简体中文，
/// 否则会污染断言中文文案的其它测试。
/// </para>
/// </summary>
[TestClass]
// 这个类会改全进程的语言单例，必须与其它测试串行执行（本项目默认开启方法级并行）。
[DoNotParallelize]
public sealed partial class LocalizationTests
{
    /// <summary>
    /// 故意保持原样的条目。
    /// </summary>
    /// <remarks>
    /// ① 语言名按惯例用该语言自己书写、品牌名不翻译（「简体中文」/「Modrinth」）。
    /// ② **纯数据键**（M6-e-2a 起）：键就是"这一段内容全是数据、没有可翻译的词"的写法 ——
    /// 例如 `{0} MB`（内存值）、`{0} · {1}%`（文件计数 + 百分比）、`{0}` 与 `?`。
    /// 它们的中英译文**本来就该一样**，不是漏翻；把中文当参数传已被 <c>UserTextKeyTests</c> 第 ④ 条堵住，
    /// 所以这是"数据原样显示"的唯一合法出口。
    /// </remarks>
    private static readonly string[] UntranslatedByDesign =
        ["简体中文", "Modrinth", "{0}", "{0} MB", "{0} · {1}%", "?", "—", "{0} · {1}", "{0} {1}"];

    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位，避免影响其它测试类。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void ChineseAndEnglishResourcesHaveExactlyTheSameKeys()
    {
        var chinese = LocalizationService.KeysOf(LocalizationService.Chinese).Order(StringComparer.Ordinal).ToArray();
        var english = LocalizationService.KeysOf(LocalizationService.English).Order(StringComparer.Ordinal).ToArray();

        Assert.IsTrue(chinese.Length > 200, "资源条目太少，说明提取脚本没有把界面文案都收进来。");

        var missingInEnglish = chinese.Except(english, StringComparer.Ordinal).ToArray();
        var extraInEnglish = english.Except(chinese, StringComparer.Ordinal).ToArray();

        Assert.AreEqual(
            0,
            missingInEnglish.Length,
            "英文资源缺少这些键：" + string.Join(" / ", missingInEnglish.Take(10)));
        Assert.AreEqual(
            0,
            extraInEnglish.Length,
            "英文资源多出这些键：" + string.Join(" / ", extraInEnglish.Take(10)));
    }

    [TestMethod]
    public void EnglishTranslationsDoNotFallBackToChinese()
    {
        List<string> untranslated = [];

        foreach (var key in LocalizationService.KeysOf(LocalizationService.Chinese))
        {
            var english = TranslateAs(LocalizationService.English, key);

            // 故意保持原样的条目：语言名按惯例用该语言自己书写、品牌/产品名不翻译
            // （事件表的「来源」列里 Modrinth 就是一个品牌，它与中文键本来就该一样）。
            if (UntranslatedByDesign.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (string.Equals(english, key, StringComparison.Ordinal))
            {
                untranslated.Add(key + "  (未翻译)");
            }
            else if (english.Any(IsCjk))
            {
                untranslated.Add(key + "  ->  " + english);
            }
        }

        Assert.AreEqual(
            0,
            untranslated.Count,
            "以下条目在英文里仍是中文或未翻译：" + Environment.NewLine + string.Join(Environment.NewLine, untranslated.Take(15)));
    }

    [TestMethod]
    public void SwitchingLanguageChangesTheTextAndNotifiesIndexerBindings()
    {
        List<string> notified = [];
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            notified.Add(e.PropertyName ?? string.Empty);

        var service = Loc.Service;
        service.PropertyChanged += Handler;
        try
        {
            Assert.AreEqual("本地实例", service.Translate("本地实例"));

            service.SetLanguage(LocalizationService.English);

            Assert.AreEqual("Local instances", service.Translate("本地实例"));
            Assert.AreEqual(LocalizationService.English, service.CurrentLanguage);

            // XAML 的 {loc:Text} 是绑到索引器上的，靠这个名字刷新。
            CollectionAssert.Contains(notified, "Item[]");
        }
        finally
        {
            service.PropertyChanged -= Handler;
        }
    }

    [TestMethod]
    public void PageTitlesFollowTheLanguage()
    {
        var instances = TestViewModels.Instances();

        Assert.AreEqual("本地实例", instances.Title);

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual("Local instances", instances.Title);
        Assert.AreEqual("Local instances", instances.Breadcrumb[0].Text);
    }

    [TestMethod]
    public void LanguageSelectionInJavaSettingsSwitchesImmediately()
    {
        var page = TestViewModels.JavaSettings();

        // 语言名用各自的语言书写，所以两种模式下选项都一样。
        CollectionAssert.AreEqual(
            new[] { "简体中文", "English" },
            page.LanguageOptions.Select(static option => option.Text).ToArray());

        page.Language = LocalizationService.English;

        Assert.AreEqual(LocalizationService.English, Loc.Service.CurrentLanguage);
        Assert.AreEqual("Global default + instance override", page.SectionSubtitle);
    }

    [TestMethod]
    public void EnglishAndChineseAreTheOnlyOfferedLanguages()
    {
        CollectionAssert.AreEqual(
            new[] { LocalizationService.Chinese, LocalizationService.English },
            Loc.Service.Languages.Select(static info => info.Tag).ToArray());
    }

    [TestMethod]
    public void UnknownLanguageFallsBackToChinese()
    {
        Loc.Service.SetLanguage("fr-FR");

        Assert.AreEqual(LocalizationService.Chinese, Loc.Service.CurrentLanguage);
        Assert.AreEqual("关于", Loc.Service.Translate("关于"));
    }

    [TestMethod]
    public void StartupLanguagePrefersTheSavedChoiceOverTheConfiguredDefault()
    {
        var settings = new SampleSettingsCatalog();

        // 没存过 → 用 appsettings.json 的值。
        Assert.AreEqual(LocalizationService.English, LocalizationStartup.Resolve(settings, LocalizationService.English));

        // 存过 → 存过的优先（部署默认值不该覆盖用户的选择）。
        settings.SaveGlobal(settings.GetGlobal() with { Language = LocalizationService.Chinese });
        Assert.AreEqual(LocalizationService.Chinese, LocalizationStartup.Resolve(settings, LocalizationService.English));
    }

    [TestMethod]
    public void StartupLanguageFallsBackToChineseWhenNothingIsUsable()
    {
        var settings = new SampleSettingsCatalog();

        Assert.AreEqual(LocalizationService.Chinese, LocalizationStartup.Resolve(settings, null));
        Assert.AreEqual(LocalizationService.Chinese, LocalizationStartup.Resolve(settings, string.Empty));
        Assert.AreEqual(LocalizationService.Chinese, LocalizationStartup.Resolve(settings, "fr-FR"));

        settings.SaveGlobal(settings.GetGlobal() with { Language = "klingon" });
        Assert.AreEqual(LocalizationService.Chinese, LocalizationStartup.Resolve(settings, "fr-FR"));
    }

    [TestMethod]
    public void ChoosingEnglishInJavaSettingsPersistsItForTheNextStart()
    {
        var settings = new SampleSettingsCatalog();
        var page = TestViewModels.JavaSettings(settings);

        // 从「设置目录」这一侧看：初始语言就是 appsettings 给的那份，页面不应显示成「有未保存的修改」。
        Assert.IsFalse(page.IsDirty);

        page.Language = LocalizationService.English;

        // 实时生效 + 立刻可保存（IsDirty 必须为真，否则「保存设置」按钮是灰的，选择就存不下来）。
        Assert.AreEqual(LocalizationService.English, Loc.Service.CurrentLanguage);
        Assert.IsTrue(page.IsDirty);
        Assert.IsTrue(page.SaveCommand.CanExecute(null));
        page.SaveCommand.Execute(null);

        // 存过之后：不再有未保存修改，而且下次启动会采用英文。
        Assert.IsFalse(page.IsDirty);
        Assert.AreEqual(LocalizationService.English, LocalizationStartup.Resolve(settings, LocalizationService.Chinese));
    }

    /// <summary>
    /// 界面上用到的每一个文案键都必须真的在资源里。
    /// <para>
    /// 这条防的是"键写错了"这一类**静默**故障：<see cref="Loc"/> 找不到键时会把键本身当译文返回，
    /// 于是中文界面看起来完全正常（键就是中文原文），只有切成英文才会露出中文。
    /// 之前确实漏过一个 <c>Subtitle</c>，靠人工逐页看是看不出来的 —— 所以让测试来看。
    /// </para>
    /// </summary>
    [TestMethod]
    public void EveryTextKeyUsedInXamlOrViewModelsExistsInTheResources()
    {
        var appRoot = FindAppSourceRoot();
        var available = LocalizationService.KeysOf(LocalizationService.Chinese).ToHashSet(StringComparer.Ordinal);
        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateSources(appRoot, "*.xaml"))
        {
            foreach (Match match in XamlTextKey().Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups[1].Value.Trim());
            }
        }

        foreach (var file in EnumerateSources(appRoot, "*.cs"))
        {
            foreach (Match match in CodeTextKey().Matches(File.ReadAllText(file)))
            {
                used.Add(Unescape(match.Groups[1].Value));
            }
        }

        Assert.IsTrue(used.Count > 200, $"只扫到 {used.Count} 个文案键，说明扫描逻辑已经失效了。");

        var missing = used.Where(key => !available.Contains(key)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些文案键不在资源里（切成英文时会直接显示中文键）：" + string.Join(" / ", missing.Take(10)));
    }

    private static string FindAppSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
            {
                return Path.Combine(directory.FullName, "src", "EiTRVO.Core.App");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（EiTRVO.Core.sln）。");
    }

    private static IEnumerable<string> EnumerateSources(string root, string pattern) =>
        Directory
            .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(static path => !path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                                  && !path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase));

    /// <summary>把 C# 字面量里的转义还原成真正的键。</summary>
    private static string Unescape(string literal) => literal
        .Replace("\\\"", "\"", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary><c>{loc:Text 原文}</c>。</summary>
    [GeneratedRegex(@"\{loc:Text\s+([^}]+)\}")]
    private static partial Regex XamlTextKey();

    /// <summary><c>Loc.T("原文"…)</c>。</summary>
    [GeneratedRegex(@"Loc\.T\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeTextKey();

    /// <summary>
    /// 控件的文案**不得**写进依赖属性默认值。
    /// <para>
    /// 依赖属性的默认值只在类型初始化时求值一次，所以
    /// <c>new FrameworkPropertyMetadata(Loc.T("任务"))</c> 等于把"那一刻的语言"烙进控件：
    /// 用户在「Java 与设置」里切成 English 之后，13 处「任务」按钮会继续显示中文。
    /// 而以英文**启动**时（类型初始化时语言已是 en）反而看不出问题 —— 截图是蒙对的，
    /// 所以这条只能靠测试守：默认值必须是空串，文案一律由使用点的
    /// <c>Text="{loc:Text 任务}"</c> 提供（<see cref="TextExtension"/> 返回的是绑定，能实时刷新）。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ControlTextDefaultsMustNotBakeInATranslation()
    {
        Assert.AreEqual(
            string.Empty,
            TaskButton.TextProperty.DefaultMetadata.DefaultValue,
            "TaskButton 的默认文字必须留空，否则切换语言后不会跟着变。");

        List<string> offenders = [];

        foreach (var file in EnumerateSources(FindAppSourceRoot(), "*.xaml"))
        {
            foreach (Match match in TaskButtonWithoutText().Matches(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetFileName(file) + ": " + match.Value.Trim());
            }
        }

        Assert.AreEqual(
            0,
            offenders.Count,
            "这些 TaskButton 没有显式文案（会显示空按钮，或退回到控件默认值）："
            + string.Join(" / ", offenders));
    }

    /// <summary>没有显式 <c>Text=</c> 的 <c>TaskButton</c> 使用点。</summary>
    [GeneratedRegex(@"<c:TaskButton(?![^>]*\bText=)[^>]*>")]
    private static partial Regex TaskButtonWithoutText();

    private static string TranslateAs(string language, string key)
    {
        var previous = Loc.Service.CurrentLanguage;
        Loc.Service.SetLanguage(language);
        var text = Loc.Service.Translate(key);
        Loc.Service.SetLanguage(previous);
        return text;
    }

    private static bool IsCjk(char c) => c is >= '\u4e00' and <= '\u9fff';
}
