namespace EiTRVO.Core.Launcher.Runtime;

/// <summary>
/// 用户手动添加 Java 时的风险位。App 层据此弹窗，用户坚持时才真正加入列表。
/// </summary>
[Flags]
public enum JavaRuntimeRisk
{
    /// <summary>没有发现风险。</summary>
    None = 0,

    /// <summary>版本输出里认不出供应商。</summary>
    UnknownVendor = 1,

    /// <summary>可执行文件位于临时目录 / 下载目录 / 桌面等用户可写位置。</summary>
    UserWritableLocation = 2,
}

/// <summary>手动添加 Java 时的检查结果。</summary>
/// <param name="Installation">通过版本探测的运行时事实。</param>
/// <param name="Risk">风险位；<see cref="JavaRuntimeRisk.None"/> 表示可直接加入。</param>
public sealed record JavaRuntimeInspection(JavaInstallation Installation, JavaRuntimeRisk Risk);

/// <summary>手动选择的可执行文件不是有效 Java 时抛出。</summary>
public sealed class JavaRuntimeInspectionException : Exception
{
    /// <summary>构造检查失败异常。</summary>
    /// <param name="message">面向排障的说明。</param>
    public JavaRuntimeInspectionException(string message)
        : base(message)
    {
    }
}


