using CommunityToolkit.Mvvm.Input;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p01 首次运行清单里的一行：一个"还没准备好"的事实 + 一个明确去向。
/// </summary>
/// <param name="Title">一句话标题（如「还没有实例」）。</param>
/// <param name="Detail">为什么挡住、点右边会发生什么。</param>
/// <param name="ActionText">按钮文案。</param>
/// <param name="Command">去处理这件事的命令。</param>
public sealed record FirstRunIssueViewModel(
    string Title,
    string Detail,
    string ActionText,
    RelayCommand Command);
