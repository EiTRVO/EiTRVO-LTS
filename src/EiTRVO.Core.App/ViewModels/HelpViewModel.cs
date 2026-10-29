using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;
using EiTRVO.Core.App.Services;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>
/// 帮助与支持中心（独立窗口）的 ViewModel —— **2026-10-18 这一步只做框架，不做内容**。
/// </summary>
/// <remarks>
/// 设计稿 p20：工具栏（前进 / 后退 / 主页 + 搜索）+ 左主题 + 内容 + 底栏。
/// 这一步落地的是**窗口、主题导航、搜索过滤与"没有找到"这些框架行为**；
/// 每个主题的正文还没有，内容区如实显示"详细内容正在整理"，而不是编一段假文档。
/// <para>
/// 帮助窗口是**独立窗口**（用户 2026-10-18 指定）：打开时不取代主窗口，已打开则前置。
/// 「关于」入口把主窗口切到 p12 并还原主窗口，帮助窗口不关 —— 用户可能还要回来接着看。
/// </para>
/// </remarks>
internal sealed partial class HelpViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IWindowService _windows;
    private readonly ILocalizationService _localization;

    private readonly List<int> _history = [];
    private int _position = -1;
    private int _currentStepIndex;
    private bool _applyingSelection;

    private HelpTopicViewModel? _selectedTopic;
    private IReadOnlyList<HelpTopicViewModel> _filteredTopics = [];
    private string _searchText = string.Empty;

    /// <summary>构造帮助中心 ViewModel。</summary>
    /// <param name="navigation">导航服务（「关于」入口用）。</param>
    /// <param name="windows">窗口服务（把主窗口还原 / 置前）。</param>
    /// <param name="localization">语言服务（切语言后主题名与说明要跟着变）。</param>
    public HelpViewModel(
        INavigationService navigation,
        IWindowService windows,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(localization);

        _navigation = navigation;
        _windows = windows;
        _localization = localization;

        _localization.PropertyChanged += OnLocalizationChanged;

        Title = Loc.T("帮助与支持中心");
        RebuildTopics();
        NavigateTo(Topics[0], record: true);
        BuildSteps();
    }

    /// <summary>窗口标题（不带产品名前缀，用户 2026-10-18 指定）。</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>全部主题（保留历史/搜索模型；左栏现在只显示 <see cref="DisplayTopics"/>）。</summary>
    public IReadOnlyList<HelpTopicViewModel> Topics { get; private set; } = [];

    /// <summary>帮助窗口左侧实际显示的主题（2026-10-25 起只保留「快速入门」）。</summary>
    public IReadOnlyList<HelpTopicViewModel> DisplayTopics =>
        Topics.Count == 0 ? [] : [Topics[0]];

    /// <summary>搜索过滤后的主题。</summary>
    public IReadOnlyList<HelpTopicViewModel> FilteredTopics
    {
        get => _filteredTopics;
        private set => SetProperty(ref _filteredTopics, value);
    }

    /// <summary>当前选中的主题（ListBox 双向绑定；代码回填时按 §2.2 ③ 抑制"用户操作"）。</summary>
    public HelpTopicViewModel? SelectedTopic
    {
        get => _selectedTopic;
        set
        {
            if (!SetProperty(ref _selectedTopic, value))
            {
                return;
            }

            if (!_applyingSelection && value is not null)
            {
                NavigateTo(value, record: true);
            }

            OnPropertyChanged(nameof(CurrentTopicTitle));
            OnPropertyChanged(nameof(ContentPlaceholder));
        }
    }

    /// <summary>搜索框文本（本地过滤主题标题，不联网）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            RebuildFilteredTopics();
        }
    }

    /// <summary>内容区标题（当前主题名）。</summary>
    public string CurrentTopicTitle => SelectedTopic?.Title ?? Loc.T("帮助与支持中心");

    /// <summary>
    /// 内容区的占位说明。**这不是文档内容**：这一步只有框架，正文还没接入；
    /// 写"正在整理"是如实的，编一段假帮助才是骗人。
    /// </summary>
    public string ContentPlaceholder => Loc.T(
        "「{0}」主题的详细内容正在整理；当前版本先提供窗口框架与主题导航。",
        CurrentTopicTitle);

    /// <summary>搜索没有匹配项（内容区显示一句明确的话，而不是空白）。</summary>
    public bool HasNoSearchResults => SearchText.Trim().Length > 0 && FilteredTopics.Count == 0;

    /// <summary>后退。</summary>
    public IRelayCommand BackCommand => _backCommand ??= new RelayCommand(GoBack, () => _position > 0);

    /// <summary>前进。</summary>
    public IRelayCommand ForwardCommand => _forwardCommand ??= new RelayCommand(GoForward, () => _position >= 0 && _position < _history.Count - 1);

    /// <summary>回到第一个主题（快速入门）。</summary>
    public IRelayCommand HomeCommand => _homeCommand ??= new RelayCommand(GoHome);

    /// <summary>打开主窗口的「关于」页（诊断、许可、版本都在那里），并把主窗口还原置前。</summary>
    public IRelayCommand OpenAboutCommand => _openAboutCommand ??= new RelayCommand(OpenAbout);

    /// <summary>快速入门的分页内容（来自 helpdoc/doc.docx；不含“步骤x”字样）。</summary>
    public IReadOnlyList<HelpStepViewModel> Steps { get; private set; } = [];

    /// <summary>当前步骤。</summary>
    public HelpStepViewModel? CurrentStep => Steps.Count == 0 ? null : Steps[_currentStepIndex];

    /// <summary>当前步骤的标题（文档第一行）。</summary>
    public string CurrentStepTitle => CurrentStep?.Title ?? string.Empty;

    /// <summary>当前步骤的说明文字（文档其余行；不包含“步骤x”）。</summary>
    public string CurrentStepBody => CurrentStep?.Body ?? string.Empty;

    /// <summary>当前步骤的截图 pack URI。</summary>
    public string CurrentStepImageUri => CurrentStep?.ImageUri ?? string.Empty;

    /// <summary>上一页。</summary>
    public IRelayCommand PreviousStepCommand => _previousStepCommand ??= new RelayCommand(
        () => GoToStep(_currentStepIndex - 1),
        () => _currentStepIndex > 0);

    /// <summary>下一页。</summary>
    public IRelayCommand NextStepCommand => _nextStepCommand ??= new RelayCommand(
        () => GoToStep(_currentStepIndex + 1),
        () => _currentStepIndex < Steps.Count - 1);

    private IRelayCommand? _backCommand;
    private IRelayCommand? _forwardCommand;
    private IRelayCommand? _homeCommand;
    private IRelayCommand? _openAboutCommand;
    private IRelayCommand? _previousStepCommand;
    private IRelayCommand? _nextStepCommand;

    private void BuildSteps() =>
        Steps =
        [
            Step("感谢您选择EiTRVO", ["此教程将引导您入门EiTRVO LTS"], "step01.png"),
            Step(
                "点击左边栏的账户，进入账户管理页面",
                ["如果您未登录有效的微软账户，启动器部分功能将会受限"],
                "step08.png"),
            Step(
                "点击添加微软账户按钮，等待弹出登录引导窗口后",
                ["复制八位代码，在浏览器打开对应网址后按步骤操作"],
                "step09.png"),
            Step("等待浏览器出现该页面后即代表登录成功", ["启动器将自动获取账户信息"], "step10.png"),
            Step("为游戏文件首次下载做好准备", ["点击左边栏的所有版本，进入版本选择页面"], "step02.png"),
            Step("选定版本后，点击右下角“用所选版本新建实例”按钮", [], "step03.png"),
            Step("进入下载向导，选择要安装的模组加载器及版本", ["选定后，点击右下角的“下一步”按钮"], "step04.png"),
            Step(
                "为准备下载的实例设定实例名",
                ["实例名不会影响实际版本名称，安装后也可手动修改", "填写后，点击右下角的“下一步”按钮"],
                "step05.png"),
            Step("确认安装信息无问题后", ["即可点击“确认并安装“按钮"], "step06.png"),
            Step("安装过程将自动开始", ["您也可以点击“取消”按钮以取消安装"], "step07.png"),
            Step(
                "点击左边栏的本地实例，进入本地实例选择页面",
                ["选中将启动的实例后点击右下角的“启动游戏“按钮，将进入启动流程"],
                "step11.png"),
            Step("等待游戏启动完成即可", [], "step12.png"),
        ];

    private static HelpStepViewModel Step(string titleKey, IReadOnlyList<string> bodyKeys, string imageName) =>
        new(titleKey, bodyKeys, $"pack://application:,,,/EiTRVO.Core.App;component/Assets/Help/{imageName}");

    private void GoToStep(int index)
    {
        if (index < 0 || index >= Steps.Count || index == _currentStepIndex)
        {
            return;
        }

        _currentStepIndex = index;
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(CurrentStepBody));
        OnPropertyChanged(nameof(CurrentStepImageUri));
        PreviousStepCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
    }

    private void RebuildTopics()
    {
        Topics =
        [
            new HelpTopicViewModel("快速入门", "\uE80F"),
            new HelpTopicViewModel("安装与下载", "\uE896"),
            new HelpTopicViewModel("实例与启动", "\uE768"),
            new HelpTopicViewModel("模组与资源", "\uE7FC"),
            new HelpTopicViewModel("账户与登录", "\uE77B"),
            new HelpTopicViewModel("疑难解答", "\uE783"),
            new HelpTopicViewModel("关于与诊断", "\uE946"),
        ];

        RebuildFilteredTopics();

        // 语言切换后重建主题名：选中的主题要按**键**保留，不能按对象引用对回去。
        if (_selectedTopic is not null)
        {
            _applyingSelection = true;
            SelectedTopic = Topics.FirstOrDefault(topic => topic.Key == _selectedTopic.Key) ?? Topics[0];
            _applyingSelection = false;
        }
    }

    private void RebuildFilteredTopics()
    {
        var query = SearchText.Trim();

        FilteredTopics = query.Length == 0
            ? Topics
            : [.. Topics.Where(topic => topic.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                         || topic.Key.Contains(query, StringComparison.CurrentCultureIgnoreCase))];

        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    private void NavigateTo(HelpTopicViewModel topic, bool record)
    {
        if (record)
        {
            // 与浏览器同一语义：从历史中间跳转时，先砍掉"前进"那一段。
            if (_position < _history.Count - 1)
            {
                _history.RemoveRange(_position + 1, _history.Count - _position - 1);
            }

            var topicIndex = 0;
            for (var i = 0; i < Topics.Count; i++)
            {
                if (ReferenceEquals(Topics[i], topic))
                {
                    topicIndex = i;
                    break;
                }
            }

            _history.Add(topicIndex);
            _position = _history.Count - 1;
        }

        _applyingSelection = true;
        SelectedTopic = topic;
        _applyingSelection = false;

        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    private void GoBack()
    {
        if (_position <= 0)
        {
            return;
        }

        _position--;
        SelectByIndex(_history[_position], record: false);
    }

    private void GoForward()
    {
        if (_position < 0 || _position >= _history.Count - 1)
        {
            return;
        }

        _position++;
        SelectByIndex(_history[_position], record: false);
    }

    private void GoHome()
    {
        SelectByIndex(0, record: true);
        GoToStep(0);
    }

    private void SelectByIndex(int index, bool record)
    {
        if (index < 0 || index >= Topics.Count)
        {
            return;
        }

        NavigateTo(Topics[index], record);
    }

    private void OpenAbout()
    {
        _navigation.Navigate(PageId.About);
        _windows.Restore();
    }

    private void OnLocalizationChanged(object? sender, EventArgs e)
    {
        Title = Loc.T("帮助与支持中心");
        RebuildTopics();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// 帮助主题的一行：<see cref="Key"/> 是**简体中文原文**（资源键，也是切语言后对回同一个主题的身份），
/// <see cref="Title"/> 是当前语言下的显示名。
/// </summary>
/// <param name="key">主题键（简体中文原文）。</param>
/// <param name="glyph">Segoe MDL2 字形。</param>
internal sealed class HelpTopicViewModel(string key, string glyph)
{
    /// <summary>主题键（与资源键一致）。</summary>
    public string Key { get; } = key;

    /// <summary>当前语言下的主题名。</summary>
    public string Title { get; } = Loc.T(key);

    /// <summary>列表左侧的字形。</summary>
    public string Glyph { get; } = glyph;
}

/// <summary>帮助中心「快速入门」的一页：标题 + 说明键 + 截图资源。</summary>
/// <param name="TitleKey">文档第一行的资源键。</param>
/// <param name="BodyKeys">文档其余行的资源键（不含“步骤x”）。</param>
/// <param name="ImageUri">截图 pack URI。</param>
internal sealed record HelpStepViewModel(string TitleKey, IReadOnlyList<string> BodyKeys, string ImageUri)
{
    /// <summary>当前语言下的标题。</summary>
    public string Title => Loc.T(TitleKey);

    /// <summary>当前语言下的说明（多个正文行按换行拼接）。</summary>
    public string Body => string.Join(Environment.NewLine, BodyKeys.Select(Loc.T));
}
