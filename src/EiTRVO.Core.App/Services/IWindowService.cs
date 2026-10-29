namespace EiTRVO.Core.App.Services;

/// <summary>
/// 主窗口的显示状态控制（最小化 / 还原 / 置前 / 关闭）。
/// <para>
/// 界面里只用到这四个动作：「启动游戏后最小化启动器」（全局设置里的选项）、
/// 启动前还原窗口、下载进行中关闭程序时的确认流程。模态确认**不走这里** ——
/// 设计稿用的是窗口内模态层（p13），由 <c>PageViewModelBase.HasDialog</c> 承载。
/// </para>
/// </summary>
internal interface IWindowService
{
    /// <summary>主窗口当前是否可见。</summary>
    bool IsMainWindowVisible { get; }

    /// <summary>最小化主窗口。</summary>
    void Minimize();

    /// <summary>还原并置前主窗口。</summary>
    void Restore();

    /// <summary>关闭主窗口（等价于用户点右上角关闭）。</summary>
    void Close();
}
