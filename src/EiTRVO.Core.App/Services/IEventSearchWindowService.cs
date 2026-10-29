using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.Services;

/// <summary>打开事件搜索窗口的显示服务。</summary>
internal interface IEventSearchWindowService
{
    /// <summary>打开搜索窗口；已打开时前置现有窗口。</summary>
    /// <param name="events">当前页面的事件快照。</param>
    void Show(IReadOnlyList<EventEntry> events);
}
