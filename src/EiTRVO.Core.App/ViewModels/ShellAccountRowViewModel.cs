using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 页头「账户」下拉菜单里的一行：玩家名 + 一行摘要 + 是否为默认账户。
/// </summary>
/// <remarks>
/// 与 p11 账户列表的 <see cref="AccountRowViewModel"/> **刻意分开**：两处的"摘要"不一样 ——
/// 账户页要显示 UUID（便于区分同名/同机多账户），下拉菜单只有 300px 宽，
/// 显示 UUID 会把玩家名挤掉。这里只写"账户类型 + 上次使用"。
/// <para>
/// 默认账户的「默认」标记由 <see cref="IsDefault"/> 表达；菜单里点一行 = 设为默认
/// （<see cref="IAccountCatalog.SetDefault"/>），**不是**"打开账户页"。
/// </para>
/// </remarks>
/// <param name="PlayerName">玩家名（点这一行时交给内核的那个名字）。</param>
/// <param name="Kind">账户类型（视图据此选说明色 / 图标）。</param>
/// <param name="IsDefault">是否为默认账户（启动游戏时使用它）。</param>
/// <param name="Meta">行摘要（已本地化：`Microsoft 账户 · 上次使用 今天 16:01`）。</param>
internal sealed record ShellAccountRowViewModel(
    string PlayerName,
    AccountKind Kind,
    bool IsDefault,
    string Meta)
{
    /// <summary>把一个账户投影成下拉菜单里的一行（摘要在这里拼成当前语言）。</summary>
    /// <param name="account">内核账户。</param>
    /// <returns>投影。</returns>
    public static ShellAccountRowViewModel From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var lastUsed = LocalizedText.Of(account.LastUsed);

        var meta = account.Kind == AccountKind.Microsoft
            ? Loc.T("Microsoft 账户 · 上次使用 {0}", lastUsed)
            : Loc.T("离线账户 · 上次使用 {0}", lastUsed);

        return new ShellAccountRowViewModel(account.PlayerName, account.Kind, account.IsDefault, meta);
    }
}
