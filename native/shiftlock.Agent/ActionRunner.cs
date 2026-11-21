using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 見張りの結果（通知と強制の実行の一覧）を順に出す．決まりは pc/guard.ps1 の Send-Notice・Invoke-Enforce と同じ．
/// 通知は「音 → トーストかポップアップ → 読み上げ」．強制の実行は，直前（と，音を鳴らした後）に STOP.txt を見る．
/// 1 件の失敗はログに書いて次へ進む．cancel が立ったら（Agent の「終了」）残りを出さずに戻る．
/// 強制の実行は，Core の Tick が返したもの（本人が同意済みの宣言から出たもの）だけ．ここで新しく作らない．
/// </summary>
public sealed class ActionRunner(INotifier notifier, IPowerControl power, Store store, AgentLog log)
{
    /// <summary>シャットダウンの予約から実行までの秒数（この間に kaijo で中止できる）．</summary>
    private const int ShutdownDelaySeconds = 60;

    public void Run(TickResult result, CancellationToken cancel)
    {
        foreach (var action in result.Actions)
        {
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            try
            {
                if (action.Method == "enforce")
                {
                    RunEnforce(action, result, cancel);
                }
                else
                {
                    Notify(action, result, cancel);
                }
            }
            catch (Exception e)
            {
                log.Write($"action {action.Kind} failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void Notify(TickAction a, TickResult result, CancellationToken cancel)
    {
        // 音 → 通知 → 読み上げ（ポップアップは 20 秒止まるので先に鳴らす）
        var played = Play(a.Sound, result, cancel);
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        if (a.Method == "popup")
        {
            Popup(a);
        }
        else
        {
            try
            {
                notifier.Toast(a.Title, a.Text, silent: played);   // 鳴らせなかったら既定の通知音は残す
            }
            catch (Exception e)
            {
                log.Write($"toast failed, fallback popup: {e.GetType().Name}: {e.Message}");
                Popup(a);
            }
        }
        if (a.Speak && !cancel.IsCancellationRequested)
        {
            try
            {
                notifier.Speak(a.Text.Replace("♡", "", StringComparison.Ordinal));
            }
            catch (Exception e)
            {
                log.Write($"speak failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void Popup(TickAction a)
    {
        try
        {
            notifier.Popup(a.Title, a.Text);
        }
        catch (Exception e)
        {
            log.Write($"popup failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void RunEnforce(TickAction a, TickResult result, CancellationToken cancel)
    {
        var act = a.Action ?? "";
        if (store.StopActive)
        {
            log.Write($"enforce {act} skipped: STOP.txt");
            return;
        }
        Play(a.Sound, result, cancel);
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        if (store.StopActive)
        {
            // 音の間に緊急解除された．実行の直前にもう一度見る
            log.Write($"enforce {act} skipped: STOP.txt (placed during sound)");
            return;
        }
        try
        {
            switch (act)
            {
                case "lock":
                    log.Write("enforce: lock");
                    power.Lock();
                    break;
                case "sleep":
                    log.Write("enforce: sleep");
                    power.Sleep();
                    break;
                case "shutdown":
                    log.Write("enforce: shutdown");
                    power.ScheduleShutdown(ShutdownDelaySeconds);
                    if (store.StopActive)
                    {
                        // 最後に STOP.txt を見た直後に緊急解除が入った．予約してしまった分をすぐ取り消す
                        log.Write("enforce shutdown aborted: STOP.txt (placed while scheduling)");
                        power.AbortShutdown();
                    }
                    break;
                default:
                    log.Write($"enforce: unknown action {act}");
                    break;
            }
        }
        catch (Exception e)
        {
            log.Write($"enforce failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>通知音を鳴らす．鳴らせたら true．音が無い・鳴らせない・例外は false（通知は続ける）．</summary>
    private bool Play(string sound, TickResult result, CancellationToken cancel)
    {
        if (sound.Length == 0)
        {
            return false;
        }
        try
        {
            return notifier.PlaySound(sound, result.SoundVolume, result.SoundMaxSeconds, cancel);
        }
        catch (Exception e)
        {
            log.Write($"sound failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }
}
