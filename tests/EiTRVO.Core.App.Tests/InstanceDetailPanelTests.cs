using System.IO;
using System.Text.RegularExpressions;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth.Sample;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-e-3c：实例属性面板与账户行的**投影**。
/// </summary>
/// <remarks>
/// 这一步把内核里剩下的展示字段（游戏版本 / Java / 账户 / 模组摘要 / 时长 / 窗口 / 内存 /
/// 退出码 / 隔离目录 / 皮肤）也换成了「键 + 参数」，于是它们**不能再直接绑到界面上** ——
/// 字符串内插一个模板只会调 <c>ToString()</c>，拿到的是键、参数全丢
/// （AGENTS.md §5.3 第 ⑨ 条）。这组用例钉两件事：
/// <list type="number">
/// <item>投影真的把每一个字段都拼成了句子（中文一份、英文一份 —— 只有英文那份能证明
/// 它走的不是"键就是中文原文"的假象）；</item>
/// <item>**过去真的漏过的两格**：p13 共用的属性面板（那两个包装属性只加在了 p03 的
/// ViewModel 上，于是删除页一直是空的）与 p15 的账户行（与 p11 不同，它直接绑了内核记录）。</item>
/// </list>
/// <para>
/// 这个类会切全进程的语言单例，所以必须与其它测试串行（本项目默认方法级并行）。
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class InstanceDetailPanelTests
{
    /// <summary>每个用例开始前复位成简体中文。</summary>
    [TestInitialize]
    public void ResetToChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    /// <summary>用例结束后也复位。</summary>
    [TestCleanup]
    public void RestoreChinese() => Loc.Service.SetLanguage(LocalizationService.Chinese);

    [TestMethod]
    public void ThePanelRendersEveryTemplateFieldInChinese()
    {
        var panel = InstanceDetailPanelViewModel.From(new SampleInstanceCatalog().GetDetail("1.20.4-Forge"));

        Assert.AreEqual("1.20.4-Forge", panel.Name);
        Assert.AreEqual("1.20.4", panel.GameVersion);
        Assert.AreEqual("Forge 49.0.30", panel.LoaderDisplay);
        Assert.AreEqual("17.0.9 (64 位 · Temurin)", panel.JavaRuntime);
        Assert.AreEqual("17.0.9 (64 位)", panel.JavaRuntimeSummary);
        Assert.AreEqual("今天 16:01", panel.LastLaunched);
        Assert.AreEqual("已完成 今天 15:52", panel.ResourceVerification);
        Assert.AreEqual("Steve (Microsoft)", panel.Account);
        Assert.AreEqual("4096 MB", panel.MemoryText);
        Assert.AreEqual("87 个 (已启用 84)", panel.ModSummary);
        Assert.AreEqual("126 小时 31 分", panel.PlayTime);
        Assert.AreEqual("0 (正常退出)", panel.LastExitCodeText);
        Assert.AreEqual("1920 × 1080 窗口化", panel.WindowMode);
        Assert.AreEqual("已启用", panel.IsolatedDirectoryText);
    }

    /// <summary>
    /// 同一个面板在英文下也要是英文 —— 这才是"它拼过"的证据。
    /// </summary>
    /// <remarks>
    /// 只断言中文那一份是不够的：模板的**键就是简体中文原文**，于是"没拼、直接把键显示出来"
    /// 在中文界面上看起来完全正常（这正是 e-3b 那个静默 bug 活下来的原因：
    /// <c>Account.Meta</c> 里内插模板，p11 显示成 <c>今天 {0}</c>，而当时没人看得出来）。
    /// </remarks>
    [TestMethod]
    public void ThePanelFollowsTheLanguageSwitch()
    {
        Loc.Service.SetLanguage(LocalizationService.English);

        var panel = InstanceDetailPanelViewModel.From(new SampleInstanceCatalog().GetDetail("1.20.4-Forge"));

        Assert.AreEqual("1.20.4", panel.GameVersion);
        Assert.AreEqual("17.0.9 (64-bit · Temurin)", panel.JavaRuntime);
        Assert.AreEqual("87 (84 enabled)", panel.ModSummary);
        Assert.AreEqual("126 h 31 min", panel.PlayTime);
        Assert.AreEqual("0 (clean exit)", panel.LastExitCodeText);
        Assert.AreEqual("1920 × 1080 Windowed", panel.WindowMode);
        Assert.AreEqual("4096 MB", panel.MemoryText);
        Assert.AreEqual("Enabled", panel.IsolatedDirectoryText);
        Assert.AreEqual("Today 16:01", panel.LastLaunched);
        Assert.AreEqual("Steve (Microsoft)", panel.Account, "账户名是数据，两种语言都一样。");

        // 任何一格露出 `{0}` 都说明那个字段没经过投影。
        foreach (var value in new[]
                     {
                         panel.GameVersion,
                         panel.JavaRuntime,
                         panel.JavaRuntimeSummary,
                         panel.LastLaunched,
                         panel.ResourceVerification,
                         panel.ModSummary,
                         panel.PlayTime,
                         panel.WindowMode,
                         panel.MemoryText,
                         panel.LastExitCodeText,
                         panel.IsolatedDirectoryText,
                     })
        {
            Assert.IsFalse(value.Contains("{0}", StringComparison.Ordinal), $"这一格还是模板键：{value}");
        }
    }

    [TestMethod]
    public void ThePlaceholderPanelRendersSentencesNotKeys()
    {
        var empty = InstanceDetailPanelViewModel.Empty;

        // 名字那句"（没有实例）"是**文案**，由投影给（内核占位详情的 Name 是空串：名字是数据）。
        Assert.AreEqual("（没有实例）", empty.Name);
        Assert.AreEqual("未指定", empty.GameVersion);
        Assert.AreEqual("自动匹配", empty.JavaRuntime);
        Assert.AreEqual("未绑定", empty.Account);
        Assert.AreEqual("从未启动", empty.LastLaunched);
        Assert.AreEqual("从未校验", empty.ResourceVerification);
        Assert.AreEqual("0 个", empty.ModSummary);
        Assert.AreEqual("0 分", empty.PlayTime);
        Assert.AreEqual("0 MB", empty.MemoryText);
        Assert.AreEqual("0 (正常退出)", empty.LastExitCodeText);
        Assert.AreEqual("—", empty.WindowMode);
        Assert.AreEqual("未启用", empty.IsolatedDirectoryText);
    }

    [TestMethod]
    public void ThePlaceholderPanelAlsoFollowsTheLanguage()
    {
        Loc.Service.SetLanguage(LocalizationService.English);

        var empty = InstanceDetailPanelViewModel.Empty;

        Assert.AreEqual("(no instance)", empty.Name);
        Assert.AreEqual("Unspecified", empty.GameVersion);
        Assert.AreEqual("Automatic", empty.JavaRuntime);
        Assert.AreEqual("Not bound", empty.Account);
        Assert.AreEqual("0 items", empty.ModSummary);
        Assert.AreEqual("0 min", empty.PlayTime);
        Assert.AreEqual("0 (clean exit)", empty.LastExitCodeText);
        Assert.AreEqual("Disabled", empty.IsolatedDirectoryText);
    }

    /// <summary>
    /// p13 与 p03 共用 <c>InstanceOverviewPanel</c>，所以两页的属性必须一模一样。
    /// </summary>
    /// <remarks>
    /// 这一条是**回归**：e-3b 把面板的那两格从 <c>Detail.LastLaunched</c> 换成了
    /// ViewModel 上的包装属性，而包装属性只加在 p03 的 ViewModel 上 ——
    /// 于是删除实例页的「上次启动」与「资源校验」两格一直是**空白**，没有任何检查会说话
    /// （XAML 里绑一个不存在的属性是静默的）。现在两页绑的是同一个投影，这条用例钉住它。
    /// </remarks>
    [TestMethod]
    public void TheDeletePageShowsTheSameTwoRowsAsTheDetailPage()
    {
        var instances = new SampleInstanceCatalog();
        var navigation = new FakeNavigationService();
        var detailPage = new InstanceDetailViewModel(instances, new SampleEventLog(), new FakeShellService());
        var deletePage = new DeleteInstanceViewModel(
            instances,
            new SampleEventLog(),
            new FakeShellService(),
            new DeleteConfirmDialogViewModel(instances, navigation));

        Assert.AreEqual(detailPage.Detail.LastLaunched, deletePage.Detail.LastLaunched);
        Assert.AreEqual(detailPage.Detail.ResourceVerification, deletePage.Detail.ResourceVerification);
        Assert.AreEqual("今天 16:01", deletePage.Detail.LastLaunched);
        Assert.AreEqual("已完成 今天 15:52", deletePage.Detail.ResourceVerification);
    }

    /// <summary>
    /// p15 的「已有账户」列表与 p11 一样，必须是**投影**而不是内核记录。
    /// </summary>
    /// <remarks>
    /// 回归：<c>AccountLoginViewModel.Accounts</c> 之前直接暴露内核的
    /// <c>IReadOnlyList&lt;Account&gt;</c>，而 XAML 绑的是 <c>{Binding Meta}</c> ——
    /// e-3b 把 <c>Account.Meta</c> 换成模板之后，这一页显示的就是**键**
    /// （`Microsoft 正版 · {0} · 上次使用`）。中文界面上那句看起来"有点像中文"，
    /// 所以只能靠断言 <c>{0}</c> 不在结果里来抓。
    /// </remarks>
    [TestMethod]
    public void TheAddAccountRowsShowSentencesNotKeys()
    {
        var navigation = new FakeNavigationService();
        var shell = new FakeShellService();
        var page = new AccountLoginViewModel(
            new SampleAccountCatalog(),
            new AddAccountDialogViewModel(new FakeMicrosoftSignIn(), new FakeDeviceCodeFlow(), shell, navigation),
            navigation);

        Assert.AreEqual(2, page.Accounts.Count);

        foreach (var row in page.Accounts)
        {
            Assert.IsFalse(row.Meta.Contains("{0}", StringComparison.Ordinal), $"账户行显示的还是模板键：{row.Meta}");
        }

        // 与 p11 的行摘要用的是同一把尺子。
        var accountsPage = TestViewModels.Accounts();
        CollectionAssert.AreEqual(
            accountsPage.Accounts.Select(static row => row.Meta).ToArray(),
            page.Accounts.Select(static row => row.Meta).ToArray());
    }

    /// <summary>
    /// XAML 里每一个 <c>{Binding Detail.xxx}</c> 都必须在投影上真的存在。
    /// </summary>
    /// <remarks>
    /// 这条是 p13 那个空白格的机械门禁：**XAML 绑一个不存在的属性不会报任何错**
    /// （编译通过、门禁通过、属性扫描也通过 —— 它只是安静地留空）。
    /// 四个页面共用同一份投影之后，"谁忘了加包装属性"这类错误在类型层面就不成立了，
    /// 但"XAML 里打错一个名字"仍然只能靠这条来抓。
    /// </remarks>
    [TestMethod]
    public void EveryDetailBindingPathExistsOnTheProjection()
    {
        var available = typeof(InstanceDetailPanelViewModel)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var root = Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App");
        var pattern = new Regex(@"\{Binding\s+Detail\.([A-Za-z_][A-Za-z0-9_]*)");
        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                || file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        // 扫描器自己也要能被证明扫到了东西（否则这条用例会安静地永远通过）。
        Assert.IsTrue(used.Count >= 15, $"只扫到 {used.Count} 个 Detail 绑定路径 —— 扫描逻辑失效了。");

        var missing = used.Where(name => !available.Contains(name)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些绑定路径在投影上不存在（界面上会静默留空）：" + string.Join(" / ", missing));
    }

    /// <summary>仓库根（从测试输出目录往上找 <c>EiTRVO.Core.sln</c>）。</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到仓库根（从 {AppContext.BaseDirectory} 往上找 EiTRVO.Core.sln）。");
    }
}
