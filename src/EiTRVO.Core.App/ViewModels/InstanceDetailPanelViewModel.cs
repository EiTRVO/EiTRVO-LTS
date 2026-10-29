using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher;
using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p01 / p03 / p13 / p17 共用的「实例属性」投影：内核 <see cref="InstanceDetail"/> 里的展示字段是
/// 「键 + 参数」（<c>TextTemplate</c>），这里拼成当前语言的句子（Q8）。
/// </summary>
/// <remarks>
/// **为什么必须是独立类型，而不是在每个 ViewModel 上加几个 <c>XxxText</c> 包装属性**
/// （AGENTS.md §5.3 第 ⑨ 条）：字符串内插一个 <c>TextTemplate</c> 只会调它的 <c>ToString()</c> ——
/// 拿到的是**键**、参数全丢，而编译器、门禁与单测都不会说话。包装属性的坏处是"谁都能忘"：
/// p13 就真的忘了加那两个包装属性，于是共用的属性面板上「上次启动」「资源校验」两格一直是空的。
/// 把拼装放在一个投影类型里，四个页面拿到的是同一份，**没有哪个 ViewModel 能漏**。
/// <para>
/// 属性名与 <see cref="InstanceDetail"/> 逐字一致，所以四个页面的 XAML 一个字都不用改。
/// </para>
/// </remarks>
/// <param name="Name">实例名。</param>
/// <param name="GameVersion">游戏版本（已本地化）。</param>
/// <param name="LoaderDisplay">加载器显示名。</param>
/// <param name="JavaRuntime">Java 运行时（已本地化）。</param>
/// <param name="JavaRuntimeSummary">Java 运行时简写（已本地化）。</param>
/// <param name="GameDirectory">游戏根目录。</param>
/// <param name="InstancePath">实例目录。</param>
/// <param name="LastLaunched">上次启动（已本地化）。</param>
/// <param name="ResourceVerification">资源校验（已本地化）。</param>
/// <param name="Account">绑定账户（已本地化）。</param>
/// <param name="MemoryMb">内存分配（MB）。</param>
/// <param name="SizeOnDisk">占用空间。</param>
/// <param name="ModSummary">模组摘要（已本地化）。</param>
/// <param name="PlayTime">累计时长（已本地化）。</param>
/// <param name="LaunchCount">启动次数。</param>
/// <param name="LastExitCode">最后退出码。</param>
/// <param name="WindowMode">游戏窗口（已本地化）。</param>
/// <param name="IsolatedDirectory">是否启用隔离目录。</param>
/// <param name="MemoryText">内存那一格的显示文本。</param>
/// <param name="LastExitCodeText">退出码那一格的显示文本。</param>
/// <param name="LastExitCodeTone">退出码的色调。</param>
/// <param name="IsolatedDirectoryText">隔离目录那一格的显示文本。</param>
internal sealed record InstanceDetailPanelViewModel(
    string Name,
    string GameVersion,
    string LoaderDisplay,
    string JavaRuntime,
    string JavaRuntimeSummary,
    string GameDirectory,
    string InstancePath,
    string LastLaunched,
    string ResourceVerification,
    string Account,
    int MemoryMb,
    string SizeOnDisk,
    string ModSummary,
    string PlayTime,
    int LaunchCount,
    int LastExitCode,
    string WindowMode,
    bool IsolatedDirectory,
    string MemoryText,
    string LastExitCodeText,
    Tone LastExitCodeTone,
    string IsolatedDirectoryText)
{
    /// <summary>零实例状态下的投影（首次运行的正常状态，不是错误）。</summary>
    /// <remarks>
    /// 名字那句"（没有实例）"是**文案**，由这里给（内核占位详情里的 <c>Name</c> 是空串：
    /// 名字是数据，用于目录与日志，而"没有实例"是一句话）。
    /// </remarks>
    public static InstanceDetailPanelViewModel Empty => From(InstancePlaceholder.Detail) with
    {
        Name = LocalizedText.Of(InstancePlaceholder.DisplayName),
    };

    /// <summary>把一个内核详情投影成可绑定的属性集。</summary>
    /// <param name="detail">内核详情。</param>
    /// <returns>投影。</returns>
    public static InstanceDetailPanelViewModel From(InstanceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return new InstanceDetailPanelViewModel(
            Name: detail.Name,
            GameVersion: LocalizedText.Of(detail.GameVersion),
            LoaderDisplay: LocalizedText.Of(detail.LoaderDisplay),
            JavaRuntime: LocalizedText.Of(detail.JavaRuntime),
            JavaRuntimeSummary: LocalizedText.Of(detail.JavaRuntimeSummary),
            GameDirectory: detail.GameDirectory,
            InstancePath: detail.InstancePath,
            LastLaunched: LocalizedText.Of(detail.LastLaunched),
            ResourceVerification: LocalizedText.Of(detail.ResourceVerification),
            Account: LocalizedText.Of(detail.Account),
            MemoryMb: detail.MemoryMb,
            SizeOnDisk: detail.SizeOnDisk,
            ModSummary: LocalizedText.Of(detail.ModSummary),
            PlayTime: LocalizedText.Of(detail.PlayTime),
            LaunchCount: detail.LaunchCount,
            LastExitCode: detail.LastExitCode,
            WindowMode: LocalizedText.Of(detail.WindowMode),
            IsolatedDirectory: detail.IsolatedDirectory,
            MemoryText: LocalizedText.Of(detail.MemoryText),
            LastExitCodeText: LocalizedText.Of(detail.LastExitCodeText),
            LastExitCodeTone: detail.LastExitCodeTone,
            IsolatedDirectoryText: LocalizedText.Of(detail.IsolatedDirectoryText));
    }
}
