using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Abstractions;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 下载服务准入：正版账号登录过一次之后，才允许下载游戏文件、模组加载器和整合包。
/// </summary>
/// <remarks>
/// 只限制这三类（MC 游戏文件 / 加载器 / 整合包）；模组、资源包、存档备份与 Java 运行时
/// 不在这次限制范围内。判断依据是账户库里有没有 <see cref="AccountKind.Microsoft"/>，
/// 令牌过期也算“登录过”——重新登录是账户页的事，不该让用户卡在下载入口。
/// </remarks>
internal interface IAccountDownloadPolicy
{
    /// <summary>是否允许下载游戏文件、模组加载器和整合包。</summary>
    bool CanDownloadGameContent { get; }

    /// <summary>不允许时的说明（已本地化）。</summary>
    string LockedReason { get; }
}

/// <summary>基于账户目录的策略实现。</summary>
internal sealed class AccountDownloadPolicy(IAccountCatalog accounts) : IAccountDownloadPolicy
{
    private readonly IAccountCatalog _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    /// <inheritdoc />
    public bool CanDownloadGameContent =>
        _accounts.GetAccounts().Any(static account => account.Kind == AccountKind.Microsoft);

    /// <inheritdoc />
    public string LockedReason => Loc.T(
        "还没有登录过 Microsoft 正版账号，暂不提供游戏文件、模组加载器和整合包下载。请先到「账户」页登录正版账号。");
}

/// <summary>测试与无账户场景使用的默认放行策略（生产组合根不会注册它）。</summary>
internal sealed class AllowAllDownloadPolicy : IAccountDownloadPolicy
{
    /// <summary>单例。</summary>
    public static AllowAllDownloadPolicy Instance { get; } = new();

    /// <inheritdoc />
    public bool CanDownloadGameContent => true;

    /// <inheritdoc />
    public string LockedReason => string.Empty;
}
