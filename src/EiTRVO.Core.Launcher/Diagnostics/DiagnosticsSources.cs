namespace EiTRVO.Core.Launcher.Diagnostics;

/// <summary>
/// 诊断事件的来源标识。
/// <para>
/// 取值刻意与 App 层 <c>EventSource</c> 枚举的成员名一致，这样 M6 把事件表接上时
/// 一句 <c>Enum.TryParse</c> 就能映射，不需要维护一张对照表。
/// </para>
/// <para>
/// 但类型是 <see cref="string"/> 而不是枚举：内核还要能记录界面枚举尚未定义的来源
/// （例如认证链），加一个来源不该逼着 UI 先改枚举、再改内核。
/// </para>
/// </summary>
public static class DiagnosticsSources
{
    /// <summary>启动器本体（全局设置、崩溃、路径安全、文件损坏）。</summary>
    public const string Launcher = "Launcher";

    /// <summary>实例配置变更（实例的新建 / 删除 / 覆盖项保存）。</summary>
    public const string InstanceSettings = "InstanceSettings";

    /// <summary>下载器。</summary>
    public const string Downloader = "Downloader";

    /// <summary>Java 运行时管理。</summary>
    public const string JavaRuntime = "JavaRuntime";

    /// <summary>模组加载器安装器。</summary>
    public const string ModLoader = "ModLoader";

    /// <summary>启动核心。</summary>
    public const string LaunchCore = "LaunchCore";

    /// <summary>认证链（设备代码流、令牌刷新链）。</summary>
    public const string Auth = "Auth";

    /// <summary>Modrinth 内容源。</summary>
    public const string Modrinth = "Modrinth";
}
