using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.Launcher.Launch;

/// <summary>
/// 游戏进程的运行器（C-12 / C-13 / C-14）。
/// <para>
/// 之所以抽成接口：启动编排（<see cref="GameLauncher"/>）要能只依赖接口，
/// 于是"阶段推进、失败拦截、会话记账"这些编排逻辑可以在**不起真进程**的情况下被断言；
/// 而"进程真的能被回收"那部分由 <see cref="GameProcessRunner"/> 用真进程测（见 <c>GameProcessRunnerTests</c>）。
/// 两层各测各的边界。
/// </para>
/// </summary>
public interface IGameProcessRunner : IDisposable
{
    /// <summary>一批输出就绪。<b>在后台线程触发</b>；不同批次可能并发，订阅方自行加锁。</summary>
    event EventHandler<GameOutputBatch>? OutputReceived;

    /// <summary>
    /// 进程已经起来（pid 可用），输出泵也已就位。<b>在后台线程触发，每次会话只触发一次。</b>
    /// <para>
    /// 编排层靠它把会话状态切到"正在运行" —— 不去猜"<c>RunAsync</c> 在第一个 await 之前是不是已经把进程起好了"，
    /// 那种依赖时序的假设会在某次重构后静默失效。
    /// </para>
    /// </summary>
    event EventHandler<int>? Started;

    /// <summary>是否有一个进程正在跑。</summary>
    bool IsRunning { get; }

    /// <summary>正在运行的进程 id；没有在跑时为 <see langword="null"/>。</summary>
    int? ProcessId { get; }

    /// <summary>启动并等它结束。</summary>
    /// <param name="request">启动输入。</param>
    /// <param name="cancellationToken">取消（用户取消启动 / 请求结束游戏）。</param>
    /// <returns>本次会话的结局。</returns>
    Task<GameProcessResult> RunAsync(GameLaunchRequest request, CancellationToken cancellationToken);

    /// <summary>请求结束游戏（进程树由 Job Object 兜底回收）。</summary>
    void Kill();
}

/// <summary>
/// 启动编排的配置。
/// <para>
/// <see cref="ClientId"/> 是命令行里 <c>${clientid}</c> 的来源，**必须来自配置文件**
/// （AGENTS.md §6：Neo 把这个值硬编码在两处，而且只在配置里改一处会漏另一处）。
/// 单独一个类型而不是让它进 ctor 的字符串参数：注册时一眼能看出"这是个配置项"，
/// 也不会和别的字符串参数混淆。
/// </para>
/// </summary>
public sealed record LaunchOptions
{
    /// <summary>微软应用注册的 Client ID。</summary>
    public string ClientId { get; init; } = string.Empty;
}

/// <summary>
/// 实例凭据的来源。
/// <para>
/// 内核**不引用认证层**（依赖方向是 Auth → Launcher），所以"账户令牌从哪来"这件事由调用方实现：
/// App 层把 <c>IAccountCatalog.GetCredentialAsync</c> 的结果映射成 <see cref="LaunchIdentity"/>。
/// 这样启动编排不需要知道账户是怎么存的，而编排的测试可以给一个假凭据。
/// </para>
/// <para>
/// **为什么是异步的**：正版账户的访问令牌只活约 24 小时，而"点启动时它刚好过期"是最常见的情形。
/// 同步的签名会逼出一个选择：要么在启动那一刻阻塞（界面假死），要么让用户先去账户页手动刷新
/// （.Result / .Wait() 被 AGENTS §2.2 明令禁止，手动刷新则把一件本该自动的事推给了用户）。
/// </para>
/// </summary>
public interface ILaunchIdentityProvider
{
    /// <summary>取某个实例要用的凭据（正版账户可能在这一次调用里刷新令牌）。</summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="cancellationToken">取消标记（用户取消启动 / 启动器退出）。</param>
    /// <returns>可以进命令行的凭据。</returns>
    /// <exception cref="LaunchIdentityException">拿不到可用凭据（没登录、令牌不可用、账户已失效）。</exception>
    Task<LaunchIdentity> GetIdentityAsync(string instanceName, CancellationToken cancellationToken = default);
}

/// <summary>拿不到启动凭据。原因是**给用户看的**（键 + 参数，由 App 层填、由 App 层拼句子）。</summary>
public sealed class LaunchIdentityException : Exception
{
    /// <summary>构造凭据异常。</summary>
    /// <param name="reason">给用户看的原因（键 + 参数）。</param>
    public LaunchIdentityException(TextTemplate reason)
        : base((reason ?? throw new ArgumentNullException(nameof(reason))).Key)
    {
        Reason = reason;
    }

    /// <summary>构造凭据异常（带内部异常）。</summary>
    /// <param name="reason">给用户看的原因（键 + 参数）。</param>
    /// <param name="innerException">内部异常。</param>
    public LaunchIdentityException(TextTemplate reason, Exception innerException)
        : base((reason ?? throw new ArgumentNullException(nameof(reason))).Key, innerException)
    {
        Reason = reason;
    }

    /// <summary>失败原因（键 + 参数；句子由 App 层拼）。</summary>
    public TextTemplate Reason { get; }
}

/// <summary>
/// 启动游戏（M4 的编排层）。
/// <para>
/// 主流程只依赖接口：实例目录、设置、版本 JSON 存储、Java 运行时检测、本机信息、进程运行器、诊断日志。
/// 界面侧只看到 <see cref="GetSnapshot"/> 与 <see cref="Changed"/> —— 这就是"6 个 UI 回调 → 快照 + 事件"
/// 的落点（Neo 的编排里塞了 6 个 <c>Action</c> 回调，于是它没法在不起 UI 的情况下测）。
/// </para>
/// </summary>
public interface IGameLauncher
{
    /// <summary>会话状态发生变化（阶段推进、进程起停、结束）。实现方在后台线程触发。</summary>
    event EventHandler? Changed;

    /// <summary>当前会话的只读快照。</summary>
    /// <returns>快照。</returns>
    LaunchSnapshot GetSnapshot();

    /// <summary>最近的游戏输出（给实时日志用；最多 <paramref name="maximumCount"/> 行，由旧到新）。</summary>
    /// <param name="maximumCount">最多返回多少行。</param>
    /// <returns>输出行（已脱敏）。</returns>
    IReadOnlyList<GameOutputLine> GetRecentOutput(int maximumCount);

    /// <summary>
    /// 启动一个实例，并等游戏结束。
    /// </summary>
    /// <param name="instanceName">实例名。</param>
    /// <param name="cancellationToken">取消（取消启动 / 结束游戏）。</param>
    /// <returns>本次启动的结果。</returns>
    Task<LaunchOutcome> LaunchAsync(string instanceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 结束当前会话（取消启动或结束正在跑的游戏）。
    /// <para>
    /// 名字不是 <c>Stop</c>：那个词在别的 .NET 语言里是保留关键字，接口成员用它会让实现方别扭（CA1716）。
    /// </para>
    /// </summary>
    void RequestStop();
}
