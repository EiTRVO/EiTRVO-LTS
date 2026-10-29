using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using EiTRVO.Core.App.ViewModels;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 页头两个下拉菜单（S10-a / S10-b）的 XAML 接线：浮层必须真的绑到 ShellViewModel 的开关与命令上。
/// </summary>
/// <remarks>
/// 与 <c>ModsWiringXamlTests</c> 同一个理由：**绑一个不存在的属性 / 命令不会编译失败**，
/// 它只在浮层真的展开时才在界面上静默留空、或按钮点下去什么都不发生。
/// 而这台机器上"队列里没有任务 / 只有一个账户"，菜单的一部分模板平时根本实例化不到。
/// <para>
/// 另一条更值的钉住：队列按钮曾经**直达 p06**（2026-10-18 改成下拉）。把 <c>NavigateCommand</c>
/// 绑回去会让"点开下拉"悄悄变回"跳页"，而界面看起来完全正常。
/// </para>
/// </remarks>
[TestClass]
public sealed class ShellMenuWiringXamlTests
{
    /// <summary>菜单里至少该扫到这么多条命令绑定（低于它就是扫描逻辑失效了）。</summary>
    private const int MinimumMenuCommands = 7;

    [TestMethod]
    public void QueueButtonOpensTheMenuAndNoLongerNavigatesToTheDownloadsPage()
    {
        var text = File.ReadAllText(MainWindowPath());

        StringAssert.Contains(text, "Command=\"{Binding ToggleQueueMenuCommand}\"", "队列按钮必须展开菜单。");
        StringAssert.Contains(text, "Visibility=\"{Binding IsQueueMenuOpen, Converter={StaticResource BoolToVisibility}}\"", "队列浮层必须绑到 IsQueueMenuOpen。");
        StringAssert.Contains(text, "Command=\"{Binding QueueRowActionCommand}\"", "行内取消 / 重试要交给 QueueRowActionCommand。");
        StringAssert.Contains(text, "Command=\"{Binding OpenQueuePageCommand}\"", "「打开页面」要进 p06。");
        StringAssert.Contains(text, "Command=\"{Binding CancelAllQueueCommand}\"", "「取消全部」要真的取消。");
        StringAssert.Contains(text, "Command=\"{Binding OpenQueuePageCommand}\"", "行本体也要能打开 p06。");

        var queueBlock = Block(text, "Command=\"{Binding ToggleQueueMenuCommand}\"", "Command=\"{Binding ToggleAccountMenuCommand}\"");
        Assert.IsFalse(
            queueBlock.Contains("PageId.Downloads", StringComparison.Ordinal),
            "队列按钮不该再直接导航到下载页（2026-10-18 起是下拉菜单）。");
    }

    [TestMethod]
    public void AccountButtonOpensTheMenuWithAllFourActions()
    {
        var text = File.ReadAllText(MainWindowPath());

        StringAssert.Contains(text, "Command=\"{Binding ToggleAccountMenuCommand}\"", "账户按钮必须展开菜单。");
        StringAssert.Contains(text, "Visibility=\"{Binding IsAccountMenuOpen, Converter={StaticResource BoolToVisibility}}\"", "账户浮层必须绑到 IsAccountMenuOpen。");
        StringAssert.Contains(text, "DataContext.SelectAccountCommand", "点账户行要设为默认账户（经 ItemsControl 的 DataContext）。");
        StringAssert.Contains(text, "Command=\"{Binding AddMicrosoftAccountCommand}\"", "「添加微软账户」入口。");
        StringAssert.Contains(text, "Command=\"{Binding AddOfflineAccountCommand}\"", "「添加离线账户」入口。");
        StringAssert.Contains(text, "Command=\"{Binding ManageAccountsCommand}\"", "「管理账户」入口。");
    }

    [TestMethod]
    public void EveryCommandBoundInTheShellMenuBlockExistsOnShellViewModelAsAnICommand()
    {
        var text = File.ReadAllText(MainWindowPath());
        var block = Block(text, "<!-- ==================== 页头下拉浮层", "<!-- ==================== 模态层（p13 / p15） ====================");

        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(block, @"Command=""\{Binding\s+([A-Za-z_][\w.]*)"))
        {
            var path = match.Groups[1].Value;

            // 带点的是 `DataContext.X`（ItemsControl 里的行命令），由下面那条单独钉住。
            if (!path.Contains('.'))
            {
                used.Add(path);
            }
        }

        Assert.IsTrue(used.Count >= MinimumMenuCommands, $"只扫到 {used.Count} 条菜单命令绑定 —— 扫描逻辑失效了。");

        var properties = typeof(ShellViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(static property => property.Name, StringComparer.Ordinal);

        foreach (var name in used)
        {
            Assert.IsTrue(properties.ContainsKey(name), $"菜单绑定的命令「{name}」在 ShellViewModel 上不存在（按钮会静默无效）。");
            Assert.IsTrue(typeof(ICommand).IsAssignableFrom(properties[name].PropertyType), $"「{name}」不是 ICommand（XAML 会绑定失败）。");
        }

        // 行内动作由 ItemsControl 里的 DataContext 命令分派（队列行、账户行）。
        StringAssert.Contains(block, "DataContext.QueueRowActionCommand", "队列行里的动作按钮要经 ItemsControl 的 DataContext 找到外壳命令。");
        StringAssert.Contains(block, "DataContext.SelectAccountCommand", "账户行要经 ItemsControl 的 DataContext 找到外壳命令。");
        StringAssert.Contains(block, "DataContext.OpenQueuePageCommand", "队列行本体要经 ItemsControl 的 DataContext 找到外壳命令。");
    }

    [TestMethod]
    public void EveryStaticResourceInTheShellMenuBlockIsDefined()
    {
        var text = File.ReadAllText(MainWindowPath());
        var block = Block(text, "<!-- ==================== 页头下拉浮层", "<!-- ==================== 模态层（p13 / p15） ====================");
        var defined = DefinedResourceKeys();

        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(block, @"\{StaticResource\s+([A-Za-z_][\w.]*)\}"))
        {
            used.Add(match.Groups[1].Value);
        }

        Assert.IsTrue(used.Count >= 12, $"只扫到 {used.Count} 个资源引用 —— 扫描逻辑失效了。");

        var missing = used.Where(key => !defined.Contains(key)).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些 StaticResource 键在 Resources\\*.xaml 里没有定义（菜单展开时会崩）：" + string.Join(" / ", missing));
    }

    [TestMethod]
    public void TheMenuTemplatesOnlyBindPropertiesThatExistOnTheirProjections()
    {
        var queueRows = typeof(DownloadQueueRowViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[] { "Title", "Detail", "ActionText", "ActionDetail", "CanAct", "State", "IsRetry" })
        {
            Assert.IsTrue(queueRows.Contains(name), $"队列行少了「{name}」这一格（菜单里的绑定会静默留空）。");
        }

        var accountRows = typeof(ShellAccountRowViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[] { "PlayerName", "Meta", "IsDefault" })
        {
            Assert.IsTrue(accountRows.Contains(name), $"账户行少了「{name}」这一格（菜单里的绑定会静默留空）。");
        }

        var shell = typeof(ShellViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[]
                 {
                     "ActiveQueueTask", "ActiveQueuePercent", "ActiveQueueMeta", "WaitingQueueTasks",
                     "RecentQueueTasks", "QueueMenuSummary", "HasActiveQueueTask", "HasWaitingQueueTasks",
                     "HasRecentQueueTasks", "HasNoQueueTasks", "CanCancelAllQueueTasks",
                     "AccountRows", "AccountMenuSummary", "AccountMenuError", "HasAccountMenuError",
                     "HasAccounts", "HasNoAccounts", "IsQueueMenuOpen", "IsAccountMenuOpen", "IsAnyMenuOpen",
                 })
        {
            Assert.IsTrue(shell.Contains(name), $"ShellViewModel 少了「{name}」（菜单里的绑定会静默留空）。");
        }
    }

    [TestMethod]
    public void Menus_LiveInTheWindowOverlayAndCloseOnEscape()
    {
        var text = File.ReadAllText(MainWindowPath());

        StringAssert.Contains(text, "Visibility=\"{Binding IsAnyMenuOpen, Converter={StaticResource BoolToVisibility}}\"", "浮层总开关要绑 IsAnyMenuOpen。");
        StringAssert.Contains(text, "Command=\"{Binding CloseMenusCommand}\"", "点浮层外部要收起菜单。");
        StringAssert.Contains(text, "Key=\"Escape\"", "Esc 要能收起菜单。");
        StringAssert.Contains(text, "Panel.ZIndex=\"50\"", "浮层要盖住内容区。");
        Assert.IsFalse(text.Contains("<Popup", StringComparison.Ordinal), "浮层不要用 Popup（独立 HWND：偏移量在本项目里不可靠，且截图拍不到）。");
    }

    [TestMethod]
    public void TheMenuProgressBarBindsItsValueOneWay()
    {
        var text = File.ReadAllText(MainWindowPath());

        // ProgressBar.Value 的默认绑定模式是 **TwoWay**（与 Run.Text 同类陷阱）：
        // 绑一个只读的 ViewModel 属性会在**窗口加载的那一刻**抛 InvalidOperationException，
        // 整个窗口起不来 —— 编译、门禁、ViewModel 单测都看不见。
        var values = Regex.Matches(text, @"Value=""\{Binding\s+[A-Za-z_][\w.]*(?<mode>[^""]*)\}""");

        Assert.IsTrue(values.Count >= 1, "只扫到 0 个 Value 绑定 —— 扫描逻辑失效了。");

        foreach (Match match in values)
        {
            StringAssert.Contains(match.Groups["mode"].Value, "Mode=OneWay", "绑定到只读属性的 ProgressBar.Value 必须显式写 Mode=OneWay。");
        }
    }

    private static string Block(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"找不到菜单块起点「{startMarker}」—— 模板搬家了？");

        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"找不到菜单块终点「{endMarker}」—— 模板搬家了？");

        return text[start..end];
    }

    private static string MainWindowPath() =>
        Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views", "MainWindow.xaml");

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
