namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 事件键 / 用户文本键两把尺子共用的扫描器（<see cref="KernelTextScan"/>）自己的用例。
/// </summary>
/// <remarks>
/// 为什么单独测它：**扫描器失效是静默的**（AGENTS.md §5.3 那条"漏检的检查比没有检查更糟"）。
/// 2026-10-09（M6-e-3d）实测到其中一面：参数表里插一行 `// …` 注释之后，`args[3]` 以注释开头，
/// 于是「第 4 个参数是不是 `[...]` 列表」被数成 **0 个参数** —— 同一个机制既会**假红**
/// （本次就是被它拦住的），也能让"占位符 == 参数"这条检查**静默通过**。
/// </remarks>
[TestClass]
public sealed class KernelTextScanTests
{
    [TestMethod]
    public void ArgumentsAreSplitIgnoringLineAndBlockComments()
    {
        var call = """
            Information,
            Source,
            "已创建实例：{0}（{1}）",
            // 事件参数是排障数据：用 DataDisplay（ASCII、与语言无关），不是界面上的显示名。
            /* 顺带一句块注释（里面还有 ( ) [ ] 与 , 都不该算进参数） */
            [name, InstanceNaming.DataDisplay(request.Loader, request.LoaderVersion)],
            name
            """;

        var args = KernelTextScan.SplitArguments(call);

        Assert.AreEqual(5, args.Count, "注释不该被当成参数：" + string.Join(" | ", args));
        Assert.IsTrue(args[3].TrimStart().StartsWith('['), $"第 4 个参数应当是列表：{args[3]}");
        Assert.AreEqual(2, KernelTextScan.CountItems(args[3]), "列表里是两项。");
        Assert.AreEqual(2, KernelTextScan.PlaceholderCount("已创建实例：{0}（{1}）"));
    }

    [TestMethod]
    public void CommentsInsideStringsAreNotComments()
    {
        var args = KernelTextScan.SplitArguments("\"见 https://example.com/a//b\", \"x\"");

        Assert.AreEqual(2, args.Count);
        StringAssert.Contains(args[0], "//b", "字符串里的 // 不是注释，不能被吃掉。");
    }
}
