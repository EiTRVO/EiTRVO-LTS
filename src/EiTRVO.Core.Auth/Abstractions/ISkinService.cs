using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Auth.Abstractions;

/// <summary>
/// 皮肤操作失败的原因（D-9）。
/// <para>
/// 每一种取值在界面上都是**一句不同的话**，这是刻意的：把 401/403 与 404 合成
/// "上传失败" 会让用户对着一个"账号没问题、只是没买游戏"的提示去反复重启启动器。
/// </para>
/// </summary>
public enum SkinChangeFailure
{
    /// <summary>库里没有这个账户。</summary>
    NoSuchAccount,

    /// <summary>这是一个离线账户 —— 官方档案只属于正版账户。</summary>
    OfflineAccount,

    /// <summary>令牌已被作废，必须重新登录一次。</summary>
    NotSignedIn,

    /// <summary>这个账户没有 Minecraft Java 版档案（尚未购买）。</summary>
    NoJavaProfile,

    /// <summary>选中的文件读不出来（被移动、删除，或没有读取权限）。</summary>
    ImageUnreadable,

    /// <summary>图片本身不合规（不是 PNG、尺寸不对、超过自定上限）。</summary>
    InvalidImage,

    /// <summary>官方接口拒收这张图片（<c>400</c>）。</summary>
    ImageRejected,

    /// <summary>
    /// 官方接口不接受我们发的媒体类型（<c>415</c>）。
    /// <para>
    /// 这一条只可能是**启动器的缺陷**（multipart 的 <c>Content-Type</c> 或字段名写错），
    /// 所以要如实说成"我们的问题"，而不是让用户去换图片。
    /// </para>
    /// </summary>
    UnsupportedMedia,

    /// <summary>请求太频繁（<c>429</c>）或还在本地冷却窗口里 —— **不得循环重试**。</summary>
    Cooldown,

    /// <summary>官方服务器返回了别的状态码。</summary>
    ServerError,

    /// <summary>网络失败（连不上、超时、DNS）。</summary>
    Network,

    /// <summary>账户文件读不出来（拒绝写入，原文件未改动），或改完之后写不进去。</summary>
    StoreUnavailable,

    /// <summary>
    /// 这个账户当前用的是官方默认皮肤 —— 换模型要用"档案里已有的纹理地址"，没有地址就没得换。
    /// </summary>
    NoCustomSkin,
}

/// <summary>
/// 一次皮肤操作失败。原因**是「键 + 参数」**（句子由 App 层拼，Q8），每一种失败各是一句不同的话。
/// </summary>
public sealed class SkinChangeException : Exception
{
    /// <summary>构造皮肤异常。</summary>
    /// <param name="reason">给用户看的那句话（键 + 参数）。</param>
    /// <param name="failure">失败原因。</param>
    /// <param name="innerException">底层异常。</param>
    public SkinChangeException(TextTemplate reason, SkinChangeFailure failure, Exception? innerException = null)
        : base((reason ?? throw new ArgumentNullException(nameof(reason))).Key, innerException)
    {
        Reason = reason;
        Failure = failure;
    }

    /// <summary>失败原因（键 + 参数）。</summary>
    public TextTemplate Reason { get; }

    /// <summary>失败原因的分类。</summary>
    public SkinChangeFailure Failure { get; }
}

/// <summary>
/// 皮肤（D-9）：上传 PNG、换模型、恢复默认皮肤。
/// <para>
/// 三条纪律写在这里，因为它们是**接口的语义**而不是实现的细节：
/// </para>
/// <list type="number">
/// <item>只能改**当前登录账户自己**的皮肤：官方接口按 bearer 令牌定主体，不存在"给别人改"这条路，
/// 所以这里的 <c>playerName</c> 只是"用哪一个本地账户的令牌"，不是一个可选择的目标；</item>
/// <item>成功后 <b>60 秒内不得再上传</b>（官方没有公布冷却秒数，这是一条自定策略）——
/// <see cref="CooldownRemaining"/> 让界面能把按钮真的灰掉，而不是只在事后报错；</item>
/// <item>遇到 <c>429</c> **不得循环重试**：连续失败会让账户被临时封禁，宁可让用户等一分钟。</item>
/// </list>
/// </summary>
public interface ISkinService
{
    /// <summary>
    /// 上传一张 PNG 作为该账户的皮肤。
    /// </summary>
    /// <param name="playerName">本地账户的玩家名（决定用哪一个令牌）。</param>
    /// <param name="pngFilePath">PNG 文件路径（上传前做本地校验）。</param>
    /// <param name="model">皮肤模型（classic / slim）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>上传后的皮肤信息（已用响应里的档案刷新，无需再发一次请求）。</returns>
    /// <exception cref="SkinChangeException">任何一步失败（原因见 <see cref="SkinChangeFailure"/>）。</exception>
    Task<SkinProfile> UploadAsync(
        string playerName,
        string pngFilePath,
        SkinModel model,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只换模型，重新上传**档案里已有的那张纹理**（官方 JSON 形态 <c>{"variant":…,"url":…}</c>）。
    /// <para>
    /// 这是 p11 那个「皮肤模型」下拉框的真正落点：换模型不必让用户再选一次文件。
    /// </para>
    /// </summary>
    /// <param name="playerName">玩家名。</param>
    /// <param name="model">目标模型。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>更新后的皮肤信息。</returns>
    /// <exception cref="SkinChangeException">
    /// 任何一步失败；当前是官方默认皮肤时是 <see cref="SkinChangeFailure.NoCustomSkin"/>。
    /// </exception>
    Task<SkinProfile> SetModelAsync(
        string playerName,
        SkinModel model,
        CancellationToken cancellationToken = default);

    /// <summary>恢复官方默认皮肤（<c>DELETE …/skins/active</c>）。</summary>
    /// <param name="playerName">玩家名。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>恢复后的皮肤信息。</returns>
    /// <exception cref="SkinChangeException">任何一步失败。</exception>
    Task<SkinProfile> ResetAsync(string playerName, CancellationToken cancellationToken = default);

    /// <summary>距下一次可操皮肤还剩多久；可以操作时为 <see cref="TimeSpan.Zero"/>。</summary>
    /// <param name="playerName">玩家名。</param>
    /// <returns>剩余冷却时间。</returns>
    TimeSpan CooldownRemaining(string playerName);
}
