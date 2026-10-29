using System.IO;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Storage;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// p11 的「移除账户」。
/// </summary>
/// <remarks>
/// 2026-10-06 用户反馈："点击移除账户按钮会跳转到移除实例页面，无法删除账户" ——
/// 那个按钮以前绑的是导航到 <c>PageId.DeleteInstance</c>（删**实例**的页面）。
/// 这里钉住三件事：它开的是自己的确认框、确认后真的把账户从库里删掉、失败时如实说且不关框。
/// </remarks>
[TestClass]
public sealed class RemoveAccountTests
{
    private static AccountsViewModel NewPage(FakeAccountCatalog catalog) => TestViewModels.Accounts(catalog);

    private static RemoveAccountDialogViewModel OpenDialog(AccountsViewModel page)
    {
        Assert.IsFalse(page.HasDialog, "没点之前不该有模态层。");

        page.OpenRemoveAccountCommand.Execute(null);

        Assert.IsTrue(page.HasDialog, "点了「移除账户」就该出现模态层。");
        Assert.IsInstanceOfType<RemoveAccountDialogViewModel>(page.Dialog);
        return (RemoveAccountDialogViewModel)page.Dialog!;
    }

    [TestMethod]
    public void TheRemoveButtonOpensAConfirmDialogForTheSelectedAccount()
    {
        var catalog = new FakeAccountCatalog();
        var page = NewPage(catalog);
        var offline = page.Accounts.Single(account => account.Kind == AccountKind.Offline);

        page.SelectedAccount = offline;

        Assert.IsTrue(page.OpenRemoveAccountCommand.CanExecute(null));

        var dialog = OpenDialog(page);

        Assert.AreEqual("Alex_Offline", dialog.PlayerName);
        StringAssert.Contains(dialog.Question, "Alex_Offline", "提问句里必须写明删的是哪一个账户。");
        Assert.IsTrue(dialog.HasTarget);
        Assert.IsTrue(dialog.ConfirmCommand.CanExecute(null));
    }

    /// <summary>
    /// 这条是用户报的那个 bug：按钮**绑自己的命令**，不再跳到删实例那一页去。
    /// </summary>
    /// <remarks>
    /// ViewModel 里没有导航服务（这个页面要跳转时走的是外壳的 <c>NavigateCommand</c>），
    /// 所以"它现在不导航了"这件事只能在**视图**这一层钉住：直接读 p11 的 XAML。
    /// 这正是当初出错的地方 —— 绑定写成了 <c>PageId.DeleteInstance</c>，编译与所有门禁都看不见。
    /// </remarks>
    [TestMethod]
    public void TheRemoveButtonIsBoundToTheRemoveCommandAndNotToTheInstancePage()
    {
        var xaml = File.ReadAllText(FindPageXaml("P11AccountsView.xaml"));

        var index = xaml.IndexOf("Text=\"{loc:Text 移除账户}\"", StringComparison.Ordinal);
        Assert.IsTrue(index >= 0, "「移除账户」按钮要在（防呆：改名时别把它连根删了）。");

        // 看按钮这一小段：它必须绑本页的移除命令，且不该出现"删实例"的页面 id。
        var button = xaml[index..Math.Min(xaml.Length, index + 400)];

        StringAssert.Contains(button, "Command=\"{Binding OpenRemoveAccountCommand}\"", "它必须绑到本页的移除命令上。");
        Assert.IsFalse(button.Contains("DeleteInstance", StringComparison.Ordinal), "它不该再跳到删实例那一页（用户报的就是这个）。");
    }

    /// <summary>从测试输出目录往上找到仓库根，再取 p11 的 XAML。</summary>
    /// <param name="fileName">文件名。</param>
    /// <returns>绝对路径。</returns>
    private static string FindPageXaml(string fileName) => FindViewXaml("Pages", fileName);

    private static string FindViewXaml(string folder, string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
            {
                return Path.Combine(directory.FullName, "src", "EiTRVO.Core.App", "Views", folder, fileName);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到仓库根（从 {AppContext.BaseDirectory} 往上找 EiTRVO.Core.sln）。");
    }

    [TestMethod]
    public void TheDialogTitleHasNoProductSuffixAndTheBodyUsesTheLocalSignInCopy()
    {
        var xaml = File.ReadAllText(FindViewXaml("Dialogs", "RemoveAccountDialogView.xaml"));

        Assert.IsFalse(xaml.Contains("EiTRVO For Preview", StringComparison.Ordinal), "账户确认框标题不该再带产品名后缀。");
        StringAssert.Contains(xaml, "Text=\"{loc:Text 移除账户}\"", "标题只保留「移除账户」。");
        StringAssert.Contains(
            xaml,
            "Text=\"{loc:Text 将删除本机保存的登录信息}\"",
            "说明文字改为「将删除本机保存的登录信息」。");
    }

    [TestMethod]
    public void TheDeleteDialogTitleHasNoProductSuffix()
    {
        var xaml = File.ReadAllText(FindViewXaml("Dialogs", "DeleteConfirmDialogView.xaml"));

        Assert.IsFalse(xaml.Contains("EiTRVO For Preview", StringComparison.Ordinal), "删除实例确认框标题不该再带产品名后缀。");
        StringAssert.Contains(xaml, "Text=\"{loc:Text 删除实例}\"", "标题只保留「删除实例」。");
    }

    [TestMethod]
    public void ConfirmingRemovesTheAccountAndReloadsTheList()
    {
        var catalog = new FakeAccountCatalog();
        var page = NewPage(catalog);

        var dialog = OpenDialog(page);
        dialog.ConfirmCommand.Execute(null);

        // 真的调到了内核，而且删的是选中的那一个。
        Assert.AreEqual("Steve", catalog.RemoveCalls.Single());
        Assert.IsTrue(dialog.Removed);
        Assert.IsFalse(page.HasDialog, "删成功之后要把框关掉。");

        // 列表跟着少一行（被删的账户不该继续显示）。
        Assert.IsFalse(page.Accounts.Any(account => account.PlayerName == "Steve"));
        Assert.AreEqual("1 个已登录账户", page.SectionSubtitle);
        Assert.AreEqual("Alex_Offline", page.SelectedAccount?.PlayerName, "选中项要落到还剩下的那个账户上。");
    }

    [TestMethod]
    public void CancellingChangesNothing()
    {
        var catalog = new FakeAccountCatalog();
        var page = NewPage(catalog);

        var dialog = OpenDialog(page);
        dialog.CancelCommand.Execute(null);

        Assert.AreEqual(0, catalog.RemoveCalls.Count);
        Assert.IsFalse(dialog.Removed);
        Assert.IsFalse(page.HasDialog);
        Assert.AreEqual(2, page.Accounts.Count);
    }

    [TestMethod]
    public void AFailedRemovalIsShownAndTheDialogStaysOpen()
    {
        var catalog = new FakeAccountCatalog
        {
            RemoveFailure = new AccountStoreException(
                AccountStoreFailure.DecryptionFailed,
                @"C:\data\accounts.json"),
        };

        var page = NewPage(catalog);

        var dialog = OpenDialog(page);
        dialog.ConfirmCommand.Execute(null);

        Assert.AreEqual("账户文件读不出来，已取消这次移除（原文件未改动）。", dialog.Error);
        Assert.IsTrue(dialog.HasError);
        Assert.IsTrue(page.HasDialog, "失败时框要留着 —— 用户需要看到原因，而不是看着框消失、账户还在。");
        Assert.IsFalse(dialog.Removed);
        Assert.AreEqual(2, page.Accounts.Count, "失败时列表不该少行。");
    }

    [TestMethod]
    public void TheRemoveButtonIsDisabledWithoutAnyAccount()
    {
        var catalog = new FakeAccountCatalog();
        catalog.Accounts.Clear();

        var page = NewPage(catalog);

        Assert.IsNull(page.SelectedAccount);
        Assert.IsFalse(page.CanRemoveAccount);
        Assert.IsFalse(page.OpenRemoveAccountCommand.CanExecute(null));
    }
}
