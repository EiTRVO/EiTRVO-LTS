using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Runtime;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 2026-10-25：p10「浏览」添加用户自有 Java；风险项必须先弹窗，用户坚持后才入列表。
/// </summary>
[TestClass]
public sealed class CustomJavaRuntimeTests
{
    [TestMethod]
    public async Task ASafeCustomJavaIsAddedWithoutAWarning()
    {
        var catalog = new FakeCustomJavaCatalog
        {
            Inspection = new JavaRuntimeInspection(Installation(17), JavaRuntimeRisk.None),
        };
        var page = NewPage(catalog);

        await page.BrowseCustomJavaCommand.ExecuteAsync(null);

        Assert.IsFalse(page.HasDialog);
        Assert.AreEqual(1, catalog.Added.Count);
    }

    [TestMethod]
    public async Task ARiskyCustomJavaShowsAWarningAndIsAddedOnlyAfterConfirmation()
    {
        var catalog = new FakeCustomJavaCatalog
        {
            Inspection = new JavaRuntimeInspection(
                Installation(17),
                JavaRuntimeRisk.UnknownVendor | JavaRuntimeRisk.UserWritableLocation),
        };
        var page = NewPage(catalog);

        await page.BrowseCustomJavaCommand.ExecuteAsync(null);

        Assert.IsTrue(page.HasDialog);
        var dialog = (JavaRuntimeWarningDialogViewModel)page.Dialog!;
        StringAssert.Contains(dialog.RiskText, "供应商");
        StringAssert.Contains(dialog.RiskText, "用户可写");
        Assert.AreEqual(0, catalog.Added.Count, "弹窗没确认之前不许加入列表。");

        dialog.AcceptCommand.Execute(null);

        Assert.IsFalse(page.HasDialog);
        Assert.AreEqual(1, catalog.Added.Count, "用户坚持添加时必须加入。");
    }

    [TestMethod]
    public async Task AnInvalidJavaShowsACloseOnlyWarning()
    {
        var catalog = new FakeCustomJavaCatalog
        {
            Failure = new JavaRuntimeInspectionException("not java"),
        };
        var page = NewPage(catalog);

        await page.BrowseCustomJavaCommand.ExecuteAsync(null);

        Assert.IsTrue(page.HasDialog);
        var dialog = (JavaRuntimeWarningDialogViewModel)page.Dialog!;
        Assert.IsFalse(dialog.CanAdd);
        Assert.IsFalse(dialog.AcceptCommand.CanExecute(null));
        Assert.AreEqual(0, catalog.Added.Count);
    }

    private static JavaSettingsViewModel NewPage(FakeCustomJavaCatalog catalog)
    {
        var dialogs = new FakeDialogService { FileToPick = @"C:\Java\custom\bin\javaw.exe" };
        return TestViewModels.JavaSettings(
            java: catalog,
            dialogs: dialogs,
            warningDialog: new JavaRuntimeWarningDialogViewModel());
    }

    private static JavaInstallation Installation(int major) => new(
        ExecutablePath: @"C:\Java\custom\bin\javaw.exe",
        HomeDirectory: @"C:\Java\custom",
        VersionText: $"{major}.0.1",
        MajorVersion: major,
        Vendor: "Eclipse Temurin",
        Bits: 64);

    private sealed class FakeCustomJavaCatalog : IJavaRuntimeCatalog
    {
        public JavaRuntimeInspection? Inspection { get; set; }

        public Exception? Failure { get; set; }

        public List<JavaInstallation> Added { get; } = [];

        public IReadOnlyList<JavaInstallation> GetInstallations() => [];

        public bool HasDetectionResult => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RedetectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JavaRuntimeInspection> InspectCustomAsync(
            string executablePath,
            CancellationToken cancellationToken = default) =>
            Failure is not null
                ? Task.FromException<JavaRuntimeInspection>(Failure)
                : Task.FromResult(Inspection!);

        public void AddCustom(JavaInstallation installation) => Added.Add(installation);
    }
}
