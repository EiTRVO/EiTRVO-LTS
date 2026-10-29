using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 2026-10-25 用户要求：没有登录过正版账号时，锁定版本页的新建实例按钮，
/// 并且不提供 MC 游戏文件 / 模组加载器 / 整合包下载；模组与资源包不受影响。
/// </summary>
[TestClass]
public sealed class DownloadAccessTests
{
    [TestMethod]
    public void NoMicrosoftAccountMeansGameContentDownloadsAreLocked()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Accounts.RemoveAll(static account => account.Kind == AccountKind.Microsoft);

        var policy = new AccountDownloadPolicy(accounts);

        Assert.IsFalse(policy.CanDownloadGameContent);
        StringAssert.Contains(policy.LockedReason, "正版账号");
    }

    [TestMethod]
    public void AMicrosoftAccountUnlocksGameContentDownloads()
    {
        var policy = new AccountDownloadPolicy(new FakeAccountCatalog());

        Assert.IsTrue(policy.CanDownloadGameContent);
    }

    [TestMethod]
    public void TheVersionsNewInstanceButtonIsLockedWithoutAPremiumAccount()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Accounts.RemoveAll(static account => account.Kind == AccountKind.Microsoft);
        var seed = new EiTRVO.Core.Launcher.Sample.SampleVersionCatalog();
        var page = new VersionsViewModel(
            new FakeVersionCatalog(seed.GetVersions(), seed.GetLoaders("1.20.4")),
            new FakeNavigationService(),
            new FakeJavaRuntimeCatalog(),
            new AccountDownloadPolicy(accounts));

        var row = page.FilteredVersions[0];

        Assert.IsFalse(page.CreateInstanceCommand.CanExecute(row));
        StringAssert.Contains(page.NewInstanceLockedReason, "正版账号");
    }

    [TestMethod]
    public void TheWizardCannotFinishWithoutAPremiumAccount()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Accounts.RemoveAll(static account => account.Kind == AccountKind.Microsoft);
        var policy = new AccountDownloadPolicy(accounts);
        var wizard = TestViewModels.NewInstance(
            new EiTRVO.Core.Launcher.Sample.SampleVersionCatalog(),
            new FakeInstanceCatalog(),
            new FakeNavigationService(),
            new FakeDownloadCatalog(),
            downloadAccess: policy);

        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);

        Assert.IsFalse(wizard.FinishCommand.CanExecute(null));
        StringAssert.Contains(wizard.DownloadActionHint, "正版账号");
    }

    [TestMethod]
    public void ModpackImportIsLockedWithoutAPremiumAccount()
    {
        var accounts = new FakeAccountCatalog();
        accounts.Accounts.RemoveAll(static account => account.Kind == AccountKind.Microsoft);
        var page = new ModpacksViewModel(
            new FakeContentCatalog(),
            new FakeDownloadCatalog(),
            new FakeDialogService(),
            new FakeNavigationService(),
            new EiTRVO.Core.Launcher.Sample.SampleVersionCatalog(),
            new FakeInstanceCatalog(),
            new AccountDownloadPolicy(accounts));

        Assert.IsFalse(page.CanUseDownloadService);
        Assert.IsFalse(page.ImportMrpackCommand.CanExecute(null));
        StringAssert.Contains(page.DownloadLockedReason, "正版账号");
    }
}
