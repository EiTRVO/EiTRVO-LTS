using EiTRVO.Core.App.Services;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Sample;
using Microsoft.Extensions.DependencyInjection;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// 演示数据源开关（M0-7）。
/// <para>
/// 这里钉住的不是"能跑"，而是**关掉演示数据时会当场报错并指出所属里程碑** ——
/// 静默返回假数据才是真正的风险。
/// </para>
/// </summary>
[TestClass]
public sealed class DataSourceRegistrationTests
{
    [TestMethod]
    public void SampleImplementationIsUsedWhenTheSwitchIsOn()
    {
        var services = new ServiceCollection();

        DataSourceRegistration.AddCatalog<IInstanceCatalog, SampleInstanceCatalog>(services, useSampleData: true, "M1");

        using var provider = services.BuildServiceProvider();
        Assert.IsInstanceOfType<SampleInstanceCatalog>(provider.GetRequiredService<IInstanceCatalog>());
    }

    [TestMethod]
    public void TurningSamplesOffFailsLoudlyAndNamesTheMilestone()
    {
        var services = new ServiceCollection();

        DataSourceRegistration.AddCatalog<IInstanceCatalog, SampleInstanceCatalog>(services, useSampleData: false, "M1");

        using var provider = services.BuildServiceProvider();
        var ex = Assert.ThrowsException<NotSupportedException>(
            () => provider.GetRequiredService<IInstanceCatalog>());

        StringAssert.Contains(ex.Message, "IInstanceCatalog");
        StringAssert.Contains(ex.Message, "M1");
        StringAssert.Contains(ex.Message, DataSourceRegistration.ConfigurationKey);
    }

    [TestMethod]
    public void MilestoneIsRequired()
    {
        var services = new ServiceCollection();

        Assert.ThrowsException<ArgumentException>(
            () => DataSourceRegistration.AddCatalog<IInstanceCatalog, SampleInstanceCatalog>(services, false, "  "));
    }
}
