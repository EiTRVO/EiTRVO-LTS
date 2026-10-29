using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-e-3f：内容表（p07 模组备注、p07 统计、p09 资源包/存档）与 Java 位数（p10）
/// 在**英文**界面下必须真的是英文。
/// </summary>
/// <remarks>
/// 这几处的键（`需要 {0}` / `生存` / `{0} 位`）与前面几步同理：**键就是简体中文原文**，
/// 所以"没拼、直接把键显示出来"在中文界面上完全正常，只有英文那一份能证明它真的走过投影
/// （AGENTS.md §5.3 第 ⑨ / ⑩ 条）。
/// <para>
/// 本类会切全进程的语言单例，所以必须与其它测试串行（项目默认方法级并行）。
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ContentTextTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void TheModTableNotesFollowTheLanguageSwitch()
    {
        var mods = new FakeContentCatalog().GetMods("1.20.4-Forge");

        string NoteOf(string name) => ModRowViewModel.From(mods.Single(m => m.Name == name)).Note;

        Assert.AreEqual("需要 flywheel", NoteOf("Create"));
        Assert.AreEqual("最新 0.6.5", NoteOf("Sodium"));
        Assert.AreEqual("与 Sodium 冲突", NoteOf("OptiFine（作为模组）"));
        Assert.AreEqual("不在 Modrinth 收录", NoteOf("未收录模组（3 个）"));
        Assert.AreEqual(string.Empty, NoteOf("Flywheel"), "没有备注时内核给 null、投影给空串。");

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual("Requires flywheel", NoteOf("Create"));
        Assert.AreEqual("Latest 0.6.5", NoteOf("Sodium"));
        Assert.AreEqual("Conflicts with Sodium", NoteOf("OptiFine（作为模组）"));
        Assert.AreEqual("Not on Modrinth", NoteOf("未收录模组（3 个）"));
        Assert.AreEqual(string.Empty, NoteOf("Flywheel"));
    }

    [TestMethod]
    public void TheResourcePackAndWorldSaveTextsFollowTheLanguageSwitch()
    {
        var content = new FakeContentCatalog();
        var packs = content.GetResourcePacks("1.20.4-Forge");
        var saves = content.GetWorldSaves("1.20.4-Forge");

        var pack = ResourcePackRowViewModel.From(packs[0]);
        var today = WorldSaveRowViewModel.From(saves[0]);
        var absolute = WorldSaveRowViewModel.From(saves[1]);

        Assert.AreEqual("32× · 18.4 MB · 优先级 1", pack.Detail);
        Assert.AreEqual("实体动画 · 4.2 MB · 优先级 2", ResourcePackRowViewModel.From(packs[1]).Detail);
        Assert.AreEqual("今天 16:01", today.LastPlayed);
        Assert.AreEqual("生存", today.GameMode);

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual("32× · 18.4 MB · priority 1", ResourcePackRowViewModel.From(packs[0]).Detail);
        Assert.AreEqual("Entity animations · 4.2 MB · priority 2", ResourcePackRowViewModel.From(packs[1]).Detail, "这是**另一条键**（类型词写进键里），所以整句跟着切。");
        Assert.AreEqual("Today 16:01", WorldSaveRowViewModel.From(saves[0]).LastPlayed);
        Assert.AreEqual("Survival", WorldSaveRowViewModel.From(saves[0]).GameMode);
        Assert.AreEqual("2026/9/28 22:40", absolute.LastPlayed, "绝对时间是数据（纯数据键），两种语言逐字相同。");
        Assert.AreEqual("Creative", WorldSaveRowViewModel.From(saves[1]).GameMode);
    }

    [TestMethod]
    public void TheModSummaryAndTheJavaBitsFollowTheLanguageSwitch()
    {
        var summary = ModSummaryViewModel.From(new FakeContentCatalog().GetModSummary("1.21.4-Vanilla"));
        var java = new JavaInstallation(
            ExecutablePath: @"C:\Java\jdk-17\bin\javaw.exe",
            HomeDirectory: @"C:\Java\jdk-17",
            VersionText: "17.0.9",
            MajorVersion: 17,
            Vendor: "Eclipse Temurin",
            Bits: 64);

        Assert.AreEqual(0, summary.Enabled);
        Assert.AreEqual("原版", summary.LoaderMatch);
        Assert.AreEqual("1.21.4", summary.GameVersionMatch);
        Assert.AreEqual("64 位", LocalizedText.Of(java.BitsText));
        Assert.AreEqual("—", LocalizedText.Of(new JavaInstallation("", "", "1.8.0", 8, "—", 0).BitsText), "认不出位数时不猜，写纯数据键「—」。");

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual("Vanilla", ModSummaryViewModel.From(new FakeContentCatalog().GetModSummary("1.21.4-Vanilla")).LoaderMatch);
        Assert.AreEqual("64-bit", LocalizedText.Of(java.BitsText), "数字在参数里、`位` 在键里（第 ⑩ 条）。");
    }

    /// <summary>
    /// M7-a：缺失依赖横幅原来是**设计稿的静态文案**（写死「1 个」与两个示例文件名），
    /// 现在数字与细节都来自真实内容目录 —— 所以"英文界面下这句话是不是英文"要重新钉一遍。
    /// </summary>
    [TestMethod]
    public void TheMissingDependencyBannerComesFromTheScanAndFollowsTheLanguageSwitch()
    {
        var page = new ModsViewModel(
            new FakeContentCatalog(),
            new SampleInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        Assert.IsTrue(page.HasMissingDependency);
        Assert.IsTrue(page.HasMods, "有模组时表格显示、空状态隐藏（两者共用同一个单元格）。");
        Assert.IsFalse(page.HasNoMods);
        Assert.AreEqual("1 个模组缺失依赖", page.MissingDependencyCountText);
        Assert.AreEqual("create-0.5.1f.jar 需要 flywheel-0.6.10，当前未安装", page.MissingDependencyDetail);

        Loc.Service.SetLanguage(LocalizationService.English);

        Assert.AreEqual("1 mods have missing dependencies", page.MissingDependencyCountText);
        Assert.AreEqual(
            "create-0.5.1f.jar requires flywheel-0.6.10, which is not installed",
            page.MissingDependencyDetail);
    }
}
