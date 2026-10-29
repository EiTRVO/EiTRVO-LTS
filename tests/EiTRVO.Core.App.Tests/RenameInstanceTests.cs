using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.App.Tests;

/// <summary>p02「重命名」：命令必须打开对话框并真的调内核重命名。</summary>
[TestClass]
public sealed class RenameInstanceTests
{
    [TestMethod]
    public void TheRenameButtonOpensTheDialogAndCommitsTheNewName()
    {
        var instances = new FakeInstanceCatalog();
        instances.CreateInstance(new InstanceCreationRequest("1.20.4-Forge", "1.20.4", LoaderKind.Forge, "49.0.30"));

        var page = TestViewModels.Instances(instances);
        page.SelectedInstance = page.Instances.Single();

        page.OpenRenameCommand.Execute(null);

        Assert.IsTrue(page.HasDialog);
        var dialog = (RenameInstanceDialogViewModel)page.Dialog!;
        Assert.AreEqual("1.20.4-Forge", dialog.OldName);
        Assert.AreEqual("1.20.4-Forge", dialog.NewName);

        dialog.NewName = "我的整合包";
        Assert.IsTrue(dialog.CanConfirm);
        dialog.ConfirmCommand.Execute(null);

        Assert.IsFalse(page.HasDialog);
        Assert.AreEqual(("1.20.4-Forge", "我的整合包"), instances.Renamed.Single());
    }

    [TestMethod]
    public void AnInvalidOrUnchangedNameCannotBeConfirmed()
    {
        var dialog = new RenameInstanceDialogViewModel(new FakeInstanceCatalog());
        dialog.Reset("Old");

        dialog.NewName = "别处";
        dialog.NewName = "Old";
        Assert.IsFalse(dialog.CanConfirm);
        StringAssert.Contains(dialog.Error, "不能与原名字相同");

        dialog.NewName = "a/b";
        Assert.IsFalse(dialog.CanConfirm);
        Assert.IsTrue(dialog.HasError);
    }
}
