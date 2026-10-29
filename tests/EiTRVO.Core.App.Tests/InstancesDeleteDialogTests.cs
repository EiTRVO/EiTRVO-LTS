using System.IO;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Dialogs;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 2026-10-25 用户反馈：从「本地实例」点删除时，确认框背后的页面被换成了实例详情。
/// 这里钉住「从 p02 打开时背景仍是 p02、不导航」，以及从实例详情打开时仍回详情。
/// </summary>
[TestClass]
public sealed class InstancesDeleteDialogTests
{
    [TestMethod]
    public void DeleteFromLocalInstancesOpensAModalOnTheSamePage()
    {
        var navigation = new FakeNavigationService();
        var page = TestViewModels.Instances(navigation: navigation);

        page.OpenDeleteCommand.Execute(null);

        Assert.IsTrue(page.HasDialog, "删除确认框应该作为 p02 的模态层出现。");
        Assert.IsInstanceOfType<DeleteConfirmDialogViewModel>(page.Dialog);
        Assert.AreEqual(0, navigation.Visited.Count, "打开确认框时不该导航到 p13 / 实例详情。");

        var dialog = (DeleteConfirmDialogViewModel)page.Dialog!;
        dialog.CancelCommand.Execute(null);

        Assert.IsFalse(page.HasDialog, "取消后模态层要收起，页面仍停在 p02。");
        Assert.AreEqual(0, navigation.Visited.Count);
    }

    [TestMethod]
    public void DeleteFromInstanceDetailKeepsTheDetailReturnOnCancel()
    {
        var navigation = new FakeNavigationService();
        var dialog = new DeleteConfirmDialogViewModel(new FakeInstanceCatalog(), navigation);
        dialog.Open(fromInstances: false);

        dialog.CancelCommand.Execute(null);

        CollectionAssert.Contains(navigation.Visited.ToList(), PageId.InstanceDetail);
    }

    [TestMethod]
    public void TheLocalInstancesDeleteButtonOpensTheLocalModalInsteadOfNavigatingToP13()
    {
        var xaml = File.ReadAllText(FindPageXaml("P02InstancesView.xaml"));

        var index = xaml.IndexOf("Text=\"{loc:Text 删除}\"", StringComparison.Ordinal);
        Assert.IsTrue(index >= 0, "p02 的「删除」按钮要在（防呆：改名时别把它连根删了）。");

        var button = xaml[index..Math.Min(xaml.Length, index + 300)];
        StringAssert.Contains(button, "Command=\"{Binding OpenDeleteCommand}\"");
        Assert.IsFalse(button.Contains("PageId.DeleteInstance", StringComparison.Ordinal), "不该再导航到 p13 实例详情背景。");
    }

    private static string FindPageXaml(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
            {
                return Path.Combine(directory.FullName, "src", "EiTRVO.Core.App", "Views", "Pages", fileName);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到仓库根（从 {AppContext.BaseDirectory} 往上找 EiTRVO.Core.sln）。");
    }
}
