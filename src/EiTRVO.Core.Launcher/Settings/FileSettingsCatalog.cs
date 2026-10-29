using System.Text.Json;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Instances;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.Launcher.Settings;

/// <summary>
/// <see cref="ISettingsCatalog"/> 的真实实现：全局设置落在 <c>settings.json</c>，
/// 逐实例覆盖项落在**实例目录**（<c>&lt;游戏目录&gt;\versions\&lt;实例&gt;\overrides.json</c>，
/// 与 <c>instance.json</c> 同一个目录）。
/// <para>
/// 三条行为是刻意设计的，都有反例测试保着：
/// </para>
/// <list type="number">
/// <item><b>读失败绝不写文件</b>：设置文件被截断成半截 JSON 时，用默认值启动、
/// 写一条警告事件，**原文件原样留在那里**。曾经见过"读取失败就重置"的实现，
/// 用户只是编辑时少了一个花括号，几百行配置就没了。</item>
/// <item><b>逐字段回落</b>：少哪个字段就回落到该字段的默认值，而不是整份丢弃。</item>
/// <item><b>读时不做缓存</b>：每次现读。设置文件只有几百字节，省下的那点 IO
/// 换不来"用户手改文件必须重启才生效"这种困惑。</item>
/// </list>
/// </summary>
public sealed class FileSettingsCatalog : ISettingsCatalog
{
    private readonly ILauncherPaths _paths;
    private readonly IDiagnosticsLog _diagnostics;

    /// <summary>已经就"文件损坏"报过警的文件，用来避免每次读都刷一条事件。</summary>
    private readonly FileStampGate _warnedStamps = new();

    /// <summary>构建设置目录。</summary>
    /// <param name="paths">数据目录布局。</param>
    /// <param name="diagnostics">诊断事件（损坏 / 非法数值要留下记录）。</param>
    public FileSettingsCatalog(ILauncherPaths paths, IDiagnosticsLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _paths = paths;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public GlobalSettings GetGlobal()
    {
        var json = AtomicFile.ReadAllTextOrNull(_paths.SettingsFile);
        if (string.IsNullOrWhiteSpace(json))
        {
            // 空文件是"还没有设置"，不是"设置坏了"：不该报警，也不该动它。
            return SettingsDefaults.CreateGlobal();
        }

        try
        {
            var file = LauncherJson.Deserialize<SettingsFile>(json, LauncherJson.Indented);
            return SettingsDefaults.Merge(file?.Global);
        }
        catch (JsonException ex)
        {
            WarnOnce(_paths.SettingsFile, "设置文件不是合法 JSON，已按默认设置启动（原文件未改动）", ex);
            return SettingsDefaults.CreateGlobal();
        }
    }

    /// <inheritdoc />
    public InstanceOverrides GetInstanceOverrides(string instanceName)
    {
        ArgumentNullException.ThrowIfNull(instanceName);

        var global = GetGlobal();

        // 没有实例上下文（例如一个实例都还没建）时按"全继承"返回，而不是抛异常：
        // 这个成员会被页面构造函数直接调用，抛异常等于让整页打不开。
        if (!LauncherPaths.IsValidInstanceName(instanceName))
        {
            return SettingsDefaults.CreateInstanceOverrides(instanceName, global);
        }

        var path = InstancePaths.OverridesFile(global, instanceName);
        var json = AtomicFile.ReadAllTextOrNull(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return SettingsDefaults.CreateInstanceOverrides(instanceName, global);
        }

        try
        {
            var file = LauncherJson.Deserialize<OverridesFile>(json, LauncherJson.Indented);
            return SettingsDefaults.Merge(file, instanceName, global);
        }
        catch (JsonException ex)
        {
            WarnOnce(path, "实例启动参数文件不是合法 JSON，已按继承全局处理（原文件未改动）", ex, instanceName);
            return SettingsDefaults.CreateInstanceOverrides(instanceName, global);
        }
    }

    /// <inheritdoc />
    public void SaveGlobal(GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var json = LauncherJson.Serialize(SettingsDefaults.ToFile(settings), LauncherJson.Indented);
        AtomicFile.WriteAllText(_paths.SettingsFile, json);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.Launcher,
            "已保存全局设置");
    }

    /// <inheritdoc />
    public void SaveInstanceOverrides(InstanceOverrides instanceOverrides)
    {
        ArgumentNullException.ThrowIfNull(instanceOverrides);

        // 写入时实例名必须真的能当目录名：否则会在数据目录里造出奇怪的目录。
        if (!LauncherPaths.IsValidInstanceName(instanceOverrides.InstanceName))
        {
            throw new ArgumentException(
                $"实例名不能用作目录名：'{instanceOverrides.InstanceName}'。",
                nameof(instanceOverrides));
        }

        var global = GetGlobal();
        var directory = InstancePaths.Directory(global, instanceOverrides.InstanceName);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                $"实例目录不存在，无法保存启动参数：'{instanceOverrides.InstanceName}'。");
        }

        var json = LauncherJson.Serialize(SettingsDefaults.ToFile(instanceOverrides), LauncherJson.Indented);
        AtomicFile.WriteAllText(InstancePaths.OverridesFile(global, instanceOverrides.InstanceName), json);

        _diagnostics.Write(
            LauncherEventLevel.Information,
            DiagnosticsSources.InstanceSettings,
            "已保存实例 {0} 的启动参数",
            [instanceOverrides.InstanceName],
            instanceOverrides.InstanceName);
    }

    /// <summary>
    /// 同一个坏文件只在**内容变化后**再报一次警：这个成员在每次页面激活时都会被调用，
    /// 不设这道闸门的话，日志会被同一条消息刷满，真正的新事件反而被埋掉。
    /// </summary>
    private void WarnOnce(string path, string messageKey, JsonException exception, string? instanceName = null)
    {
        if (!_warnedStamps.ShouldReport(path))
        {
            return;
        }

        _diagnostics.Write(
            LauncherEventLevel.Warning,
            DiagnosticsSources.Launcher,
            messageKey,
            [Path.GetFileName(path), exception.GetType().Name],
            instanceName);
    }
}
