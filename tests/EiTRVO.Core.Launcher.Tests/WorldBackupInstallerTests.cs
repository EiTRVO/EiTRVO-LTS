using System.IO.Compression;
using System.Security.Cryptography;
using EiTRVO.Core.Launcher.Diagnostics;
using EiTRVO.Core.Launcher.Downloads;
using EiTRVO.Core.Launcher.Installer;
using EiTRVO.Core.Launcher.Tests.Fakes;

namespace EiTRVO.Core.Launcher.Tests;

/// <summary>
/// M7-f：存档的备份与恢复（<see cref="WorldBackupInstaller"/>）。
/// </summary>
/// <remarks>
/// 全部在**真文件**上跑：这一块的每一条判据都是"磁盘上到底留下了什么"——
/// 归档里是不是每一个字节、当前的存档有没有被直接覆盖、被换下来的那一份还在不在、
/// 暂存目录有没有清干净、读不动的文件会不会被静默跳过。
/// </remarks>
[TestClass]
public sealed class WorldBackupInstallerTests
{
    private string _root = null!;
    private string _game = null!;
    private WorldBackupInstaller _installer = null!;
    private RecordingDiagnosticsLog _diagnostics = null!;

    /// <summary>每个用例一套全新的目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "eitrvo-core-backup", Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(_game);
        _diagnostics = new RecordingDiagnosticsLog();
        _installer = new WorldBackupInstaller(
            new FixedClock(new DateTimeOffset(2026, 10, 16, 20, 3, 31, TimeSpan.FromHours(8))),
            _diagnostics);
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ==================== 往返 ====================

    [TestMethod]
    public async Task BackupThenDeleteThenRestoreGivesBackEveryFileByteForByte()
    {
        var save = WriteSave(("level.dat", "level"), ("region/r.0.0.mca", "region bytes"), ("playerdata/abc.dat", "player"));
        var before = Describe(save);

        var backup = await _installer.BackupAsync(_game, Target());

        Assert.IsTrue(backup.Succeeded, Why(backup));
        Assert.AreEqual(3, backup.NativeFilesExtracted, "写进归档的文件数要如实报出来。");
        Assert.IsTrue(File.Exists(Archive("New World")));

        // 删掉整个存档（模拟"存档坏了 / 被误删"），再恢复。
        Directory.Delete(save, recursive: true);
        Assert.IsFalse(Directory.Exists(save));

        var restore = await _installer.RestoreAsync(_game, Target());

        Assert.IsTrue(restore.Succeeded, Why(restore));
        Assert.AreEqual(before, Describe(save), "恢复之后必须逐文件哈希一致。");

        // 备份本身留着（所以恢复之后 p09 仍然显示「已备份」）。
        Assert.IsTrue(File.Exists(Archive("New World")));
    }

    [TestMethod]
    public async Task RestoringCreatesTheSaveDirectoryWhenTheSaveIsGone()
    {
        WriteSave(("level.dat", "level"));
        await _installer.BackupAsync(_game, Target());
        Directory.Delete(Path.Combine(_game, "saves", "New World"), recursive: true);

        var restore = await _installer.RestoreAsync(_game, Target());

        Assert.IsTrue(restore.Succeeded, Why(restore));
        Assert.IsTrue(File.Exists(Path.Combine(_game, "saves", "New World", "level.dat")));
    }

    // ==================== 恢复不是覆盖 ====================

    [TestMethod]
    public async Task RestoringMovesTheCurrentSaveIntoTheBackupDirectoryInsteadOfOverwritingIt()
    {
        var save = WriteSave(("level.dat", "第一版"));
        await _installer.BackupAsync(_game, Target());

        // 备份之后用户又玩了一会儿。
        File.WriteAllText(Path.Combine(save, "level.dat"), "第二版");

        var restore = await _installer.RestoreAsync(_game, Target());

        Assert.IsTrue(restore.Succeeded, Why(restore));
        Assert.AreEqual("第一版", File.ReadAllText(Path.Combine(save, "level.dat")));

        // 被换下来的那一份在 backups\saves\<时间戳>\<存档目录名>\ 里，一个字节都没动。
        var stamps = Directory.GetDirectories(Path.Combine(_game, "backups", "saves"));
        Assert.AreEqual(1, stamps.Length);
        Assert.AreEqual(
            "第二版",
            File.ReadAllText(Path.Combine(stamps[0], "New World", "level.dat")));
    }

    [TestMethod]
    public async Task RestoreKeepsTheStashOfEveryEarlierRestore()
    {
        var save = WriteSave(("level.dat", "第一版"));
        await _installer.BackupAsync(_game, Target());

        File.WriteAllText(Path.Combine(save, "level.dat"), "第二版");
        await _installer.RestoreAsync(_game, Target());

        File.WriteAllText(Path.Combine(save, "level.dat"), "第三版");
        await _installer.RestoreAsync(_game, Target());

        // 两次恢复各留下一份"当时的样子" —— 时间戳目录不重名（同一秒内也不会撞）。
        var stamps = Directory.GetDirectories(Path.Combine(_game, "backups", "saves"));
        Assert.AreEqual(2, stamps.Length);

        CollectionAssert.AreEquivalent(
            new[] { "第二版", "第三版" },
            stamps.Select(static stamp => File.ReadAllText(Path.Combine(stamp, "New World", "level.dat"))).ToArray());

        Assert.AreEqual("第一版", File.ReadAllText(Path.Combine(save, "level.dat")));
    }

    // ==================== 拒绝路径 ====================

    [TestMethod]
    public async Task RestoringASaveThatWasNeverBackedUpChangesNothing()
    {
        var save = WriteSave(("level.dat", "现在的内容"));

        var restore = await _installer.RestoreAsync(_game, Target());

        Assert.IsFalse(restore.Succeeded);
        StringAssert.Contains(restore.Failure!.Key, "还没有备份");
        Assert.AreEqual("现在的内容", File.ReadAllText(Path.Combine(save, "level.dat")));
    }

    [TestMethod]
    public async Task AnEmptyArchiveIsNotRestored()
    {
        var save = WriteSave(("level.dat", "现在的内容"));

        var backupDirectory = Path.Combine(_game, "backups");
        Directory.CreateDirectory(backupDirectory);
        WriteZip(Archive("New World"), []);

        var restore = await _installer.RestoreAsync(_game, Target());

        // "解压出来是空的"不是"恢复成功"：那会留下一个空存档，而备份里什么都没有。
        Assert.IsFalse(restore.Succeeded);
        StringAssert.Contains(restore.Failure!.Key, "一个文件都没有");
        Assert.AreEqual("现在的内容", File.ReadAllText(Path.Combine(save, "level.dat")));
        Assert.IsFalse(Directory.Exists(Path.Combine(backupDirectory, ".staging")));
    }

    [TestMethod]
    public async Task ACorruptArchiveIsRejectedAndTheSaveIsUntouched()
    {
        var save = WriteSave(("level.dat", "现在的内容"));

        var backupDirectory = Path.Combine(_game, "backups");
        Directory.CreateDirectory(backupDirectory);
        File.WriteAllText(Archive("New World"), "这不是一个 zip");

        var restore = await _installer.RestoreAsync(_game, Target());

        Assert.IsFalse(restore.Succeeded);
        StringAssert.Contains(restore.Failure!.Key, "损坏");
        Assert.AreEqual("现在的内容", File.ReadAllText(Path.Combine(save, "level.dat")));
    }

    [TestMethod]
    public async Task AnEntryThatEscapesTheSaveDirectoryIsRejectedWithoutWritingAnything()
    {
        var save = WriteSave(("level.dat", "现在的内容"));

        var backupDirectory = Path.Combine(_game, "backups");
        Directory.CreateDirectory(backupDirectory);
        WriteZip(Archive("New World"), [("../evil.txt", "boom")]);

        var restore = await _installer.RestoreAsync(_game, Target());

        // 手动放进 backups\ 的 zip 不是我们写的：条目名同样要过穿越检查。
        Assert.IsFalse(restore.Succeeded);
        StringAssert.Contains(restore.Failure!.Key, "越出");
        Assert.AreEqual("现在的内容", File.ReadAllText(Path.Combine(save, "level.dat")));
        Assert.AreEqual(0, Directory.GetFiles(_root, "evil.txt", SearchOption.AllDirectories).Length);
        Assert.IsFalse(Directory.Exists(Path.Combine(backupDirectory, ".staging")));
    }

    [TestMethod]
    public async Task AFolderNameThatIsNotASinglePathSegmentIsRejected()
    {
        WriteSave(("level.dat", "现在的内容"));

        var sneaky = new WorldBackupTarget(@"..\..\Windows", "坏名字");

        var backup = await _installer.BackupAsync(_game, sneaky);
        var restore = await _installer.RestoreAsync(_game, sneaky);

        Assert.IsFalse(backup.Succeeded);
        Assert.IsFalse(restore.Succeeded);
        StringAssert.Contains(backup.Failure!.Key, "目录名不可用");
        StringAssert.Contains(restore.Failure!.Key, "目录名不可用");

        // 一个字节都不该写出去（连 backups 目录都不该被建出来）。
        Assert.IsFalse(Directory.Exists(Path.Combine(_game, "backups")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Windows")));
    }

    [TestMethod]
    public async Task BackingUpASaveThatIsNotThereFails()
    {
        var result = await _installer.BackupAsync(_game, Target());

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure!.Key, "找不到这个存档的目录");
    }

    // ==================== 读不动的文件 ====================

    [TestMethod]
    public async Task AFileThatCannotBeReadFailsTheBackupInsteadOfSkippingIt()
    {
        var save = WriteSave(("level.dat", "level"), ("session.lock", "lock"));

        var zipPath = Archive("New World");

        // 游戏正在运行时存档里的文件会被独占占用（Neo 的同名实现把这种文件静默跳过 ——
        // 那样会产出一份"看起来成功"的不完整备份）。
        FileStream? locked = null;

        try
        {
            locked = new FileStream(
                Path.Combine(save, "level.dat"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var result = await _installer.BackupAsync(_game, Target());

            Assert.IsFalse(result.Succeeded, "读不到文件就不能产出一份不完整的备份。");
            Assert.IsFalse(File.Exists(zipPath), "失败的备份不该留下 zip。");
            Assert.IsFalse(File.Exists(zipPath + ".part"), "临时文件也要清掉。");

            // 必须留一条事件：任务在 p06 上只会显示一行失败，日志里得能查到为什么。
            Assert.IsTrue(
                _diagnostics.Entries.Any(static entry => entry.Level is LauncherEventLevel.Warning or LauncherEventLevel.Error),
                "读不到文件必须写成一条警告 / 错误事件。");
        }
        finally
        {
            locked?.Dispose();
        }
    }

    // ==================== 覆盖与复核 ====================

    [TestMethod]
    public async Task BackingUpTwiceKeepsOneArchiveWithTheLatestContent()
    {
        var save = WriteSave(("level.dat", "第一版"));

        await _installer.BackupAsync(_game, Target());
        File.WriteAllText(Path.Combine(save, "level.dat"), "第二版");
        var second = await _installer.BackupAsync(_game, Target());

        Assert.IsTrue(second.Succeeded, Why(second));

        // 约定就是一个存档一份 zip（读取端 FileContentCatalog.HasBackup 只认这一个名字）。
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_game, "backups"), "*.zip").Length);
        Assert.AreEqual("第二版", ReadEntry(Archive("New World"), "level.dat"));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(_game, "backups"), "*.part").Length);
    }

    // ==================== 进度与阶段 ====================

    [TestMethod]
    public async Task StagesAndProgressAreReportedInOrder()
    {
        WriteSave(("level.dat", "12345"), ("region/a.mca", "123"));

        var progress = new RecordingProgress();
        var changes = new List<(InstallStageKind Kind, bool Completed)>();

        var result = await _installer.BackupAsync(
            _game,
            Target(),
            progress,
            new SyncProgress<InstallStageChange>(change => changes.Add((change.Kind, change.Completed))));

        Assert.IsTrue(result.Succeeded, Why(result));

        CollectionAssert.AreEqual(
            new[]
            {
                (InstallStageKind.MeasureWorld, false),
                (InstallStageKind.MeasureWorld, true),
                (InstallStageKind.BackupWorld, false),
                (InstallStageKind.BackupWorld, true),
                (InstallStageKind.VerifyArchive, false),
                (InstallStageKind.VerifyArchive, true),
            },
            changes);

        var last = progress.Reports[^1];
        Assert.AreEqual(2, last.CompletedFiles);
        Assert.AreEqual(2, last.TotalFiles);
        Assert.AreEqual(8L, last.CompletedBytes, "两次报告的字节数都按真实文件长度算（5 + 3）。");
        Assert.AreEqual(8L, last.TotalBytes);
    }

    [TestMethod]
    public async Task RestoringReportsTheThreeRestoreStages()
    {
        WriteSave(("level.dat", "level"));
        await _installer.BackupAsync(_game, Target());

        var changes = new List<(InstallStageKind Kind, bool Completed)>();

        var result = await _installer.RestoreAsync(
            _game,
            Target(),
            stages: new SyncProgress<InstallStageChange>(change => changes.Add((change.Kind, change.Completed))));

        Assert.IsTrue(result.Succeeded, Why(result));

        CollectionAssert.AreEqual(
            new[]
            {
                (InstallStageKind.ExtractBackup, false),
                (InstallStageKind.ExtractBackup, true),
                (InstallStageKind.StashWorld, false),
                (InstallStageKind.StashWorld, true),
                (InstallStageKind.PromoteWorld, false),
                (InstallStageKind.PromoteWorld, true),
            },
            changes);
    }

    // ==================== 事件 ====================

    [TestMethod]
    public async Task BothDirectionsLeaveASuccessEvent()
    {
        WriteSave(("level.dat", "level"));

        await _installer.BackupAsync(_game, Target());
        await _installer.RestoreAsync(_game, Target());

        Assert.IsTrue(_diagnostics.Entries.Any(static entry => entry.MessageKey == "备份存档完成：{0}（{1} 个文件，{2} 字节）"));
        Assert.IsTrue(_diagnostics.Entries.Any(static entry => entry.MessageKey == "恢复存档完成：{0}（{1} 个文件，{2} 字节）"));
    }

    // ==================== 工具 ====================

    private static WorldBackupTarget Target() => new("New World", "新世界");

    /// <summary>建一个存档目录并放几个文件。</summary>
    /// <param name="files">相对路径 + 内容。</param>
    /// <returns>存档目录。</returns>
    private string WriteSave(params (string Relative, string Body)[] files)
    {
        var save = Path.Combine(_game, "saves", "New World");
        Directory.CreateDirectory(save);

        foreach (var (relative, body) in files)
        {
            var path = Path.Combine(save, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        return save;
    }

    /// <summary>备份归档的路径（写入端与读取端约定的那一处）。</summary>
    /// <param name="folder">存档目录名。</param>
    /// <returns>归档路径。</returns>
    private string Archive(string folder) => Path.Combine(_game, "backups", folder + ".zip");

    /// <summary>把一个 zip 直接写到指定路径（用例要造"手动放进 backups 的归档"）。</summary>
    /// <param name="path">归档路径。</param>
    /// <param name="entries">条目（名字 + 内容）。</param>
    private static void WriteZip(string path, (string Name, string Body)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, body) in entries)
        {
            var entry = archive.CreateEntry(name);

            using var writer = new StreamWriter(entry.Open());
            writer.Write(body);
        }
    }

    /// <summary>读归档里某个条目的内容。</summary>
    /// <param name="path">归档路径。</param>
    /// <param name="name">条目名。</param>
    /// <returns>内容。</returns>
    private static string ReadEntry(string path, string name)
    {
        using var archive = ZipFile.OpenRead(path);

        var entry = archive.Entries.Single(candidate => candidate.FullName == name);

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>目录里每个文件的相对路径 + SHA-256（"逐文件哈希一致"就靠它比）。</summary>
    /// <param name="directory">目录。</param>
    /// <returns>可比较的一行式快照。</returns>
    private static string Describe(string directory) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/')
                                + "="
                                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
                .OrderBy(static line => line, StringComparer.Ordinal));

    private static string Why(InstallResult result) =>
        result.Succeeded ? string.Empty : "失败原因：" + result.Failure?.Key;

    /// <summary>同步进度接收者（<see cref="Progress{T}"/> 会把回调丢到线程池，断言会与它赛跑）。</summary>
    /// <typeparam name="T">报告类型。</typeparam>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onReport;

        public SyncProgress(Action<T> onReport) => _onReport = onReport;

        /// <inheritdoc />
        public void Report(T value) => _onReport(value);
    }

    /// <summary>记录每次进度报告。</summary>
    private sealed class RecordingProgress : IProgress<FileTransferProgress>
    {
        /// <summary>收到的报告。</summary>
        public List<FileTransferProgress> Reports { get; } = [];

        /// <inheritdoc />
        public void Report(FileTransferProgress value) => Reports.Add(value);
    }
}
