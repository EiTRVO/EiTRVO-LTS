using Microsoft.Extensions.DependencyInjection;

namespace EiTRVO.Core.App.Services;

/// <summary>
/// 演示数据源与真实实现的注册开关。
/// <para>
/// 前端样本阶段，11 个目录/账户接口背后都是内存演示实现（<c>Sample*</c>）。随着后端里程碑推进，
/// 它们会被逐个换成真实实现。这个开关保证两件事：
/// </para>
/// <list type="number">
/// <item>演示阶段仍然能跑起来（<c>DataSources:UseSampleData = true</c>，默认）；</item>
/// <item>一旦把开关关掉，**未接入真实实现的接口会当场报出它属于哪个里程碑**，而不是安静地继续给假数据 ——
/// 后者最容易发生的事故是"以为功能已经做好了"。</item>
/// </list>
/// <para>
/// 注意这不违背"真实实现不得回退"的原则：开关只用于**开发期**切换，正式构建应关闭它。
/// 已经接上真实实现的接口直接从注册里删掉演示实现即可，不需要保留两条分支。
/// </para>
/// </summary>
internal static class DataSourceRegistration
{
    /// <summary>配置键（<c>appsettings.json</c>）。</summary>
    public const string ConfigurationKey = "DataSources:UseSampleData";

    /// <summary>注册一个目录/账户接口，按开关决定用演示实现还是"尚未接入"。</summary>
    /// <typeparam name="TService">接口。</typeparam>
    /// <typeparam name="TSample">演示实现。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="useSampleData">是否使用演示数据。</param>
    /// <param name="milestone">接入该接口真实实现的里程碑（写进错误消息）。</param>
    public static void AddCatalog<TService, TSample>(
        IServiceCollection services,
        bool useSampleData,
        string milestone)
        where TService : class
        where TSample : class, TService
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(milestone);

        if (useSampleData)
        {
            services.AddSingleton<TService, TSample>();
            return;
        }

        services.AddSingleton<TService>(_ => throw new NotSupportedException(
            $"'{typeof(TService).Name}' 的真实实现尚未接入（计划里程碑 {milestone}）。" +
            $"把 {ConfigurationKey} 打开可以先用演示数据运行。"));
    }
}
