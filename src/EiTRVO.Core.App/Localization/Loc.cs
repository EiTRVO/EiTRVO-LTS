namespace EiTRVO.Core.App.Localization;

/// <summary>
/// 界面文案查找的静态门面。
/// <para>
/// 为什么是静态的：文案查找要在 ~430 处使用，其中 ~330 处在 XAML 里（走
/// <c>{loc:Text 原文}</c> 标记扩展，绑定的源必须是某个稳定的对象），
/// 另外 100 处在 ViewModel 里（多数是 <c>$"共 {n} 个"</c> 这种组合串）。
/// 逐处注入 <see cref="ILocalizationService"/> 会让 20 个 ViewModel 的构造函数
/// 都多一个与职责无关的参数，收益只是「纯」。
/// </para>
/// <para>
/// 因此这里沿用 WPF 里成熟的做法：<b>展示层</b>持有一个由组合根配置的单例门面。
/// 它不是服务定位器 —— ViewModel 不通过它拿业务服务，只拿自己的界面文案；
/// 组合根在 <c>OnStartup</c> 里调用 <see cref="Initialize"/> 指定实现，
/// 单测里也可以换成别的实现。
/// </para>
/// </summary>
public static class Loc
{
    private static ILocalizationService? _service;

    /// <summary>
    /// 当前实现。首次访问时自建一个默认实例，这样设计器、XAML 解析期与未初始化单测都能工作；
    /// 组合根调用 <see cref="Initialize"/> 之后用的就是组合根给的那个（**同一个实例**，
    /// 不能替换 —— XAML 绑定在解析期就抓住了源对象，换实例会让语言切换失效）。
    /// </summary>
    public static ILocalizationService Service => _service ??= new LocalizationService();

    /// <summary>由组合根指定实现；只应调用一次。</summary>
    /// <param name="service">实现。</param>
    public static void Initialize(ILocalizationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>取当前语言的文案。</summary>
    /// <param name="text">简体中文原文，同时也是资源键。</param>
    /// <returns>当前语言的文案。</returns>
    public static string T(string text) => Service.Translate(text);

    /// <summary>取带占位符的文案并格式化。</summary>
    /// <param name="format">含 <c>{0}</c> 占位符的中文原文（资源键）。</param>
    /// <param name="arguments">占位符实参。</param>
    /// <returns>当前语言的文案。</returns>
    public static string T(string format, params object?[] arguments) =>
        Service.Translate(format, arguments);
}