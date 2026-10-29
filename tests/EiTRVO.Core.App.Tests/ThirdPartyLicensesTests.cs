using EiTRVO.Core.App.ViewModels;

namespace EiTRVO.Core.App.Tests;

/// <summary>p12 第三方许可窗口：组件清单、MIT 全文与关闭命令。</summary>
[TestClass]
public sealed class ThirdPartyLicensesTests
{
    [TestMethod]
    public void EveryShippedComponentListsItsMitLicense()
    {
        var page = new ThirdPartyLicensesViewModel();

        Assert.IsTrue(page.Components.Count >= 8);
        Assert.IsTrue(page.Components.All(static component => component.License == "MIT"));
        Assert.AreEqual(page.Components[0], page.SelectedComponent);
        StringAssert.Contains(page.SelectedLicenseText, "MIT License");
        StringAssert.Contains(page.SelectedLicenseText, "Permission is hereby granted");
        StringAssert.Contains(page.SelectedLicenseText, page.Components[0].Copyright);
        StringAssert.Contains(page.SelectedMetadata, "8.0");
    }

    [TestMethod]
    public void SelectingAnotherComponentChangesTheLicenseText()
    {
        var page = new ThirdPartyLicensesViewModel();
        var second = page.Components[1];

        page.SelectedComponent = second;

        Assert.AreEqual(second.LicenseText, page.SelectedLicenseText);
        StringAssert.Contains(page.SelectedMetadata, second.Version);
        StringAssert.Contains(page.SelectedMetadata, "MIT");
    }

    [TestMethod]
    public void CloseRaisesTheWindowCloseRequest()
    {
        var page = new ThirdPartyLicensesViewModel();
        var closed = false;
        page.CloseRequested += (_, _) => closed = true;

        page.CloseCommand.Execute(null);

        Assert.IsTrue(closed);
    }
}
