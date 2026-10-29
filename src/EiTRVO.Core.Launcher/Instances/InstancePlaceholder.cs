using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Instances;

/// <summary>
/// 「没有任何实例」时使用的占位详情。
/// <para>
/// 为什么需要它：接了真实实例目录之后，"一个实例都没有"是**首次运行的正常状态**
/// （不再是演示数据里那 6 个）。而 p03 / p13 / p17 的 ViewModel 会在构造与面包屑里
/// 直接读当前实例的详情 —— 那里抛异常会让整页打不开，或者更糟：让外壳在导航时崩掉。
/// </para>
/// <para>
/// 占位值刻意都是"未指定 / 0"这类**如实表达未知**的文本，而不是编一份看起来正常的实例。
/// </para>
/// <para>
/// M6-e-3c：这些文本一律是「键 + 参数」（<see cref="TextTemplate"/>），由 App 层的
/// <c>InstanceDetailPanelViewModel.Empty</c> 拼成当前语言的句子 —— 这一层是必需的，
/// 记录里的模板字段直接绑到界面上只会显示**键**（AGENTS.md §5.3 第 ⑨ 条）。
/// </para>
/// </summary>
public static class InstancePlaceholder
{
    /// <summary>
    /// 零实例状态下的展示名（键 + 参数）。
    /// <para>
    /// 它是一句**话**（"没有实例"），不是实例名，所以它不放在 <see cref="Detail"/> 的
    /// <c>Name</c> 上 —— 名字是数据（用于目录、面包屑与日志），而这句话是文案。
    /// </para>
    /// </summary>
    public static TextTemplate DisplayName => TextTemplate.Of("（没有实例）");

    /// <summary>没有实例上下文时使用的详情。</summary>
    /// <remarks><c>Name</c> 是空串：展示用的"（没有实例）"由 App 层的投影给（见 <see cref="DisplayName"/>）。</remarks>
    public static InstanceDetail Detail { get; } = new(
        Name: string.Empty,
        GameVersion: TextTemplate.Of("未指定"),
        LoaderDisplay: TextTemplate.Of("未指定"),
        JavaRuntime: TextTemplate.Of("自动匹配"),
        JavaRuntimeSummary: TextTemplate.Of("自动匹配"),
        GameDirectory: string.Empty,
        InstancePath: string.Empty,
        LastLaunched: TextTemplate.Of("从未启动"),
        ResourceVerification: TextTemplate.Of("从未校验"),
        Account: TextTemplate.Of("未绑定"),
        MemoryMb: 0,
        SizeOnDisk: "0 B",
        ModSummary: TextTemplate.Of("{0} 个", "0"),
        PlayTime: InstanceText.FormatPlayTime(0),
        LaunchCount: 0,
        LastExitCode: 0,
        WindowMode: TextTemplate.Of("—"),
        IsolatedDirectory: false);
}
