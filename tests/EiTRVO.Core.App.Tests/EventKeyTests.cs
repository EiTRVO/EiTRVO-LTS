using System.IO;
using System.Text.RegularExpressions;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-b：内核事件键与资源、以及与**参数个数**的对账。
/// </summary>
/// <remarks>
/// Q8 定的形状是「内核给键 + 参数，App 层用 `.resx` 拼句子」。两件事都只能靠机械检查兜住：
/// <list type="number">
/// <item><b>键必须都在资源里</b> —— 缺了不会编译失败，中文界面看起来还"正常"（键就是中文原文），
/// 只有切成英文才会原样露出中文；</item>
/// <item><b>键里的占位符个数必须等于调用点传的参数个数</b> —— 少于占位符时 `Loc.T` 会抛
/// `FormatException`（事件表整块崩掉），多于占位符时**参数被静默丢掉**
/// （这正是 M6-a 真机截图暴露的问题：`版本安装完成` + `["26.1","1","4827"]` 只剩前半句）。</item>
/// </list>
/// <para>
/// **扫描必须是"真的解析调用"，不能只是正则匹配一个字面量**：第一版只认
/// `Write(级别, 来源, "字面量", [参数])`，于是漏掉了 **11 个键** —— 它们的键是三元表达式的两个分支
/// （写成 `failures.IsEmpty ? "下载完成" : "下载完成但有文件失败"` 那种三元表达式）或带 `（）` 的变体，
/// 其中就包括 3 个参数的「下载完成」与 5 个参数的「已回滚这次安装的写入」。
/// 漏检的检查比没有检查更糟：它会对 11 个未收口的键一声不响。所以这里按**括号配对**取整个调用，
/// 再按顶层逗号切参数，并收集第 3 个参数里**所有**中文字面量。
/// </para>
/// </remarks>
[TestClass]
public sealed class EventKeyTests
{
    /// <summary>至少该扫到这么多键；低于它就是扫描逻辑坏了（BCD 交付后是 **143** 个）。</summary>
    private const int MinimumKeys = 114;

    [TestMethod]
    public void EveryKernelEventKeyIsInTheResourcesAndMatchesItsParameterCount()
    {
        var root = KernelTextScan.RepositoryRoot();
        var available = LocalizationService.KeysOf(LocalizationService.Chinese).ToHashSet(StringComparer.Ordinal);
        List<(string Key, int Args, string Where)> sites = [];

        foreach (var file in KernelTextScan.KernelSourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // 事件的写入口：诊断日志的 Write。
            foreach (var call in KernelTextScan.CallsOf(text, "Write"))
            {
                Collect(call, name, sites);
            }

            // 皮肤服务把键交给 Finish(账户, 档案, "键", [参数]) 再写事件 —— 那一行看不见键；
            // 设备代码流也有个同名 Finish，但它是 (状态, 原因, 进度)、第 4 个参数不是参数表，
            // 原因是 TextTemplate（它的键由 UserTextKeyTests 管），所以按"第 4 个参数是 [] 列表"区分。
            foreach (var call in KernelTextScan.CallsOf(text, "Finish"))
            {
                Collect(call, name, sites, requireParameterList: true);
            }
        }

        var distinct = sites.Select(site => site.Key).Distinct(StringComparer.Ordinal).Count();

        Assert.IsTrue(
            distinct >= MinimumKeys,
            $"只扫到 {distinct} 个事件键（应 ≥ {MinimumKeys}），说明扫描逻辑已经失效了。");

        AssertSitesAreSound(sites, available);
    }

    /// <summary>
    /// M6-f：**App 侧写下的事件键**也要对账。
    /// </summary>
    /// <remarks>
    /// 起因是一个真缺陷：<c>CrashHandler</c>（在 App 里）写
    /// <c>_diagnostics.Write(级别, 来源, "启动器发生未处理的异常", [来源, 类型, 消息, 调用栈])</c>，
    /// 而那个键**两份资源里都没有**、也**没有任何占位符** ⇒ 事件表里只剩一句干巴巴的
    /// `启动器发生未处理的异常`，异常详情（含调用栈）被**静默丢掉**。
    /// <para>
    /// 两把既有尺子都够不着它：`EveryKernelEventKeyIsInTheResources…` 只扫内核两个工程，
    /// `EveryTextKeyUsedInXamlOrViewModelsExistsInTheResources` 只认 <c>Loc.T(...)</c> 字面量。
    /// 这条补上第三个入口 —— "崩溃路径写进事件流**且可读**"（F-6）从此有机械保障。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void EveryAppEventKeyIsInTheResourcesAndMatchesItsParameterCount()
    {
        var root = KernelTextScan.RepositoryRoot();
        var available = LocalizationService.KeysOf(LocalizationService.Chinese).ToHashSet(StringComparer.Ordinal);
        List<(string Key, int Args, string Where)> sites = [];

        foreach (var file in KernelTextScan.AppSourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var call in KernelTextScan.CallsOf(text, "Write"))
            {
                Collect(call, name, sites);
            }
        }

        Assert.IsTrue(
            sites.Count >= 1,
            "只扫到 " + sites.Count + " 个 App 侧事件键，说明扫描逻辑已经失效了（崩溃路径至少有一个）。");

        AssertSitesAreSound(sites, available);
    }

    /// <summary>三条对账（与内核那把尺子逐字相同）：键在资源里 / 占位符 == 参数 / 同一键参数个数一致。</summary>
    private static void AssertSitesAreSound(
        List<(string Key, int Args, string Where)> sites,
        HashSet<string> available)
    {

        // ① 键都在资源里。
        var missing = sites
            .Where(site => !available.Contains(site.Key))
            .Select(site => site.Key + "  (" + site.Where + ")")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些事件键不在资源里（英文界面会原样显示中文键）：" + string.Join(" / ", missing.Take(8)));

        // ② 占位符个数 == 参数个数。
        var mismatched = sites
            .Where(site => KernelTextScan.PlaceholderCount(site.Key) != site.Args)
            .Select(site => $"{site.Where}: 「{site.Key}」有 {KernelTextScan.PlaceholderCount(site.Key)} 个占位符，但传了 {site.Args} 个参数")
            .ToArray();

        Assert.AreEqual(
            0,
            mismatched.Length,
            "事件键的占位符与参数对不上（少了会抛 FormatException，多了会把参数静默丢掉）："
            + Environment.NewLine + string.Join(Environment.NewLine, mismatched.Take(8)));

        // ③ 同一个键在所有调用点必须传同样多的参数 —— 否则一个模板服务不了两处。
        var inconsistent = sites
            .GroupBy(site => site.Key, StringComparer.Ordinal)
            .Where(group => group.Select(site => site.Args).Distinct().Count() > 1)
            .Select(group => group.Key + " -> " + string.Join(" / ", group.Select(site => site.Args).Distinct()))
            .ToArray();

        Assert.AreEqual(
            0,
            inconsistent.Length,
            "同一个事件键在不同调用点传的参数个数不同：" + string.Join(" / ", inconsistent.Take(5)));
    }

    /// <summary>
    /// 从第 3 个参数里收集所有中文字面量（三元表达式的两个分支都算），并记下这次的参数个数。
    /// </summary>
    /// <param name="call">一次调用的参数文本（不含外层括号）。</param>
    /// <param name="where">文件名（报错用）。</param>
    /// <param name="sites">收集结果。</param>
    /// <param name="requireParameterList">第 4 个参数必须是 `[...]` 列表（区分同名的 Finish 重载）。</param>
    private static void Collect(
        string call,
        string where,
        List<(string Key, int Args, string Where)> sites,
        bool requireParameterList = false)
    {
        var args = KernelTextScan.SplitArguments(call);

        if (args.Count < 3)
        {
            return;
        }

        if (requireParameterList && (args.Count < 4 || !args[3].TrimStart().StartsWith('[')))
        {
            return;
        }

        var parameterCount = args.Count >= 4 ? KernelTextScan.CountItems(args[3]) : 0;

        foreach (Match match in Regex.Matches(args[2], "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            var key = match.Groups[1].Value;

            if (Regex.IsMatch(key, @"[\u4e00-\u9fff]"))
            {
                sites.Add((key, parameterCount, where));
            }
        }
    }
}
