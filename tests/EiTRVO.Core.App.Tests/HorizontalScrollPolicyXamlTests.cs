using System.IO;
using System.Text.RegularExpressions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 横向滚动条策略（2026-10-19 用户要求）：去掉各页面里"无用"的横向滚动条。
/// </summary>
/// <remarks>
/// 两类东西都在这里钉住：
/// <list type="bullet">
/// <item>装饰性滚动条条带（<c>ScrollBand</c> / <c>.hscroll</c>）：设计稿把 Server Manager 属性区的横向滚动条
/// 如实画了出来，但我们的属性表是固定列 + 省略号，**它没有可滚动的对象** —— 用户已要求删除，不能再长回来。</item>
/// <item>真实的横向滚动：页面级 <c>ScrollViewer</c> 一律 <c>Disabled</c>，列表与共享表格样式同样禁用；
/// 否则窗口一窄就出现一条能拖动、却只是把内容推出视口的横条。</item>
/// </list>
/// </remarks>
[TestClass]
public sealed class HorizontalScrollPolicyXamlTests
{
    [TestMethod]
    public void NoDecorativeScrollBandsRemainInTheAppSources()
    {
        var root = AppRoot();
        var offenders = Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(static file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                                  || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(static file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                  && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(static file => File.ReadAllText(file).Contains("ScrollBand", StringComparison.Ordinal)
                                  || File.ReadAllText(file).Contains("hscroll", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.AreEqual(0, offenders.Length, "装饰性横向滚动条条带已经删除，不允许再加回来：" + string.Join(" / ", offenders));
    }

    [TestMethod]
    public void EveryPageScrollViewerAndListDisablesHorizontalScrolling()
    {
        var views = Path.Combine(AppRoot(), "Views");
        var scrollViewers = 0;
        var lists = 0;

        foreach (var file in Directory.GetFiles(views, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(text, "<ScrollViewer(?=[\\s>])"))
            {
                var tag = OpeningTag(text, match.Index);
                Assert.IsFalse(tag.Contains("HorizontalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal), $"{file} 的 ScrollViewer 又允许横向滚动了。");
                Assert.IsTrue(tag.Contains("HorizontalScrollBarVisibility=\"Disabled\"", StringComparison.Ordinal), $"{file} 的 ScrollViewer 必须禁用横向滚动。");
                scrollViewers++;
            }

            foreach (Match match in Regex.Matches(text, "<ListBox(?=[\\s>])"))
            {
                var tag = OpeningTag(text, match.Index);
                Assert.IsTrue(tag.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", StringComparison.Ordinal), $"{file} 的 ListBox 必须禁用横向滚动。");
                lists++;
            }
        }

        Assert.IsTrue(scrollViewers >= 6, $"只扫到 {scrollViewers} 个页面级 ScrollViewer —— 扫描逻辑失效了。");
        Assert.IsTrue(lists >= 3, $"只扫到 {lists} 个 ListBox —— 扫描逻辑失效了。");
    }

    [TestMethod]
    public void TheSharedDataGridStyleDisablesHorizontalScrolling()
    {
        var text = File.ReadAllText(Path.Combine(AppRoot(), "Resources", "Lists.xaml"));

        StringAssert.Contains(
            text,
            "<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Disabled\" />",
            "表格的共享样式必须禁用横向滚动（否则窗口一窄就多一条横条）。");
    }

    /// <summary>从 <paramref name="start"/> 处的 <c>&lt;</c> 取到本次标签的 <c>&gt;</c>（跳过引号里的尖括号）。</summary>
    private static string OpeningTag(string text, int start)
    {
        var quote = '\0';

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '>')
            {
                return text[start..(i + 1)];
            }
        }

        Assert.Fail("标签没有闭合 —— XAML 本身坏了。");
        return string.Empty;
    }

    private static string AppRoot() =>
        Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根。");
    }
}
