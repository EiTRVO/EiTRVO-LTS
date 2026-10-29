using System.ComponentModel;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// 模态层内容的统一契约。
/// <para>
/// 模态框的 DataContext 是「对话框 ViewModel」，和宿主页面不是同一个对象 ——
/// 所以页面被导航到时（<see cref="PageViewModelBase.OnActivated"/>）
/// 必须能顺带把它也叫醒一次，否则换了实例再打开 p13，
/// 确认框里还会写着上一个实例的名字与占用空间。
/// </para>
/// </summary>
internal interface IPageDialog : INotifyPropertyChanged
{
    /// <summary>重新求值所有绑定。</summary>
    void Refresh();
}