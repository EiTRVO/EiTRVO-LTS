using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Content;
using EiTRVO.Core.Launcher.Text;

namespace EiTRVO.Core.App.Tests;

/// <summary>p09「添加文件」：文件选择 → 内核导入 → 页面提示。</summary>
[TestClass]
public sealed class ResourcePackImportTests
{
    [TestMethod]
    public async Task TheAddFileButtonPicksAZipAndHandsItToTheImporter()
    {
        var importer = new FakeResourcePackImporter
        {
            Result = new ContentActionResult(true, TextTemplate.Of("已把资源包 {0} 导入到当前实例。", "MyPack.zip")),
        };
        var page = NewPage(importer, @"C:\packs\MyPack.zip");

        Assert.IsTrue(page.AddFileCommand.CanExecute(null));

        await page.AddFileCommand.ExecuteAsync(null);

        Assert.AreEqual(("1.20.4-Forge", @"C:\packs\MyPack.zip"), importer.Calls.Single());
        StringAssert.Contains(page.ActionNote, "MyPack.zip");
        Assert.AreEqual(string.Empty, page.ActionError);
    }

    [TestMethod]
    public async Task CancellingThePickerDoesNotCallTheImporter()
    {
        var importer = new FakeResourcePackImporter();
        var page = NewPage(importer, fileToPick: null);

        await page.AddFileCommand.ExecuteAsync(null);

        Assert.AreEqual(0, importer.Calls.Count);
    }

    private static ResourcesViewModel NewPage(FakeResourcePackImporter importer, string? fileToPick) =>
        new(
            new FakeContentCatalog(),
            new FakeInstanceCatalog(),
            new FakeShellService(),
            new FakeDownloadCatalog(),
            new FakeNavigationService(),
            modrinthDialog: null,
            dialogs: new FakeDialogService { FileToPick = fileToPick },
            resourcePackImporter: importer);
}

internal sealed class FakeResourcePackImporter : IResourcePackImporter
{
    public List<(string InstanceName, string SourcePath)> Calls { get; } = [];

    public ContentActionResult Result { get; set; } = ContentActionResult.Success;

    public Task<ContentActionResult> ImportAsync(
        string instanceName,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((instanceName, sourcePath));
        return Task.FromResult(Result);
    }
}
