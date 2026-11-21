using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>ログオン時の自動起動の登録先（テストで差し替える）．</summary>
public interface IAutoStart
{
    /// <summary>今の登録（無ければ null）．</summary>
    string? Current();

    /// <summary>登録する（在れば置き換える）．</summary>
    void Set(string command);

    /// <summary>登録を外す（無ければ何もしない）．</summary>
    void Remove();
}

/// <summary>Python 版の見張りのタスク（shiftlock-guard）の状態と切り替え（テストで差し替える）．</summary>
public interface IGuardTaskControl
{
    GuardTaskState State();

    /// <summary>有効・無効を切り替える．できなければ例外．</summary>
    void SetEnabled(bool enabled);
}

/// <summary>切り替えが触るもの．StopAgent は動いていた Agent を終わらせたら true（動いていなければ false）．</summary>
public sealed record SetupDeps(IAutoStart AutoStart, IGuardTaskControl Guard, Action RegisterToast, Action UnregisterToast,
    Action<string> StartAgent, Func<bool> StopAgent, Action<string> Output);

/// <summary>
/// Python 版の見張りから Agent への切り替えと，戻す操作（shiftlock-agent.exe --install / --uninstall）．
/// 管理者権限は要らない．shiftlock-report のタスクには触らない．結果は 1 行ずつ Output に出す．
/// 大事なのは「Agent と Python 版のタスクが両方見張る」状態と「どちらも見張らない」状態を黙って残さないこと．
/// </summary>
public sealed class Setup(SetupDeps deps, Messages m, string taskName)
{
    /// <summary>自動起動に登録するコマンド（空白のあるパスでも動くよう引用符で囲む）．</summary>
    public static string Command(string exePath) => $"\"{exePath}\"";

    /// <summary>
    /// 自動起動を登録 → トーストの名前を登録 → タスクを無効化 → Agent を起動．成功 0，失敗 1．
    /// タスクを無効にできなければ自動起動の登録を取り消す（次のログオンから二重に見張らないように）．タスクが無ければ飛ばす．
    /// </summary>
    public int Install(string exePath)
    {
        try
        {
            deps.AutoStart.Set(Command(exePath));
            deps.RegisterToast();
        }
        catch (Exception e)
        {
            deps.Output(m.Format("AGENT_SETUP_FAILED", ("err", e.Message)));
            Try(deps.AutoStart.Remove);
            return 1;
        }
        deps.Output(m["AGENT_SETUP_AUTOSTART_ON"]);

        if (!Switch(enable: false))
        {
            Try(deps.AutoStart.Remove);
            deps.Output(m["AGENT_SETUP_ROLLED_BACK"]);
            return 1;
        }
        try
        {
            deps.StartAgent(exePath);
        }
        catch (Exception e)
        {
            // 登録は済んでいるので次のログオンからは動く．今は動いていない
            deps.Output(m.Format("AGENT_SETUP_FAILED", ("err", e.Message)));
            return 1;
        }
        deps.Output(m["AGENT_SETUP_STARTED"]);
        return 0;
    }

    /// <summary>
    /// 動いている Agent を終わらせる → 登録を外す → タスクを有効に戻す．成功 0，失敗 1．
    /// タスクを有効に戻せなければ失敗（何も見張っていない状態なので，必ず知らせる）．タスクが無ければ飛ばす．
    /// </summary>
    public int Uninstall()
    {
        var ok = true;
        try
        {
            if (deps.StopAgent())
            {
                deps.Output(m["AGENT_SETUP_STOPPED"]);
            }
            deps.AutoStart.Remove();
            deps.UnregisterToast();
            deps.Output(m["AGENT_SETUP_AUTOSTART_OFF"]);
        }
        catch (Exception e)
        {
            // 止められない・外せないものがあっても，タスクを戻すところまでは進む（Agent はタスクが有効なら見張らない）
            deps.Output(m.Format("AGENT_SETUP_FAILED", ("err", e.Message)));
            ok = false;
        }
        return Switch(enable: true) && ok ? 0 : 1;
    }

    /// <summary>タスクを切り替えて，切り替わったことを確かめる．タスクが無ければ何もせず true．</summary>
    private bool Switch(bool enable)
    {
        var want = enable ? GuardTaskState.Enabled : GuardTaskState.Disabled;
        try
        {
            var state = deps.Guard.State();
            if (state == GuardTaskState.Missing)
            {
                deps.Output(m.Format("AGENT_SETUP_TASK_MISSING", ("task", taskName)));
                return true;
            }
            if (state != want)
            {
                deps.Guard.SetEnabled(enable);
                state = deps.Guard.State();
            }
            if (state != want)
            {
                deps.Output(m.Format("AGENT_SETUP_TASK_FAILED", ("task", taskName), ("err", state.ToString())));
                return false;
            }
        }
        catch (Exception e)
        {
            deps.Output(m.Format("AGENT_SETUP_TASK_FAILED", ("task", taskName), ("err", e.Message)));
            return false;
        }
        deps.Output(m.Format(enable ? "AGENT_SETUP_TASK_ENABLED" : "AGENT_SETUP_TASK_DISABLED", ("task", taskName)));
        return true;
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 取り消しの失敗は，元の失敗の知らせを優先する
        }
    }
}
