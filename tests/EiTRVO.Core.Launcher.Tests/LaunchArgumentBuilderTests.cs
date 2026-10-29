using EiTRVO.Core.Launcher.Launch;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M4-a：启动参数构建（C-1 参数构建、C-2 <c>--userProperties</c> 兜底、C-3 <c>--add-opens</c> 修正、
/// C-4 类路径去重、C-5 日志配置）。
/// <para>
/// 参考向量取自**真实的版本 JSON**（1.20.4 / 1.12.2 形状的库、1.7.10、1.5.2，从 piston-meta
/// 逐字段抄进来），因为参数构建最怕"看起来对"：多一个参数、少一个值、路径被空格切开、
/// 同一个参数传两次，都只在游戏真的起来那一刻才现形。这里的断言是**逐个参数**比对整条命令行。
/// </para>
/// <para>
/// 文件是否存在由 <see cref="LaunchArgumentRequest.FileExists"/> 注入，所以这些用例**不碰磁盘**：
/// 类路径去重、缺失文件上报、module-path 排除都能在没有安装任何游戏的情况下断言。
/// </para>
/// </summary>
[TestClass]
public sealed class LaunchArgumentBuilderTests : LaunchTestData
{
    // 参考数据（真实清单的版本 JSON、库摘录、账户）在 LaunchTestData 里 ——
    // 与 M4-b 的脱敏测试共用同一批输入，避免两边各抄一份真实数据。

    [TestMethod]
    public void TheReal1204CommandLineIsBuiltParameterByParameter()
    {
        var launch = LaunchArgumentBuilder.Build(Request());

        var expectedClasspath = new[]
        {
            Path.Combine(LibrariesDirectory, @"com\github\oshi\oshi-core\6.4.5\oshi-core-6.4.5.jar"),
            Path.Combine(LibrariesDirectory, @"com\google\code\gson\gson\2.10.1\gson-2.10.1.jar"),
            Path.Combine(LibrariesDirectory, @"com\mojang\authlib\6.0.52\authlib-6.0.52.jar"),
            Path.Combine(LibrariesDirectory, @"org\lwjgl\lwjgl\3.3.2\lwjgl-3.3.2.jar"),
            Path.Combine(LibrariesDirectory, @"org\lwjgl\lwjgl\3.3.2\lwjgl-3.3.2-natives-windows.jar"),
            Path.Combine(GameDirectory, @"versions\1.20.4\1.20.4.jar"),
        };

        var expectedJvm = new[]
        {
            "-Xmx4096M",
            @"-Djava.library.path=" + NativesDirectory,
            "-Dminecraft.launcher.brand=eitrvo-core",
            "-Dminecraft.launcher.version=1.0",
            "-Dlog4j2.formatMsgNoLookups=true",
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
            @"-Dlog4j.configurationFile=" + LoggingConfigPath,
            HeapDumpArgument,
            @"-Djna.tmpdir=" + NativesDirectory,
            @"-Dorg.lwjgl.system.SharedLibraryExtractPath=" + NativesDirectory,
            @"-Dio.netty.native.workdir=" + NativesDirectory,
            "-cp",
            string.Join(Path.PathSeparator, expectedClasspath),
        };

        var expectedGame = new[]
        {
            "--username", PlayerName,
            "--version", "1.20.4",
            "--gameDir", InstanceDirectory,
            "--assetsDir", AssetsDirectory,
            "--assetIndex", "12",
            "--uuid", "b50ad385829d3141a2167e7d7539ba7f",
            "--accessToken", "0",
            "--clientId", ClientId,
            "--xuid", "0",
            "--userType", "mojang",
            "--versionType", "release",
        };

        AssertSequence(expectedJvm, launch.JvmArguments, "JVM 参数");
        Assert.AreEqual("net.minecraft.client.main.Main", launch.MainClass);
        AssertSequence(expectedGame, launch.GameArguments, "游戏参数");
        AssertSequence(expectedClasspath, launch.Classpath, "类路径");
        Assert.AreEqual(0, launch.Problems.Count, "这份 JSON 与这些文件都在，不该有任何问题：" + Describe(launch));
    }

    [TestMethod]
    public void TheVersionDeclaredClasspathBrandAndLibraryPathAreNotDuplicated()
    {
        // 1.20.4 的 arguments.jvm 自己声明了 -Djava.library.path / 品牌 / -cp；
        // 启动器的默认值先入表，所以这几个只会出现一次（Neo 会把它们传两遍，JVM 取最后一次）。
        var launch = LaunchArgumentBuilder.Build(Request());

        Assert.AreEqual(1, launch.Arguments.Count(argument => argument == "-cp"));
        Assert.AreEqual(1, launch.Arguments.Count(argument => argument == "-Dminecraft.launcher.brand=eitrvo-core"));
        Assert.AreEqual(1, launch.Arguments.Count(argument => argument == @"-Djava.library.path=" + NativesDirectory));
    }

    [TestMethod]
    public void OsxAndX86OnlyArgumentsAreExcludedOnWindowsX64()
    {
        var launch = LaunchArgumentBuilder.Build(Request());

        Assert.IsFalse(launch.JvmArguments.Contains("-XstartOnFirstThread"), "os.name=osx 的规则不该在 Windows 上生效。");
        Assert.IsFalse(launch.JvmArguments.Contains("-Xss1M"), "os.arch=x86 的规则不该在 x64 上生效。");
        Assert.IsTrue(launch.JvmArguments.Contains(HeapDumpArgument));
    }

    [TestMethod]
    public void TheSameRulesPickDifferentArgumentsOnAnotherPlatform()
    {
        // 平台是**数据**：这条用例证明规则求值没有偷偷读 OperatingSystem.IsWindows()。
        var launch = LaunchArgumentBuilder.Build(
            Request(platform: GamePlatform.WindowsX64 with { OsName = "linux", OsVersion = "6.5" }));

        Assert.IsFalse(launch.JvmArguments.Contains(HeapDumpArgument));
    }

    [TestMethod]
    public void AStructuredGameArgumentKeepsSpacesInAPath()
    {
        var request = Request() with { InstanceDirectory = @"C:\Program Files\mc\instances\1.20.4" };
        var launch = LaunchArgumentBuilder.Build(request);
        var game = launch.GameArguments.ToList();

        Assert.AreEqual(
            @"C:\Program Files\mc\instances\1.20.4",
            game[game.IndexOf("--gameDir") + 1],
            "含空格的路径必须是**一个**实参：结构化参数不做空格切分。");
    }

    [TestMethod]
    public void ALegacyArgumentStringIsSplitButPlaceholderValuesStayWhole()
    {
        var request = Request(version: Legacy1710Json(), versionId: "1.7.10", javaMajor: 8) with
        {
            InstanceDirectory = @"C:\Program Files\mc\1.7.10",
        };

        var launch = LaunchArgumentBuilder.Build(request);

        AssertSequence(
            [
                "--username", PlayerName,
                "--version", "1.7.10",
                "--gameDir", @"C:\Program Files\mc\1.7.10",
                "--assetsDir", AssetsDirectory,
                "--assetIndex", "1.7.10",
                "--uuid", "b50ad385829d3141a2167e7d7539ba7f",
                "--accessToken", "0",
                "--userProperties", "{}",
                "--userType", "mojang",
            ],
            launch.GameArguments,
            "1.7.10 的游戏参数");
    }

    [TestMethod]
    public void AVeryOldVersionResolvesTheLegacyPlaceholders()
    {
        var request = Request(version: Legacy152Json(), versionId: "1.5.2", javaMajor: 8) with
        {
            GameAssetsDirectory = @"C:\mc\.minecraft\assets\virtual\pre-1.6",
        };

        var launch = LaunchArgumentBuilder.Build(request);

        // 1.5.2 的模板里没有 --username（玩家名直接是第一个位置参数），资源用的是 ${game_assets}。
        AssertSequence(
            [
                PlayerName,
                "0",
                "--gameDir", InstanceDirectory,
                "--assetsDir", @"C:\mc\.minecraft\assets\virtual\pre-1.6",
            ],
            launch.GameArguments,
            "1.5.2 的游戏参数");

        Assert.AreEqual(0, launch.Problems.Count, "这些占位符都应该认得：" + Describe(launch));
        Assert.AreEqual("net.minecraft.launchwrapper.Launch", launch.MainClass);
    }

    [TestMethod]
    public void ClasspathDedupeKeepsTheHighestVersionAndItsFirstPosition()
    {
        var version = Real1204Json(
        [
            MavenLibrary("org.ow2.asm:asm:9.9", "org/ow2/asm/asm/9.9/asm-9.9.jar"),
            MavenLibrary("com.google.code.gson:gson:2.10.1", "com/google/code/gson/gson/2.10.1/gson-2.10.1.jar"),
            MavenLibrary("org.ow2.asm:asm:9.10.1", "org/ow2/asm/asm/9.10.1/asm-9.10.1.jar"),
        ]);

        var launch = LaunchArgumentBuilder.Build(Request(version: version));

        Assert.AreEqual(3, launch.Classpath.Count, "asm 只留一个版本，再加 gson 与客户端 JAR。");
        Assert.IsTrue(launch.Classpath[0].EndsWith(@"asm\9.10.1\asm-9.10.1.jar", StringComparison.Ordinal));
        Assert.IsTrue(launch.Classpath[1].EndsWith(@"gson\2.10.1\gson-2.10.1.jar", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ANativesClassifierIsNotDedupedAgainstTheMainJar()
    {
        var version = Real1204Json(
        [
            MavenLibrary("org.lwjgl:lwjgl:3.3.2", "org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2.jar"),
            PlatformLibrary("org.lwjgl:lwjgl:3.3.2:natives-windows", "org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-windows.jar", "windows"),
        ]);

        var launch = LaunchArgumentBuilder.Build(Request(version: version));

        Assert.AreEqual(3, launch.Classpath.Count, "主 JAR、natives 分类器、客户端 JAR 三者都在。");
        Assert.IsTrue(launch.Classpath[0].EndsWith(@"lwjgl-3.3.2.jar", StringComparison.Ordinal));
        Assert.IsTrue(launch.Classpath[1].EndsWith(@"lwjgl-3.3.2-natives-windows.jar", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LibrariesOnTheModulePathAreKeptOutOfTheClasspath()
    {
        const string asmJar = @"C:\mc\.minecraft\libraries\org\ow2\asm\asm\9.6\asm-9.6.jar";

        var version = Real1204Json(
        [
            MavenLibrary("org.ow2.asm:asm:9.6", "org/ow2/asm/asm/9.6/asm-9.6.jar"),
            MavenLibrary("com.google.code.gson:gson:2.10.1", "com/google/code/gson/gson/2.10.1/gson-2.10.1.jar"),
        ]) with
        {
            Arguments = new VersionArguments
            {
                Jvm = [VersionArgument.Plain("-p"), VersionArgument.Plain(asmJar), .. Real1204JvmArguments()],
                Game = Real1204GameArguments(),
            },
        };

        var launch = LaunchArgumentBuilder.Build(Request(version: version));

        Assert.IsTrue(launch.JvmArguments.Contains("-p"));
        Assert.IsTrue(launch.JvmArguments.Contains(asmJar), "module-path 本身要保留。");
        Assert.IsFalse(
            launch.Classpath.Any(path => path.EndsWith("asm-9.6.jar", StringComparison.Ordinal)),
            "同一个构件既在 -p 上又在 classpath 上，会被两个加载器各加载一次。");
        Assert.IsTrue(launch.Classpath.Any(path => path.EndsWith("gson-2.10.1.jar", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ANativesOnlyLibraryWithoutAMainArtifactIsNotReportedAsMissing()
    {
        // 1.12.2 及以前的 LWJGL 2 有一批"只有分类器"的库；把它们报成缺失会让每个老版本都刷一堆假故障。
        var version = Real1204Json(
        [
            NativesOnlyLibrary(
                "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
                "org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-windows.jar"),
        ]);

        var launch = LaunchArgumentBuilder.Build(
            Request(version: version, fileExists: path => !path.Contains("lwjgl-platform", StringComparison.Ordinal)));

        Assert.AreEqual(0, launch.ProblemsOf(LaunchProblemKind.MissingFile).Count, Describe(launch));
    }

    [TestMethod]
    public void AMissingClientJarIsReported()
    {
        var launch = LaunchArgumentBuilder.Build(
            Request(fileExists: path => !path.EndsWith(@"1.20.4.jar", StringComparison.Ordinal)));

        var problems = launch.ProblemsOf(LaunchProblemKind.MissingFile);

        Assert.AreEqual(1, problems.Count);
        Assert.AreEqual("客户端 JAR", problems[0].Subject);
        Assert.IsFalse(launch.Classpath.Any(path => path.EndsWith(@"1.20.4.jar", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AMissingLoggingConfigIsReported()
    {
        var launch = LaunchArgumentBuilder.Build(
            Request(fileExists: path => !path.EndsWith("client-1.12.xml", StringComparison.Ordinal)));

        var problems = launch.ProblemsOf(LaunchProblemKind.MissingFile);

        Assert.AreEqual(1, problems.Count);
        Assert.AreEqual("日志配置", problems[0].Subject);
        Assert.IsFalse(launch.JvmArguments.Any(argument =>
            argument.StartsWith("-Dlog4j.configurationFile", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AUnsafeLibraryPathIsRejectedWithoutCrashing()
    {
        var version = Real1204Json([MavenLibrary("evil:evil:1.0", "../../evil.jar")]);

        var launch = LaunchArgumentBuilder.Build(Request(version: version));
        var problems = launch.ProblemsOf(LaunchProblemKind.UnsafeLibraryPath);

        Assert.AreEqual(1, problems.Count);
        Assert.AreEqual("evil:evil:1.0", problems[0].Subject);
        Assert.IsFalse(launch.Classpath.Any(path => path.Contains("evil", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ALibraryWithoutACoordinateIsRejectedWithoutCrashing()
    {
        var version = Real1204Json([new Library { Name = "not-a-maven-coordinate" }]);

        var launch = LaunchArgumentBuilder.Build(Request(version: version));

        Assert.AreEqual(1, launch.ProblemsOf(LaunchProblemKind.UnsafeLibraryPath).Count);
    }

    [TestMethod]
    public void JavaEightDropsModuleOptionsAndSkipsTheLoggingConfig()
    {
        var version = Real1204Json() with
        {
            Arguments = new VersionArguments
            {
                Jvm =
                [
                    VersionArgument.Plain("--add-opens"),
                    VersionArgument.Plain("java.base/java.lang=ALL-UNNAMED"),
                    .. Real1204JvmArguments(),
                ],
                Game = Real1204GameArguments(),
            },
        };

        var launch = LaunchArgumentBuilder.Build(Request(version: version, javaMajor: 8));

        Assert.IsFalse(launch.JvmArguments.Contains("--add-opens"), "Java 8 上没有 --add-opens。");
        Assert.IsTrue(launch.JvmArguments.Contains("-Xss1M"), "Java 8 的兜底参数。");
        Assert.AreEqual(1, launch.JvmArguments.Count(argument => argument == HeapDumpArgument));
        Assert.AreEqual(
            0,
            launch.JvmArguments.Count(argument => argument.StartsWith("-Dlog4j.configurationFile", StringComparison.Ordinal)),
            "C-5：Java 8 不注入日志配置参数。");
        Assert.AreEqual(1, launch.ProblemsOf(LaunchProblemKind.RejectedArgument).Count, "被挡掉的参数要如实上报。");
    }

    [TestMethod]
    public void InjectionArgumentsAreRejectedButAValueMentioningThemIsNot()
    {
        var version = Real1204Json() with
        {
            Arguments = new VersionArguments
            {
                Jvm =
                [
                    VersionArgument.Plain(@"-javaagent:C:\evil.jar"),
                    VersionArgument.Plain("-XX:OnOutOfMemoryError=calc.exe"),
                    VersionArgument.Plain("-Dfoo=-javaagent:not-an-agent"),
                    .. Real1204JvmArguments(),
                ],
                Game = Real1204GameArguments(),
            },
        };

        var launch = LaunchArgumentBuilder.Build(Request(version: version));

        Assert.AreEqual(2, launch.ProblemsOf(LaunchProblemKind.RejectedArgument).Count);
        Assert.IsFalse(launch.JvmArguments.Any(argument =>
            argument.StartsWith("-javaagent", StringComparison.Ordinal)));
        Assert.IsTrue(
            launch.JvmArguments.Contains("-Dfoo=-javaagent:not-an-agent"),
            "值里提到 javaagent 的普通系统属性不该被毙掉（Neo 用 Contains 判断会误杀）。");
    }

    [TestMethod]
    public void AForgeBootstrapperGetsTheFixedAddOpensInBothSpellings()
    {
        var version = Real1204Json() with
        {
            MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
            Arguments = new VersionArguments
            {
                Jvm =
                [
                    // 分开写与合并写都要能被识别（Neo 只处理分开写）。
                    VersionArgument.Plain("--add-opens"),
                    VersionArgument.Plain("java.base/java.util=ALL-UNNAMED"),
                    VersionArgument.Plain("--add-exports=java.base/sun.security.util=ALL-UNNAMED"),
                    VersionArgument.Plain("--add-opens=java.base/java.lang=ALL-UNNAMED"),
                    // 开放给具名模块的那条不是"给无名模块的"，必须留下。
                    VersionArgument.Plain("--add-opens=java.base/java.nio=org.example.mod"),
                    .. Real1204JvmArguments(),
                ],
                Game = Real1204GameArguments(),
            },
        };

        var launch = LaunchArgumentBuilder.Build(Request(version: version));
        var jvm = launch.JvmArguments.ToList();

        Assert.IsTrue(jvm.Contains("--add-opens=java.base/java.nio=org.example.mod"));
        Assert.IsFalse(jvm.Any(argument =>
            argument.StartsWith("--add-opens=", StringComparison.Ordinal)
            && argument.EndsWith("=ALL-UNNAMED", StringComparison.Ordinal)));
        Assert.IsFalse(jvm.Any(argument => argument == "java.base/java.util=ALL-UNNAMED"));
        Assert.IsFalse(jvm.Any(argument => argument == "java.base/java.lang=ALL-UNNAMED"));

        var start = jvm.IndexOf("--add-opens");
        AssertSequence(
            [
                "--add-opens", "java.base/java.lang.invoke=ALL-UNNAMED",
                "--add-opens", "java.base/java.util.jar=ALL-UNNAMED",
                "--add-exports", "java.base/sun.security.util=ALL-UNNAMED",
            ],
            jvm.GetRange(start, 6).ToArray(),
            "固定的三组 ALL-UNNAMED 开放");
    }

    [TestMethod]
    public void TheUserPropertiesFlagIsGuaranteedExactlyOnce()
    {
        // (1) 正常情况：1.7.10 的模板声明了它，替换后键值都在。
        var legacy = LaunchArgumentBuilder.Build(
            Request(version: Legacy1710Json(), versionId: "1.7.10", javaMajor: 8));
        var legacyGame = legacy.GameArguments.ToList();

        Assert.AreEqual(1, legacyGame.Count(argument => argument == "--userProperties"));
        Assert.AreEqual("{}", legacyGame[legacyGame.IndexOf("--userProperties") + 1]);

        // (2) 被裁剪过的 JSON：仍声明 ${user_properties}，但 --userProperties 这个键没了。
        var trimmed = Legacy1710Json() with
        {
            MinecraftArguments = Legacy1710Arguments.Replace("--userProperties ", string.Empty, StringComparison.Ordinal),
        };
        var repaired = LaunchArgumentBuilder.Build(
            Request(version: trimmed, versionId: "1.7.10", javaMajor: 8));
        var repairedGame = repaired.GameArguments.ToList();

        Assert.AreEqual(1, repairedGame.Count(argument => argument == "--userProperties"), "缺失的必需项应被补全。");
        Assert.AreEqual("{}", repairedGame[repairedGame.LastIndexOf("--userProperties") + 1]);
    }

    [TestMethod]
    public void AnAncientVersionIsNotGivenTheUserPropertiesOption()
    {
        // 1.5.2 的 Main 不认 --userProperties，注入会直接抛 UnrecognizedOptionException。
        var launch = LaunchArgumentBuilder.Build(
            Request(version: Legacy152Json(), versionId: "1.5.2", javaMajor: 8));

        Assert.IsFalse(launch.GameArguments.Contains("--userProperties"));
    }

    [TestMethod]
    public void TheResolutionGoesThroughTheOfficialFeatureAndPlaceholders()
    {
        var launch = LaunchArgumentBuilder.Build(Request() with { Width = 1280, Height = 720 });
        var game = launch.GameArguments.ToList();

        Assert.AreEqual(1, game.Count(argument => argument == "--width"), "JSON 的规则已经产生 --width，不该再补一次。");
        Assert.AreEqual("1280", game[game.IndexOf("--width") + 1]);
        Assert.AreEqual("720", game[game.IndexOf("--height") + 1]);
    }

    [TestMethod]
    public void ALegacyVersionGetsTheResolutionArgumentsItself()
    {
        var launch = LaunchArgumentBuilder.Build(
            Request(version: Legacy1710Json(), versionId: "1.7.10", javaMajor: 8) with { Width = 1280, Height = 720 });
        var game = launch.GameArguments.ToList();

        Assert.AreEqual(1, game.Count(argument => argument == "--width"));
        Assert.AreEqual("1280", game[game.IndexOf("--width") + 1]);
        Assert.AreEqual("720", game[game.IndexOf("--height") + 1]);
    }

    [TestMethod]
    public void WithoutAResolutionNoWidthIsPassed()
    {
        var launch = LaunchArgumentBuilder.Build(Request());

        Assert.IsFalse(launch.GameArguments.Contains("--width"));
        Assert.IsFalse(launch.GameArguments.Contains("--height"));
    }

    [TestMethod]
    public void DemoModeAddsTheDemoFlagAndQuickPlayStaysOff()
    {
        var demo = LaunchArgumentBuilder.Build(Request() with { IsDemoUser = true });
        var normal = LaunchArgumentBuilder.Build(Request());

        Assert.IsTrue(demo.GameArguments.Contains("--demo"));
        Assert.IsFalse(normal.GameArguments.Contains("--demo"));
        Assert.IsFalse(normal.GameArguments.Any(argument =>
            argument.StartsWith("--quickPlay", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnUnknownPlaceholderIsReportedOnceAndKeptLiteral()
    {
        var version = Real1204Json() with
        {
            Arguments = new VersionArguments
            {
                Jvm =
                [
                    VersionArgument.Plain("-Dunknown=${totally_unknown}"),
                    VersionArgument.Plain("-Dalso=${totally_unknown}"),
                    .. Real1204JvmArguments(),
                ],
                Game = [VersionArgument.Plain("--thing"), VersionArgument.Plain("${totally_unknown}"), .. Real1204GameArguments()],
            },
        };

        var launch = LaunchArgumentBuilder.Build(Request(version: version));
        var problems = launch.ProblemsOf(LaunchProblemKind.UnresolvedPlaceholder);

        Assert.AreEqual(1, problems.Count, "同一个未知占位符只报一次。");
        Assert.AreEqual("${totally_unknown}", problems[0].Subject);
        Assert.IsTrue(
            launch.GameArguments.Contains("${totally_unknown}"),
            "游戏参数里的未知占位符原样保留 —— 悄悄换成空串会让一个坏参数看起来正常。");
    }

    [TestMethod]
    public void ExtraJvmArgumentsComeLastAndCanOverrideTheMemoryDefault()
    {
        var request = Request() with { ExtraJvmArguments = "-XX:+UseG1GC -Xmx8G", ExtraGameArguments = "--fullscreen" };

        var launch = LaunchArgumentBuilder.Build(request);

        Assert.AreEqual("-Xmx4096M", launch.JvmArguments[0]);
        Assert.IsTrue(launch.JvmArguments.Contains("-XX:+UseG1GC"));
        Assert.IsTrue(
            launch.JvmArguments.Contains("-Xmx8G"),
            "用户的附加参数不参与去重，必须能覆盖默认的 -Xmx（JVM 取最后一次出现）。");
        Assert.AreEqual("--fullscreen", launch.GameArguments[^1], "附加游戏参数放在最后。");
    }

    [TestMethod]
    public void TheBrandingCanBeOverridden()
    {
        var launch = LaunchArgumentBuilder.Build(
            Request() with { Branding = new LauncherBranding("my-launcher", "9.9") });

        Assert.AreEqual(
            1,
            launch.JvmArguments.Count(argument =>
                argument.StartsWith("-Dminecraft.launcher.brand=", StringComparison.Ordinal)),
            "版本 JSON 里的 ${launcher_name} 是同一个键，会被默认值顶掉，而不是传两遍。");
        Assert.IsTrue(launch.JvmArguments.Contains("-Dminecraft.launcher.brand=my-launcher"));
        Assert.IsTrue(launch.JvmArguments.Contains("-Dminecraft.launcher.version=9.9"));
    }

    [TestMethod]
    public void AnEmptyClientIdStillProducesAValue()
    {
        var launch = LaunchArgumentBuilder.Build(Request() with { ClientId = string.Empty });
        var game = launch.GameArguments.ToList();

        Assert.AreEqual("0", game[game.IndexOf("--clientId") + 1]);
    }

    [TestMethod]
    public void TheOutputEncodingIsPinnedOnBothEnds()
    {
        // 启动器按 UTF-8 读标准输出（GameProcessRunner 的 StandardOutputEncoding），
        // 所以 JVM 那端也必须钉成 UTF-8 —— 否则 Java ≤17 在中文系统上输出 GBK，p14 上就是乱码。
        var launch = LaunchArgumentBuilder.Build(Request());

        Assert.IsTrue(launch.JvmArguments.Contains("-Dfile.encoding=UTF-8"));
        Assert.IsTrue(launch.JvmArguments.Contains("-Dstdout.encoding=UTF-8"));
        Assert.IsTrue(launch.JvmArguments.Contains("-Dstderr.encoding=UTF-8"));

        // 版本 JSON 想改也不行：同键的默认值先入表（两端不一致的话，一样是乱码）。
        var version = Real1204Json() with
        {
            Arguments = new VersionArguments
            {
                Jvm = [VersionArgument.Plain("-Dfile.encoding=GBK"), .. Real1204JvmArguments()],
                Game = Real1204GameArguments(),
            },
        };

        var overridden = LaunchArgumentBuilder.Build(Request(version: version));

        Assert.IsTrue(overridden.JvmArguments.Contains("-Dfile.encoding=UTF-8"));
        Assert.IsFalse(overridden.JvmArguments.Contains("-Dfile.encoding=GBK"));
    }

    [TestMethod]
    public void AnUnsafeVersionIdIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            LaunchArgumentBuilder.Build(Request(versionId: @"..\..\evil")));
    }
}
