using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// <see cref="IShellService"/> 的 View 层实现。
/// <para>
/// 这里允许出现 <c>System.Windows.Clipboard</c> —— 它就是 View 层的一部分，
/// 职责正是替 ViewModel 把 WPF 类型挡在外面。
/// </para>
/// </summary>
internal sealed partial class ShellService : IShellService
{
    private readonly ILogger<ShellService> _logger;

    /// <summary>构造外壳服务。</summary>
    /// <param name="logger">日志。</param>
    public ShellService(ILogger<ShellService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Start(new ProcessStartInfo(path) { UseShellExecute = true }, path);
    }

    /// <inheritdoc />
    public bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return Start(new ProcessStartInfo(url) { UseShellExecute = true }, url);
    }

    /// <inheritdoc />
    public bool CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // 剪贴板被别的进程独占时 SetText 会失败；这不是致命错误。
            LogClipboardFailure(ex);
            return false;
        }
    }

    private bool Start(ProcessStartInfo startInfo, string what)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            LogShellFailure(ex, what);
            return false;
        }
    }

    // 源生成的高性能日志（CA1848）：避免每次调用都做装箱与字符串插值。
    [LoggerMessage(Level = LogLevel.Warning, Message = "写入剪贴板失败。")]
    private partial void LogClipboardFailure(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "无法用系统外壳打开 {Target}。")]
    private partial void LogShellFailure(Exception exception, string target);
}
