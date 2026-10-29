using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Auth;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p11 账户列表里的一行：内核给的行摘要（`Microsoft 正版 · UUID · 上次使用 …`）是「键 + 参数」，
/// 时间那一格由 App 层拼（Q8）。
/// </summary>
/// <remarks>
/// 属性名与内核的 <see cref="Account"/> 一致（<c>PlayerName</c> / <c>Kind</c> / <c>Uuid</c> / <c>TokenState</c> /
/// <c>IsDefault</c> / <c>IsActive</c> / <c>Meta</c>），所以 XAML 的绑定路径一个字都不用改。
/// <para>
/// **这一层是必需的**：`Account.Meta` 里嵌着"上次使用"的时间句子，而记录是内核类型 ——
/// 直接把 `Meta` 绑到界面上会显示它的**键**（占位符参数全丢）。
/// </para>
/// </remarks>
/// <param name="PlayerName">玩家名。</param>
/// <param name="Kind">账户种类。</param>
/// <param name="Uuid">UUID 文本（离线账户是派生值）。</param>
/// <param name="TokenState">令牌状态。</param>
/// <param name="IsDefault">是否为默认账户。</param>
/// <param name="IsActive">是否被选中。</param>
/// <param name="Meta">行摘要（已本地化）。</param>
internal sealed record AccountRowViewModel(
    string PlayerName,
    AccountKind Kind,
    string Uuid,
    TokenState TokenState,
    bool IsDefault,
    bool IsActive,
    string Meta)
{
    /// <summary>把内核的一个账户投影成列表里的一行（行摘要在这里拼成当前语言）。</summary>
    /// <param name="account">内核账户。</param>
    /// <returns>投影。</returns>
    public static AccountRowViewModel From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new AccountRowViewModel(
            account.PlayerName,
            account.Kind,
            account.Uuid,
            account.TokenState,
            account.IsDefault,
            account.IsActive,
            LocalizedText.Of(account.Meta));
    }
}
