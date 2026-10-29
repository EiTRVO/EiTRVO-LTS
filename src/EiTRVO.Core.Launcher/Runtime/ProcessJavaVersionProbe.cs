using System.ComponentModel;
using System.Diagnostics;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;

namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// <see cref="IJavaProcessProbe"/> 的真实实现：跑一次 <c>&lt;java&gt; -version</c>。
/// <para>
/// 三个必须处理好的现实问题：
/// </para>
/// <list type="number">
/// <item><b>版本写在 stderr</b>：<c>java -version</c> 不写 stdout。这是实测结论（迁移评估也点名了），
/// 读错流会拿到空串，然后看起来像"这个 Java 是坏的"。</item>
/// <item><b>必须有超时</b>：候选来自扫描目录，可能是个挂着的网络盘或半损坏的安装；
/// 没有超时的话"检测 Java"会一直转，用户只能杀进程。</item>
/// <item><b>stdout 不重定向</b>：只重定向 stderr 就没有第二条管道要排空，
/// 也就不存在"子进程写满管道缓冲而父进程在等它退出"的死锁。</item>
/// </list>
/// </summary>
public sealed class ProcessJavaVersionProbe : IJavaProcessProbe
{
    /// <summary>单个候选的等待上限。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造探测者。</summary>
    /// <param name="diagnostics">诊断事件（超时与启动失败要留痕，否则"检测不到 Java"无从排查）。</param>
    public ProcessJavaVersionProbe(IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public async Task<string?> ReadVersionOutputAsync(string executablePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-version");

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var output = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消（例如用户切走了页面）：不是故障，照常向上传递。
            Kill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.JavaRuntime,
                "读取 Java 版本超时（{0}，{1}），已跳过这个候选",
                [executablePath, $"{Timeout.TotalSeconds:F0} 秒"]);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // 文件在、但起不来（权限、位数不符、安装已损坏）：跳过它，继续看别的候选。
            _diagnostics.Write(
                LauncherEventLevel.Warning,
                DiagnosticsSources.JavaRuntime,
                "无法运行该 Java 候选（{0}，{1}），已跳过",
                [executablePath, ex.GetType().Name]);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>超时或取消时把子进程收掉（否则它会一直挂在后台）。</summary>
    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // 已经退出了、或收不掉：都不影响"这次检测放弃这个候选"这个结论。
        }
    }
}
