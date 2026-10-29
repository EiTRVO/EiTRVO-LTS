using System.IO;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>p12 动作条的「查看第三方许可」必须真的打开窗口，而不是禁用的假按钮。</summary>
[TestClass]
public sealed class AboutActionBarTests
{
    [TestMethod]
    public void TheLicenseButtonIsWiredToTheLicensesWindow()
    {
        var text = File.ReadAllText(Path.Combine(
            PageFilterRowXamlTests.RepositoryRoot(),
            "src",
            "EiTRVO.Core.App",
            "Views",
            "Pages",
            "P12AboutView.xaml"));

        AssertButton(text, "查看第三方许可", "打开第三方许可窗口");
        Assert.IsFalse(text.Contains("重置所有设置", StringComparison.Ordinal), "用户要求去掉「重置所有设置」。");
    }

    [TestMethod]
    public void TheMitLicenseCommandOpensTheOfficialLicensePage()
    {
        var shell = new FakeShellService();
        var page = new AboutViewModel(
            new SampleLauncherInfo(),
            new AuthOptions { ClientId = "11111111-2222-3333-4444-555555555555" },
            shell);

        page.OpenMitLicenseCommand.Execute(null);

        Assert.AreEqual("https://opensource.org/license/mit", shell.Urls.Single());
    }

    [TestMethod]
    public void TheLicenseCommandCallsTheWindowService()
    {
        var service = new FakeLicenseWindowService();
        var page = new AboutViewModel(
            new SampleLauncherInfo(),
            new AuthOptions { ClientId = "11111111-2222-3333-4444-555555555555" },
            new FakeShellService(),
            service);

        Assert.IsTrue(page.OpenLicensesCommand.CanExecute(null));
        page.OpenLicensesCommand.Execute(null);

        Assert.IsTrue(service.Shown);
    }

    private static void AssertButton(string text, string label, string tooltipFragment)
    {
        var start = text.IndexOf(label, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"找不到按钮「{label}」。");

        var end = text.IndexOf("/>", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"按钮「{label}」没有正常闭合。");

        var tag = text[start..end];
        StringAssert.Contains(tag, "Command=\"{Binding OpenLicensesCommand}\"", "「查看第三方许可」必须接到真实命令。");
        Assert.IsFalse(tag.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "许可按钮不应再是禁用态。");
        StringAssert.Contains(tag, "ToolTip=", $"「{label}」要说明点击后的行为。");
        StringAssert.Contains(tag, tooltipFragment, $"「{label}」的 ToolTip 要说清行为。");
    }

    private sealed class FakeLicenseWindowService : ILicenseWindowService
    {
        public bool Shown { get; private set; }

        public void Show() => Shown = true;
    }
}
