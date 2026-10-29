using EiTRVO.Core.Launcher.Installer;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// <see cref="IToolRunner"/> 的替身：记下每次调用，并按用例给定的结果返回。
/// <para>
/// 单测里不起真 Java 进程（机器上有没有 Java 会让结果飘）；真进程的那一半由
/// <c>ToolProcessRunnerTests</c> 与真机探针负责。
/// </para>
/// </summary>
internal sealed class FakeToolRunner : IToolRunner
{
    /// <summary>被调用过的每一次（可执行文件 / 参数 / 工作目录 / 覆盖的环境变量）。</summary>
    public List<(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment)> Runs { get; } = [];

    /// <summary>要返回的结果。</summary>
    public ToolRunResult Result { get; set; } = new(0, string.Empty, TimedOut: false);

    /// <summary>每次调用时执行的动作（用例用它"模拟安装器写出产物"）。</summary>
    public Action<IReadOnlyList<string>, string>? OnRun { get; set; }

    /// <summary>要抛出的异常（验"调用方兜底"）。</summary>
    public Exception? Throws { get; set; }

    /// <inheritdoc />
    public Task<ToolRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        Runs.Add((executablePath, [.. arguments], workingDirectory, environment));
        OnRun?.Invoke(arguments, workingDirectory);

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(Result);
    }
}
