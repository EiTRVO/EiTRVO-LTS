using System.IO;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.Services;

/// <summary>一次崩溃的对外描述（标题、正文、日志位置）。</summary>
/// <param name="Title">对话框标题。</param>
/// <param name="Detail">对话框正文。</param>
/// <param name="LogFilePath">事件日志路径（用户可据此自查或贴给别人）。</param>
public sealed record CrashReport(string Title, string Detail, string LogFilePath);

/// <summary>
/// 未处理异常的落点（F-10）。
/// <para>
/// 它**不引用任何 UI 类型**：只负责「写一条 Critical 事件 + 拼出给人看的文字」。
/// 弹窗由 <c>App</c>（View 层）做 —— 这样这段逻辑可以在单测里直接验证，
/// 而崩溃处理本身最怕的就是"没法测、只能靠真崩一次看看"。
/// </para>
/// <para>
/// 三条铁律：
/// </para>
/// <list type="number">
/// <item>写日志本身**绝不能抛异常**（否则崩溃处理会把进程二次打崩）；</item>
/// <item>日志按 Q8 的形状写（级别 + 来源 + 参数），用户可见的字在 App 层用 <c>.resx</c> 拼；</item>
/// <item>把日志路径告诉用户 —— "已记录日志"而不给路径，等于没记。</item>
/// </list>
/// </summary>
internal sealed class CrashHandler
{
    private readonly IDiagnosticsLog _diagnostics;
    private readonly ILauncherPaths _paths;

    /// <summary>构造崩溃处理器。</summary>
    /// <param name="diagnostics">诊断事件（崩溃按 Critical 级别写入）。</param>
    /// <param name="paths">数据目录布局（日志位置要从这里取）。</param>
    public CrashHandler(IDiagnosticsLog diagnostics, ILauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(paths);

        _diagnostics = diagnostics;
        _paths = paths;
    }

    /// <summary>事件日志文件路径。</summary>
    public string LogFilePath => Path.Combine(_paths.LogDirectory, FileDiagnosticsLog.FileName);

    /// <summary>记录一次未处理异常，并返回给用户看的描述。</summary>
    /// <param name="exception">异常。</param>
    /// <param name="origin">来源标识，例如 <c>DispatcherUnhandledException</c>。</param>
    /// <returns>崩溃描述。</returns>
    public CrashReport Capture(Exception exception, string origin)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var logFilePath = LogFilePath;

        try
        {
            _diagnostics.Write(
                LauncherEventLevel.Critical,
                DiagnosticsSources.Launcher,
                // 键必须**带占位符**（Q8 的形状是「键 + 参数」）：只写前四个字的话，
                // 下面这四个参数在界面上会被 string.Format 静默丢掉（M6-f 实测）。
                "启动器发生未处理的异常（{0} · {1}）：{2} 调用栈：{3}",
                [
                    origin,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    exception.StackTrace ?? string.Empty,
                ]);
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            // 日志都写不进去时也不能再抛：崩溃路径的可用性优先于"记录完整"。
        }

        return new CrashReport(
            Title: Loc.T("启动器遇到未处理的错误"),
            Detail: string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{Environment.NewLine}{Loc.T("错误详情已写入日志：{0}", logFilePath)}"),
            LogFilePath: logFilePath);
    }
}
