using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 绑定写进来 <see langword="null"/> 的那一类崩溃。
/// <para>
/// <b>真实缺陷（2026-10-04 实机）</b>：在 p10 切换语言时抛
/// <c>NullReferenceException</c>（<c>JavaSettingsViewModel.Build()</c> 里的 <c>JavaOverride.Length</c>）。
/// 原因不是用户输入：p10/p17 的「实例 Java 覆盖」是 <c>SelectedValue</c> 双向绑定的 ComboBox，
/// 而它第一项的显示文本**随语言变**（"使用全局默认…"）—— 于是切换语言会重建选项、
/// WPF 把 <c>SelectedValue</c> 清成 null 并**写回这个 string 属性**（生成器不拦、编译器以为不可能），
/// 紧接着 <c>IsDirty</c> → <c>Build()</c> 就踩上去了。
/// </para>
/// <para>
/// 这些用例直接用 <c>null!</c> 模拟"绑定写了 null"—— 这正是 WPF 做的事，
/// 而且它比"点一次界面"更稳：不需要 UI 线程，也不会因为渲染时序而偶发。
/// 语言相关的两条与 <c>LocalizationTests</c> 同理标了 <see cref="DoNotParallelizeAttribute"/>：
/// 语言是**全进程单例**，并行跑会互相踩。
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BindingWritesNullTests
{
    [TestInitialize]
    public void UseAStableLanguage() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void TheSettingsPageSurvivesANullWrittenIntoTheJavaOverride()
    {
        var page = TestViewModels.JavaSettings();

        page.JavaOverride = null!;

        // 归一成空串（"使用全局默认"），而不是把 null 留在属性里等着被解引用。
        Assert.AreEqual(string.Empty, page.JavaOverride);

        // 崩溃现场就是这两条读取路径：Build()（IsDirty 会调用）与选项列表。
        Assert.IsFalse(page.IsDirty);
        Assert.AreEqual("使用全局默认（自动按版本匹配）", page.JavaOverrideOptions[0].Text);
    }

    [TestMethod]
    public void TheInstanceSettingsPageSurvivesANullWrittenIntoTheJavaOverride()
    {
        var page = TestViewModels.InstanceSettings();

        page.JavaOverride = null!;

        Assert.AreEqual(string.Empty, page.JavaOverride);
        Assert.IsFalse(page.IsDirty);

        // RequiredJavaHint 会走 EffectiveJavaOverride（另一条解引用路径）。
        Assert.IsNotNull(page.RequiredJavaHint);
    }

    [TestMethod]
    public void ANullLanguageWriteKeepsTheCurrentLanguageInsteadOfSwitchingBack()
    {
        // 先切到英文：早先的缺陷是 Normalize(null) 把界面**悄悄切回中文**，
        // 所以只有当前语言不是中文时这条才有意义。
        Loc.Service.SetLanguage(LocalizationService.English);

        var page = TestViewModels.JavaSettings();
        Assert.AreEqual(LocalizationService.English, page.Language, "页面构造时应当取当前语言。");

        page.Language = null!;

        Assert.AreEqual(LocalizationService.English, Loc.Service.CurrentLanguage, "空值不该被当成「切换到默认语言」。");
        Assert.AreEqual(LocalizationService.English, page.Language, "属性本身也要写回当前语言，否则下拉框会显示空白。");
    }

    [TestMethod]
    public void TheLocalizationServiceIgnoresAnEmptyLanguageTag()
    {
        var before = Loc.Service.CurrentLanguage;

        Loc.Service.SetLanguage(null!);
        Assert.AreEqual(before, Loc.Service.CurrentLanguage);

        Loc.Service.SetLanguage("   ");
        Assert.AreEqual(before, Loc.Service.CurrentLanguage);
    }
}
