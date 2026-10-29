using EiTRVO.Core.Launcher.Abstractions;

namespace EiTRVO.Core.App.Localization;

/// <summary>
/// 启动语言的解析。
/// <para>
/// 顺序：**设置目录里存过的**（用户在「Java 与设置」页选过并按了保存）→
/// <c>appsettings.json</c> 的 <c>Language</c>（部署时的默认值）→ 简体中文。
/// </para>
/// <para>
/// 单独抽出来是为了能测：这段逻辑在 <c>App.OnStartup</c> 里，而组合根本身不好做单测，
/// 但「选过英文 → 保存 → 下次启动是英文」这条链路必须有测试钉住。
/// </para>
/// </summary>
internal static class LocalizationStartup
{
    /// <summary>解析本次启动应采用的界面语言。</summary>
    /// <param name="settings">设置读写（用户的选择存在全局设置里）。</param>
    /// <param name="configured">appsettings.json 里的 Language，可能为空。</param>
    /// <returns>语言标签。</returns>
    public static string Resolve(ISettingsCatalog settings, string? configured)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var stored = settings.GetGlobal().Language;
        if (LocalizationService.IsSupported(stored))
        {
            return stored;
        }

        return LocalizationService.IsSupported(configured)
            ? configured!
            : LocalizationService.Chinese;
    }
}