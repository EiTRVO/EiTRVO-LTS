using System.IO;
using System.Text.RegularExpressions;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 扫内核源码的两把公共尺子：**取一次调用**与**数占位符**。
/// </summary>
/// <remarks>
/// 两条门禁都要用它们：事件键（<c>EventKeyTests</c>）与用户可见文本键（<c>UserTextKeyTests</c>）。
/// 抽出来是因为"重复一份扫描逻辑"的下场很具体：两份实现迟早只改到一处，
/// 而扫描器一旦失效是**静默**的（M6-b 的第一版就漏了 11 个键，见 <c>EventKeyTests</c> 的说明）。
/// <para>
/// 铁律：扫描必须**真的解析调用**（括号配对 → 顶层逗号切参数），不能只正则匹配一种字面量写法。
/// </para>
/// </remarks>
internal static class KernelTextScan
{
    /// <summary>仓库根（从测试输出目录往上找 <c>EiTRVO.Core.sln</c>）。</summary>
    /// <returns>仓库根路径。</returns>
    public static string RepositoryRoot()
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

        throw new FileNotFoundException($"找不到仓库根（从 {AppContext.BaseDirectory} 往上找 EiTRVO.Core.sln）。");
    }

    /// <summary>内核两个工程的全部 <c>*.cs</c>（跳过 <c>obj</c>/<c>bin</c>）。</summary>
    /// <param name="root">仓库根。</param>
    /// <returns>文件列表。</returns>
    public static IEnumerable<string> KernelSourceFiles(string root)
    {
        foreach (var folder in new[] { "EiTRVO.Core.Launcher", "EiTRVO.Core.Auth" })
        {
            foreach (var file in Directory.EnumerateFiles(
                Path.Combine(root, "src", folder), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\", StringComparison.Ordinal) || file.Contains(@"\bin\", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>App 工程的全部 <c>*.cs</c>（跳过 <c>obj</c>/<c>bin</c>）。</summary>
    /// <remarks>
    /// M6-f 新增：App 侧也**会写事件**（`CrashHandler` 就是），而内核那把尺子只扫内核两个工程、
    /// App 那把只认 <c>Loc.T(...)</c> —— 于是它的键谁都没查（实测漏掉了一个）。
    /// </remarks>
    /// <param name="root">仓库根。</param>
    /// <returns>文件列表。</returns>
    public static IEnumerable<string> AppSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, "src", "EiTRVO.Core.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\", StringComparison.Ordinal) || file.Contains(@"\bin\", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }
    /// <summary>取某个方法名在源码里的每一次调用的参数文本（按括号配对，跳过字符串与注释里的括号）。</summary>
    /// <param name="text">源码。</param>
    /// <param name="methodName">方法名（如 <c>Write</c>）。</param>
    /// <returns>每次调用的参数文本。</returns>
    public static List<string> CallsOf(string text, string methodName)
    {
        List<string> calls = [];
        var pattern = new Regex(@"\b" + methodName + @"\s*\(");

        foreach (Match match in pattern.Matches(text))
        {
            var start = match.Index + match.Length;
            var depth = 1;
            var i = start;
            var inString = false;

            while (i < text.Length && depth > 0)
            {
                var ch = text[i];

                if (inString)
                {
                    if (ch == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (ch == '"')
                    {
                        inString = false;
                    }
                }
                else if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                }

                i++;
            }

            if (depth == 0)
            {
                calls.Add(text[start..(i - 1)]);
            }
        }

        return calls;
    }

    /// <summary>按**顶层**逗号切分参数文本（括号/方括号/花括号与字符串里的逗号不算分隔）。</summary>
    /// <param name="text">参数文本。</param>
    /// <returns>每个参数的原文。</returns>
    public static List<string> SplitArguments(string text)
    {
        List<string> parts = [];
        var depth = 0;
        var inString = false;
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                current.Append(ch);

                if (ch == '\\' && i + 1 < text.Length)
                {
                    current.Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            // 注释**不算参数内容**（2026-10-09 实测，M6-e-3d）：参数表里插一行 `// …` 会让
            // `args[3]` 以注释开头，于是「第 4 个参数是不是 `[...]` 列表」数成 0 个参数 ——
            // 既会造成假红，也能让"占位符 == 参数"这条检查**静默通过**（同一个机制的两个方向）。
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
            }

            current.Append(ch);
        }

        if (current.ToString().Trim().Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>数一个参数列表 `[...]` 里**顶层**有几项（尾部多写的逗号不算）。</summary>
    /// <param name="argument">参数原文。</param>
    /// <returns>项数。</returns>
    public static int CountItems(string argument)
    {
        var trimmed = argument.Trim();

        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
        {
            return 0;
        }

        var inner = trimmed[1..^1].Trim();

        if (inner.EndsWith(','))
        {
            inner = inner[..^1].TrimEnd();
        }

        return inner.Length == 0 ? 0 : SplitArguments(inner).Count;
    }

    /// <summary>键里的占位符个数（<c>{0}</c>、<c>{1}</c> …，必须是 0..n-1 连续）。</summary>
    /// <param name="key">模板键（= 简体中文原文模板）。</param>
    /// <returns>占位符个数。</returns>
    public static int PlaceholderCount(string key)
    {
        var indexes = new SortedSet<int>();

        foreach (Match match in Regex.Matches(key, @"\{(\d+)\}"))
        {
            indexes.Add(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (indexes.Count == 0)
        {
            return 0;
        }

        Assert.AreEqual(
            indexes.Max + 1,
            indexes.Count,
            $"「{key}」的占位符编号不连续（应该是 0..{indexes.Max}）：{string.Join(" ", indexes)}");

        return indexes.Count;
    }

    /// <summary>一段源码里所有中文字符串字面量。</summary>
    /// <param name="text">源码片段。</param>
    /// <returns>中文字面量。</returns>
    public static IEnumerable<string> ChineseLiterals(string text) =>
        Regex.Matches(text, "\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(match => match.Groups[1].Value)
            .Where(value => Regex.IsMatch(value, @"[\u4e00-\u9fff]"));
}
