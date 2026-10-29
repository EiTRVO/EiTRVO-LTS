using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Pages;

/// <summary>
/// p09 资源包 / 存档。对应 mockup/pages/p09-resources.html。
/// <para>
/// 上半是资源包优先级列表（含启用/禁用/上移/下移），下半是存档表（含备份状态）。
/// </para>
/// <para>
/// M7-f 起「备份所选 / 备份全部存档 / 恢复所选」三个按钮是**真的**：备份与恢复都排进下载队列
/// （设计稿 <c>mockup/pages/p09-resources.html</c> 里这两个按钮的 <c>href</c> 指的就是 p06），
/// 排完队跳到那一页看进度与失败原因。资源包那一排按钮仍属 M7-g/S9。
/// </para>
/// <para>
/// 与 p07 一样绑定**当前实例**：换个实例再进来，看到的就是那个实例的内容。
/// </para>
/// </summary>
internal sealed partial class ResourcesViewModel : PageViewModelBase
{
    private readonly IContentCatalog _content;
    private readonly IInstanceCatalog _instances;
    private readonly IShellService _shell;
    private readonly IDownloadCatalog _downloads;
    private readonly INavigationService _navigation;
    private readonly ModrinthDownloadDialogViewModel? _modrinthDialog;
    private readonly IDialogService? _dialogs;
    private readonly IResourcePackImporter? _resourcePackImporter;

    private WorldSaveRowViewModel? _selectedSave;

    /// <summary>「从 Modrinth 下载资源包」的对话框是否打开。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    private bool _isModrinthDownloadOpen;
    private ResourcePackRowViewModel? _selectedResourcePack;

    /// <summary>上一次资源包动作失败的原因（为空时不占高度）。</summary>
    /// <remarks>
    /// 与 p07 的 <c>ActionError</c> 同一个理由：这些动作**直接改用户的 <c>options.txt</c>**，
    /// 失败原因（文件被游戏占用、options.txt 读不懂）必须看得见 —— 什么都不显示只会让人以为"点了没生效"。
    /// </remarks>
    [ObservableProperty]
    private string _actionError = string.Empty;

    /// <summary>上一次资源包动作成功之后要交代的话（例如"被移进了哪个备份目录"）。</summary>
    [ObservableProperty]
    private string _actionNote = string.Empty;

    /// <summary>构造资源包 / 存档 ViewModel。</summary>
    /// <param name="content">内容目录。</param>
    /// <param name="instances">实例目录。</param>
    /// <param name="shell">外壳服务（打开目录）。</param>
    /// <param name="downloads">下载队列（备份 / 恢复把任务排进去）。</param>
    /// <param name="navigation">导航（排完队跳到「下载与队列」看进度）。</param>
    /// <param name="modrinthDialog">「从 Modrinth 下载资源包」对话框。</param>
    /// <param name="dialogs">文件选择框（「添加文件」用）。</param>
    /// <param name="resourcePackImporter">本地资源包导入器（zip slip / 压缩炸弹扫描）。</param>
    public ResourcesViewModel(
        IContentCatalog content,
        IInstanceCatalog instances,
        IShellService shell,
        IDownloadCatalog downloads,
        INavigationService navigation,
        ModrinthDownloadDialogViewModel? modrinthDialog = null,
        IDialogService? dialogs = null,
        IResourcePackImporter? resourcePackImporter = null)
        : base(PageId.Resources, "资源包 / 存档")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(navigation);


        _content = content;
        _instances = instances;
        _shell = shell;
        _downloads = downloads;
        _navigation = navigation;
        _modrinthDialog = modrinthDialog;
        _dialogs = dialogs;
        _resourcePackImporter = resourcePackImporter;
        if (_modrinthDialog is not null)
        {
            _modrinthDialog.Closed += (_, _) => IsModrinthDownloadOpen = false;
        }

        // 设计稿把第一行存档画成高亮行：这里用「预先选中它」复现，之后点哪行高亮就跟到哪行。
        _selectedSave = Saves.FirstOrDefault(static save => save.IsActive);

        // 资源包列表同理：设计稿把**优先级最高的那一行**画成 `.opt.sel`。
        _selectedResourcePack = ResourcePacks.FirstOrDefault(static pack => pack.IsActive);
    }

    /// <inheritdoc />
    public override bool HasDialog => IsModrinthDownloadOpen;

    /// <inheritdoc />
    public override object? Dialog => _modrinthDialog;

    /// <summary>打开「从 Modrinth 下载资源包」对话框。</summary>
    [RelayCommand]
    private void OpenModrinth()
    {
        _modrinthDialog?.Open(ModrinthContentKind.ResourcePack, _instances.ActiveInstanceName);
        IsModrinthDownloadOpen = true;
    }

    /// <inheritdoc />
    public override NavKey NavKey => NavKey.Resources;

    /// <inheritdoc />
    public override string SectionSubtitle => Loc.T("用于 {0}", _instances.ActiveInstanceName);

    /// <inheritdoc />
    public override IReadOnlyList<BreadcrumbPartViewModel> Breadcrumb =>
        NestedCrumb(_instances.ActiveInstanceName, PageId.InstanceDetail);

    /// <summary>
    /// 资源包列表（按优先级排序）。
    /// </summary>
    /// <remarks>
    /// M6-e-3f：内核的 `Detail` 是「键 + 参数」，界面不能直接绑（§5.3 第 ⑨ 条），
    /// 所以经 <see cref="ResourcePackRowViewModel"/> 投影；属性名与内核一致，XAML 没改。
    /// </remarks>
    public IReadOnlyList<ResourcePackRowViewModel> ResourcePacks =>
        [.. _content.GetResourcePacks(_instances.ActiveInstanceName).Select(ResourcePackRowViewModel.From)];

    /// <summary>
    /// 存档列表。
    /// </summary>
    /// <remarks>
    /// M6-e-3f：`LastPlayed` 与 `GameMode` 是「键 + 参数」，同样经投影（<see cref="WorldSaveRowViewModel"/>）。
    /// M7-f 起投影里还带着**存档目录名**（备份文件名与恢复目标都按它定，而显示名可以重名）。
    /// </remarks>
    public IReadOnlyList<WorldSaveRowViewModel> Saves =>
        [.. _content.GetWorldSaves(_instances.ActiveInstanceName).Select(WorldSaveRowViewModel.From)];

    /// <summary>存档表当前选中的行（由 DataGrid.SelectedItem 双向绑定驱动）。</summary>
    public WorldSaveRowViewModel? SelectedSave
    {
        get => _selectedSave;
        set
        {
            if (SetProperty(ref _selectedSave, value))
            {
                // 三个按钮的可执行性是"现算"的（有没有选中行、选中的那个有没有备份），
                // 它们不跟着属性名通知走 —— 必须显式再问一次，否则选中一行后按钮仍然是灰的。
                BackupSelectedCommand.NotifyCanExecuteChanged();
                RestoreSelectedCommand.NotifyCanExecuteChanged();
                DeleteSelectedSaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>资源包列表当前选中的行（由 ListBox.SelectedItem 双向绑定驱动）。</summary>
    /// <remarks>
    /// <para>
    /// 设计稿的资源包列表第一行是 `.opt.sel`（高亮 + 左侧蓝条），而那一行就是**优先级最高**的包 ——
    /// 所以构造时预先选中它，之后点哪行就跟到哪行（与存档表同一条规则）。
    /// </para>
    /// <para>
    /// 动作之后**按文件名把同一行选回来**：启用 / 停用 / 上下移都不改文件名（与模组的改名不同），
    /// 因此这里不存在"挑到另一个文件"的问题 —— 保持高亮才能连着点几次上移 / 下移。
    /// </para>
    /// </remarks>
    public ResourcePackRowViewModel? SelectedResourcePack
    {
        get => _selectedResourcePack;
        set
        {
            if (SetProperty(ref _selectedResourcePack, value))
            {
                // 五颗按钮的可执行性都是"现算"的（选中行有没有启用、是不是已经到顶 / 到底），
                // 它们不跟着属性名通知走 —— 必须显式再问一次。
                EnableResourcePackCommand.NotifyCanExecuteChanged();
                DisableResourcePackCommand.NotifyCanExecuteChanged();
                MoveResourcePackUpCommand.NotifyCanExecuteChanged();
                MoveResourcePackDownCommand.NotifyCanExecuteChanged();
                DeleteResourcePackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>已启用的资源包数量（"能不能再往下移"的边界）。</summary>
    private int EnabledResourcePackCount => ResourcePacks.Count(static pack => pack.IsEnabled);

    /// <summary>资源包数量文本。</summary>
    public string ResourcePackCountText => Loc.T("{0} 个", ResourcePacks.Count);

    /// <summary>存档数量文本（与表格数据同源，因此不会出现「表里 4 行却写 9 个」这种自相矛盾）。</summary>
    public string SaveCountText =>
        Loc.T("{0} 个 · {1} 个已备份", Saves.Count, Saves.Count(static s => s.IsBackedUp));

    /// <summary>当前是否有实例上下文。</summary>
    public bool HasInstance => _instances.ActiveInstanceName.Length > 0;

    /// <summary>每次进这一页都从干净的一次开始（上一次的失败原因 / 备份去向不留在页面上）。</summary>
    public override void OnActivated()
    {
        base.OnActivated();
        ActionError = string.Empty;
        ActionNote = string.Empty;
        AddFileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 选择一个本地 <c>.zip</c> 资源包导入当前实例的 <c>resourcepacks\</c>。
    /// </summary>
    /// <remarks>
    /// 选择文件是 View 侧能力（<see cref="IDialogService"/>），导入与 zip 安全扫描全在内核的
    /// <see cref="IResourcePackImporter"/>：ViewModel 只显示结果，不碰文件与压缩包。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanAddResourcePack))]
    private async Task AddFileAsync()
    {
        if (!HasInstance || _dialogs is null || _resourcePackImporter is null)
        {
            return;
        }

        var path = _dialogs.PickFile(
            Loc.T("添加资源包"),
            Loc.T("资源包 (*.zip)|*.zip"));

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ActionError = string.Empty;
        ActionNote = string.Empty;

        var result = await _resourcePackImporter
            .ImportAsync(_instances.ActiveInstanceName, path)
            .ConfigureAwait(true);

        ActionError = result is { Succeeded: false, Message: { } failure }
            ? LocalizedText.Of(failure)
            : string.Empty;
        ActionNote = result is { Succeeded: true, Message: { } note }
            ? LocalizedText.Of(note)
            : string.Empty;

        // 资源包列表是现算的：通知整页重读，新的 zip 会立刻出现在列表里。
        OnPropertyChanged(string.Empty);

        var imported = Path.GetFileName(path);
        SelectedResourcePack = ResourcePacks.FirstOrDefault(pack =>
            string.Equals(pack.FileName, imported, StringComparison.OrdinalIgnoreCase));

        EnableResourcePackCommand.NotifyCanExecuteChanged();
        DisableResourcePackCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddResourcePack() =>
        HasInstance && _dialogs is not null && _resourcePackImporter is not null;

    /// <summary>启用选中的资源包（M7-g-2b：往 <c>options.txt</c> 的启用列表里加一条）。</summary>
    [RelayCommand(CanExecute = nameof(CanEnableResourcePack))]
    private void EnableResourcePack() => ApplyResourcePackAction(
        _selectedResourcePack,
        static (content, instance, fileName) => content.SetResourcePackEnabled(instance, fileName, enabled: true));

    /// <summary>停用选中的资源包（从 <c>options.txt</c> 的启用列表里去掉一条）。</summary>
    [RelayCommand(CanExecute = nameof(CanDisableResourcePack))]
    private void DisableResourcePack() => ApplyResourcePackAction(
        _selectedResourcePack,
        static (content, instance, fileName) => content.SetResourcePackEnabled(instance, fileName, enabled: false));

    /// <summary>把这个资源包的优先级上移一位（界面列表往上 = 游戏里压在上面）。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveResourcePackUp))]
    private void MoveResourcePackUp() => ApplyResourcePackAction(
        _selectedResourcePack,
        static (content, instance, fileName) => content.MoveResourcePack(instance, fileName, direction: -1));

    /// <summary>把这个资源包的优先级下移一位。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveResourcePackDown))]
    private void MoveResourcePackDown() => ApplyResourcePackAction(
        _selectedResourcePack,
        static (content, instance, fileName) => content.MoveResourcePack(instance, fileName, direction: 1));

    /// <summary>
    /// 删除选中的资源包（M7-g-2b：移进 <c>backups\resourcepacks\</c>，不是真删）。
    /// </summary>
    /// <remarks>
    /// 与 p07 的删除同理：文件只是被移走、随时能拿回来 —— 没有一步不可逆，所以不需要模态确认框；
    /// 去向写在按钮 ToolTip 与动作完成后的提示里。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDeleteResourcePack))]
    private void DeleteResourcePack() => ApplyResourcePackAction(
        _selectedResourcePack,
        static (content, instance, fileName) => content.DeleteResourcePack(instance, fileName));

    /// <summary>选中了一个**未启用**的资源包时才能启用。</summary>
    private bool CanEnableResourcePack() =>
        HasInstance && _selectedResourcePack is { IsEnabled: false };

    /// <summary>选中了一个**已启用**的资源包时才能停用。</summary>
    private bool CanDisableResourcePack() =>
        HasInstance && _selectedResourcePack is { IsEnabled: true };

    /// <summary>已经是最高的那个（优先级 1）时不能再上移。</summary>
    private bool CanMoveResourcePackUp() =>
        HasInstance && _selectedResourcePack is { IsEnabled: true, Priority: > 1 };

    /// <summary>已经是启用列表里最低的那个时不能再下移（不会挪到内置包下面去）。</summary>
    private bool CanMoveResourcePackDown() =>
        HasInstance
        && _selectedResourcePack is { IsEnabled: true } pack
        && pack.Priority < EnabledResourcePackCount;

    /// <summary>选中了一行就能删除（与启用状态无关）。</summary>
    private bool CanDeleteResourcePack() =>
        HasInstance && _selectedResourcePack is not null;

    /// <summary>
    /// 执行一次资源包动作、把结果落到界面，并让整页重新求值。
    /// </summary>
    /// <param name="pack">选中的行（动作的目标）。</param>
    /// <param name="action">内核动作（参数：内容目录、实例名、文件名）。</param>
    private void ApplyResourcePackAction(
        ResourcePackRowViewModel? pack,
        Func<IContentCatalog, string, string, ContentActionResult> action)
    {
        if (pack is null || !HasInstance)
        {
            // 按钮本来就不可按（见六个 CanExecute）；真走到这里说明状态在两次求值之间变了。
            return;
        }

        var fileName = pack.FileName;
        var result = action(_content, _instances.ActiveInstanceName, fileName);

        ActionError = result is { Succeeded: false, Message: { } failure }
            ? LocalizedText.Of(failure)
            : string.Empty;

        ActionNote = result is { Succeeded: true, Message: { } note }
            ? LocalizedText.Of(note)
            : string.Empty;

        // 行是**现算**的，而 options.txt / 目录内容已经变了 —— 让本页所有绑定重新求值。
        OnPropertyChanged(string.Empty);

        // 按文件名把同一行选回来（启用 / 停用 / 上下移都不改文件名；删除时它自然变成 null）。
        SelectedResourcePack = ResourcePacks
            .FirstOrDefault(candidate => string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        EnableResourcePackCommand.NotifyCanExecuteChanged();
        DisableResourcePackCommand.NotifyCanExecuteChanged();
        MoveResourcePackUpCommand.NotifyCanExecuteChanged();
        MoveResourcePackDownCommand.NotifyCanExecuteChanged();
        DeleteResourcePackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>用资源管理器打开 resourcepacks 目录。</summary>
    [RelayCommand]
    private void OpenResourcePacksFolder()
    {
        // 没有实例时不把空路径交给资源管理器（与 p03 的「打开目录」同一规则）。
        if (!HasInstance)
        {
            return;
        }

        _shell.OpenFolder(Path.Combine(_instances.GetDetail(_instances.ActiveInstanceName).InstancePath, "resourcepacks"));
    }

    /// <summary>
    /// 备份选中的存档（M7-f）。
    /// </summary>
    /// <remarks>
    /// 排完队**跳到「下载与队列」**（与 p07 的「更新所选」、p08 的导入同一条理由）：
    /// 一个存档可以几个 GB，压缩要几十秒到几分钟，停在 p09 只会让人以为"点了没反应"。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanBackupSelected))]
    private void BackupSelected()
    {
        if (_selectedSave is null || !HasInstance)
        {
            // 按钮本来就不可按（见 CanBackupSelected）；真走到这里说明状态在两次求值之间变了。
            return;
        }

        _downloads.EnqueueWorldBackup(
            new WorldBackupRequest(_instances.ActiveInstanceName, _selectedSave.BackupTarget));
        _navigation.Navigate(PageId.Downloads);
    }

    /// <summary>备份这一页里的每一个存档（设计稿的「备份全部存档」）。</summary>
    [RelayCommand(CanExecute = nameof(CanBackupAll))]
    private void BackupAll()
    {
        if (!HasInstance)
        {
            return;
        }

        var instance = _instances.ActiveInstanceName;

        // 一个存档一个任务（不是批量任务）：队列本来就顺序执行，而进度、失败原因与以后的取消
        // 都因此是**按存档**的粒度 —— "第 3 个存档失败了"比"整批失败"有用得多。
        foreach (var save in Saves)
        {
            _downloads.EnqueueWorldBackup(new WorldBackupRequest(instance, save.BackupTarget));
        }

        _navigation.Navigate(PageId.Downloads);
    }

    /// <summary>恢复选中的存档（M7-f）。</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreSelected))]
    private void RestoreSelected()
    {
        if (_selectedSave is null || !_selectedSave.IsBackedUp || !HasInstance)
        {
            return;
        }

        _downloads.EnqueueWorldRestore(
            new WorldRestoreRequest(_instances.ActiveInstanceName, _selectedSave.BackupTarget));
        _navigation.Navigate(PageId.Downloads);
    }

    /// <summary>
    /// 删除选中的存档（S9-f：整目录移进 <c>backups\saves\&lt;时间戳&gt;\</c>，不是真删）。
    /// </summary>
    /// <remarks>
    /// 与 p07 / p09 的资源包删除同一条规矩：文件只是被移走、随时能拿回来 —— 没有一步不可逆，
    /// 所以不需要模态确认框；去向写在按钮 ToolTip 与完成后的提示里。
    /// 这一步**不排队**：移目录是本地毫秒级操作，排进队列反而让"删了没有"变得不直观。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedSave))]
    private void DeleteSelectedSave()
    {
        if (_selectedSave is null || !HasInstance)
        {
            return;
        }

        var result = _content.DeleteWorldSave(_instances.ActiveInstanceName, _selectedSave.BackupTarget.Folder);

        ActionError = result is { Succeeded: false, Message: { } failure }
            ? LocalizedText.Of(failure)
            : string.Empty;

        ActionNote = result is { Succeeded: true, Message: { } note }
            ? LocalizedText.Of(note)
            : string.Empty;

        // 存档表是现算的、磁盘已经变了 —— 让本页所有绑定重新求值。
        OnPropertyChanged(string.Empty);

        if (result.Succeeded)
        {
            // 被删的那一行已经不在了：不按显示名把「另一个同名存档」选回来（与 p07 删除同一个理由）。
            SelectedSave = null;
        }

        BackupSelectedCommand.NotifyCanExecuteChanged();
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedSaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>选了行、并且有实例上下文时才能删除存档。</summary>
    private bool CanDeleteSelectedSave() => HasInstance && _selectedSave is not null;

    /// <summary>选了行、并且有实例上下文时才能备份。</summary>
    private bool CanBackupSelected() => HasInstance && _selectedSave is not null;

    /// <summary>这一页至少有一个存档时才能"备份全部"。</summary>
    private bool CanBackupAll() => HasInstance && Saves.Count > 0;

    /// <summary>只有**备份过**的存档才能恢复（没备份时按钮是灰的，而不是按下去才发现没有备份）。</summary>
    private bool CanRestoreSelected() => HasInstance && _selectedSave is { IsBackedUp: true };
}
