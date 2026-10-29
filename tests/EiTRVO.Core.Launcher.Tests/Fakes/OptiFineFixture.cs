using System.IO.Compression;
using System.Text;
using EiTRVO.Core.Launcher.Installer;

namespace EiTRVO.Core.Launcher.Tests.Fakes;

/// <summary>
/// OptiFine（M4-i）用例共用的假数据：官方 downloads 页 HTML、一个"长得像 OptiFine 安装器"的 jar、沙箱产物。
/// <para>
/// 2026-10-23 起列表来自 **optifine.net 官方 downloads 页**（与 Neo 一样抓页面），
/// 不再使用任何镜像 JSON。链接形状照抄实测页面：<c>href="http://optifine.net/adloadx?f=..."</c>。
/// </para>
/// </summary>
internal static class OptiFineFixture
{
    /// <summary>只有正式版的下载页。</summary>
    public static readonly string ListHtml = Html(
        "OptiFine_1.20.4_HD_U_I8.jar");

    /// <summary>两个正式版 + 一个预览版（用来钉住排序与"预览版不列进下拉框"）。</summary>
    public static readonly string ListHtmlThreeBuilds = Html(
        "OptiFine_1.20.4_HD_U_I6.jar",
        "OptiFine_1.20.4_HD_U_I8.jar",
        "preview_OptiFine_1.20.4_HD_U_I8_pre4.jar");

    /// <summary>正式版对应的版本 id（== 官方安装器写出的目录名）。</summary>
    public const string VersionId = "1.20.4-OptiFine_HD_U_I8";

    /// <summary>取件页面地址（安装器再解析出带 token 的 downloadx 地址）。</summary>
    public static readonly string DownloadUrl =
        "https://optifine.net/adloadx?f=OptiFine_1.20.4_HD_U_I8.jar";

    /// <summary>造一个官方 downloads 页（只含给定文件名的 adloadx 链接）。</summary>
    /// <param name="fileNames">官方文件名。</param>
    /// <returns>HTML。</returns>
    public static string Html(params string[] fileNames) =>
        "<html><body>"
        + string.Join(
            string.Empty,
            fileNames.Select(static name =>
                "<a href=\"http://optifine.net/adloadx?f=" + name + "\">download</a>"))
        + "</body></html>";

    /// <summary>
    /// 造一个"长得像 OptiFine 安装器"的 jar。
    /// </summary>
    /// <param name="withInstallerClass">是否包含 <c>optifine/Installer.class</c>。</param>
    /// <param name="withExpectedMainClass">MANIFEST 里是否声明 <c>Main-Class: optifine.InstallerFrame</c>。</param>
    /// <returns>jar 的字节。</returns>
    public static byte[] InstallerJar(bool withInstallerClass = true, bool withExpectedMainClass = true)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
            using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
            {
                writer.Write("Manifest-Version: 1.0\nMain-Class: "
                    + (withExpectedMainClass ? OptiFineInstaller.InstallerMainClass : "something.else.Main")
                    + "\n\n");
            }

            if (withInstallerClass)
            {
                archive.CreateEntry(OptiFineInstaller.InstallerClassEntry);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>模拟官方安装器在沙箱里的写入：版本 JSON + 一个库文件。</summary>
    /// <param name="sandboxParent">沙箱的上一级（安装器自己的 <c>%APPDATA%</c>）。</param>
    /// <param name="versionId">版本 id。</param>
    public static void WriteSandboxProduct(string sandboxParent, string versionId)
    {
        var sandbox = Path.Combine(sandboxParent, OptiFineInstaller.SandboxFolderName);
        var directory = Path.Combine(sandbox, "versions", versionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, versionId + ".json"),
            """{"id":"x","inheritsFrom":"1.20.4"}""",
            new UTF8Encoding(false));

        var library = Path.Combine(sandbox, "libraries", "optifine", "OptiFine", "1.20.4_HD_U_I8");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "OptiFine-1.20.4_HD_U_I8.jar"), "patched", new UTF8Encoding(false));
    }
}
