using System.IO;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M6-e：内核那些**要显示给用户**的文本（失败原因、启动详情、账户提示）与资源的对账。
/// </summary>
/// <remarks>
/// 与 <c>EventKeyTests</c>（事件键）同源，但管的是另一半：事件键进的是日志表，
/// 这些键进的是**提示条、对话框与失败原因**。<see cref="Launcher.Text.TextTemplate"/> 是它们唯一合法的形状 ——
/// 键（简体中文原文）+ 参数，句子由 App 层用 <c>.resx</c> 拼。
/// <para>
/// 四条断言，每一条都对应一种"不会报错但会骗人"的坏法：
/// <list type="number">
/// <item><b>键都在资源里</b>：缺了在中文界面看起来完全正常（键就是中文原文），只有英文界面会原样露出中文；</item>
/// <item><b>占位符个数 == 参数个数</b>：少了会抛 <c>FormatException</c>，多了会把参数**静默丢掉**
/// （M6-a 的真机截图就是这么暴露"版本安装完成"少了半句话的）；</item>
/// <item><b>同一个键在所有调用点参数个数一致</b>：一个模板服务不了两种形状（M6-b 的
/// <c>FileJavaRuntimeCatalog</c> 就是因此拆成两条事件）；</item>
/// <item><b>参数里不许有中文字面量</b>：参数是**原样显示**的，塞一句中文进去，
/// 英文界面就会在英文句子里露出三个汉字（这一条不是假想 —— 第一版就写了
/// <c>Of("刷新令牌失败：{0}。", … ?? "未知原因")</c>）。</item>
/// </list>
/// </para>
/// </remarks>
[TestClass]
public sealed class UserTextKeyTests
{
    /// <summary>至少该扫到这么多键；低于它就是扫描逻辑坏了（BCD 交付后是 **368** 个）。</summary>
    private const int MinimumKeys = 330;

    [TestMethod]
    public void TemplatesWithTheSameKeyAndParametersAreEqual()
    {
        // record 自动生成的相等性对列表成员用的是**引用相等**，于是"内容相同的两个模板"会不相等 ——
        // 那种相等性会在比对快照、去重、测试断言上静默出错。TextTemplate 自己实现了值相等（含 Note）。
        Assert.AreEqual(TextTemplate.Of("等待中"), TextTemplate.Of("等待中"));
        Assert.AreEqual(TextTemplate.Of("{0} MB", "4096"), TextTemplate.Of("{0} MB", "4096"));
        Assert.AreNotEqual(TextTemplate.Of("{0} MB", "4096"), TextTemplate.Of("{0} MB", "8192"));
        Assert.AreNotEqual(TextTemplate.Of("{0} MB", "4096"), TextTemplate.Of("这个版本需要 Java {0}", "4096"));
        Assert.AreEqual(
            TextTemplate.Of("下载未完成。") with
            {
                Note = TextTemplate.Of(
                    "已回滚这次安装写入的 {0} 项（文件 {1}、目录 {2}、恢复 {3}）。",
                    "1",
                    "1",
                    "0",
                    "0"),
            },
            TextTemplate.Of("下载未完成。") with
            {
                Note = TextTemplate.Of(
                    "已回滚这次安装写入的 {0} 项（文件 {1}、目录 {2}、恢复 {3}）。",
                    "1",
                    "1",
                    "0",
                    "0"),
            });
        Assert.AreNotEqual(
            TextTemplate.Of("下载未完成。") with { Note = TextTemplate.Of("安装器最后说：{0}", "x") },
            TextTemplate.Of("下载未完成。"));
    }

    [TestMethod]
    public void EveryKernelUserTextKeyIsInTheResourcesAndMatchesItsParameterCount()
    {
        var root = KernelTextScan.RepositoryRoot();
        var chinese = LocalizationService.KeysOf(LocalizationService.Chinese).ToHashSet(StringComparer.Ordinal);
        var english = LocalizationService.KeysOf(LocalizationService.English).ToHashSet(StringComparer.Ordinal);

        List<(string Key, int Args, string Where)> sites = [];
        List<string> chineseParameters = [];

        foreach (var file in KernelTextScan.KernelSourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var call in KernelTextScan.CallsOf(text, "Of"))
            {
                Collect(call, name, sites, chineseParameters);
            }
        }

        var distinct = sites.Select(site => site.Key).Distinct(StringComparer.Ordinal).Count();

        Assert.IsTrue(
            distinct >= MinimumKeys,
            $"只扫到 {distinct} 个用户可见文本键（应 ≥ {MinimumKeys}），说明扫描逻辑已经失效了。");

        // ① 键都在资源里（两种语言都要有：只有中文的键在英文界面上会露出中文）。
        var missing = sites
            .Where(site => !chinese.Contains(site.Key) || !english.Contains(site.Key))
            .Select(site => site.Key + "  (" + site.Where + ")")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "这些文本键不在资源里（英文界面会原样显示中文）：" + Environment.NewLine
            + string.Join(Environment.NewLine, missing.Take(12)));

        // ② 占位符个数 == 参数个数。
        var mismatched = sites
            .Where(site => KernelTextScan.PlaceholderCount(site.Key) != site.Args)
            .Select(site => $"{site.Where}: 「{site.Key}」有 {KernelTextScan.PlaceholderCount(site.Key)} 个占位符，但传了 {site.Args} 个参数")
            .ToArray();

        Assert.AreEqual(
            0,
            mismatched.Length,
            "文本键的占位符与参数对不上（少了会抛 FormatException，多了会把参数静默丢掉）："
            + Environment.NewLine + string.Join(Environment.NewLine, mismatched.Take(12)));

        // ③ 同一个键在所有调用点必须传同样多的参数。
        var inconsistent = sites
            .GroupBy(site => site.Key, StringComparer.Ordinal)
            .Where(group => group.Select(site => site.Args).Distinct().Count() > 1)
            .Select(group => group.Key + " -> " + string.Join(" / ", group.Select(site => site.Args).Distinct()))
            .ToArray();

        Assert.AreEqual(
            0,
            inconsistent.Length,
            "同一个文本键在不同调用点传的参数个数不同（一个模板服务不了两种形状）："
            + string.Join(" / ", inconsistent.Take(5)));

        // ④ 参数里不许有中文字面量。
        Assert.AreEqual(
            0,
            chineseParameters.Count,
            "参数会**原样显示**，不能塞中文字面量（英文界面会露出汉字）—— 把那段文字做成自己的键："
            + Environment.NewLine + string.Join(Environment.NewLine, chineseParameters.Take(12)));
    }

    /// <summary>
    /// 从一次 <c>TextTemplate.Of(...)</c> 调用里收集键、参数个数，并检查参数里有没有中文字面量。
    /// </summary>
    /// <param name="call">一次调用的参数文本（不含外层括号）。</param>
    /// <param name="where">文件名（报错用）。</param>
    /// <param name="sites">收集到的键。</param>
    /// <param name="chineseParameters">参数里出现的中文字面量。</param>
    private static void Collect(
        string call,
        string where,
        List<(string Key, int Args, string Where)> sites,
        List<string> chineseParameters)
    {
        var args = KernelTextScan.SplitArguments(call);

        if (args.Count == 0)
        {
            return;
        }

        // 方法名匹配会把**定义**也匹配进来（`public static TextTemplate Of(string key, ...)`）：
        // 第一个参数不是字面量时就跳过（定义处的参数名是 key/parameters，不是中文键）。
        var key = KernelTextScan.ChineseLiterals(args[0]).FirstOrDefault();

        if (key is null)
        {
            return;
        }

        sites.Add((key, args.Count - 1, where));

        for (var index = 1; index < args.Count; index++)
        {
            foreach (var literal in KernelTextScan.ChineseLiterals(args[index]))
            {
                chineseParameters.Add($"{where}: 参数 {index} = 「{literal}」（键：{key}）");
            }
        }
    }
}
