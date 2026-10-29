using EiTRVO.Core.Auth.Microsoft;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Auth.Tests.Fakes;

/// <summary>
/// 令牌刷新链的替身：不联网，按预置的档案与失败返回。
/// <para>
/// 账户目录与刷新有关的用例都要它 —— 那几条用例要验的是"刷新成功了会不会存下来""刷新失败说成哪句话"，
/// 而不是 XBL/XSTS 的报文（那一层有自己的用例，全部断言到具体 URL 与请求体）。
/// </para>
/// </summary>
internal sealed class FakeMinecraftAuthChain : IMinecraftAuthChain
{
    /// <summary>被调用的次数。</summary>
    public int Calls { get; private set; }

    /// <summary>调用时要抛的异常（模拟刷新失败）。</summary>
    public AuthException? Failure { get; set; }

    /// <summary>要返回的档案（默认是 Steve 的经典皮肤）。</summary>
    public MinecraftProfile Profile { get; set; } = new(
        Uuid: "069a79f4-44e9-4726-a5be-fca90e38aaf5",
        Name: "Steve",
        SkinUrl: "http://textures.minecraft.net/texture/abcdef1234567890",
        SkinVariant: "CLASSIC",
        HasCape: false);

    /// <summary>刷新链走完后拿到的 Minecraft 访问令牌（D-9 的"重试用的是刷新后的令牌"要断言它）。</summary>
    public string AccessToken { get; set; } = "mc-access-token";

    /// <inheritdoc />
    public Task<MicrosoftLoginResult> CompleteAsync(
        MicrosoftTokens microsoft,
        IProgress<AuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new MicrosoftLoginResult(
            microsoft,
            new MinecraftToken(AccessToken, microsoft.ExpiresAt),
            Profile));
    }
}

/// <summary>设备代码 / 令牌端点客户端的替身（账户目录只用它的"刷新令牌"那一步）。</summary>
internal sealed class FakeDeviceCodeClient : IDeviceCodeClient
{
    /// <summary>刷新被调用的次数（断言"令牌还有效时不联网"）。</summary>
    public int RefreshCalls { get; set; }

    /// <summary>刷新时要抛的异常。</summary>
    public AuthException? RefreshFailure { get; set; }

    /// <summary>刷新返回的新令牌。</summary>
    public MicrosoftTokens Tokens { get; set; } = new(
        AccessToken: "ms-access-token",
        RefreshToken: "rotated-refresh-token",
        ExpiresAt: new DateTimeOffset(2026, 9, 30, 17, 1, 2, TimeSpan.FromHours(8)));

    /// <inheritdoc />
    public Task<DeviceCodeChallenge> RequestCodeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("这一层用例不涉及设备代码申请（见 DeviceCodeFlowTests）。");

    /// <inheritdoc />
    public Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("这一层用例不涉及轮询（见 DeviceCodeFlowTests）。");

    /// <inheritdoc />
    public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        RefreshCalls++;

        if (RefreshFailure is not null)
        {
            throw RefreshFailure;
        }

        return Task.FromResult(Tokens);
    }
}

/// <summary>固定时钟：让「上次使用」这类断言的期望值是一个具体值。</summary>
internal sealed class FixedClock : IClock
{
    /// <summary>构造固定时钟。</summary>
    /// <param name="now">要返回的时间。</param>
    public FixedClock(DateTimeOffset now) => Now = now;

    /// <inheritdoc />
    public DateTimeOffset Now { get; set; }
}

/// <summary>可推进的时钟：设备代码流的"过期"要能测，而真等 15 分钟没人愿意。</summary>
internal sealed class MutableClock : IClock
{
    /// <summary>构造时钟。</summary>
    /// <param name="now">起始时间。</param>
    public MutableClock(DateTimeOffset now) => Now = now;

    /// <inheritdoc />
    public DateTimeOffset Now { get; set; }
}

/// <summary>
/// **同步**收集进度上报的接收者。
/// <para>
/// 不用 <see cref="Progress{T}"/>：它把回调 post 到同步上下文（测试里就是线程池），
/// 于是"刚报过"与"断言"之间会有一个竞态的窗口 —— 那种用例会偶发红。
/// </para>
/// </summary>
/// <typeparam name="T">上报类型。</typeparam>
internal sealed class CollectingProgress<T> : IProgress<T>
{
    /// <summary>按顺序收到的上报。</summary>
    public List<T> Values { get; } = [];

    /// <inheritdoc />
    public void Report(T value) => Values.Add(value);
}

/// <summary>只把事件记在内存里的诊断日志。</summary>
internal sealed class RecordingDiagnosticsLog : IDiagnosticsLog
{
    /// <summary>按写入顺序记录的事件。</summary>
    public List<LauncherEvent> Entries { get; } = [];

    /// <inheritdoc />
    public void Write(
        LauncherEventLevel level,
        string source,
        string messageKey,
        IReadOnlyList<string>? parameters = null,
        string? instanceName = null) =>
        Entries.Add(new LauncherEvent
        {
            Level = level,
            Source = source,
            MessageKey = messageKey,
            Parameters = parameters ?? [],
            InstanceName = instanceName,
        });

    /// <inheritdoc />
    public EventLogSnapshot Read(int maximumCount) =>
        new(Entries.Count, maximumCount <= 0 ? [] : [.. Entries.TakeLast(maximumCount)]);
    /// <inheritdoc />
    /// <remarks>M6-f：这个替身只记录事件，不发通知（Auth 侧没有界面在等它）。</remarks>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>测试用的数据目录布局（固定的临时根目录）。</summary>
internal static class TestPaths
{
    /// <summary>建一个全新的临时数据目录。</summary>
    /// <param name="suffix">目录名后缀，便于定位失败的用例。</param>
    /// <returns>数据目录布局。</returns>
    public static LauncherPaths Create(string suffix) =>
        new(Path.Combine(Path.GetTempPath(), "eitrvo-core-accounts", suffix + "-" + Guid.NewGuid().ToString("N")));
}
