using System.IO;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Auth.Sample;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Sample;
using EiTRVO.Core.Launcher.Runtime;
using EiTRVO.Core.Launcher.Versions;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 页面 ViewModel 的状态机与命令测试。
/// 只测「有分支、有状态」的地方：向导步进、设置脏标记与保存、令牌刷新、目录打开。
/// </summary>
[TestClass]
public sealed class PageViewModelTests
{
    // ==================== p04 新建实例向导 ====================

    [TestMethod]
    public void Wizard_OpensOnTheSecondStepWithVanillaSelected()
    {
        // 「所需 Java」那行 M3 起是真实的检测结论：本机装着 Java 17 时它必须把版本号说出来。
        var wizard = NewWizard(java: new FakeJavaRuntimeCatalog([Java17]));

        Assert.AreEqual(2, wizard.CurrentStepNumber);
        Assert.AreEqual("模组加载器", wizard.CurrentStepTitle);
        Assert.AreEqual(LoaderKind.Vanilla, wizard.SelectedLoader!.Kind, "默认不装加载器，避免无意改游戏本体。");
        Assert.AreEqual(string.Empty, wizard.SelectedLoaderVersion, "原版没有加载器版本。");
        Assert.AreEqual("1.20.4（正式版，发布于 2023/12/07）", wizard.SelectedVersionText);
        Assert.AreEqual("Java 17（已检测到 17.0.9）", wizard.RequiredJavaText);
    }

    [TestMethod]
    public async Task Wizard_ReopeningAfterForgeSelectionResetsToVanilla()
    {
        var wizard = NewWizard();
        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Forge));

        wizard.OnActivated();
        await wizard.RefreshCommand.ExecuteAsync(null);

        Assert.AreEqual(LoaderKind.Vanilla, wizard.SelectedLoader!.Kind, "取消后重新进入向导，默认必须回到原版。");
    }

    [TestMethod]
    public void Wizard_SaysTheJavaIsMissingOnlyAfterADetectionSaidSo()
    {
        // 没检测过 → 只说需要哪个版本，不编"没检测到"；检测过、确实没有 → 如实说缺。
        var undetected = NewWizard(java: new FakeJavaRuntimeCatalog([], hasDetectionResult: false));
        Assert.AreEqual("Java 17", undetected.RequiredJavaText);

        var detectedWithout = NewWizard(java: new FakeJavaRuntimeCatalog([], hasDetectionResult: true));
        Assert.AreEqual("Java 17（未检测到，启动前需要获取）", detectedWithout.RequiredJavaText);
    }

    /// <summary>一个 64 位 Temurin 17（多处用例共用）。</summary>
    private static JavaInstallation Java17 { get; } = new(
        ExecutablePath: @"C:\Java\jdk-17\bin\javaw.exe",
        HomeDirectory: @"C:\Java\jdk-17",
        VersionText: "17.0.9",
        MajorVersion: 17,
        Vendor: "Eclipse Temurin",
        Bits: 64);

    [TestMethod]
    public void Wizard_StepStatesFollowTheCurrentStep()
    {
        var wizard = NewWizard();

        CollectionAssert.AreEqual(
            new[] { WizardStepState.Done, WizardStepState.Current, WizardStepState.Pending, WizardStepState.Pending },
            wizard.Steps.Select(static s => s.State).ToArray());

        wizard.NextCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { WizardStepState.Done, WizardStepState.Done, WizardStepState.Current, WizardStepState.Pending },
            wizard.Steps.Select(static s => s.State).ToArray());
    }

    [TestMethod]
    public void Wizard_BackAndNextAreBoundedByTheStepRange()
    {
        var wizard = NewWizard();

        Assert.IsTrue(wizard.BackCommand.CanExecute(null), "第 2 步时应当可以后退。");

        wizard.BackCommand.Execute(null);
        Assert.AreEqual(1, wizard.CurrentStepNumber);
        Assert.IsFalse(wizard.BackCommand.CanExecute(null));

        while (wizard.NextCommand.CanExecute(null))
        {
            wizard.NextCommand.Execute(null);
        }

        Assert.AreEqual(4, wizard.CurrentStepNumber);
        Assert.IsTrue(wizard.BackCommand.CanExecute(null));
    }

    [TestMethod]
    public void Wizard_SelectingALoaderResetsTheVersionOptions()
    {
        var wizard = NewWizard();
        Assert.AreEqual(LoaderKind.Vanilla, wizard.SelectedLoader!.Kind);
        Assert.AreEqual(string.Empty, wizard.SelectedLoaderVersion);

        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Forge));

        Assert.AreEqual("49.0.30", wizard.SelectedLoaderVersion, "选中的是纯版本号（显示文本才带「（推荐）」）。");
        Assert.AreEqual("49.0.30（推荐）", wizard.LoaderVersionOptions[0].Text, "下拉框里那一项显示「（推荐）」。");

        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Fabric));

        Assert.AreEqual(LoaderKind.Fabric, wizard.SelectedLoader!.Kind);
        Assert.AreEqual("0.15.7", wizard.SelectedLoaderVersion);
        Assert.AreEqual(1, wizard.Loaders.Count(static l => l.IsActive));
    }

    [TestMethod]
    public void Wizard_RefusesToSelectTheUnavailableLoader()
    {
        var wizard = NewWizard();

        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.NeoForge));

        Assert.AreEqual(LoaderKind.Vanilla, wizard.SelectedLoader!.Kind, "不可用的加载器不应被选中。");
    }

    [TestMethod]
    public void Wizard_TheLastStepCreatesTheInstanceAndHandsTheInstallToTheQueue()
    {
        var instances = new FakeInstanceCatalog();
        var navigation = new FakeNavigationService();
        var downloads = new FakeDownloadCatalog();
        var wizard = NewWizard(instances, navigation, downloads);

        // 最后一步之前没有创建动作：按钮换成了「下一步」。
        Assert.IsFalse(wizard.FinishCommand.CanExecute(null));
        Assert.IsFalse(wizard.IsLastStep);

        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        Assert.IsTrue(wizard.IsLastStep);
        Assert.IsTrue(wizard.FinishCommand.CanExecute(null));

        wizard.FinishCommand.Execute(null);

        var created = instances.Created.Single();
        Assert.AreEqual("1.20.4-Vanilla", created.Name, "默认原版：实例名带 Vanilla 后缀。");
        Assert.AreEqual("1.20.4", created.GameVersion);
        Assert.AreEqual(LoaderKind.Vanilla, created.Loader);
        Assert.IsNull(created.LoaderVersion, "原版没有加载器版本；内核用 null 表示。");
        Assert.IsTrue(created.IsolatedDirectory);

        // M2 起"确认并安装"还要**把安装交给队列**，并跳到下载页看进度 ——
        // 只建元数据不装游戏的话，用户点启动只会得到"缺文件"。
        var request = downloads.Enqueued.Single();
        Assert.AreEqual("1.20.4", request.VersionId);
        Assert.AreEqual(LoaderKind.Vanilla, request.Loader);
        Assert.IsNull(request.LoaderVersion, "原版没有加载器版本；请求里也不编一个。");
        CollectionAssert.AreEqual(new[] { PageId.Downloads }, navigation.Visited);
    }

    [TestMethod]
    public void Wizard_OffersOptiFineOnlyForForgeAndFabricApiOnlyForFabric()
    {
        var wizard = NewWizard();
        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Forge));

        Assert.AreEqual(LoaderKind.Forge, wizard.SelectedLoader!.Kind);
        Assert.IsTrue(wizard.ShowOptiFineComponent);
        Assert.IsFalse(wizard.ShowFabricApiComponent);

        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Fabric));

        Assert.IsFalse(wizard.ShowOptiFineComponent);
        Assert.IsTrue(wizard.ShowFabricApiComponent);
    }

    [TestMethod]
    public void Wizard_PassesOptionalComponentFlagsToTheQueue()
    {
        var downloads = new FakeDownloadCatalog();
        var wizard = NewWizard(downloads: downloads);
        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Forge));

        wizard.WithOptiFine = true;
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);

        var request = downloads.Enqueued.Single();
        Assert.IsTrue(request.WithOptiFine, "Forge 勾选的 OptiFine 要随安装请求进队列。");
        Assert.IsFalse(request.WithFabricApi);

        var fabricDownloads = new FakeDownloadCatalog();
        var fabric = NewWizard(downloads: fabricDownloads);
        fabric.SelectLoaderCommand.Execute(fabric.Loaders.Single(static l => l.Kind == LoaderKind.Fabric));
        fabric.WithFabricApi = true;
        fabric.NextCommand.Execute(null);
        fabric.NextCommand.Execute(null);
        fabric.FinishCommand.Execute(null);

        var fabricRequest = fabricDownloads.Enqueued.Single();
        Assert.IsTrue(fabricRequest.WithFabricApi, "Fabric 勾选的 Fabric API 要随安装请求进队列。");
        Assert.IsFalse(fabricRequest.WithOptiFine);
    }

    [TestMethod]
    public void Wizard_ARepeatedVersionAndLoaderGetsAUniqueName()
    {
        var instances = new FakeInstanceCatalog();
        instances.CreateInstance(new InstanceCreationRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));
        var wizard = NewWizard(instances, new FakeNavigationService());
        wizard.SelectLoaderCommand.Execute(wizard.Loaders.Single(static l => l.Kind == LoaderKind.Forge));

        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);

        Assert.AreEqual("1.20.4-Forge-2", instances.Created[^1].Name);
    }

    private static NewInstanceViewModel NewWizard(
        FakeInstanceCatalog? instances = null,
        FakeNavigationService? navigation = null,
        FakeDownloadCatalog? downloads = null,
        IJavaRuntimeCatalog? java = null) =>
        new(
            new SampleVersionCatalog(),
            instances ?? new FakeInstanceCatalog(),
            navigation ?? new FakeNavigationService(),
            downloads ?? new FakeDownloadCatalog(),
            java ?? new FakeJavaRuntimeCatalog());

    [TestMethod]
    public async Task Wizard_ReopensAtTheEntryStepAfterFinishingAndUsesTheNewlySelectedVersion()
    {
        var seed = new SampleVersionCatalog();
        var catalog = new FakeVersionCatalog(seed.GetVersions(), seed.GetLoaders("1.20.4"));
        var instances = new FakeInstanceCatalog();
        var wizard = new NewInstanceViewModel(
            catalog,
            instances,
            new FakeNavigationService(),
            new FakeDownloadCatalog(),
            new FakeJavaRuntimeCatalog());

        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        wizard.FinishCommand.Execute(null);
        Assert.AreEqual(4, wizard.CurrentStepNumber, "安装动作发生在最后一步。");

        catalog.ActiveVersionId = "1.20.1";
        wizard.OnActivated();
        await (wizard.RefreshCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.AreEqual(2, wizard.CurrentStepNumber, "再次进入向导必须回到入口步骤，而不是停在旧的最后一步。");
        Assert.AreEqual("1.20.1", wizard.SelectedVersion!.Id, "向导要跟 p05 刚选中的版本走。");
        StringAssert.Contains(wizard.InstanceName, "1.20.1");
        Assert.IsNotNull(wizard.SelectedLoader);
    }

    [TestMethod]
    public void Wizard_StepThreeEditsTheInstanceNameAndRejectsInvalidOrDuplicateNames()
    {
        var instances = new FakeInstanceCatalog();
        instances.CreateInstance(new InstanceCreationRequest("Taken", "1.20.4", LoaderKind.Forge, "49.0.30"));
        var wizard = NewWizard(instances);

        wizard.NextCommand.Execute(null);
        Assert.IsTrue(wizard.IsStep3);
        Assert.IsFalse(string.IsNullOrWhiteSpace(wizard.InstanceName), "第 3 步要带一个可编辑的建议名。");

        wizard.InstanceName = "bad/name";
        Assert.IsFalse(wizard.NextCommand.CanExecute(null));
        StringAssert.Contains(wizard.NameError, "不能用作目录名");

        wizard.InstanceName = "Taken";
        Assert.IsFalse(wizard.NextCommand.CanExecute(null));
        StringAssert.Contains(wizard.NameError, "同名实例");

        wizard.InstanceName = "My Pack";
        Assert.IsTrue(wizard.NextCommand.CanExecute(null), wizard.NameError);
        wizard.NextCommand.Execute(null);
        Assert.IsTrue(wizard.IsStep4);
        wizard.FinishCommand.Execute(null);
        Assert.AreEqual("My Pack", instances.Created[^1].Name, "建实例用的是用户填的名字，不是自动推导名。");
    }
    // ==================== p10 Java 与设置（全局） ====================

    [TestMethod]
    public void GlobalSettings_StartCleanAndOnlySaveWhenDirty()
    {
        var settings = new SampleSettingsCatalog();
        var page = TestViewModels.JavaSettings(settings);

        Assert.IsFalse(page.IsDirty);
        Assert.IsFalse(page.SaveCommand.CanExecute(null));

        page.MaxMemoryMb = 8192;

        Assert.IsTrue(page.IsDirty);
        Assert.IsTrue(page.SaveCommand.CanExecute(null));

        page.SaveCommand.Execute(null);

        Assert.IsFalse(page.IsDirty);
        Assert.AreEqual(8192, settings.GetGlobal().MaxMemoryMb);
    }

    [TestMethod]
    public void GlobalSettings_ResetDiscardsUnsavedChanges()
    {
        var page = TestViewModels.JavaSettings();
        var original = page.GameDirectory;

        page.GameDirectory = @"D:\Other";
        Assert.IsTrue(page.IsDirty);

        page.ResetToDefaultCommand.Execute(null);

        Assert.AreEqual(original, page.GameDirectory);
        Assert.IsFalse(page.IsDirty);
    }

    [TestMethod]
    public void GlobalSettings_MemoryHintUsesTheMachineTotal()
    {
        var page = TestViewModels.JavaSettings();

        Assert.AreEqual("物理内存 32.0 GB · 推荐 8–16 GB · 安全区上限 16384 MB", page.MemoryHint);
    }

    // ==================== p17 实例设置（逐实例） ====================

    [TestMethod]
    public void InstanceSettings_TrackDirtyStateWithoutRecursing()
    {
        // 这个 ViewModel 覆盖了 OnPropertyChanged 来维护 IsDirty；
        // 曾经因为「通知自己」而无限递归成 StackOverflowException，p17 连窗都起不来，
        // 所以这里除了断言行为，也是在钉住「通知一次就够」这件事。
        var settings = new SampleSettingsCatalog();
        var page = TestViewModels.InstanceSettings(settings);

        Assert.IsFalse(page.IsDirty);
        Assert.IsTrue(page.MemoryHint.Contains("全局默认 4096 MB", StringComparison.Ordinal));

        page.MaxMemoryMb = 8192;

        Assert.IsTrue(page.IsDirty);
        Assert.IsTrue(page.MemoryHint.Contains("本实例覆盖为 8192 MB", StringComparison.Ordinal));

        page.SaveCommand.Execute(null);

        Assert.IsFalse(page.IsDirty);
        Assert.AreEqual(8192, settings.GetInstanceOverrides("1.20.4-Forge").MaxMemoryMb);
    }

    [TestMethod]
    public void InstanceSettings_ResetToGlobalClearsEveryOverride()
    {
        var settings = new SampleSettingsCatalog();
        var page = TestViewModels.InstanceSettings(settings);

        page.ResetToGlobalCommand.Execute(null);

        var overrides = settings.GetInstanceOverrides("1.20.4-Forge");
        Assert.IsNull(overrides.JavaOverride);
        Assert.IsNull(overrides.MaxMemoryMb);
        Assert.IsNull(overrides.JvmArguments);
        Assert.AreEqual(settings.GetGlobal().WindowWidth, overrides.WindowWidth);
        Assert.IsFalse(page.IsDirty);
    }

    [TestMethod]
    public void InstanceSettings_HasNoNavigationHighlightButCarriesAContext()
    {
        var page = TestViewModels.InstanceSettings();

        Assert.AreEqual(NavKey.None, page.NavKey);
        Assert.AreEqual("实例设置", page.SectionTitle);
        Assert.AreEqual("用于 1.20.4-Forge · 仅影响该实例", page.SectionSubtitle);
        Assert.AreEqual("该实例的配置变更记录 | 共 4 条", page.ChangeCountText);
    }

    // ==================== p11 账户 ====================

    [TestMethod]
    public async Task Accounts_RefreshTokensCallsTheKernelAndReloadsTheRow()
    {
        var accounts = new FakeAccountCatalog();
        var page = TestViewModels.Accounts(accounts);

        page.SelectedAccount = page.Accounts.Single(static a => a.Kind == AccountKind.Offline);
        Assert.IsFalse(page.RefreshTokensCommand.CanExecute(null), "离线账户没有令牌可刷新。");

        page.SelectedAccount = page.Accounts[0];
        Assert.IsTrue(page.RefreshTokensCommand.CanExecute(null));

        await page.RefreshTokensCommand.ExecuteAsync(null);

        Assert.AreEqual(1, accounts.RefreshCalls, "按钮必须真的调内核，而不是在界面上把状态改成绿的。");
        Assert.AreEqual(TokenState.Valid, page.Accounts[0].TokenState);
        Assert.AreEqual(string.Empty, page.ActionError);
    }

    [TestMethod]
    public async Task Accounts_ARefreshThatFailsSaysSoInsteadOfLookingLikeNothingHappened()
    {
        // 刷新令牌是**轮换**的：静默失败会让用户以为令牌还好，而下次启动会当场失败。
        var accounts = new FakeAccountCatalog
        {
            RefreshFailure = new AuthException(TextTemplate.Of("这个账户的登录已过期，需要重新登录一次。")),
        };
        var page = TestViewModels.Accounts(accounts);

        page.SelectedAccount = page.Accounts[0];
        await page.RefreshTokensCommand.ExecuteAsync(null);

        Assert.AreEqual("这个账户的登录已过期，需要重新登录一次。", page.ActionError);
    }

    [TestMethod]
    public void Accounts_TheDefaultAccountCannotBeMadeDefaultAgain()
    {
        var page = TestViewModels.Accounts(new SampleAccountCatalog());

        Assert.IsFalse(page.CanSetDefault);
    }

    // ==================== 打开目录类命令 ====================

    [TestMethod]
    public void Mods_OpenModsFolderGoesThroughTheShellService()
    {
        var shell = new FakeShellService();
        var page = new ModsViewModel(
            new FakeContentCatalog(),
            new SampleInstanceCatalog(),
            shell,
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        page.OpenModsFolderCommand.Execute(null);

        Assert.AreEqual(1, shell.Folders.Count);
        Assert.AreEqual(@"E:\Minecraft\.minecraft\versions\1.20.4-Forge\mods", shell.Folders[0]);
    }

    [TestMethod]
    public void Resources_OpenResourcePacksFolderGoesThroughTheShellService()
    {
        var shell = new FakeShellService();
        var page = new ResourcesViewModel(
            new FakeContentCatalog(),
            new SampleInstanceCatalog(),
            shell,
            new FakeDownloadCatalog(),
            new FakeNavigationService());

        page.OpenResourcePacksFolderCommand.Execute(null);

        Assert.AreEqual(@"E:\Minecraft\.minecraft\versions\1.20.4-Forge\resourcepacks", shell.Folders[0]);
    }

    [TestMethod]
    public void About_MasksTheClientIdAndOpensTheLogFolder()
    {
        var shell = new FakeShellService();
        var options = new AuthOptions { ClientId = "11111111-2222-3333-4444-555555555555" };
        var page = new AboutViewModel(new SampleLauncherInfo(), options, shell);

        Assert.AreEqual("11111111-…（EiTRVO 产品线共用，从配置读取）", page.ClientIdText);
        Assert.IsFalse(page.ClientIdText.Contains("2222", StringComparison.Ordinal), "完整 Client ID 不应出现在界面上。");

        page.OpenLogFolderCommand.Execute(null);
        Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "log") + Path.DirectorySeparatorChar, shell.Folders[0]);
    }

    // ==================== 模态层 ====================

    [TestMethod]
    public void DeleteDialog_NavigatesAwayOnBothPaths()
    {
        var navigation = new FakeNavigationService();
        var instances = new FakeInstanceCatalog { ActiveInstanceName = "1.20.4-Forge" };
        var dialog = new DeleteConfirmDialogViewModel(instances, navigation);

        Assert.AreEqual("确定要删除实例「1.20.4-Forge」吗？", dialog.Question);
        Assert.IsTrue(dialog.KeepSavesAndConfig, "可恢复选项默认勾选。");

        dialog.CancelCommand.Execute(null);
        dialog.ConfirmCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { PageId.InstanceDetail, PageId.Instances }, navigation.Visited);

        // 取消不该删掉任何东西；确认必须把两个勾选项原样交给内核。
        var deleted = instances.Deleted.Single();
        Assert.AreEqual("1.20.4-Forge", deleted.Name);
        Assert.IsTrue(deleted.Options.KeepSavesAndConfig);
        Assert.IsFalse(deleted.Options.DeleteAccountBinding);

        // 勾选项跟着界面走。
        dialog.KeepSavesAndConfig = false;
        dialog.DeleteAccountBinding = true;
        dialog.ConfirmCommand.Execute(null);
        Assert.IsFalse(instances.Deleted[^1].Options.KeepSavesAndConfig);
        Assert.IsTrue(instances.Deleted[^1].Options.DeleteAccountBinding);
    }

    [TestMethod]
    public async Task AddAccountDialog_ShowsTheRealCodeAndCopiesItWithoutEverAskingForAPassword()
    {
        // M5-1 起这一页是真的：会话来自设备代码流的真实实现（内核 DeviceCodeFlow），
        // 而流程本身由登录服务负责。这里用两个替身把界面这一层钉住 ——
        // 设备代码流的行为（轮询、钓鱼防护、间隔）在内核侧另有 12 条用例。
        var shell = new FakeShellService();
        var navigation = new FakeNavigationService();
        var flow = new FakeDeviceCodeFlow();
        var signIn = new FakeMicrosoftSignIn { Flow = flow };
        var dialog = new AddAccountDialogViewModel(signIn, flow, shell, navigation);

        // 打开这一页 = 发起登录（外壳导航到本页时会调 Refresh）。
        var pending = dialog.SignInCommand.ExecuteAsync(null);

        Assert.AreEqual(1, signIn.Calls);
        Assert.AreEqual(1, flow.Starts);
        Assert.AreEqual("https://microsoft.com/link", dialog.Session.VerificationUrl);
        Assert.AreEqual("microsoft.com/link", dialog.LinkText, "界面上显示的是设计稿那种不带协议的短形式（那一行还要放下两个按钮）。");
        Assert.AreEqual("K7QP-4M2X", dialog.Session.UserCode);
        Assert.IsTrue(dialog.Session.IsInProgress);
        Assert.AreEqual("等待你在浏览器中完成授权…（代码 15 分钟内有效）", dialog.WaitingText);

        dialog.CopyLinkCommand.Execute(null);
        dialog.CopyCodeCommand.Execute(null);
        dialog.OpenLinkCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { "https://microsoft.com/link", "K7QP-4M2X" }, shell.Clipboard);
        CollectionAssert.AreEqual(new[] { "https://microsoft.com/link" }, shell.Urls);

        // 成功之后自动回账户页（新账户已经在列表里）。
        signIn.Gate.SetResult(signIn.Account);
        await pending;

        CollectionAssert.AreEqual(new[] { PageId.Accounts }, navigation.Visited);
    }

    [TestMethod]
    public async Task AddAccountDialog_ShowsTheFailureInsteadOfSilentlyStayingOpen()
    {
        var shell = new FakeShellService();
        var navigation = new FakeNavigationService();
        var signIn = new FakeMicrosoftSignIn
        {
            Failure = new AuthException(TextTemplate.Of("设备代码已过期，请重新发起登录。")),
        };

        var dialog = new AddAccountDialogViewModel(signIn, new FakeDeviceCodeFlow(), shell, navigation);

        await dialog.SignInCommand.ExecuteAsync(null);

        Assert.AreEqual("设备代码已过期，请重新发起登录。", dialog.WaitingText);
        Assert.AreEqual(0, navigation.Visited.Count, "失败时留在本页，让用户看得见原因。");
        Assert.IsTrue(dialog.SignInCommand.CanExecute(null), "失败之后可以再试一次（重新打开本页即可）。");
    }

    [TestMethod]
    public void AddAccountDialog_CancelAbortsTheFlowAndReturnsToAccounts()
    {
        var navigation = new FakeNavigationService();
        var dialog = new AddAccountDialogViewModel(
            new FakeMicrosoftSignIn(),
            new FakeDeviceCodeFlow(),
            new FakeShellService(),
            navigation);

        _ = dialog.SignInCommand.ExecuteAsync(null);
        dialog.CancelCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { PageId.Accounts }, navigation.Visited);
    }

    [TestMethod]
    public void Pages_DeclareTheirModalLayerOnlyWhereTheDesignHasOne()
    {
        var navigation = new FakeNavigationService();
        var instances = new SampleInstanceCatalog();
        var events = new SampleEventLog();
        var accounts = new SampleAccountCatalog();
        var shell = new FakeShellService();

        var deletePage = new DeleteInstanceViewModel(
            instances,
            events,
            shell,
            new DeleteConfirmDialogViewModel(instances, navigation));
        var loginPage = new AccountLoginViewModel(
            accounts,
            new AddAccountDialogViewModel(new FakeMicrosoftSignIn(), new FakeDeviceCodeFlow(), shell, navigation),
            navigation);
        var detailPage = new InstanceDetailViewModel(instances, events, shell);

        Assert.IsTrue(deletePage.HasDialog);
        Assert.IsInstanceOfType<DeleteConfirmDialogViewModel>(deletePage.Dialog);
        Assert.IsTrue(loginPage.HasDialog);
        Assert.IsInstanceOfType<AddAccountDialogViewModel>(loginPage.Dialog);
        Assert.IsFalse(detailPage.HasDialog);
        Assert.IsNull(detailPage.Dialog);
    }

    // ==================== 实例操作入口的映射 ====================

    [TestMethod]
    public void InstanceActions_MapSemanticTargetsToPagesAndGlyphs()
    {
        var items = InstanceActionItemViewModel.Map(new SampleInstanceCatalog().GetActions("1.20.4-Forge"));

        Assert.AreEqual(3, items.Count);
        CollectionAssert.AreEqual(
            new[] { PageId.Mods, PageId.Resources, PageId.InstanceSettings },
            items.Select(static i => i.Target).ToArray());
        CollectionAssert.AreEqual(
            new[] { "\uE7FC", "\uE8F1", "\uE713" },
            items.Select(static i => i.Icon).ToArray());
    }

    // ==================== 其它页面的标题与副标题 ====================

    [TestMethod]
    public void Pages_UseTheSectionTitleFromTheDesignNotAlwaysThePageName()
    {
        var instances = new SampleInstanceCatalog();
        var events = new SampleEventLog();
        var shell = new FakeShellService();
        var downloads = new SampleDownloadCatalog();

        // p03 / p13 的区块标题是「属性」，而不是页面名。
        Assert.AreEqual("属性", new InstanceDetailViewModel(instances, events, shell).SectionTitle);
        Assert.AreEqual("正在启动", TestViewModels.Launching(instances: instances).SectionTitle);

        // p02 是唯一带启动入口的页面，它自身没有命令，全部靠导航 —— 这里只校验副标题。
        Assert.AreEqual("共 6 个实例", TestViewModels.Instances(instances: instances).SectionSubtitle);
        Assert.AreEqual("1 个活动任务 · 2 个排队", new DownloadsViewModel(downloads, events, new ImmediateRefreshThrottle()).SectionSubtitle);
        Assert.AreEqual("2 个已登录账户", TestViewModels.Accounts(new SampleAccountCatalog()).SectionSubtitle);
    }
}
