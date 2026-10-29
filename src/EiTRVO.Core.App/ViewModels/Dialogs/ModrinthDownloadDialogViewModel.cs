using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Installer;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// p07 / p09 的「从 Modrinth 下载」对话框：搜索模组或资源包 → 选一项 → 按当前实例的
/// 游戏版本与加载器挑兼容版本 → 排进下载队列（下载本体在 p06 里看得见）。
/// </summary>
/// <remarks>
/// 同一个对话框从模组页与资源包页打开，靠 <see cref="ModrinthContentKind"/> 区分；
/// 目标实例在打开时由页面传进来（页面自己知道"当前实例"是哪个，对话框不重复解析）。
/// </remarks>
internal sealed partial class ModrinthDownloadDialogViewModel : ObservableObject, IPageDialog
{
    private readonly IContentCatalog _content;
    private readonly IDownloadCatalog _downloads;
    private readonly INavigationService _navigation;

    private ModrinthContentKind _kind = ModrinthContentKind.Mod;
    private string _instanceName = string.Empty;

    /// <summary>对话框标题（随内容种类变化）。</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>搜索框里的关键词。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>当前页的结果。</summary>
    [ObservableProperty]
    private IReadOnlyList<ModrinthEntry> _entries = [];

    /// <summary>选中的那一行。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private ModrinthEntry? _selectedEntry;

    /// <summary>失败原因（搜索 / 下载都写这里）；为空时不占高度。</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>搜索或下载进行中（按钮置灰，避免重复点）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _isBusy;

    /// <summary>用户关掉了对话框（页面据此收起模态层）。</summary>
    public event EventHandler? Closed;

    /// <summary>构造对话框 ViewModel。</summary>
    /// <param name="content">内容目录（搜索与准备下载目标）。</param>
    /// <param name="downloads">下载队列（下载排进去）。</param>
    /// <param name="navigation">导航（下载后跳到 p06 看进度）。</param>
    public ModrinthDownloadDialogViewModel(
        IContentCatalog content,
        IDownloadCatalog downloads,
        INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(navigation);

        _content = content;
        _downloads = downloads;
        _navigation = navigation;
    }

    /// <summary>打开对话框并开始一次搜索。</summary>
    /// <param name="kind">模组或资源包。</param>
    /// <param name="instanceName">目标实例。</param>
    public void Open(ModrinthContentKind kind, string instanceName)
    {
        _kind = kind;
        _instanceName = instanceName;
        Title = kind == ModrinthContentKind.Mod
            ? Loc.T("从 Modrinth 下载模组")
            : Loc.T("从 Modrinth 下载资源包");

        SearchText = string.Empty;
        Entries = [];
        SelectedEntry = null;
        Error = string.Empty;

        SearchCommand.Execute(null);
    }

    /// <inheritdoc />
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>按关键词搜索（空关键词就是按下载量列出适配当前实例的内容）。</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_instanceName.Length == 0)
        {
            return;
        }

        IsBusy = true;
        Error = string.Empty;

        try
        {
            var entries = await _content.SearchModrinthContentAsync(_instanceName, _kind, SearchText);

            Entries = entries ?? [];
            SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
            Error = entries is null ? Loc.T("搜索失败，稍后再试。") : string.Empty;
        }
        catch (Exception)
        {
            Entries = [];
            SelectedEntry = null;
            Error = Loc.T("搜索失败，稍后再试。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把选中项排进下载队列，然后跳到 p06。</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (SelectedEntry is not { CanInstall: true, ProjectId: { } projectId } entry)
        {
            return;
        }

        IsBusy = true;
        Error = string.Empty;

        try
        {
            var prepared = await _content.PrepareModrinthContentAsync(
                _instanceName,
                _kind,
                projectId,
                entry.Name);

            if (!prepared.Succeeded || prepared.Target is not { } target)
            {
                Error = prepared.Failure is { } failure
                    ? LocalizedText.Of(failure)
                    : Loc.T("下载失败，稍后再试。");
                return;
            }

            _downloads.EnqueueContentDownload(new ContentDownloadRequest(
                _instanceName,
                [target],
                InstalledProjectIds: [])
            {
                Kind = _kind,
            });

            Closed?.Invoke(this, EventArgs.Empty);
            _navigation.Navigate(PageId.Downloads);
        }
        catch (Exception)
        {
            Error = Loc.T("下载失败，稍后再试。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDownload() => !IsBusy && SelectedEntry is { CanInstall: true };

    /// <summary>关掉对话框（不排队）。</summary>
    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
