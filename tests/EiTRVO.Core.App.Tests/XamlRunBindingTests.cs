using System.IO;
using System.Text.RegularExpressions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// XAML 里 <c>Run</c> 的 <c>Text</c> 绑定必须显式写 <c>Mode=OneWay</c>。
/// </summary>
/// <remarks>
/// **这是一条真机崩溃的回归用例**（2026-10-14，M7-a 拍截图时暴露）：
/// <c>Run.Text</c> 的绑定**默认是 TwoWay**（与 <c>TextBlock.Text</c> 不同），于是
/// <c>&lt;Run Text="{Binding 只读属性}"/&gt;</c> 会在 **WPF 解析 XAML、把绑定挂上去的那一刻**抛
/// <c>InvalidOperationException</c>："无法对只读属性进行 TwoWay 或 OneWayToSource 绑定" ——
/// 整个页面直接崩掉（`OnDispatcherUnhandledException` → 写一条 Critical 事件 → 显示"启动器遇到问题"）。
/// <para>
/// 为什么既有的 <c>Run</c> 一直没事：它们全都写 <c>{loc:Text …}</c>，而那个标记扩展**显式**设了
/// <see cref="System.Windows.Data.BindingMode.OneWay"/>（<c>TextExtension</c> 第 37 行）。
/// 也就是说这条陷阱一直存在，只是被标记扩展挡住了。
/// </para>
/// <para>
/// **为什么单测先前抓不到**：崩溃发生在 XAML 加载期，而 <c>EmptyFirstRunTests</c> 那一类是直接
/// `new` ViewModel 再读属性（根本不解析 XAML）。所以这里用**扫源码**的方式兜住 ——
/// 与 `LocalizationTests` 扫文案键同一类做法，并且带一条"至少扫到 N 个 `Run`"的下限，
/// 免得扫描器悄悄匹配不到任何东西还以为通过了。
/// </para>
/// </remarks>
[TestClass]
public sealed partial class XamlRunBindingTests
{
    /// <summary>至少该扫到这么多 <c>Run</c> 元素；低于它就是扫描逻辑坏了（当前 13 个，其中 2 个是绑定）。</summary>
    private const int MinimumRuns = 12;

    [TestMethod]
    public void EveryRunBindingIsExplicitlyOneWay()
    {
        var appRoot = FindAppSourceRoot();
        var runs = 0;
        List<string> offenders = [];

        foreach (var file in Directory
                     .EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
                     .Where(static path => !path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                                           && !path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match element in RunElement().Matches(text))
            {
                runs++;

                // `{loc:Text …}` 是标记扩展（自己设 OneWay），不是绑定，不在此列。
                foreach (Match binding in BindingExpression().Matches(element.Value))
                {
                    if (!binding.Value.Contains("Mode=OneWay", StringComparison.Ordinal))
                    {
                        offenders.Add($"{name}: {binding.Value.Trim()}");
                    }
                }
            }
        }

        Assert.IsTrue(
            runs >= MinimumRuns,
            $"只扫到 {runs} 个 Run 元素（应 ≥ {MinimumRuns}），说明扫描逻辑已经失效了。");

        Assert.AreEqual(
            0,
            offenders.Count,
            "Run.Text 的绑定默认是 TwoWay，绑到只读属性会在页面加载时抛异常并崩掉整页 —— 必须写 Mode=OneWay："
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(10)));
    }

    private static string FindAppSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
            {
                return Path.Combine(directory.FullName, "src", "EiTRVO.Core.App");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（EiTRVO.Core.sln）。");
    }

    /// <summary>一个 <c>&lt;Run … /&gt;</c> 元素（自闭合；本项目里的 Run 都是自闭合的）。</summary>
    [GeneratedRegex(@"<Run\b[^>]*/>", RegexOptions.Singleline)]
    private static partial Regex RunElement();

    /// <summary>元素属性里的一个 <c>{Binding …}</c> 表达式。</summary>
    [GeneratedRegex(@"\{Binding\s[^}]*\}")]
    private static partial Regex BindingExpression();
}
