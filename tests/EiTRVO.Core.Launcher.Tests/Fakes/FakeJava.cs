using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// Java 环境来源的替身：真实的 Program Files 与 HKLM 在单测里造不出来（也不该去碰），
/// 因此发现逻辑的输入全部由这里给定 —— 于是"目录去重、javaw 优先、递归深度"这些能真的测到。
/// </summary>
internal sealed class FakeJavaEnvironment : IJavaEnvironment
{
    /// <summary><c>PATH</c> 里的目录。</summary>
    public List<string> PathDirectories { get; } = [];

    /// <summary><c>JAVA_HOME</c> 与注册表报出的根目录。</summary>
    public List<string> JavaHomes { get; } = [];

    /// <summary>要递归扫描的根目录。</summary>
    public List<JavaSearchRoot> SearchRoots { get; } = [];

    /// <summary>可执行文件名（默认与 Windows 上的真实实现一致）。</summary>
    public List<string> ExecutableNames { get; } = ["javaw.exe", "java.exe"];

    /// <inheritdoc />
    public IReadOnlyList<string> GetPathDirectories() => PathDirectories;

    /// <inheritdoc />
    public IReadOnlyList<string> GetJavaHomes() => JavaHomes;

    /// <inheritdoc />
    public IReadOnlyList<JavaSearchRoot> GetSearchRoots() => SearchRoots;

    /// <inheritdoc />
    public IReadOnlyList<string> GetExecutableNames() => ExecutableNames;
}

/// <summary>
/// <c>java -version</c> 的替身：按**文件名**回答预置的输出，不真的起进程。
/// 想跑真进程的用例不该出现在单测里（机器上有没有 Java 会让结果飘）。
/// </summary>
internal sealed class FakeJavaProcessProbe : IJavaProcessProbe
{
    private readonly Dictionary<string, string> _outputs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>被探测过的可执行文件（断言"没有重复跑进程"用）。</summary>
    public List<string> Probed { get; } = [];

    /// <summary>登记某个可执行文件的版本输出。</summary>
    /// <param name="executablePath">可执行文件路径。</param>
    /// <param name="output">要返回的输出。</param>
    /// <returns>自身，便于链式写法。</returns>
    public FakeJavaProcessProbe Respond(string executablePath, string output)
    {
        _outputs[executablePath] = output;
        return this;
    }

    /// <inheritdoc />
    public Task<string?> ReadVersionOutputAsync(string executablePath, CancellationToken cancellationToken)
    {
        Probed.Add(executablePath);
        return Task.FromResult(_outputs.TryGetValue(executablePath, out var output) ? output : null);
    }
}

/// <summary>
/// Java 运行时目录的替身：给定一批运行时事实，不跑任何检测。
/// 版本目录（<c>FileVersionCatalog</c>）的单测需要它来决定"缺少 Java"这一态。
/// </summary>
internal sealed class FakeJavaRuntimeCatalog : IJavaRuntimeCatalog
{
    private readonly List<JavaInstallation> _installations;

    /// <summary>构造替身。</summary>
    /// <param name="installations">要报出的运行时。</param>
    /// <param name="hasDetectionResult">是否算"已经检测过"。</param>
    public FakeJavaRuntimeCatalog(
        IReadOnlyList<JavaInstallation>? installations = null,
        bool hasDetectionResult = false)
    {
        _installations = [.. installations ?? []];
        HasDetectionResult = hasDetectionResult;
    }

    /// <inheritdoc />
    public bool HasDetectionResult { get; set; }

    /// <summary>被要求刷新的次数。</summary>
    public int RefreshCount { get; private set; }

    /// <summary>被要求强制检测的次数。</summary>
    public int RedetectCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<JavaInstallation> GetInstallations() => _installations;

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RedetectAsync(CancellationToken cancellationToken = default)
    {
        RedetectCount++;
        return Task.CompletedTask;
    }
}
