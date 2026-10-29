using System.IO;
using System.Text;
using EiTRVO.Core.App.ViewModels.Dialogs;
using EiTRVO.Core.App.ViewModels.Pages;
using EiTRVO.Core.Auth;
using EiTRVO.Core.Auth.Storage;
using EiTRVO.Core.Launcher.Storage;

namespace EiTRVO.Core.App.Tests;

/// <summary>
/// M5 的 D-6 在界面层的那一半：p11 的「添加离线账户」（填玩家名 → 派生 UUID → 落盘 → 出现在列表里）。
/// <para>
/// 2026-10-06 起这条路走的是**独立对话框**（用户反馈：离线账户要有自己的增加入口，
/// 而不是账户列表底下悬一条输入框）。用例因此先开框、再填名字、再按「添加」——
/// 也就是用户真实走的那三步。
/// </para>
/// <para>
/// 这里刻意用**真实的** <see cref="FileAccountCatalog"/> + 临时数据目录，而不是替身：
/// 这条链的价值就在"输入的名字真的变成了磁盘上的一个账户，而且 UUID 与 Java 版一致"，
/// 用替身测只会证明"ViewModel 调用了接口"。
/// </para>
/// </summary>
[TestClass]
public sealed class OfflineAccountTests
{
    private string _tempRoot = string.Empty;
    private LauncherPaths _paths = null!;
    private FileAccountCatalog _catalog = null!;

    /// <summary>每个用例一个全新的数据目录。</summary>
    [TestInitialize]
    public void CreateTempRoot()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "eitrvo-core-offline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _paths = new LauncherPaths(_tempRoot);
        _catalog = new FileAccountCatalog(
            new AccountStore(_paths, new RecordingDiagnosticsLog()),
            new SystemClock(),
            new RecordingDiagnosticsLog(),
            new UnusedAuthChain(),
            new UnusedDeviceCodeClient());
    }

    /// <summary>清理临时目录。</summary>
    [TestCleanup]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private AccountsViewModel NewPage() => TestViewModels.Accounts(_catalog);

    /// <summary>走一遍真实的三步：打开「添加离线账户」→ 得到对话框。 </summary>
    /// <param name="page">账户页。</param>
    /// <returns>那个对话框 ViewModel（外壳会把它画在遮罩上）。</returns>
    private static OfflineAccountDialogViewModel OpenDialog(AccountsViewModel page)
    {
        Assert.IsFalse(page.HasDialog, "没点之前不该有模态层。");

        page.OpenAddOfflineCommand.Execute(null);

        Assert.IsTrue(page.HasDialog, "点了「添加离线账户」就该出现模态层。");
        Assert.IsInstanceOfType<OfflineAccountDialogViewModel>(page.Dialog);
        return (OfflineAccountDialogViewModel)page.Dialog!;
    }

    [TestMethod]
    public void AddingAnOfflineAccountPutsItInTheListAndSelectsIt()
    {
        var page = NewPage();
        var dialog = OpenDialog(page);

        dialog.PlayerName = "Alex_Offline";

        Assert.IsTrue(dialog.ConfirmCommand.CanExecute(null));

        dialog.ConfirmCommand.Execute(null);

        // 加成功就该把框关掉（用户接下来要看的是列表里的新行）。
        Assert.IsFalse(page.HasDialog);

        // 列表里真的有了一行，并且它就是当前选中项（用户刚建好，接下来要用的就是它）。
        var account = page.Accounts.Single();
        Assert.AreEqual("Alex_Offline", account.PlayerName);
        Assert.AreEqual(AccountKind.Offline, account.Kind);
        Assert.AreEqual("Alex_Offline", page.SelectedAccount?.PlayerName);
        Assert.IsTrue(page.SelectedAccount!.IsDefault);

        // UUID 是 Java 兼容的那个值，而且界面行里显示的就是它。
        Assert.AreEqual("f284a681-7831-35a6-847e-1234112049bf", account.Uuid);
        StringAssert.Contains(account.Meta, "f284a681-7831-35a6-847e-1234112049bf");
        StringAssert.Contains(account.Meta, "离线模式");

        // 框里的输入被清空、副标题的计数跟着变。
        Assert.AreEqual(string.Empty, dialog.PlayerName);
        Assert.AreEqual(string.Empty, dialog.Error);
        Assert.AreEqual("1 个已登录账户", page.SectionSubtitle);
    }

    [TestMethod]
    public void TheSkinBlockFollowsTheSelectedAccount()
    {
        // 「选中即切换」是这个项目的既定规矩（实例列表早就这么做）；账户页同理：
        // 换一行选中，皮肤区块里的玩家名必须跟着换。
        var page = NewPage();
        var dialog = OpenDialog(page);
        dialog.PlayerName = "Alex_Offline";
        dialog.ConfirmCommand.Execute(null);

        Assert.AreEqual("Alex_Offline", page.Skin.PlayerName);
    }

    /// <summary>
    /// 用户点的那一条：**非默认的离线账户必须能被选中、并被设为启动账户**。
    /// </summary>
    /// <remarks>
    /// 之前账户列表的行是 `IsEnabled="False"` 的死行，除了「设为默认」没有别的办法切账户；
    /// 而"离线账户也是可选的账户（只是不经过 Xbox 服务器）"要求的正是这条。
    /// </remarks>
    [TestMethod]
    public void ANonDefaultOfflineAccountCanBeSelectedAndBecomesTheLaunchAccount()
    {
        var page = NewPage();

        var first = OpenDialog(page);
        first.PlayerName = "Alex_Offline";
        first.ConfirmCommand.Execute(null);

        var second = OpenDialog(page);
        second.PlayerName = "Steve_2";
        second.ConfirmCommand.Execute(null);

        // 后建的（正版流程同理）是默认账户，所以列表里现在有两行、当前是 Steve_2。
        Assert.AreEqual(2, page.Accounts.Count);
        Assert.AreEqual("Steve_2", page.SelectedAccount?.PlayerName);

        // 选中那一个离线账户（界面上就是点列表里那一行）→ 皮肤区块与「设为默认」都跟着它走。
        page.SelectedAccount = page.Accounts.Single(account => account.PlayerName == "Alex_Offline");

        Assert.AreEqual("Alex_Offline", page.SelectedAccount?.PlayerName);
        Assert.IsTrue(page.CanSetDefault, "它不是默认账户，所以「设为默认」该亮起来。");

        page.SetDefaultCommand.Execute(null);

        // 启动用的就是它了 —— 全程没有碰过 Xbox 服务器：离线身份只有名字与派生 UUID。
        Assert.AreEqual("Alex_Offline", _catalog.GetDefaultAccount()?.PlayerName);
    }

    [TestMethod]
    public void AnIllegalNameIsReportedWhileTypingAndBlocksTheButton()
    {
        var page = NewPage();
        var dialog = OpenDialog(page);

        // 空输入：不算错误（用户还没开始打字），但按钮是灰的。
        dialog.PlayerName = string.Empty;
        Assert.AreEqual(string.Empty, dialog.Error);
        Assert.IsFalse(dialog.HasError);
        Assert.IsFalse(dialog.ConfirmCommand.CanExecute(null));

        dialog.PlayerName = "ab";
        Assert.AreEqual("玩家名太短（至少 3 位）。", dialog.Error);
        Assert.IsTrue(dialog.HasError);
        Assert.IsFalse(dialog.ConfirmCommand.CanExecute(null));

        dialog.PlayerName = "Player_1234567890";
        Assert.AreEqual("玩家名太长（最多 16 位）。", dialog.Error);

        dialog.PlayerName = "玩家一号";
        Assert.AreEqual("玩家名只能包含字母、数字与下划线。", dialog.Error);

        dialog.PlayerName = "Alex_Offline";
        Assert.AreEqual(string.Empty, dialog.Error, "改对了就不该留着上一条提示。");
        Assert.IsTrue(dialog.ConfirmCommand.CanExecute(null));

        Assert.AreEqual(0, page.Accounts.Count, "不合规的输入从来没有被提交过。");
    }

    [TestMethod]
    public void ADuplicateOfflineNameIsReportedAndNotStored()
    {
        var page = NewPage();
        var dialog = OpenDialog(page);
        dialog.PlayerName = "Alex_Offline";
        dialog.ConfirmCommand.Execute(null);
        Assert.AreEqual(1, page.Accounts.Count);

        // 重名要**当场**被拦住，而且框不关（用户要能改了重试）。
        var again = OpenDialog(page);
        again.PlayerName = "alex_offline";
        again.ConfirmCommand.Execute(null);

        Assert.AreEqual("该玩家名的离线账号已存在。", again.Error);
        Assert.IsTrue(page.HasDialog, "失败时框要留着，用户才能改了重试。");
        Assert.AreEqual(1, page.Accounts.Count, "同名 = 同一个 UUID = 同一个账号，不能出现两行。");
        Assert.AreEqual("alex_offline", again.PlayerName, "失败时不要清空输入（用户要能改了重试）。");
    }

    [TestMethod]
    public void AddingRefusesToRunOnAnUnreadableAccountFileAndKeepsTheFile()
    {
        // 内核在"读不出来"时拒绝写入（不覆盖可能还有救的文件）；界面必须把这件事说出来。
        var original = "{ this file used to hold accounts";
        File.WriteAllText(_paths.AccountsFile, original, new UTF8Encoding(false));

        var page = NewPage();
        var dialog = OpenDialog(page);
        dialog.PlayerName = "Alex_Offline";
        dialog.ConfirmCommand.Execute(null);

        Assert.AreEqual("账户文件读不出来，已取消这次写入（原文件未改动）。", dialog.Error);
        Assert.AreEqual(0, page.Accounts.Count);
        Assert.AreEqual(original, File.ReadAllText(_paths.AccountsFile, Encoding.UTF8), "原文件必须原样保留。");
    }

    [TestMethod]
    public void SetDefaultMovesTheFlagAndTheButtonTurnsOff()
    {
        var page = NewPage();

        var first = OpenDialog(page);
        first.PlayerName = "Alex_Offline";
        first.ConfirmCommand.Execute(null);

        var second = OpenDialog(page);
        second.PlayerName = "Steve_2";
        second.ConfirmCommand.Execute(null);

        // 新建的账户是默认账户，所以现在选中的这一行「设为默认」是灰的。
        Assert.AreEqual("Steve_2", page.SelectedAccount?.PlayerName);
        Assert.IsFalse(page.CanSetDefault);
        Assert.IsFalse(page.SetDefaultCommand.CanExecute(null));

        page.SelectedAccount = page.Accounts.Single(account => account.PlayerName == "Alex_Offline");

        Assert.IsTrue(page.CanSetDefault);

        page.SetDefaultCommand.Execute(null);

        Assert.IsTrue(page.SelectedAccount!.IsDefault);
        Assert.IsFalse(page.Accounts.Single(account => account.PlayerName == "Steve_2").IsDefault);
        Assert.AreEqual("Alex_Offline", _catalog.GetDefaultAccount()?.PlayerName, "改默认要真的落盘。");
    }

    [TestMethod]
    public void AccountsSurviveARestart()
    {
        var page = NewPage();
        var dialog = OpenDialog(page);
        dialog.PlayerName = "Alex_Offline";
        dialog.ConfirmCommand.Execute(null);

        // 换一个全新的内核与页面实例 = 下一次启动。
        var reopened = new FileAccountCatalog(
            new AccountStore(_paths, new RecordingDiagnosticsLog()),
            new SystemClock(),
            new RecordingDiagnosticsLog(),
            new UnusedAuthChain(),
            new UnusedDeviceCodeClient());

        var next = TestViewModels.Accounts(reopened);

        Assert.AreEqual(1, next.Accounts.Count);
        Assert.AreEqual("Alex_Offline", next.SelectedAccount?.PlayerName);
        Assert.AreEqual("f284a681-7831-35a6-847e-1234112049bf", next.SelectedAccount!.Uuid);
    }

    [TestMethod]
    public async Task TheCredentialHandedToTheLauncherIsTheOfflineOne()
    {
        // M4 会读它：这条断言把 D-6 与 M4 之间的接口钉住。
        var page = NewPage();
        var dialog = OpenDialog(page);
        dialog.PlayerName = "Alex_Offline";
        dialog.ConfirmCommand.Execute(null);

        var credential = await _catalog.GetCredentialAsync(null);

        Assert.AreEqual("Alex_Offline", credential.PlayerName);
        Assert.AreEqual("f284a681-7831-35a6-847e-1234112049bf", credential.Uuid);
        Assert.AreEqual("0", credential.AccessToken);
        Assert.AreEqual("mojang", credential.UserType);
    }

    /// <summary>取消：什么都不做，也不落盘。</summary>
    [TestMethod]
    public void CancellingTheDialogChangesNothing()
    {
        var page = NewPage();
        var dialog = OpenDialog(page);

        dialog.PlayerName = "Alex_Offline";
        dialog.CancelCommand.Execute(null);

        Assert.IsFalse(page.HasDialog);
        Assert.AreEqual(0, page.Accounts.Count);
    }

    /// <summary>重新打开时是干净的：不留上一次的名字与错误。</summary>
    [TestMethod]
    public void ReopeningTheDialogStartsWithACleanField()
    {
        var page = NewPage();
        var dialog = OpenDialog(page);

        dialog.PlayerName = "ab";
        Assert.IsTrue(dialog.HasError);

        dialog.CancelCommand.Execute(null);

        var again = OpenDialog(page);

        Assert.AreEqual(string.Empty, again.PlayerName);
        Assert.AreEqual(string.Empty, again.Error);
        Assert.IsFalse(again.HasError);
    }
}
