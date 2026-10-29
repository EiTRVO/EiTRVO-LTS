using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using EiTRVO.Core.App.ViewModels;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 页面 XAML 里按 <c>RelativeSource</c> 找 DataContext 的命令绑定：找错了祖先，
/// 命令不会编译失败，只会在 Output 里刷一排"找不到属性"，点下去什么都不发生。
/// </summary>
/// <remarks>
/// 2026-10-21 实际踩到：p01 的实例快切行绑的是 <c>AncestorType=Window</c>（ShellViewModel），
/// 而 <c>OpenInstanceCommand</c> 在 <c>DashboardViewModel</c> 上，于是 8 条 Binding Error。
/// 这类错误的判据是**运行时的绑定解析**，XAML 编译和普通单测都看不见；这里扫源码把接线钉住。
/// </remarks>
[TestClass]
public sealed class PageCommandBindingXamlTests
{
    private static readonly Regex WindowScopedCommand = new(
        @"DataContext\.([A-Za-z_][A-Za-z0-9_]*)Command,\s*RelativeSource=\{RelativeSource AncestorType=Window\}",
        RegexOptions.CultureInvariant);

    [TestMethod]
    public void EveryWindowScopedCommandBindingExistsOnShellViewModel()
    {
        var views = Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views");
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in WindowScopedCommand.Matches(text))
            {
                count++;
                var name = match.Groups[1].Value + "Command";
                var property = typeof(ShellViewModel).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

                Assert.IsNotNull(property, $"{file} 把 {name} 绑到了 Window 的 DataContext，但 ShellViewModel 上没有它。");
                Assert.IsTrue(
                    typeof(ICommand).IsAssignableFrom(property.PropertyType),
                    $"{name} 不是 ICommand，不能当按钮的 Command。");
            }
        }

        Assert.IsTrue(count >= 15, $"只扫到 {count} 条 Window 作用域命令绑定 —— 扫描逻辑失效了。");
    }

    [TestMethod]
    public void DashboardNoLongerShowsTheInstanceQuickSwitch()
    {
        var text = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", "P01DashboardView.xaml"));

        // 2026-10-24 用户要求去掉 p01 的实例展示部分；连同快切行与 OpenInstanceCommand 绑定一起移除。
        Assert.IsFalse(text.Contains("OpenInstanceCommand", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("{Binding Overview}", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（EiTRVO.Core.sln）。");
    }
}
