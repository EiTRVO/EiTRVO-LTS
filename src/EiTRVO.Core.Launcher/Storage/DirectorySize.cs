namespace EiTRVO.Core.Launcher.Storage;

/// <summary>
/// 目录占用空间的估算（实例目录、存档目录都用它）。
/// </summary>
/// <remarks>
/// 刻意做成**一处实现**：实例页的「占用空间」与存档表的「文件大小」是同一个问题，
/// 各写一份的后果是其中一份忘了挡重解析点 —— 而那个后果是"界面线程在这个目录上转不出来"。
/// <para>
/// 两件事是刻意选择的：<b>遇到重解析点不跟进去</b>（一个指向上层的 Junction 就能让遍历永远走不完），
/// 以及<b>单个文件读不到就跳过</b>（占用空间只是估算，不该让整页失败）。
/// </para>
/// </remarks>
public static class DirectorySize
{
    /// <summary>递归统计目录字节数。</summary>
    /// <param name="directory">目录（不存在时返回 0）。</param>
    /// <returns>字节数。</returns>
    public static long Measure(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }
}
