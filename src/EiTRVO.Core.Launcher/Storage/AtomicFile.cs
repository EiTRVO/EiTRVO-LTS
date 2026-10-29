using System.Text;

namespace EiTRVO.Core.Launcher.Storage;

/// <summary>
/// 原子写：先写同目录下的临时文件，再整体替换目标文件。
/// <para>
/// 为什么必须要它：设置、账户、实例元数据都是**用户数据**。就地覆盖写入时若中途失败
/// （断电、磁盘满、进程被杀），文件会变成半截 JSON —— 下次启动就丢了用户的全部配置。
/// Neo 在部分路径上做了原子写，但实例元数据没有（见 <c>Core功能总清单.md</c> A-5 的备注）。
/// </para>
/// <para>
/// 替换用 <see cref="File.Replace(string, string, string)"/>：它同时把**上一版留成
/// <c>.bak</c>**，符合"写入前备份上一版"的既定要求（<c>后端实施规划.md</c> 风险 R7）。
/// 目标文件不存在时退化为 <see cref="File.Move(string, string)"/>。
/// </para>
/// </summary>
public static class AtomicFile
{
    /// <summary>备份文件后缀。</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>临时文件后缀（含随机段，避免并发写入互相踩）。</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>写入文本（UTF-8 无 BOM），写完后原子替换目标文件。</summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">要写入的内容。</param>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"路径没有目录部分：'{path}'。", nameof(path));

        Directory.CreateDirectory(directory);

        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Commit(tempPath, fullPath);
        }
        finally
        {
            // 失败时不能把临时文件留在用户数据目录里。
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>写入文本的异步版本。语义与 <see cref="WriteAllText"/> 完全一致。</summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">要写入的内容。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>任务。</returns>
    public static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"路径没有目录部分：'{path}'。", nameof(path));

        Directory.CreateDirectory(directory);

        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            Commit(tempPath, fullPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// 把下好的临时文件提交为正式文件（同目录改名，原子）。
    /// <para>
    /// 与 <see cref="WriteAllText"/> 的区别是**不留 <c>.bak</c>**：下载物（游戏文件、库、资源）
    /// 是可再获取的产物，留备份只会白白多占一倍磁盘；真正的用户数据（设置、账户、实例元数据）
    /// 才走 <see cref="WriteAllText"/>。两者都保证"目标文件要么是旧的、要么是完整的新文件"。
    /// </para>
    /// </summary>
    /// <param name="partPath">已完成的临时文件（<c>.part</c>）。</param>
    /// <param name="targetPath">目标路径。</param>
    public static void CommitDownload(string partPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var fullTarget = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);

        File.Move(partPath, fullTarget, overwrite: true);
    }

    /// <summary>读取文本；文件不存在时返回 <see langword="null"/>（不是抛异常）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件内容；不存在时为 <see langword="null"/>。</returns>
    public static string? ReadAllTextOrNull(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    private static void Commit(string tempPath, string fullPath)
    {
        if (File.Exists(fullPath))
        {
            File.Replace(tempPath, fullPath, fullPath + BackupSuffix, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, fullPath);
        }
    }
}
