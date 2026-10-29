namespace EiTRVO.Core.Launcher.Abstractions;

/// <summary>
/// Java 检测的**环境来源**：环境变量、注册表、常见安装根目录。
/// <para>
/// 抽成接口的理由不是"以后要换实现"，而是**可测**：真实的 Program Files 与
/// <c>HKLM\SOFTWARE\JavaSoft</c> 在单测里造不出来（也不该去碰），
/// 而"递归多深、同名目录怎么去重、<c>javaw.exe</c> 与 <c>java.exe</c> 谁优先"
/// 这些才是真正容易写错的逻辑，必须能测。
/// </para>
/// </summary>
public interface IJavaEnvironment
{
    /// <summary><c>PATH</c> 里的目录（按原顺序）。</summary>
    /// <returns>目录路径。</returns>
    IReadOnlyList<string> GetPathDirectories();

    /// <summary>已知的运行时根目录：<c>JAVA_HOME</c> 与注册表里登记的安装路径。</summary>
    /// <returns>根目录路径（下一级就是 <c>bin</c>）。</returns>
    IReadOnlyList<string> GetJavaHomes();

    /// <summary>需要递归扫描的安装根目录（各厂商在 Program Files 下的目录、启动器自己的运行时目录）。</summary>
    /// <returns>扫描根目录与各自的深度上限。</returns>
    IReadOnlyList<JavaSearchRoot> GetSearchRoots();

    /// <summary>该平台上要按顺序找的可执行文件名（Windows 下 <c>javaw.exe</c> 优先）。</summary>
    /// <returns>文件名。</returns>
    IReadOnlyList<string> GetExecutableNames();
}

/// <summary>一个要递归扫描的安装根目录。</summary>
/// <param name="Path">根目录。</param>
/// <param name="MaxDepth">
/// 向下递归的层数上限。<c>0</c> 表示只看这一层。
/// <para>
/// 之所以按根目录给深度：厂商目录（<c>Program Files\Java</c>）下一层就是 JDK，深度 2 够用；
/// 而启动器自己的 <c>runtime\</c> 是
/// <c>runtime\java-runtime-gamma\windows-x64\java-runtime-gamma\bin\javaw.exe</c>，
/// 少了深度就找不到官方 JRE —— 扫描深度不够的表现是"明明装了却检不到"。
/// </para>
/// </param>
public sealed record JavaSearchRoot(string Path, int MaxDepth);

/// <summary>运行 <c>java -version</c> 并取回它自报的版本信息。</summary>
/// <remarks>
/// 这是全仓库唯一会**运行外部程序**来问版本的地方：Java 的可执行文件名、
/// 安装目录名与真实版本都可能不一致（用户可以把 JDK 17 装在叫 "jdk-1.8" 的目录里），
/// 因此"跑了才知道"是唯一可靠的办法（也允许调用系统已安装的外部程序，见 AGENTS.md §3.2）。
/// </remarks>
public interface IJavaProcessProbe
{
    /// <summary>运行 <c>&lt;可执行文件&gt; -version</c> 并返回它写出的文本。</summary>
    /// <param name="executablePath">java / javaw 可执行文件路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>输出文本；启动失败、超时或没有输出时为 <see langword="null"/>。</returns>
    Task<string?> ReadVersionOutputAsync(string executablePath, CancellationToken cancellationToken);
}
