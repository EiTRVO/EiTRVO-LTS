using System.Globalization;
using EiTRVO.Core.App.ViewModels.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 所有页面 ViewModel 的基类。
/// <para>
/// 这里只放「每个页面都必须回答的问题」：我是谁（<see cref="Id"/>）、叫什么（<see cref="Title"/>）、
/// 面包屑怎么画（<see cref="Breadcrumb"/>）、左导航该高亮哪一项（<see cref="NavKey"/>）。
/// 任何与具体页面内容有关的东西都不放在基类。
/// </para>
/// </summary>
internal abstract class PageViewModelBase : ObservableObject
{
    /// <summary>构造基类。</summary>
    /// <param name="id">页面标识。</param>
    /// <param name="titleKey">
    /// 页面名的**资源键**（也就是简体中文原文）。存键而不是存译文，
    /// 这样切换语言时 <see cref="Title"/> 会跟着变 —— 页面 ViewModel 都是单例。
    /// </param>
    protected PageViewModelBase(PageId id, string titleKey)
    {
        Id = id;
        _titleKey = titleKey;
    }

    private readonly string _titleKey;

    /// <summary>页面标识。</summary>
    public PageId Id { get; }

    /// <summary>页面名（当前语言）。用于日志、测试断言与默认的区块标题。</summary>
    public string Title => Loc.T(_titleKey);

    /// <summary>
    /// 内容区区块标题。默认与 <see cref="Title"/> 相同。
    /// <para>
    /// 只有「区块标题故意不等于页面名」的页面才覆盖它 —— 例如实例详情页的页面名是
    /// 「实例详情」，但设计稿里那一块的标题是「属性」（与 Server Manager 的「属性」栏同名）。
    /// </para>
    /// </summary>
    public virtual string SectionTitle => Title;

    /// <summary>左导航高亮项。</summary>
    public abstract NavKey NavKey { get; }

    /// <summary>区块标题右侧的副标题（灰色小字）。空串表示不显示。</summary>
    public virtual string SectionSubtitle => string.Empty;

    /// <summary>面包屑各级。</summary>
    public abstract IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb { get; }

    /// <summary>
    /// 该页面是否带模态层（设计稿里的 p13 删除实例、p15 添加账户）。
    /// <para>
    /// 遮罩由外壳绘制，这样它能盖住整个窗口（含左导航与页头），
    /// 与设计稿 <c>.modal-scrim{position:absolute;inset:0}</c> 的行为一致。
    /// </para>
    /// </summary>
    public virtual bool HasDialog => false;

    /// <summary>模态层的内容 ViewModel；为 <see langword="null"/> 表示没有模态层。</summary>
    public virtual object? Dialog => null;

    /// <summary>当前页面是否包含事件区块，可供 Ctrl+F 搜索。</summary>
    public virtual bool CanSearchEvents => false;

    /// <summary>当前页面可搜索的事件快照；没有事件区块时为空。</summary>
    public virtual IReadOnlyList<EventEntry> SearchableEvents => [];

    /// <summary>
    /// 页面刚被导航到时调用（由外壳负责）。
    /// <para>
    /// 默认只做一件事：用空属性名通知一次，让该页面上所有绑定重新求值。
    /// 对「带实例上下文」的页面来说这就够了 —— 它们的属性都是从
    /// <c>IInstanceCatalog.ActiveInstanceName</c> 现算的，换个实例再进来自然就是新实例的内容。
    /// 需要额外重载（例如逐实例设置的草稿基线）的页面可以覆盖本方法。
    /// </para>
    /// </summary>
    public virtual void OnActivated() => OnLanguageChanged();

    /// <summary>
    /// 界面语言变更时调用（由外壳负责）。
    /// <para>
    /// 默认与 <see cref="OnActivated"/> 相同；两者分开是因为有的页面在「被导航到」时
    /// 需要做额外的事（例如实例设置页要按新实例重取草稿基线），而那件事在换语言时不该做
    /// （会把用户没保存的修改冲掉）。
    /// </para>
    /// </summary>
    public virtual void OnLanguageChanged()
    {
        OnPropertyChanged(string.Empty);
        (Dialog as IPageDialog)?.Refresh();
    }

    /// <summary>只有一级的面包屑（页名 = 导航项名时使用，不出现重复的上一级）。</summary>
    /// <returns>单级面包屑。</returns>
    protected IReadOnlyList<BreadcrumbPartViewModel> SingleCrumb() =>
        [new BreadcrumbPartViewModel(Title, null, ShowSeparator: false)];

    /// <summary>两级面包屑：上一级可点回，末级是当前页。</summary>
    /// <param name="parentText">上一级显示文本。</param>
    /// <param name="parentTarget">上一级跳转目标。</param>
    /// <returns>两级面包屑。</returns>
    protected IReadOnlyList<BreadcrumbPartViewModel> NestedCrumb(string parentText, PageId parentTarget) =>
    [
        new BreadcrumbPartViewModel(parentText, parentTarget, ShowSeparator: false),
        new BreadcrumbPartViewModel(Title, null, ShowSeparator: true),
    ];

    /// <summary>
    /// 事件表上方那行「所有事件 | 共 35 个」。
    /// 千位分隔符跟随当前区域设置（中文区域是逗号），与设计稿的 <c>1,204</c> 一致。
    /// </summary>
    /// <param name="label">左侧标签。</param>
    /// <param name="total">总数。</param>
    /// <param name="unit">量词，默认「个」。</param>
    /// <returns>该行的完整文本。</returns>
    protected static string FormatCount(string label, int total, string unit = "") =>
        string.IsNullOrEmpty(unit)
            ? Loc.T("{0} | 共 {1}", label, total.ToString("N0", CultureInfo.CurrentCulture))
            : Loc.T("{0} | 共 {1} {2}", label, total.ToString("N0", CultureInfo.CurrentCulture), unit);
}
