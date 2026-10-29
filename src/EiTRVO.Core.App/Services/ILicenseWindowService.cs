namespace EiTRVO.Core.App.Services;

/// <summary>「第三方许可」窗口的显示控制（独立窗口，不经过导航服务）。</summary>
internal interface ILicenseWindowService
{
    /// <summary>打开第三方许可窗口；已经打开时前置已有窗口，不重复创建。</summary>
    void Show();
}
