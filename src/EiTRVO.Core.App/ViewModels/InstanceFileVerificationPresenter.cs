using System.IO;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// p03 / p13 / p17 共用的「校验文件」动作：执行校验、把结果变成属性面板上的一句话。
/// </summary>
/// <remarks>
/// 三个页面共用 <see cref="InstanceDetailPanelViewModel"/>，按钮也在同一个面板里；
/// 把命令与状态收在这一个类型里，避免三个 ViewModel 各写一份（也会让 p13 再漏一次）。
/// </remarks>
/// <summary>测试/设计时没有校验服务时的占位：命令可用，但什么文件都不碰。</summary>
internal sealed class UnavailableInstanceFileVerifier : IInstanceFileVerifier
{
    public Task<InstanceFileVerificationResult> VerifyAsync(
        string instanceName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstanceFileVerificationResult(0, 0, 0, 0));
}

internal sealed class InstanceFileVerificationPresenter
{
    private readonly IInstanceFileVerifier _verifier;
    private readonly Func<string?> _activeInstance;
    private readonly Action _changed;

    /// <summary>构造 presenter。</summary>
    /// <param name="verifier">校验服务。</param>
    /// <param name="activeInstance">当前实例名（没有实例时为空）。</param>
    /// <param name="changed">状态变化后的通知回调（页面用它刷新属性面板）。</param>
    public InstanceFileVerificationPresenter(
        IInstanceFileVerifier verifier,
        Func<string?> activeInstance,
        Action changed)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(activeInstance);
        ArgumentNullException.ThrowIfNull(changed);

        _verifier = verifier;
        _activeInstance = activeInstance;
        _changed = changed;
        Command = new AsyncRelayCommand(ExecuteAsync);
    }

    /// <summary>绑定到面板按钮的命令。</summary>
    public IAsyncRelayCommand Command { get; }

    /// <summary>最近一次校验的显示文本；还没校验过时为 <see langword="null"/>。</summary>
    public string? StatusText { get; private set; }

    private string? _verifiedInstance;

    /// <summary>
    /// 只返回属于当前实例的校验结果；A 实例的校验文本不能泄漏到 B 实例的属性面板。
    /// </summary>
    /// <param name="instanceName">当前实例名。</param>
    /// <returns>该实例的校验文本；没有校验过或实例不同时为 <see langword="null"/>。</returns>
    public string? StatusTextFor(string? instanceName) =>
        _verifiedInstance is { } verified
        && string.Equals(verified, instanceName, StringComparison.Ordinal)
            ? StatusText
            : null;

    private async Task ExecuteAsync()
    {
        var instanceName = _activeInstance();
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return;
        }

        _verifiedInstance = instanceName;
        StatusText = null;

        try
        {
            var result = await _verifier.VerifyAsync(instanceName).ConfigureAwait(true);

            StatusText = result.IsClean
                ? Loc.T("已校验 {0} 个文件，全部通过", result.CheckedFiles)
                : Loc.T("已校验 {0} 个文件：缺失 {1} 个，损坏 {2} 个", result.CheckedFiles, result.MissingFiles, result.MismatchedFiles);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or VersionResolutionException
                                           or VersionDataException)
        {
            StatusText = Loc.T("校验失败：{0}", exception.Message);
        }

        _changed();
    }
}
