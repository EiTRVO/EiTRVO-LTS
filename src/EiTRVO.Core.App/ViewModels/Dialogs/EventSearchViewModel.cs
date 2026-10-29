using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.Launcher.Events;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// Ctrl+F 打开的事件搜索窗口：搜索当前页面事件区块里的消息、来源、实例名和时间。
/// </summary>
internal sealed partial class EventSearchViewModel : ObservableObject
{
    private readonly string _titleKey = "搜索事件";
    private IReadOnlyList<EventEntry> _source = [];

    /// <summary>窗口标题。</summary>
    public string Title => Loc.T(_titleKey);

    /// <summary>搜索关键字。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>当前命中的事件。</summary>
    [ObservableProperty]
    private IReadOnlyList<EventEntry> _results = [];

    /// <summary>结果数量文本。</summary>
    public string ResultCountText => Loc.T("共 {0} 条", Results.Count);

    /// <summary>用户关闭了搜索窗口。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>打开窗口并载入当前页面的事件快照。</summary>
    /// <param name="events">当前页面的事件。</param>
    public void Open(IReadOnlyList<EventEntry> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        _source = [.. events];
        SearchText = string.Empty;
        Results = _source;
        OnPropertyChanged(nameof(ResultCountText));
    }

    /// <summary>语言切换后刷新标题与结果计数。</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>关闭窗口。</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();

        IReadOnlyList<EventEntry> matches = keyword.Length == 0
            ? _source
            : [.. _source.Where(entry =>
                entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || entry.SourceText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || entry.InstanceName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || entry.Time.Contains(keyword, StringComparison.OrdinalIgnoreCase))];

        Results = matches;
        OnPropertyChanged(nameof(ResultCountText));
    }
}
