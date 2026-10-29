using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.Localization;

namespace EiTRVO.Core.App.ViewModels;

/// <summary>p12「查看第三方许可」窗口的内容：组件清单 + 选中组件的许可证全文。</summary>
/// <remarks>
/// 列表里的是**随产品分发**的运行时组件；MSTest 等测试依赖不随安装包分发，因此不列。
/// 许可证正文保留英文原文，避免翻译后与上游声明产生文本差异。
/// </remarks>
internal sealed partial class ThirdPartyLicensesViewModel : ObservableObject
{
    private readonly IReadOnlyList<LicenseComponentViewModel> _components;
    private readonly string _titleKey = "第三方许可";

    /// <summary>当前选中的组件。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLicenseText))]
    [NotifyPropertyChangedFor(nameof(SelectedMetadata))]
    private LicenseComponentViewModel? _selectedComponent;

    /// <summary>窗口要关闭了（由服务订阅后关闭真正的 Window）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>构造许可证窗口 ViewModel。</summary>
    public ThirdPartyLicensesViewModel()
    {
        _components =
        [
            new(
                ".NET 8 / Windows Desktop Runtime",
                "8.0",
                "MIT",
                "Copyright (c) .NET Foundation and Contributors",
                Mit("Copyright (c) .NET Foundation and Contributors")),
            new(
                "CommunityToolkit.Mvvm",
                "8.4.0",
                "MIT",
                "Copyright (c) .NET Foundation and Contributors",
                Mit("Copyright (c) .NET Foundation and Contributors")),
            new(
                "Microsoft.Extensions.Hosting",
                "8.0.1",
                "MIT",
                "Copyright (c) Microsoft Corporation",
                Mit("Copyright (c) Microsoft Corporation")),
            new(
                "Microsoft.Extensions.Http",
                "8.0.1",
                "MIT",
                "Copyright (c) Microsoft Corporation",
                Mit("Copyright (c) Microsoft Corporation")),
            new(
                "Microsoft.Extensions.Configuration.Json",
                "8.0.1",
                "MIT",
                "Copyright (c) Microsoft Corporation",
                Mit("Copyright (c) Microsoft Corporation")),
            new(
                "Microsoft.Extensions.Configuration.Binder",
                "8.0.2",
                "MIT",
                "Copyright (c) Microsoft Corporation",
                Mit("Copyright (c) Microsoft Corporation")),
            new(
                "Microsoft.Xaml.Behaviors.Wpf",
                "1.1.39",
                "MIT",
                "Copyright (c) Microsoft Corporation",
                Mit("Copyright (c) Microsoft Corporation")),
            new(
                "System.Security.Cryptography.ProtectedData",
                "8.0.0",
                "MIT",
                "Copyright (c) .NET Foundation and Contributors",
                Mit("Copyright (c) .NET Foundation and Contributors")),
        ];

        _selectedComponent = _components[0];
    }

    /// <summary>窗口标题。</summary>
    public string Title => Loc.T(_titleKey);

    /// <summary>组件清单。</summary>
    public IReadOnlyList<LicenseComponentViewModel> Components => _components;

    /// <summary>选中组件的许可证全文。</summary>
    public string SelectedLicenseText => SelectedComponent?.LicenseText ?? string.Empty;

    /// <summary>选中组件的「版本 / 许可证」摘要。</summary>
    public string SelectedMetadata => SelectedComponent is { } component
        ? Loc.T("版本 {0} · {1} 许可证", component.Version, component.License)
        : string.Empty;

    /// <summary>关闭窗口。</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>窗口语言变化时刷新由 <see cref="Loc"/> 求值的文案。</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>MIT 许可证全文（上游原文）。</summary>
    private static string Mit(string copyright) =>
        """
        MIT License

        """ + copyright + """


        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;
}

/// <summary>许可证窗口左侧的一个组件。</summary>
/// <param name="Name">组件名。</param>
/// <param name="Version">版本（数据，不翻译）。</param>
/// <param name="License">许可证名称（保留英文）。</param>
/// <param name="Copyright">版权行。</param>
/// <param name="LicenseText">许可证全文。</param>
internal sealed record LicenseComponentViewModel(
    string Name,
    string Version,
    string License,
    string Copyright,
    string LicenseText);
