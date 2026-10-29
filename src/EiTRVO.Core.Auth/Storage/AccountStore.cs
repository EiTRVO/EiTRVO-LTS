using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Auth.Storage;

/// <summary>
/// 账户的加密持久化（D-4 / D-5）：明文账户数组用 Windows DPAPI 加密后写进 <c>accounts.json</c>。
/// <para>
/// 为什么必须加密：账户里存的是 Microsoft 刷新令牌 —— 它能换取访问令牌，
/// 进而登录玩家的正版账号。明文落盘等于把账号交给任何能读到该文件的程序。
/// DPAPI（<see cref="DataProtectionScope.CurrentUser"/>）把密钥绑到当前 Windows 用户，
/// 因此拷到别的用户/机器上必然解不开 —— 这正是我们要的效果。
/// </para>
/// <para>
/// 三条刻意的行为：
/// </para>
/// <list type="number">
/// <item><b>解不开就报错，绝不返回空列表</b>：把"读不出来"当成"没有账户"就是静默清空，
/// 用户会以为号被删了，然后可能就去重建、覆盖掉还能救的文件。</item>
/// <item><b>读取路径不写文件</b>：连迁移都不做。用户数据只有一份，
/// 读失败时任何"顺手修复"都可能把它变得更不可恢复。</item>
/// <item><b>额外熵（entropy）固定且带产品标识</b>：让密文与"本产品的账户文件"绑定，
/// 别的程序拿同一份密文也解不开（DPAPI 的熵是第二道锁，不是替代品）。</item>
/// </list>
/// </summary>
public sealed class AccountStore
{
    /// <summary>保护方式标识（写进文件，便于将来换算法时识别旧文件）。</summary>
    public const string ProtectionName = "dpapi-current-user";

    /// <summary>DPAPI 的附加熵：固定值 + 产品标识，避免密文被跨应用复用。</summary>
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("EiTRVO Core/accounts/v1"));

    private readonly ILauncherPaths _paths;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>构造账户存储。</summary>
    /// <param name="paths">数据目录布局（账户文件位置）。</param>
    /// <param name="diagnostics">诊断事件。</param>
    public AccountStore(ILauncherPaths paths, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _paths = paths;
        _diagnostics = diagnostics;
    }

    /// <summary>账户文件路径。</summary>
    public string FilePath => _paths.AccountsFile;

    /// <summary>读取全部账户。</summary>
    /// <returns>账户；文件不存在时为空列表。</returns>
    /// <exception cref="AccountStoreException">文件损坏或无法解密。</exception>
    public IReadOnlyList<StoredAccount> Load()
    {
        var json = AtomicFile.ReadAllTextOrNull(FilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        AccountFile? file;
        try
        {
            file = LauncherJson.Deserialize<AccountFile>(json, LauncherJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new AccountStoreException(AccountStoreFailure.Corrupt, FilePath, ex);
        }

        if (file?.Payload is null || file.Payload.Length == 0)
        {
            return [];
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(file.Payload);
        }
        catch (FormatException ex)
        {
            throw new AccountStoreException(AccountStoreFailure.Corrupt, FilePath, ex);
        }

        var plaintext = Unprotect(protectedBytes);

        try
        {
            return LauncherJson.Deserialize<List<StoredAccount>>(
                Encoding.UTF8.GetString(plaintext),
                LauncherJson.Compact) ?? [];
        }
        catch (JsonException ex)
        {
            // 解密成功却解析失败：密文本身没问题，是我们自己写坏了格式。
            throw new AccountStoreException(AccountStoreFailure.Corrupt, FilePath, ex);
        }
    }

    /// <summary>写入全部账户（原子写：写一半不会把旧文件毁掉）。</summary>
    /// <param name="accounts">账户。</param>
    public void Save(IReadOnlyList<StoredAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var plaintext = LauncherJson.Serialize(accounts, LauncherJson.Compact);
        var protectedBytes = Protect(plaintext);

        var file = new AccountFile { Payload = Convert.ToBase64String(protectedBytes) };
        AtomicFile.WriteAllText(FilePath, LauncherJson.Serialize(file, LauncherJson.Indented));

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Launcher,
            "已保存账户文件（{0} 个账户）",
            [accounts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private static byte[] Protect(string plaintext)
    {
        // CA1416 要求"平台专用 API"在同一方法内被显式守护；这同时把
        // "非 Windows 上会怎样"变成一行可读的代码，而不是一个运行时惊喜。
        if (!OperatingSystem.IsWindows())
        {
            throw new AccountStoreException(AccountStoreFailure.UnsupportedPlatform, string.Empty);
        }

        try
        {
            return ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext),
                Entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new AccountStoreException(AccountStoreFailure.UnsupportedPlatform, string.Empty, ex);
        }
    }

    private byte[] Unprotect(byte[] protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AccountStoreException(AccountStoreFailure.UnsupportedPlatform, FilePath);
        }

        try
        {
            return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new AccountStoreException(AccountStoreFailure.UnsupportedPlatform, FilePath, ex);
        }
        catch (CryptographicException ex)
        {
            // 最典型的原因：文件来自另一台机器或另一个 Windows 用户。
            throw new AccountStoreException(AccountStoreFailure.DecryptionFailed, FilePath, ex);
        }
    }
}
