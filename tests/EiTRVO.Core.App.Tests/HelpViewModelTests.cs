using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.ViewModels;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 帮助与支持中心（S10-c 框架）：主题列表、前进 / 后退 / 主页、搜索过滤、「关于」入口。
/// </summary>
/// <remarks>
/// 用户明确要求这一步**只做框架、不做内容**：所以这里断言的是"窗口与导航成立"，
/// 以及"内容区如实说内容还没接入"，而不是任何帮助正文。
/// </remarks>
[TestClass]
public sealed class HelpViewModelTests
{
    [TestMethod]
    public void HelpCenter_StartsWithSevenTopicsAndTheFirstSelected()
    {
        var (help, _, _) = Create();

        Assert.AreEqual(7, help.Topics.Count);
        Assert.AreEqual(7, help.FilteredTopics.Count);
        Assert.IsNotNull(help.SelectedTopic);
        Assert.AreEqual(help.Topics[0].Key, help.SelectedTopic.Key);
        Assert.IsFalse(help.BackCommand.CanExecute(null), "刚打开时没有可后退的历史。");
        Assert.IsFalse(help.ForwardCommand.CanExecute(null));
    }

    [TestMethod]
    public void HelpCenter_ShowsOnlyTheQuickStartTopic()
    {
        var (help, _, _) = Create();

        Assert.AreEqual(1, help.DisplayTopics.Count, "2026-10-25 用户要求左栏只保留「快速入门」。");
        Assert.AreEqual("快速入门", help.DisplayTopics[0].Key);
    }

    [TestMethod]
    [DoNotParallelize]
    public void HelpCenter_QuickStartTextFollowsTheUiLanguage()
    {
        var (help, _, _) = Create();

        try
        {
            Loc.Service.SetLanguage(LocalizationService.English);

            Assert.AreEqual("Thank you for choosing EiTRVO", help.CurrentStepTitle);
            StringAssert.Contains(help.CurrentStepBody, "This tutorial will guide you through EiTRVO LTS");
        }
        finally
        {
            Loc.Service.SetLanguage(LocalizationService.Chinese);
        }

        Assert.AreEqual("感谢您选择EiTRVO", help.CurrentStepTitle);
    }

    [TestMethod]
    public void HelpCenter_QuickStartPagesComeFromTheDocumentWithoutStepLabels()
    {
        var (help, _, _) = Create();

        Assert.AreEqual(12, help.Steps.Count);
        Assert.AreEqual("感谢您选择EiTRVO", help.CurrentStepTitle);
        StringAssert.Contains(help.CurrentStepBody, "此教程将引导您入门EiTRVO LTS");
        StringAssert.Contains(help.CurrentStepImageUri, "step01.png");
        Assert.IsFalse(help.CurrentStepTitle.Contains("步骤", StringComparison.Ordinal));
        Assert.IsFalse(help.CurrentStepBody.Contains("步骤", StringComparison.Ordinal));

        help.NextStepCommand.Execute(null);

        Assert.AreEqual("点击左边栏的账户，进入账户管理页面", help.CurrentStepTitle);
        StringAssert.Contains(help.CurrentStepImageUri, "step08.png");
        StringAssert.Contains(help.CurrentStepBody, "如果您未登录有效的微软账户，启动器部分功能将会受限");
        Assert.IsTrue(help.PreviousStepCommand.CanExecute(null));

        // 账户登录教程整体排在下载教程之前：第 2–4 页是账户，第 5 页才进入首次下载。
        help.NextStepCommand.Execute(null);
        help.NextStepCommand.Execute(null);
        help.NextStepCommand.Execute(null);

        Assert.AreEqual("为游戏文件首次下载做好准备", help.CurrentStepTitle);
        StringAssert.Contains(help.CurrentStepImageUri, "step02.png");

        help.PreviousStepCommand.Execute(null);
        help.PreviousStepCommand.Execute(null);
        help.PreviousStepCommand.Execute(null);
        help.PreviousStepCommand.Execute(null);
        Assert.AreEqual("感谢您选择EiTRVO", help.CurrentStepTitle);
    }

    [TestMethod]
    public void HelpCenter_ContentAreaSaysTheContentIsStillBeingWritten()
    {
        var (help, _, _) = Create();

        // 「不做内容」必须在界面上说出来，而不是显示一段编出来的帮助正文。
        StringAssert.Contains(help.ContentPlaceholder, "正在整理");
        StringAssert.Contains(help.ContentPlaceholder, help.SelectedTopic!.Title);
    }

    [TestMethod]
    public void HelpCenter_BackAndForwardWalkTheTopicHistory()
    {
        var (help, _, _) = Create();

        help.SelectedTopic = help.Topics[2];
        help.SelectedTopic = help.Topics[4];

        Assert.IsTrue(help.BackCommand.CanExecute(null));

        help.BackCommand.Execute(null);
        Assert.AreEqual(help.Topics[2].Key, help.SelectedTopic!.Key);

        help.BackCommand.Execute(null);
        Assert.AreEqual(help.Topics[0].Key, help.SelectedTopic!.Key);
        Assert.IsFalse(help.BackCommand.CanExecute(null));

        help.ForwardCommand.Execute(null);
        Assert.AreEqual(help.Topics[2].Key, help.SelectedTopic!.Key);
    }

    [TestMethod]
    public void HelpCenter_HomeGoesBackToTheFirstTopic()
    {
        var (help, _, _) = Create();

        help.SelectedTopic = help.Topics[5];
        help.HomeCommand.Execute(null);

        Assert.AreEqual(help.Topics[0].Key, help.SelectedTopic!.Key);
    }

    [TestMethod]
    public void HelpCenter_SearchFiltersTopicsLocally()
    {
        var (help, _, _) = Create();

        help.SearchText = "账户";

        Assert.AreEqual(1, help.FilteredTopics.Count);
        Assert.AreEqual("账户与登录", help.FilteredTopics[0].Key);
        Assert.IsFalse(help.HasNoSearchResults);

        help.SearchText = "zzzz";
        Assert.AreEqual(0, help.FilteredTopics.Count);
        Assert.IsTrue(help.HasNoSearchResults, "没有匹配项要有一句明确的话。");
    }

    [TestMethod]
    public void HelpCenter_AboutOpensTheMainWindowPageAndRestoresTheMainWindow()
    {
        var (help, navigation, windows) = Create();

        help.OpenAboutCommand.Execute(null);

        CollectionAssert.Contains(navigation.Visited, PageId.About);
        Assert.AreEqual(1, windows.RestoreCount, "打开「关于」时要把主窗口还原置前（帮助窗口可能盖着它）。");
    }

    [TestMethod]
    public void HelpCenter_TitleDoesNotCarryTheProductName()
    {
        var (help, _, _) = Create();

        Assert.AreEqual("帮助与支持中心", help.Title, "用户 2026-10-18 指定：窗口标题不带产品名前缀。");
    }

    private static (HelpViewModel Help, FakeNavigationService Navigation, FakeWindowService Windows) Create()
    {
        var navigation = new FakeNavigationService();
        var windows = new FakeWindowService();

        return (new HelpViewModel(navigation, windows, Loc.Service), navigation, windows);
    }
}
