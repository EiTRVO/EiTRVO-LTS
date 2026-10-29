namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 页面标识。所有导航都以它为目标，View 里不出现「new 一个页面」这种写法。
/// 顺序与 mockup/pages 的编号一致（p01…p17，p16 是规范稿不属于产品页面）。
/// </summary>
internal enum PageId
{
    /// <summary>p01 仪表板。</summary>
    Dashboard,

    /// <summary>p02 本地实例。</summary>
    Instances,

    /// <summary>p03 实例详情。</summary>
    InstanceDetail,

    /// <summary>p04 新建实例向导。</summary>
    NewInstance,

    /// <summary>p05 所有版本。</summary>
    Versions,

    /// <summary>p06 下载与队列。</summary>
    Downloads,

    /// <summary>p07 模组管理。</summary>
    Mods,

    /// <summary>p08 整合包。</summary>
    Modpacks,

    /// <summary>p09 资源包 / 存档。</summary>
    Resources,

    /// <summary>p10 Java 与设置（全局）。</summary>
    JavaSettings,

    /// <summary>p11 账户。</summary>
    Accounts,

    /// <summary>p12 关于。</summary>
    About,

    /// <summary>p13 删除实例（模态）。</summary>
    DeleteInstance,

    /// <summary>p14 正在启动（独立窗）。</summary>
    Launching,

    /// <summary>p15 添加账户（模态）。</summary>
    AccountLogin,

    /// <summary>p17 实例设置（逐实例）。</summary>
    InstanceSettings,
}

/// <summary>左导航的项标识。用于「当前页面高亮哪一项」。</summary>
internal enum NavKey
{
    /// <summary>不高亮任何一项（p17 实例设置在设计稿里没有对应导航项）。</summary>
    None,

    /// <summary>仪表板。</summary>
    Dashboard,

    /// <summary>本地实例。</summary>
    Instances,

    /// <summary>所有版本。</summary>
    Versions,

    /// <summary>下载与队列。</summary>
    Downloads,

    /// <summary>模组管理。</summary>
    Mods,

    /// <summary>资源包 / 存档。</summary>
    Resources,

    /// <summary>整合包。</summary>
    Modpacks,

    /// <summary>Java 与设置。</summary>
    JavaSettings,

    /// <summary>账户。</summary>
    Accounts,

    /// <summary>关于。</summary>
    About,
}
