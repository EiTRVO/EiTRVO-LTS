using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using EiTRVO.Core.App.ViewModels;
using EiTRVO.Core.App.ViewModels.Pages;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// p09 的资源包列表与动作条必须与 <see cref="ResourcePackRowViewModel"/> / <see cref="ResourcesViewModel"/> 对得上（M7-g-2b）。
/// </summary>
/// <remarks>
/// 与 <c>ModsWiringXamlTests</c> 同一个理由：**XAML 里绑一个不存在的属性、或把命令绑到不存在的方法上，
/// 编译、七个门禁、所有 ViewModel 单测都不会报错** —— 它只在那一块真的被实例化时静默留空或让按钮"点了没反应"。
/// 资源包列表只在**有资源包**时才实例化，而这台机器的实例一个资源包都没有，截图照不出来。
/// </remarks>
[TestClass]
public sealed class ResourcesWiringXamlTests
{
    /// <summary>列表项模板里至少该扫到这么多绑定（低于它就是扫描逻辑失效了）。</summary>
    private const int MinimumRowBindings = 3;

    /// <summary>页面上至少该扫到这么多命令绑定。</summary>
    private const int MinimumCommands = 8;

    [TestMethod]
    public void EveryBindingInTheResourcePackRowTemplateExistsOnTheRowProjection()
    {
        var template = ResourcePackRowTemplate();
        var available = typeof(ResourcePackRowViewModel)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(template, @"\{Binding\s+([A-Za-z_][\w.]*)"))
        {
            used.Add(match.Groups[1].Value);
        }

        Assert.IsTrue(used.Count >= MinimumRowBindings, $"只扫到 {used.Count} 个行绑定 —— 扫描逻辑失效了。");

        var missing = used.Where(name => !available.Contains(name)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些绑定路径在资源包行投影上不存在（那一行出现时界面上会静默留空）：" + string.Join(" / ", missing));
    }

    [TestMethod]
    public void EveryCommandInTheResourcesPageExistsOnTheViewModelAsAnICommand()
    {
        var text = File.ReadAllText(ViewPath());
        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(text, @"Command=""\{Binding\s+([A-Za-z_][\w.]*)"))
        {
            var path = match.Groups[1].Value;

            if (!path.Contains('.'))
            {
                used.Add(path);
            }
        }

        Assert.IsTrue(used.Count >= MinimumCommands, $"只扫到 {used.Count} 个命令绑定 —— 扫描逻辑失效了。");

        var commands = typeof(ResourcesViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(static property => property.Name, StringComparer.Ordinal);

        foreach (var name in used)
        {
            Assert.IsTrue(commands.ContainsKey(name), $"p09 绑定的命令「{name}」在 ResourcesViewModel 上不存在（按钮会静默无效）。");
            Assert.IsTrue(
                typeof(ICommand).IsAssignableFrom(commands[name].PropertyType),
                $"「{name}」不是 ICommand（XAML 会绑定失败）。");
        }

        // 资源包列表必须把选中行交给 ViewModel（否则五颗按钮永远没有目标），并且列表本身接到投影列表上。
        StringAssert.Contains(
            text,
            "SelectedItem=\"{Binding SelectedResourcePack",
            "资源包列表必须把选中行交给 ViewModel。");
        StringAssert.Contains(
            text,
            "ItemsSource=\"{Binding ResourcePacks}\"",
            "资源包列表必须绑到 ResourcePacks 上。");
    }

    [TestMethod]
    public void EveryStaticResourceInTheResourcesPageIsDefined()
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

    /// <summary>p09 资源包列表项模板那一段的原文。</summary>
    /// <returns>模板文本。</returns>
    private static string ResourcePackRowTemplate()
    {
        var text = File.ReadAllText(ViewPath());

        var start = text.IndexOf("<ListBox.ItemTemplate>", StringComparison.Ordinal);
        Assert.IsTrue(start > 0, "P09ResourcesView.xaml 里找不到资源包列表的项模板 —— 模板搬家了？");

        var end = text.IndexOf("</ListBox.ItemTemplate>", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start, "资源包列表的项模板没有闭合？");

        return text[start..end];
    }

    private static string ViewPath() =>
        Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", "P09ResourcesView.xaml");

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
