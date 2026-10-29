using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>「实例操作」行与 PickerRowButton 样式之间的绑定契约。</summary>
[TestClass]
public sealed class InstanceActionItemViewModelTests
{
    [TestMethod]
    public void ActionRowsExposeIsActiveForThePickerRowButtonStyle()
    {
        var rows = InstanceActionItemViewModel.Map(
        [
            new InstanceAction(
                TextTemplate.Of("模组管理"),
                TextTemplate.Of("管理这个实例的模组"),
                InstanceActionTarget.Mods),
        ]);

        Assert.IsFalse(rows.Single().IsActive);
    }
}
