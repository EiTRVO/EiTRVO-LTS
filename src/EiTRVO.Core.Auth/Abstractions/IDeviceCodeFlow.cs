using EiTRVO.Core.Auth.Microsoft;

namespace EiTRVO.Core.Auth.Abstractions;

/// <summary>
/// 设备代码流（D-1）：申请代码 → 把链接与代码给用户 → 等他授权 → 拿到令牌。
/// <para>
/// 形状是**两段式**而不是一个 <c>SignInAsync</c>：第一段（<see cref="StartAsync"/>）几百毫秒就回来，
/// 界面必须**立刻**把代码显示出来；第二段（<see cref="WaitAsync"/>）要等用户，可能几十秒到十几分钟。
/// 合成一个方法虽然少一个成员，但界面在那十几分钟里没有任何可显示的东西，
/// 只能干等 —— 而"把代码给用户"正是整个流程最关键的一步。
/// </para>
/// <para>
/// 顺带说明为什么这里**没有**密码输入：设备代码流的意义就是启动器永远不接触用户密码
/// （AGENTS.md §6 明令禁止内嵌浏览器收密码）。
/// </para>
/// </summary>
public interface IDeviceCodeFlow
{
    /// <summary>当前会话；还没发起或已结束时仍保留最后一次的结论（供界面显示）。</summary>
    DeviceCodeSession? Current { get; }

    /// <summary>申请设备代码，并把通过白名单校验的验证地址交出来。</summary>
    /// <param name="progress">进度接收者。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>会话（含链接与用户代码）。</returns>
    /// <exception cref="AuthException">
    /// 网络/状态码异常，或返回的验证地址不在登录域名白名单内（D-3：钓鱼防护，
    /// **校验在展示之前**，可疑地址不会被交到界面上）。
    /// </exception>
    Task<DeviceCodeSession> StartAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>按官方要求的间隔轮询，直到用户授权 / 代码过期 / 被拒 / 取消。</summary>
    /// <param name="progress">进度接收者。</param>
    /// <param name="cancellationToken">取消标记（用户关掉对话框就取消）。</param>
    /// <returns>结论（含令牌；**只有认证层内部与登录服务会看到它**）。</returns>
    /// <exception cref="AuthException">轮询过程中的网络或协议错误。</exception>
    Task<DeviceCodeOutcome> WaitAsync(
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
