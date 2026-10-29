namespace EiTRVO.Core.App.Services;

/// <summary>
/// 帮助与支持中心（独立窗口）的显示控制。
/// <para>
/// 单独一个接口而不是塞进 <see cref="IWindowService"/>：后者管的是**主窗口**自身的
/// 最小化 / 还原 / 关闭；而帮助窗口是另一个窗口，且"已打开则前置、不重复开第二份"这条规则
/// 只有实现方知道。
/// </para>
/// </summary>
internal interface IHelpWindowService
{
    /// <summary>打开帮助与支持中心；已经打开时前置已有窗口。</summary>
    void Show();
}
