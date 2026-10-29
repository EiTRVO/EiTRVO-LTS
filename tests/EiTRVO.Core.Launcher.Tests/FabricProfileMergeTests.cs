using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-f：把**真实形状**的 Fabric profile 与**真实清单**的 1.20.4 合起来，看装出来的东西能不能启动。
/// <para>
/// 上游两段各有各的测试（profile 客户端、安装流水线），这里验的是中间那一步：
/// "安装器写下去的那份版本 JSON，启动链能不能读懂并能拼出命令行"。
/// 分开测都绿、合起来少一半库的事，正是这类测试要挡的。
/// </para>
/// <para>
/// 用的 profile 数据是 2026-10-04 从 <c>meta.fabricmc.net</c> 真实取回后抄成形状的：
/// 库把仓库基址与校验值写在**库对象自身**（<c>name</c> + <c>url</c> + <c>sha1</c> + <c>size</c>），
/// 而不是 <c>downloads/artifact</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class FabricProfileMergeTests : LaunchTestData
{
    private const string FabricId = "fabric-loader-0.15.7-1.20.4";

    [TestMethod]
    public void TheMergedLoaderVersionIsSelfContainedAndKeepsTheBaseGameArguments()
    {
        var merged = Merge();

        Assert.AreEqual(FabricId, merged.Merged.Id);
        Assert.IsNull(merged.Merged.InheritsFrom, "写下去的是继承链已展开的一份。");
        Assert.AreEqual("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.Merged.MainClass, "主类必须换成加载器的。");
        Assert.AreEqual(Real1204Json().JavaVersion!.MajorVersion, merged.Merged.JavaVersion!.MajorVersion, "Java 要求从父版本继承。");
        Assert.AreEqual("12", merged.Merged.Assets, "资源索引 id 来自父版本。");

        var names = merged.Merged.Libraries!.Select(static library => library.Name).ToList();

        CollectionAssert.Contains(names, "net.fabricmc:fabric-loader:0.15.7", "加载器自己的库要在。");
        CollectionAssert.Contains(names, "com.mojang:authlib:6.0.52", "原版的库也要在（否则起来就 NoClassDefFoundError）。");
    }

    [TestMethod]
    public void TheMergedLoaderVersionProducesACommandLineWithNothingMissing()
    {
        // 这份命令行是启动链真正会用的那一份：零"拦住启动"的问题，主类是加载器的，
        // 类路径里同时有加载器的库、原版的库，以及**加载器目录下**的客户端 JAR。
        var merged = Merge();

        var commandLine = LaunchArgumentBuilder.Build(
            Request(version: merged.Merged, versionId: FabricId, javaMajor: 17));

        var blocking = commandLine.ProblemsOf(LaunchProblemKind.MissingFile).Where(static p => p.BlocksLaunch).ToList();

        Assert.AreEqual(0, blocking.Count, "合并后的版本不该缺文件：" + string.Join(" / ", blocking.Select(static p => p.Subject)));

        var arguments = commandLine.Arguments.ToList();
        var classpathIndex = arguments.IndexOf("-cp");
        Assert.IsTrue(classpathIndex >= 0, "应当有 -cp。");

        var classpath = arguments[classpathIndex + 1];
        StringAssert.Contains(classpath, "fabric-loader-0.15.7.jar");
        StringAssert.Contains(classpath, "authlib-6.0.52.jar");
        StringAssert.Contains(classpath, Path.Combine("versions", FabricId, FabricId + ".jar"));
        Assert.AreEqual("net.fabricmc.loader.impl.launch.knot.KnotClient", arguments[classpathIndex + 2], "-cp 后面就是主类。");
    }

    /// <summary>把真实形状的 Fabric profile 与真实清单里的 1.20.4 合并（与安装器做的完全一样）。</summary>
    private static ResolvedVersion Merge()
    {
        var parent = Real1204Json();

        return VersionInheritanceResolver.Resolve(
            FabricProfile(),
            id => string.Equals(id, "1.20.4", StringComparison.Ordinal) ? parent : null);
    }

    private static VersionJson FabricProfile() => new()
    {
        Id = FabricId,
        InheritsFrom = "1.20.4",
        Type = "release",
        MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",

        // Fabric 的 profile 里 game 参数是空列表，jvm 里塞一句 FabricMcEmu。
        Arguments = new VersionArguments
        {
            Game = [],
            Jvm = [VersionArgument.Plain("-DFabricMcEmu= net.minecraft.client.main.Main ")],
        },

        Libraries =
        [
            // 形态一：仓库基址 + 校验值都写在库对象自身（asm 系列真实就是这样）。
            new Library
            {
                Name = "org.ow2.asm:asm:9.6",
                Url = "https://maven.fabricmc.net/",
                Sha1 = "aa205cf0a06dbd8e04ece91c0b37c3f5d567546a",
                Size = 123598,
            },

            // 形态二：连校验值都没有（Quilt 的 profile 全部是这种）。
            new Library { Name = "net.fabricmc:intermediary:1.20.4", Url = "https://maven.fabricmc.net/" },

            // 形态三：新式 downloads/artifact（fabric-loader 自己是这一种）。
            new Library
            {
                Name = "net.fabricmc:fabric-loader:0.15.7",
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadInfo
                    {
                        Path = "net/fabricmc/fabric-loader/0.15.7/fabric-loader-0.15.7.jar",
                        Url = "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.15.7/fabric-loader-0.15.7.jar",
                        Sha1 = "15f2a7c1b1c1c6f7a1b2c3d4e5f60718293a4b5c",
                        Size = 2281744,
                    },
                },
            },
        ],
    };
}
