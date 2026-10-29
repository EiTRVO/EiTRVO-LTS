using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels.Dialogs;

/// <summary>
/// 「删除实例」确认框（p13 的模态层）。对应 mockup/pages/p13-dialog-delete-instance.html。
/// <para>
/// 它是 p13 页面的子 ViewModel：页面内容由 <c>P13DeleteInstanceView</c> 画（与 p03 同源），
/// 这个对象只负责那一个确认框，由外壳渲染在遮罩之上。
/// </para>
/// </summary>
internal sealed partial class DeleteConfirmDialogViewModel : ObservableObject, IPageDialog
{
    private readonly IInstanceCatalog _instances;
    private readonly INavigationService _navigation;
    private bool _openedFromInstances;

    [ObservableProperty]
    private bool _keepSavesAndConfig = true;

    [ObservableProperty]
    private bool _deleteAccountBinding;

    /// <summary>构造删除确认框。</summary>
    /// <param name="instances">实例目录（实例名、占用空间、模组与存档数量都从它取）。</param>
    /// <param name="navigation">导航服务（取消/确认之后离开本页）。</param>
    public DeleteConfirmDialogViewModel(
        IInstanceCatalog instances,
        INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(navigation);

        _instances = instances;
        _navigation = navigation;
    }

    /// <summary>对话框关掉了（p02 用它收起模态层并刷新列表）。</summary>
    public event EventHandler? Closed;

    /// <summary>打开确认框：重置选项，并记录它是由哪个页面打开的。</summary>
    /// <param name="fromInstances">
    /// <see langword="true"/> 表示从 p02「本地实例」打开：关闭后留在 p02；
    /// <see langword="false"/> 表示从 p03 / p13 实例详情打开：取消回详情、确认回列表。
    /// </param>
    public void Open(bool fromInstances)
    {
        _openedFromInstances = fromInstances;
        KeepSavesAndConfig = true;
        DeleteAccountBinding = false;
    }

    /// <inheritdoc />
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>被删除的实例名（当前实例，现算）。</summary>
    public string InstanceName => _instances.ActiveInstanceName;

    /// <summary>当前是否有实例上下文。</summary>
    /// <remarks>
    /// 对话框的属性会在绑定求值时被读到（提问句与数量摘要），而**没有实例时一样可能被渲染**：
    /// 打开模态层与"当前实例恰好被删掉"是两条独立的时序。少这个判断就是崩在绑定里。
    /// </remarks>
    public bool HasInstance => InstanceName.Length > 0;

    /// <summary>占用空间文本。</summary>
    public string SizeOnDisk =>
        HasInstance ? _instances.GetDetail(InstanceName).SizeOnDisk : InstancePlaceholder.Detail.SizeOnDisk;

    /// <summary>模组数量。</summary>
    public int ModCount => HasInstance ? _instances.GetCounts(InstanceName).Mods : 0;

    /// <summary>存档数量。</summary>
    public int SaveCount => HasInstance ? _instances.GetCounts(InstanceName).Saves : 0;

    /// <summary>提问句。</summary>
    public string Question => Loc.T("确定要删除实例「{0}」吗？", InstanceName);

    /// <summary>数量摘要（提问句下方那行灰字）。</summary>
    public string Summary => Loc.T("该实例占用 {0}，包含 {1} 个模组与 {2} 个存档。", SizeOnDisk, ModCount, SaveCount);

    /// <summary>取消：从 p02 打开时只收起模态层；从实例详情打开时回实例详情。</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_openedFromInstances)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _navigation.Navigate(PageId.InstanceDetail);
    }

    /// <summary>
    /// 确认删除：把实例目录移进回收目录，然后回实例列表。
    /// <para>
    /// 「保留存档与配置」勾上时，内核会把 <c>saves/</c>、<c>config/</c>、<c>options.txt</c> 等
    /// 单独留在回收条目的 <c>kept/</c> 子目录里，其余内容原样留在回收条目中 ——
    /// 整次删除因此都是可回滚的。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Confirm()
    {
        // 没有实例上下文时（首次运行、或列表被清空）只离开本页：
        // 内核的删除会拒绝空实例名，而用户在这里真正需要的是回到实例列表。
        if (InstanceName.Length > 0)
        {
            _instances.DeleteInstance(
                InstanceName,
                new InstanceDeletionOptions(KeepSavesAndConfig, DeleteAccountBinding));
        }

        if (_openedFromInstances)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _navigation.Navigate(PageId.Instances);
    }
}
