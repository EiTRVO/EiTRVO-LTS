using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// p07 的模组表与动作条必须与 <see cref="ModRowViewModel"/> / <see cref="ModsViewModel"/> 对得上（M7-g-2）。
/// </summary>
/// <remarks>
/// 与 <c>QueueRowTemplateTests</c> 同一个理由：**XAML 里绑一个不存在的属性、或引一个不存在的
/// StaticResource，编译、七个门禁、所有 ViewModel 单测都不会报错** —— 它只在那一块真的被实例化时
/// 才在界面上静默留空或直接抛。而这一页的表格模板只在"该实例有模组"时才实例化，
/// 这台机器上（1 个原版实例、0 个模组）连截图都照不到它。
/// <para>
/// 「命令绑错」比「属性绑错」更值得一条门禁：按钮会正常显示、点下去**什么都不发生**，
/// 而那正是 S9 要清零的"看起来可点但没行为"。
/// </para>
/// </remarks>
[TestClass]
public sealed class ModsWiringXamlTests
{
    /// <summary>表格模板里至少该扫到这么多行绑定 / 资源引用（低于它就是扫描逻辑失效了）。</summary>
    private const int MinimumRowBindings = 6;

    /// <summary>动作条 + 页面上至少该扫到这么多命令绑定。</summary>
    private const int MinimumCommands = 5;

    [TestMethod]
    public void EveryBindingInTheModsTableExistsOnTheRowProjection()
    {
        var columns = ModsTableColumns();
        var available = typeof(ModRowViewModel)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(columns, @"\{Binding\s+([A-Za-z_][\w.]*)"))
        {
            used.Add(match.Groups[1].Value);
        }

        Assert.IsTrue(used.Count >= MinimumRowBindings, $"只扫到 {used.Count} 个行绑定 —— 扫描逻辑失效了。");

        var missing = used.Where(name => !available.Contains(name)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些绑定路径在模组行投影上不存在（那一行出现时界面上会静默留空）：" + string.Join(" / ", missing));
    }

    [TestMethod]
    public void EveryCommandInTheModsPageExistsOnTheViewModelAsAnICommand()
    {
        var text = File.ReadAllText(ViewPath());
        var used = new SortedSet<string>(StringComparer.Ordinal);

        // `Command="{Binding DataContext.NavigateCommand, RelativeSource=…}"` 的路径带点：
        // 那是**外壳**的命令（页面级导航），不在本页 ViewModel 上，按"含点就跳过"处理。
        foreach (Match match in Regex.Matches(text, @"Command=""\{Binding\s+([A-Za-z_][\w.]*)"))
        {
            var path = match.Groups[1].Value;

            if (!path.Contains('.'))
            {
                used.Add(path);
            }
        }

        Assert.IsTrue(used.Count >= MinimumCommands, $"只扫到 {used.Count} 个命令绑定 —— 扫描逻辑失效了。");

        var commands = typeof(ModsViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(static property => property.Name, StringComparer.Ordinal);

        foreach (var name in used)
        {
            Assert.IsTrue(commands.ContainsKey(name), $"模组页绑定的命令「{name}」在 ModsViewModel 上不存在（按钮会静默无效）。");
            Assert.IsTrue(
                typeof(ICommand).IsAssignableFrom(commands[name].PropertyType),
                $"「{name}」不是 ICommand（XAML 会绑定失败）。");
        }

        // 选中行是这三颗按钮的输入：这条绑定被改掉的话，按钮会永远没有目标。
        StringAssert.Contains(
            text,
            "SelectedItem=\"{Binding SelectedMod",
            "模组表必须把选中行交给 ViewModel（否则「所选」三颗按钮永远没有目标）。");
    }

    [TestMethod]
    public void EveryStaticResourceInTheModsPageIsDefined()
    {
        var text = File.ReadAllText(ViewPath());
        var defined = DefinedResourceKeys();

        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(text, @"\{StaticResource\s+([A-Za-z_][\w.]*)\}"))
        {
            used.Add(match.Groups[1].Value);
        }

        Assert.IsTrue(used.Count >= 10, $"只扫到 {used.Count} 个资源引用 —— 扫描逻辑失效了。");

        var missing = used.Where(key => !defined.Contains(key)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些 StaticResource 键在 Resources\\*.xaml 里没有定义（页面加载时会崩）：" + string.Join(" / ", missing));
    }

    /// <summary>p07 表格列与单元格模板那一段的原文。</summary>
    /// <returns>模板文本。</returns>
    private static string ModsTableColumns()
    {
        var text = File.ReadAllText(ViewPath());

        var start = text.IndexOf("<DataGrid.Columns>", StringComparison.Ordinal);
        Assert.IsTrue(start > 0, "P07ModsView.xaml 里找不到模组表的列定义 —— 模板搬家了？");

        var end = text.IndexOf("</DataGrid.Columns>", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start, "模组表的列定义没有闭合？");

        return text[start..end];
    }

    private static string ViewPath() =>
        Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", "P07ModsView.xaml");

    /// <summary><c>Resources\*.xaml</c> 里定义过的全部 <c>x:Key</c>。</summary>
    /// <returns>键集合。</returns>
    private static HashSet<string> DefinedResourceKeys()
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Resources");
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*.xaml"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        Assert.IsTrue(keys.Count >= 100, $"只读到 {keys.Count} 个资源键 —— 扫描逻辑失效了。");
        return keys;
    }

    /// <summary>仓库根（从测试输出目录往上找 <c>EiTRVO.Core.sln</c>）。</summary>
    /// <returns>仓库根路径。</returns>
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
