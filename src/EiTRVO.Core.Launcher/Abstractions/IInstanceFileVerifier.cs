namespace EiTRVO.Core.Launcher.Abstractions;

/// <summary>实例文件校验结果（纯数据；文案由 App 层拼）。</summary>
/// <param name="CheckedFiles">实际做过 SHA-1 校验的文件数。</param>
/// <param name="MissingFiles">版本 JSON 声明了、但磁盘上不存在的文件数。</param>
/// <param name="MismatchedFiles">存在但 SHA-1 不一致的文件数。</param>
/// <param name="SkippedFiles">没有官方校验值、因而无法校验的文件数（如部分加载器库）。</param>
public sealed record InstanceFileVerificationResult(
    int CheckedFiles,
    int MissingFiles,
    int MismatchedFiles,
    int SkippedFiles)
{
    /// <summary>是否全部通过。</summary>
    public bool IsClean => MissingFiles == 0 && MismatchedFiles == 0;
}

/// <summary>
/// 按实例的版本 JSON 校验磁盘上的游戏文件（客户端 JAR 与库的 SHA-1）。
/// <para>
/// 这是 p03「校验文件」按钮的真实实现：结果只返回计数，不在这里拼界面文案
/// （Q8：内核不依赖中文文案；App 用 <c>Loc.T</c> 拼句子）。
/// </para>
/// </summary>
public interface IInstanceFileVerifier
{
    /// <summary>校验一个实例的客户端 JAR 与库文件。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>校验结果。</returns>
    Task<InstanceFileVerificationResult> VerifyAsync(
        string instanceName,
        CancellationToken cancellationToken = default);
}
