using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p12 关于。对应 mockup/pages/p12-about.html。
/// <para>
/// Client ID 从配置读取后只显示脱敏形式，代码里不出现硬编码的 GUID
/// （AGENTS.md §6 / §7）。
/// </para>
/// </summary>
internal sealed partial class AboutViewModel : PageViewModelBase
{
    private readonly IShellService _shell;
    private readonly ILicenseWindowService? _licenseWindow;

    /// <summary>构造关于 ViewModel。</summary>
    /// <param name="info">启动器元信息。</param>
    /// <param name="authOptions">认证配置（取脱敏后的 Client ID）。</param>
    /// <param name="shell">外壳服务（打开日志目录）。</param>
    /// <param name="licenseWindow">第三方许可窗口（可选：单元测试与设计期不打开真实窗口）。</param>
    public AboutViewModel(
        ILauncherInfo info,
        AuthOptions authOptions,
        IShellService shell,
        ILicenseWindowService? licenseWindow = null)
        : base(PageId.About, "关于")
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(authOptions);
        ArgumentNullException.ThrowIfNull(shell);

        _shell = shell;
        _licenseWindow = licenseWindow;
        Info = info;
        ClientIdText = LocalizedText.Of(authOptions.MaskedClientId) + Loc.T("（EiTRVO 产品线共用，从配置读取）");
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.About;

    /// <inheritdoc />
    public override string SectionSubtitle => string.Empty;

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb => SingleCrumb();

    /// <summary>启动器元信息。</summary>
    public ILauncherInfo Info { get; }

    /// <summary>脱敏后的 Client ID 文本。</summary>
    public string ClientIdText { get; }

    /// <summary>用资源管理器打开日志目录。</summary>
    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(Info.LogDirectory);

    /// <summary>打开第三方许可窗口（列出随产品分发的组件与 MIT 许可证全文）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenLicenses))]
    private void OpenLicenses() => _licenseWindow?.Show();

    /// <summary>打开 MIT 许可证页面。</summary>
    [RelayCommand]
    private void OpenMitLicense() => _shell.OpenUrl("https://opensource.org/license/mit");

    private bool CanOpenLicenses() => _licenseWindow is not null;
}
