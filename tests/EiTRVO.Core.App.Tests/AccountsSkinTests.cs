using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;

using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// p11 的皮肤区（M5 的 D-9）在界面这一侧的行为。
/// <para>
/// 内核（官方接口、令牌、冷却）由 Auth 侧的 <c>MinecraftSkinServiceTests</c> 逐条钉住；
/// 这里要验的是四件事：**用户选的文件有没有交出去**、**取消选文件不算错误**、
/// **失败原因有没有原样说出来**、以及**不该调的时候一次都没调**（换选中项 / 换语言用代码写
/// <c>SkinModel</c> 时不能顺手给那个账户改一次皮肤模型）。
/// </para>
/// </summary>
[TestClass]
public sealed class AccountsSkinTests
{
    [TestMethod]
    public async Task UploadingPicksAPngAndHandsItToTheKernel()
    {
        var accounts = new FakeAccountCatalog();
        var skins = new FakeSkinService();
        var dialogs = new FakeDialogService { FileToPick = @"C:\temp\my-skin.png" };
        var page = TestViewModels.Accounts(accounts, skins, dialogs);

        await page.UploadSkinCommand.ExecuteAsync(null);

        Assert.AreEqual(1, skins.Uploads.Count);
        Assert.AreEqual(("Steve", @"C:\temp\my-skin.png", SkinModel.Classic), skins.Uploads[0]);
        StringAssert.Contains(dialogs.Picked.Single().Filter, "*.png", "只让用户选 PNG。");
        Assert.AreEqual(string.Empty, page.ActionError);
    }

    [TestMethod]
    public async Task CancellingTheFilePickerIsNotAnError()
    {
        var accounts = new FakeAccountCatalog();
        var skins = new FakeSkinService();
        var dialogs = new FakeDialogService { FileToPick = null };
        var page = TestViewModels.Accounts(accounts, skins, dialogs);

        await page.UploadSkinCommand.ExecuteAsync(null);

        Assert.AreEqual(1, dialogs.Picked.Count, "选择框还是要弹出来的。");
        Assert.AreEqual(0, skins.Uploads.Count);
        Assert.AreEqual(string.Empty, page.ActionError, "用户按了取消不是错误，不该在界面上骂他一句。");
    }

    [TestMethod]
    public async Task AfterAnUploadTheFourFieldsFollowTheNewProfile()
    {
        var accounts = new FakeAccountCatalog
        {
            // 内核（真实实现）会用响应里的档案写回账户，这里模拟"已经写回来了"。
            Skin = new SkinProfile("Steve", TextTemplate.Of("自定义皮肤 · {0}", "ab12cd34"), HasCape: true, SkinModel.Slim, TextTemplate.Of("今天 {0}", "09:00")),
        };
        var skins = new FakeSkinService();
        var page = TestViewModels.Accounts(
            accounts,
            skins,
            new FakeDialogService { FileToPick = @"C:\temp\my-skin.png" });

        await page.UploadSkinCommand.ExecuteAsync(null);

        Assert.AreEqual("自定义皮肤 · ab12cd34", page.SkinCurrentSkinText, "皮肤那一格是「键 + 参数」，由 ViewModel 拼。");
        Assert.AreEqual("今天 09:00", page.SkinLastUpdatedText);
        Assert.IsTrue(page.Skin.HasCape);
        Assert.AreEqual(SkinModel.Slim, page.SkinModel, "下拉框要跟着真实档案走。");
    }

    [TestMethod]
    public async Task TheUploadButtonGoesGreyForTheCooldownAfterASuccess()
    {
        var accounts = new FakeAccountCatalog();
        var skins = new FakeSkinService();
        var page = TestViewModels.Accounts(
            accounts,
            skins,
            new FakeDialogService { FileToPick = @"C:\temp\my-skin.png" });

        Assert.IsTrue(page.UploadSkinCommand.CanExecute(null));

        await page.UploadSkinCommand.ExecuteAsync(null);

        Assert.IsFalse(
            page.UploadSkinCommand.CanExecute(null),
            "成功之后 60 秒内按钮必须是灰的（内核记着冷却，界面只是去问一次）。");
        Assert.IsFalse(page.ResetSkinCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task AFailedUploadSaysExactlyWhatTheKernelSaid()
    {
        var accounts = new FakeAccountCatalog();
        var skins = new FakeSkinService
        {
            Failure = new SkinChangeException(
                TextTemplate.Of("这个账户需要重新登录一次（刷新令牌已被作废）。"),
                SkinChangeFailure.NotSignedIn),
        };
        var page = TestViewModels.Accounts(
            accounts,
            skins,
            new FakeDialogService { FileToPick = @"C:\temp\my-skin.png" });

        await page.UploadSkinCommand.ExecuteAsync(null);

        Assert.AreEqual("这个账户需要重新登录一次（刷新令牌已被作废）。", page.ActionError);
    }

    [TestMethod]
    public void UploadingIsOnlyOfferedForMicrosoftAccounts()
    {
        var accounts = new FakeAccountCatalog();
        var page = TestViewModels.Accounts(accounts);

        Assert.IsTrue(page.UploadSkinCommand.CanExecute(null), "默认选中的是正版账户。");
        Assert.IsTrue(page.CanChangeSkinModel, "正版账户可以改皮肤模型。");

        page.SelectedAccount = page.Accounts.Single(static account => account.Kind == AccountKind.Offline);

        Assert.IsFalse(page.UploadSkinCommand.CanExecute(null), "离线账户没有官方档案，按钮就该是灰的。");
        Assert.IsFalse(page.ResetSkinCommand.CanExecute(null));
        Assert.IsFalse(page.CanChangeSkinModel, "离线账户没有 Java 版皮肤档案，皮肤模型下拉必须置灰。");
    }

    [TestMethod]
    public async Task ChangingTheModelCallsTheKernelWithTheChosenModel()
    {
        var accounts = new FakeAccountCatalog();
        var skins = new FakeSkinService();
        var page = TestViewModels.Accounts(accounts, skins);

        page.SkinModel = SkinModel.Slim;
        await AwaitCommand(page.ChangeSkinModelCommand);

        Assert.AreEqual(1, skins.ModelChanges.Count);
        Assert.AreEqual(("Steve", SkinModel.Slim), skins.ModelChanges[0]);
    }

    [TestMethod]
    public async Task AFailedModelChangePutsTheDropdownBackToTheRealValue()
    {
        var accounts = new FakeAccountCatalog();
        var skins = new FakeSkinService
        {
            Failure = new SkinChangeException(
                TextTemplate.Of("这个账户当前是官方默认皮肤，先上传一张自定义皮肤再改模型。"),
                SkinChangeFailure.NoCustomSkin),
        };
        var page = TestViewModels.Accounts(accounts, skins);

        page.SkinModel = SkinModel.Slim;
        await AwaitCommand(page.ChangeSkinModelCommand);

        Assert.AreEqual(SkinModel.Classic, page.SkinModel, "没生效的模型不能停在下拉框里 —— 那是在骗用户。");
        StringAssert.Contains(page.ActionError, "默认皮肤");
    }

    [TestMethod]
    public void SelectingAnotherAccountDoesNotChangeItsSkinModel()
    {
        var accounts = new FakeAccountCatalog();

        // 第二条也是正版账户；它的皮肤是纤细模型 —— 选中它时**只能**是把下拉框显示成 Slim，
        // 绝不能顺手给这个账户发一次"换模型"。
        accounts.Accounts.Add(new Account(
            "Alex",
            AccountKind.Microsoft,
            "11111111-2222-3333-4444-555555555555",
            TextTemplate.Of("{0}", "2026/9/25"),
            TokenState.Valid,
            IsDefault: false,
            IsActive: false));

        // 先建页面（此时默认选中 Steve、模型是经典）。
        var skins = new FakeSkinService();
        var page = TestViewModels.Accounts(accounts, skins);

        Assert.AreEqual(SkinModel.Classic, page.SkinModel);

        // 再让"内核"报出 Alex 的皮肤：纤细模型。
        accounts.Skin = new SkinProfile("Alex", TextTemplate.Of("自定义皮肤 · {0}", "deadbeef"), HasCape: false, SkinModel.Slim, TextTemplate.Of("今天 {0}", "10:00"));

        page.SelectedAccount = page.Accounts.Single(static account => account.PlayerName == "Alex");

        Assert.AreEqual(SkinModel.Slim, page.SkinModel, "下拉框显示的是这个账户真实的模型。");
        Assert.AreEqual(0, skins.ModelChanges.Count, "选账户不是改皮肤：这里一次请求都不能发。");
    }

    [TestMethod]
    public async Task ResettingPutsTheDefaultSkinBack()
    {
        var accounts = new FakeAccountCatalog
        {
            Skin = new SkinProfile("Steve", TextTemplate.Of("自定义皮肤 · {0}", "ab12cd34"), HasCape: false, SkinModel.Classic, TextTemplate.Of("今天 {0}", "09:00")),
        };
        var skins = new FakeSkinService();
        var page = TestViewModels.Accounts(accounts, skins);

        accounts.Skin = new SkinProfile("Steve", TextTemplate.Of("默认皮肤"), HasCape: false, SkinModel.Classic, TextTemplate.Of("{0}", "刚刚"));

        await page.ResetSkinCommand.ExecuteAsync(null);

        Assert.AreEqual("Steve", skins.Resets.Single());
        Assert.AreEqual("默认皮肤", page.SkinCurrentSkinText);
    }

    /// <summary>
    /// 命令是由属性变化（下拉框）触发的，所以这里要把那一次执行等完再断言 ——
    /// 立刻断言会变成一条偶发红的用例。
    /// </summary>
    private static async Task AwaitCommand(IAsyncRelayCommand command)
    {
        if (command.ExecutionTask is { } task)
        {
            await task;
        }
    }
}
