using System.IO;

namespace EiTRVO.Core.App.Tests;

/// <summary>p11 账户动作条的三个入口必须真的接线，而不是只有文字。</summary>
[TestClass]
public sealed class AccountActionWiringXamlTests
{
    [TestMethod]
    public void TheThreeAccountActionsAreWiredToRealCommands()
    {
        var text = File.ReadAllText(Path.Combine(
            PageFilterRowXamlTests.RepositoryRoot(),
            "src",
            "EiTRVO.Core.App",
            "Views",
            "Pages",
            "P11AccountsView.xaml"));

        // 「重新登录」走设备代码流页面；另外两个走账户目录的真实命令。
        StringAssert.Contains(text, "Text=\"{loc:Text 重新登录}\"");
        StringAssert.Contains(text, "CommandParameter=\"{x:Static vm:PageId.AccountLogin}\"");
        StringAssert.Contains(text, "Command=\"{Binding RefreshTokensCommand}\"");
        StringAssert.Contains(text, "Command=\"{Binding OpenRemoveAccountCommand}\"");

        // 2026-10-24 用户要求去掉底部的令牌/密码说明条（注释里提到它不算，控件必须消失）。
        Assert.IsFalse(text.Contains("<c:Banner", StringComparison.Ordinal));

        // 2026-10-24 用户要求去掉「设为默认」与「导出账户」两个按钮（注释提到它们不算）。
        Assert.IsFalse(text.Contains("Text=\"{loc:Text 设为默认}\"", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Text=\"{loc:Text 导出账户（DPAPI 加密）}\"", StringComparison.Ordinal));
    }
}
