namespace EiTRVO.Core.Launcher.Abstractions;

/// <summary>
/// 启动器的数据目录布局。
/// <para>
/// 全应用**只允许**通过它拿路径：设置、账户、实例元数据、日志、下载缓存都在这里定义。
/// 这样做的原因是可测性 —— 单元测试可以把它指到一个临时目录，
/// 不需要去碰真实的 <c>%APPDATA%</c>；也避免路径散落在各处（Neo 的问题之一）。
/// </para>
/// <para>
/// 默认根目录是 <c>%APPDATA%\EiTRVO Core\</c>（漫游 AppData），与交付说明里写的一致。
/// </para>
/// </summary>
public interface ILauncherPaths
{
    /// <summary>数据根目录。</summary>
    string Root { get; }

    /// <summary>全局设置文件（<c>settings.json</c>）。</summary>
    string SettingsFile { get; }

    /// <summary>
    /// 账户文件（<c>accounts.json</c>，内容为 DPAPI 加密后的密文）。
    /// <para>
    /// 默认在数据根目录下；MSIX 版由 App 组合根指向
    /// <c>Documents\EiTRVO LTS\accounts.json</c>，与游戏目录、Java 运行时并列。
    /// </para>
    /// </summary>
    string AccountsFile { get; }

    /// <summary>日志目录。</summary>
    string LogDirectory { get; }

    /// <summary>
    /// **旧布局**的实例元数据根目录（<c>&lt;数据目录&gt;\instances</c>）。
    /// <para>
    /// 新布局不再使用它：一个实例 = <c>&lt;游戏目录&gt;\versions\&lt;实例&gt;</c> 一个目录
    /// （见 <see cref="Instances.InstancePaths"/>）。保留只为只读迁移与删除旧目录。
    /// </para>
    /// </summary>
    string InstancesDirectory { get; }

    /// <summary>下载缓存目录。</summary>
    string DownloadCacheDirectory { get; }

    /// <summary>
    /// 版本清单与版本 JSON 的缓存目录（<c>cache\versions</c>）。
    /// <para>
    /// 与下载缓存分开：这里的文件**必须**能长期留着并直接给界面读（离线时 p05 靠它显示版本列表），
    /// 而下载缓存是"下完就可以清"的中间产物，两者的清理策略不同。
    /// </para>
    /// </summary>
    string VersionCacheDirectory { get; }

    /// <summary>
    /// Java 运行时检测结果的缓存文件（<c>cache\java-runtimes.json</c>）。
    /// <para>
    /// 检测要跑若干次 <c>java -version</c>，冷启动时不能同步做；把上次的结果放在这里，
    /// 打开启动器就能立刻列出本机 Java（再由页面导航时的刷新去核对）。
    /// 与版本缓存同理：这是可再生成的产物，读坏了就当没有。
    /// </para>
    /// </summary>
    string JavaRuntimeCacheFile { get; }

    /// <summary>
    /// 内容元数据缓存目录（<c>cache\content</c>）。
    /// <para>
    /// 放**从 Modrinth 取回来的东西**：浏览列表快照（P1-4）与后面的模组对账结果（P1-10 的磁盘缓存）。
    /// 与 <see cref="DownloadCacheDirectory"/> 分开：那些是"下完就该清"的中间产物，
    /// 而这些是"下次离线打开还能显示上一次结果"的元数据 —— 清理策略不同，放一起迟早被一起清掉。
    /// </para>
    /// </summary>
    string ContentCacheDirectory { get; }

    /// <summary>
    /// 启动器自己安装的 Java 运行时目录（<c>&lt;数据目录&gt;\runtime</c>）。
    /// <para>
    /// 「写」与「扫」必须是同一个路径：安装器（§12.7）往这里布局官方 JRE，
    /// 而运行时发现（<c>WindowsJavaEnvironment</c>）把这里当扫描根。两边各写一遍字符串字面量的话，
    /// 改一处就会得到"明明装好了却检不到"——实测过的那种故障。
    /// 布局是官方的形状：<c>runtime\&lt;组件&gt;\&lt;平台&gt;\&lt;组件&gt;\bin\javaw.exe</c>。
    /// </para>
    /// </summary>
    string JavaRuntimeDirectory { get; }

    /// <summary>
    /// 回收目录（删除实例时目录移动到这里，而不是直接抹掉）。
    /// <para>
    /// 「删除」在启动器里是不可逆的用户损失：存档、配置、模组都是玩家几百小时的成果。
    /// 设计稿的删除确认框默认勾选「保留存档与配置」，因此内核也必须真的能"拿回来" ——
    /// 把目录移进回收目录，误删之后还能手工取回（自动清理属于后续里程碑）。
    /// </para>
    /// </summary>
    string RecycleDirectory { get; }

    /// <summary>**旧布局**的实例目录（<c>instances\&lt;实例&gt;</c>；新布局见 <see cref="Instances.InstancePaths.Directory"/>）。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>旧布局目录的绝对路径。</returns>
    string InstanceDirectory(string instanceName);

    /// <summary>**旧布局**的实例元数据文件（<c>instances\&lt;实例&gt;\instance.json</c>）。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>该文件的绝对路径。</returns>
    string InstanceMetadataFile(string instanceName);

    /// <summary>
    /// **旧布局**的实例启动参数覆盖文件（<c>instances\&lt;实例&gt;\overrides.json</c>）。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <returns>该文件的绝对路径。</returns>
    string InstanceOverridesFile(string instanceName);

    /// <summary>按需创建全部目录。已存在时不做任何事。</summary>
    void EnsureCreated();
}
