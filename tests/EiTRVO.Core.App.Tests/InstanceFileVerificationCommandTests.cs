using CommunityToolkit.Mvvm.Input;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Sample;

namespace EiTRVO.Core.App.Tests;

/// <summary>p03「校验文件」按钮：命令必须真的调用校验服务并把结果写回属性面板。</summary>
[TestClass]
public sealed class InstanceFileVerificationCommandTests
{
    [TestMethod]
    public async Task VerifyFilesRunsTheVerifierAndShowsTheCounts()
    {
        var verifier = new FakeVerifier();
        var page = new InstanceDetailViewModel(
            new FakeInstanceCatalog(),
            new SampleEventLog(),
            new FakeShellService(),
            verifier);

        await ((IAsyncRelayCommand)page.VerifyFilesCommand).ExecuteAsync(null);

        Assert.AreEqual(1, verifier.Calls.Count);
        Assert.AreEqual("1.20.4-Forge", verifier.Calls[0]);
        StringAssert.Contains(page.Detail.ResourceVerification, "缺失 1 个");
        StringAssert.Contains(page.Detail.ResourceVerification, "损坏 2 个");
    }

    [TestMethod]
    public async Task VerificationResultDoesNotLeakToAnotherInstance()
    {
        var verifier = new FakeVerifier();
        var instances = new FakeInstanceCatalog();
        var page = new InstanceDetailViewModel(
            instances,
            new SampleEventLog(),
            new FakeShellService(),
            verifier);

        await ((IAsyncRelayCommand)page.VerifyFilesCommand).ExecuteAsync(null);
        StringAssert.Contains(page.Detail.ResourceVerification, "缺失 1 个");

        instances.ActiveInstanceName = "另一个实例";
        page.OnActivated();

        Assert.AreEqual("另一个实例", page.Detail.Name);
        Assert.AreEqual("从未校验", page.Detail.ResourceVerification, "A 的校验结果不能出现在 B 的属性面板上。");
    }

    [TestMethod]
    public async Task VerifyFilesDoesNothingWithoutAnActiveInstance()
    {
        var verifier = new FakeVerifier();
        var instances = new FakeInstanceCatalog { ActiveInstanceName = string.Empty };
        var page = new InstanceDetailViewModel(
            instances,
            new SampleEventLog(),
            new FakeShellService(),
            verifier);

        await ((IAsyncRelayCommand)page.VerifyFilesCommand).ExecuteAsync(null);

        Assert.AreEqual(0, verifier.Calls.Count);
        Assert.AreEqual("从未校验", page.Detail.ResourceVerification);
    }

    private sealed class FakeVerifier : IInstanceFileVerifier
    {
        public List<string> Calls { get; } = [];

        public Task<InstanceFileVerificationResult> VerifyAsync(
            string instanceName,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(instanceName);
            return Task.FromResult(new InstanceFileVerificationResult(5, 1, 2, 0));
        }
    }
}
