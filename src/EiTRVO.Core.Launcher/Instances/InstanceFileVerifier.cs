using System.Security.Cryptography;
using EiTRVO.Core.Launcher.Abstractions;
using EiTRVO.Core.Launcher.Security;
using EiTRVO.Core.Launcher.Versions;

namespace EiTRVO.Core.Launcher.Instances;

/// <summary>按实例版本 JSON 的 SHA-1 校验客户端 JAR 与库文件。</summary>
public sealed class InstanceFileVerifier : IInstanceFileVerifier
{
    private readonly IInstanceCatalog _instances;
    private readonly VersionJsonStore _versions;

    /// <summary>构造校验器。</summary>
    /// <param name="instances">实例目录。</param>
    /// <param name="versions">版本 JSON 存储。</param>
    public InstanceFileVerifier(IInstanceCatalog instances, VersionJsonStore versions)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(versions);

        _instances = instances;
        _versions = versions;
    }

    /// <inheritdoc />
    public async Task<InstanceFileVerificationResult> VerifyAsync(
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        var info = _instances.GetLaunchInfo(instanceName);
        var versionId = File.Exists(Path.Combine(info.InstancePath, "version.json"))
            ? instanceName
            : ResolveLegacyVersionId(info);

        var resolved = _versions.Resolve(info.GameDirectory, versionId);

        var checkedFiles = 0;
        var missingFiles = 0;
        var mismatchedFiles = 0;
        var skippedFiles = 0;

        // 客户端 JAR：新布局在实例目录，旧布局可能在共享版本目录。
        if (resolved.Merged.Downloads?.Client is { Sha1.Length: 40 } client)
        {
            var jar = FindClientJar(info, instanceName);

            if (jar is null)
            {
                missingFiles++;
            }
            else if (await MatchesAsync(jar, client.Sha1, cancellationToken).ConfigureAwait(false))
            {
                checkedFiles++;
            }
            else
            {
                mismatchedFiles++;
            }
        }

        var librariesRoot = Path.GetFullPath(Path.Combine(info.GameDirectory, "libraries"));

        foreach (var library in resolved.Merged.Libraries ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 版本 JSON 里同时列着 Windows / Linux / macOS 的库；启动与安装都按规则筛过，
            // 校验也必须用同一把尺子，否则会把本来就不该装到 Windows 的库算成缺失。
            if (!LibraryFilter.Applies(library, GamePlatform.Current))
            {
                continue;
            }

            var artifact = library.Downloads?.Artifact;
            if (string.IsNullOrWhiteSpace(artifact?.Path))
            {
                // URL 形式 / 没有 artifact 的库：不知道落盘路径，也没有官方 SHA-1 可读的可靠映射。
                skippedFiles++;
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(librariesRoot, artifact.Path.Replace('/', Path.DirectorySeparatorChar)));

            if (!PathSafety.IsContained(librariesRoot, path))
            {
                skippedFiles++;
                continue;
            }

            if (!File.Exists(path))
            {
                missingFiles++;
                continue;
            }

            if (artifact.Sha1 is not { Length: 40 } sha1)
            {
                skippedFiles++;
                continue;
            }

            if (await MatchesAsync(path, sha1, cancellationToken).ConfigureAwait(false))
            {
                checkedFiles++;
            }
            else
            {
                mismatchedFiles++;
            }
        }

        return new InstanceFileVerificationResult(checkedFiles, missingFiles, mismatchedFiles, skippedFiles);
    }

    private static string ResolveLegacyVersionId(InstanceLaunchInfo info)
    {
        if (info.Loader == LoaderKind.Vanilla || string.IsNullOrWhiteSpace(info.LoaderVersion))
        {
            return info.GameVersion;
        }

        return LoaderNaming.VersionId(info.Loader, info.LoaderVersion, info.GameVersion);
    }

    private static string? FindClientJar(InstanceLaunchInfo info, string instanceName)
    {
        string[] candidates =
        [
            Path.Combine(info.InstancePath, instanceName + ".jar"),
            Path.Combine(info.InstancePath, Path.GetFileName(info.InstancePath) + ".jar"),
            Path.Combine(info.GameDirectory, "versions", info.GameVersion, info.GameVersion + ".jar"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<bool> MatchesAsync(
        string path,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

#pragma warning disable CA5350 // Minecraft 清单协议规定用 SHA-1；这里只做完整性核对，不是密码学用途。
        var actual = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5350

        return string.Equals(
            Convert.ToHexString(actual),
            expectedSha1,
            StringComparison.OrdinalIgnoreCase);
    }
}
