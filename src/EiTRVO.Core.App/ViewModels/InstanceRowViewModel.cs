using CommunityToolkit.Mvvm.ComponentModel;
using EiTRVO.Core.Launcher;
using EiTRVO.Core.Launcher.Instances;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p02 实例表格里的一行：内核给的是「键 + 参数」（上次启动时间），这里拼成当前语言的句子（Q8）。
/// </summary>
/// <remarks>
/// 属性名与内核的 <see cref="InstanceRecord"/> 一致（<c>Name</c> / <c>GameVersion</c> / <c>LoaderDisplay</c> /
/// <c>SizeOnDisk</c> / <c>LastLaunched</c> / <c>StatusText</c> / <c>StatusTone</c> / <c>IsActive</c>），
/// 所以 XAML 的绑定路径一个字都不用改。
/// </remarks>
internal sealed partial class InstanceRowViewModel : ObservableObject
{
    /// <summary>构造表格行。</summary>
    public InstanceRowViewModel(
        string name,
        string gameVersion,
        string loaderDisplay,
        string sizeOnDisk,
        string lastLaunched,
        string statusText,
        Tone statusTone,
        bool isActive)
    {
        Name = name;
        GameVersion = gameVersion;
        LoaderDisplay = loaderDisplay;
        SizeOnDisk = sizeOnDisk;
        LastLaunched = lastLaunched;
        StatusText = statusText;
        StatusTone = statusTone;
        _isActive = isActive;
    }

    /// <summary>实例名。</summary>
    public string Name { get; }

    /// <summary>游戏版本。</summary>
    public string GameVersion { get; }

    /// <summary>加载器显示名。</summary>
    public string LoaderDisplay { get; }

    /// <summary>占用空间（纯数据）。</summary>
    public string SizeOnDisk { get; }

    /// <summary>上次启动时间（已本地化）。</summary>
    public string LastLaunched { get; }

    /// <summary>状态列文本。</summary>
    public string StatusText { get; }

    /// <summary>状态色调。</summary>
    public Tone StatusTone { get; }

    /// <summary>是否为当前实例（可直接通知，不需要重建整张表）。</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>p01 快切列表里的一行：摘要里的时间戳由 App 层拼。</summary>
/// <param name="Name">实例名。</param>
/// <param name="Summary">左侧摘要（已本地化）。</param>
/// <param name="BadgeText">右侧徽标文本。</param>
/// <param name="BadgeTone">右侧徽标色调。</param>
/// <param name="IsActive">是否为当前实例。</param>
internal sealed record InstanceOverviewRowViewModel(
    string Name,
    string Summary,
    string BadgeText,
    Tone BadgeTone,
    bool IsActive);
