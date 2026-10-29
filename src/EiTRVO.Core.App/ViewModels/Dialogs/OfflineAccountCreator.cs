using System.IO;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;
using EiTRVO.Core.Auth.Offline;
using EiTRVO.Core.Auth.Storage;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// 「新建一个离线账户」这一件事的**唯一**实现（D-6）。
/// </summary>
/// <remarks>
/// 两处入口共用它：p11 账户列表右侧「添加离线账户」打开的对话框，以及 p15 页面区那条
/// 「离线登录 | 玩家名称」+ 底部「以离线模式登录」（设计稿 p15 就是那么画的）。
/// 校验规则、重名的判定与三类落盘失败的说法只能写一遍 —— 写两遍迟早两边不一致，
/// 而这里的每一条都是用户能看见的一句话。
/// </remarks>
internal static class OfflineAccountCreator
{
    /// <summary>
    /// 校验玩家名并落盘一个离线账户。
    /// </summary>
    /// <param name="accounts">账户目录。</param>
    /// <param name="rawName">用户输入的名字（未 Trim）。</param>
    /// <param name="error">失败原因（成功时为空串）。</param>
    /// <returns>落盘后的玩家名；失败时为 <see langword="null"/>。</returns>
    public static string? TryCreate(IAccountCatalog accounts, string rawName, out string error)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var name = (rawName ?? string.Empty).Trim();

        var issue = OfflineName.Validate(name);
        if (issue != OfflineNameIssue.None)
        {
            error = IssueText(issue);
            return null;
        }

        if (accounts.GetAccounts().Any(account => account.Kind == AccountKind.Offline
                                               && string.Equals(account.PlayerName, name, StringComparison.OrdinalIgnoreCase)))
        {
            error = Loc.T("该玩家名的离线账号已存在。");
            return null;
        }

        try
        {
            accounts.AddOffline(name);
        }
        catch (ArgumentException)
        {
            // 与上面的重名检查是同一件事，只是被内核又拦了一次（并发/数据变化）。
            error = Loc.T("该玩家名的离线账号已存在。");
            return null;
        }
        catch (AccountStoreException)
        {
            // 内核在"读不出来"时**拒绝写入**（不覆盖用户那份可能还有救的文件）——
            // 这里必须如实说明，而不是显示成"添加失败"或干脆什么都不发生。
            error = Loc.T("账户文件读不出来，已取消这次写入（原文件未改动）。");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去（磁盘满、权限）必须说出来，不能静默失败。
            error = Loc.T("账户文件写入失败，请检查磁盘与权限。");
            return null;
        }

        error = string.Empty;
        return name;
    }

    /// <summary>把内核报出的原因翻成界面上的一句话。</summary>
    /// <param name="issue">校验结果。</param>
    /// <returns>可直接显示的文案；<see cref="OfflineNameIssue.None"/> 时为空串。</returns>
    public static string IssueText(OfflineNameIssue issue) => issue switch
    {
        OfflineNameIssue.None => string.Empty,
        OfflineNameIssue.Empty => Loc.T("请输入玩家名。"),
        OfflineNameIssue.TooShort => Loc.T("玩家名太短（至少 {0} 位）。", OfflineName.MinLength),
        OfflineNameIssue.TooLong => Loc.T("玩家名太长（最多 {0} 位）。", OfflineName.MaxLength),
        _ => Loc.T("玩家名只能包含字母、数字与下划线。"),
    };
}
