using System.IO;
using System.Text.RegularExpressions;
using EiTRVO.Core.App.ViewModels;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// p06 队列行的模板（M7-g 新加的行内动作）必须与 <see cref="DownloadQueueRowViewModel"/> 对得上。
/// </summary>
/// <remarks>
/// 为什么单独写一条：**XAML 里绑一个不存在的属性、或引一个不存在的 StaticResource，
/// 编译、七个门禁、所有 ViewModel 单测都不会报错** —— 它只在"那一行真的出现"的那一刻才炸，
/// 而这一页的模板只在队列里有行时才实例化（M7-g 拍照时这台机器的队列是空的，
/// 所以连截图都验证不到它）。两个断言分别对应这两种静默失效：
/// <list type="number">
/// <item>模板里每个 <c>{Binding X}</c> 都要是投影上的属性（<c>DataContext.*</c> 那种页面级路径除外）；</item>
/// <item>模板里每个 <c>{StaticResource X}</c> 都要在 <c>Resources\*.xaml</c> 里真的定义过。</item>
/// </list>
/// </remarks>
[TestClass]
public sealed class QueueRowTemplateTests
{
    /// <summary>队列模板里至少该扫到这么多绑定 / 资源引用（低于它就是扫描逻辑失效了）。</summary>
    private const int MinimumBindings = 8;

    /// <summary>同一个文件里加的一处模板级资源引用，也必须能解析到。</summary>
    private const int MinimumResources = 6;

    [TestMethod]
    public void EveryBindingInTheQueueRowTemplateExistsOnTheRowProjection()
    {
        var template = QueueRowTemplate();
        var available = typeof(DownloadQueueRowViewModel)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var used = new SortedSet<string>(StringComparer.Ordinal);

        // `{Binding DataContext.QueueActionCommand, RelativeSource=…}` 是**页面级**的路径
        // （命令在页面上，不在行上），按前缀跳过。
        foreach (Match match in Regex.Matches(template, @"\{Binding\s+([A-Za-z_][\w.]*)"))
        {
            var path = match.Groups[1].Value;

            if (!path.StartsWith("DataContext.", StringComparison.Ordinal))
            {
                used.Add(path);
            }
        }

        Assert.IsTrue(used.Count >= MinimumBindings, $"只扫到 {used.Count} 个行绑定 —— 扫描逻辑失效了。");

        var missing = used.Where(name => !available.Contains(name)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些绑定路径在队列行投影上不存在（那一行出现时界面上会静默留空）：" + string.Join(" / ", missing));
    }

    [TestMethod]
    public void EveryStaticResourceInTheQueueRowTemplateIsDefined()
    {
        var template = QueueRowTemplate();
        var defined = DefinedResourceKeys();

        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(template, @"\{StaticResource\s+([A-Za-z_][\w.]*)\}"))
        {
            used.Add(match.Groups[1].Value);
        }

        Assert.IsTrue(used.Count >= MinimumResources, $"只扫到 {used.Count} 个资源引用 —— 扫描逻辑失效了。");

        var missing = used.Where(key => !defined.Contains(key)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些 StaticResource 键在 Resources\\*.xaml 里没有定义（那一行出现时页面会崩）：" + string.Join(" / ", missing));
    }

    [TestMethod]
    public void TheQueueTitleTrimsWithAnEllipsisInsteadOfOverlappingTheDetail()
    {
        var template = QueueRowTemplate();
        var title = template.IndexOf("Text=\"{Binding Title}\"", StringComparison.Ordinal);

        Assert.IsTrue(title > 0, "队列表模板里找不到标题的 Title 绑定。");

        // 标题列是固定宽度，右侧明细列从它后面开始；标题不裁剪就会盖到「已下载 / 总计」上。
        var elementStart = template.LastIndexOf("<TextBlock", title, StringComparison.Ordinal);
        var elementEnd = template.IndexOf("/>", title, StringComparison.Ordinal);
        Assert.IsTrue(elementStart > 0 && elementEnd > title, "标题的 TextBlock 没有正常闭合。");

        StringAssert.Contains(template[elementStart..elementEnd], "TextTrimming=\"CharacterEllipsis\"");
    }

    /// <summary>p06 队列那一段模板的原文。</summary>
    /// <returns>模板文本。</returns>
    private static string QueueRowTemplate()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "Pages", "P06DownloadsView.xaml");
        var text = File.ReadAllText(path);

        var control = text.IndexOf("ItemsSource=\"{Binding Queue}\"", StringComparison.Ordinal);
        Assert.IsTrue(control > 0, "P06DownloadsView.xaml 里找不到队列的 ItemsControl —— 模板搬家了？");

        // 从 `ItemTemplate` 开始取：`{Binding Queue}` 本身是**页面级**的（列表内容），不是行上的属性。
        var start = text.IndexOf("<ItemsControl.ItemTemplate>", control, StringComparison.Ordinal);
        Assert.IsTrue(start > control, "队列的 ItemsControl 没有 ItemTemplate？");

        var end = text.IndexOf("</ItemsControl>", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start, "队列的 ItemsControl 没有闭合？");

        return text[start..end];
    }

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
