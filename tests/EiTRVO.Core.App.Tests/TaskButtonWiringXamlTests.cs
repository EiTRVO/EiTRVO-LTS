using System.IO;
using System.Text.RegularExpressions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// S9-b：「任务」按钮（规范稿 D4 的当前稿 (a)）的机械门禁。
/// </summary>
/// <remarks>
/// 2026-10-21 用户要求去掉 p01（事件区块）/ p02 / p05 / p06（页头）/ p07 / p10 / p17（两处）/ 实例总览（两处）的「任务」按钮后，
/// 只剩 p06 事件区块那一处；2026-10-24 下载页面的事件部分也整体删除，因此现在一处都不应有。
/// </remarks>
[TestClass]
public sealed class TaskButtonWiringXamlTests
{
    [TestMethod]
    public void EveryTaskButtonIsEitherWiredToDownloadsOrExplicitlyDisabled()
    {
        var views = Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Views");
        var tags = new List<(string File, string Tag)>();

        foreach (var file in Directory.GetFiles(views, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "<c:TaskButton\\b[^>]*/>"))
            {
                tags.Add((Path.GetFileName(file), match.Value));
            }
        }

        Assert.AreEqual(0, tags.Count, "「任务」按钮应已全部移除；如果新增，请说明它为什么存在以及接到哪里。");
    }

    [TestMethod]
    public void TheDisabledTaskButtonTooltipExistsInBothLanguages()
    {
        var zh = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Resources", "Strings.resx"));
        var en = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "EiTRVO.Core.App", "Resources", "Strings.en.resx"));
        const string key = "当前已在「下载与队列」页，任务显示在下方队列里。";

        StringAssert.Contains(zh, key, "禁用的「任务」按钮的解释文案不在简体中文资源里。");
        StringAssert.Contains(en, key, "英文资源缺少同一个键（键就是中文原文）。");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EiTRVO.Core.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（EiTRVO.Core.sln）。");
    }
}
