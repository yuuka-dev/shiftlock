using System.Diagnostics;

namespace Shiftlock.Agent;

/// <summary>
/// 動いている Agent を外から終わらせるための合図（--uninstall と，発行物を置き直す deploy.ps1 が使う）．
/// 名前付きのイベントを立てると，Agent は「終了」を選んだときと同じ手順で終わる（アイコンを消し，出している通知を打ち切る）．
/// </summary>
public static class AgentControl
{
    /// <summary>二重起動を見分ける名前（ログオンしているセッションごと）．</summary>
    public const string MutexName = @"Local\shiftlock-agent";

    private const string QuitEventName = @"Local\shiftlock-agent-quit";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Agent の側: 合図が来たら onQuit を呼ぶ（スレッドプールのスレッドで）．戻り値は Dispose で待つのをやめる．</summary>
    public static IDisposable ListenForQuit(Action onQuit)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, timedOut) =>
        {
            if (!timedOut)
            {
                onQuit();
            }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return new Listener(signal, wait);
    }

    /// <summary>
    /// 動いている Agent を終わらせる．動いていなければ false．合図で終わらなければプロセスを止める．
    /// 終わったら true，止められなければ例外．
    /// </summary>
    public static bool Stop()
    {
        if (!IsRunning())
        {
            return false;
        }
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var signal))
        {
            using (signal)
            {
                signal.Set();
            }
            if (SpinWait.SpinUntil(() => !IsRunning(), StopTimeout))
            {
                return true;
            }
        }
        // 合図を聞いていない（古い版・固まっている）．プロセスを止める
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id != me.Id && p.SessionId == me.SessionId)
                {
                    p.Kill();
                    p.WaitForExit((int)StopTimeout.TotalMilliseconds);
                }
            }
        }
        if (IsRunning())
        {
            throw new InvalidOperationException("agent is still running");
        }
        return true;
    }

    /// <summary>このセッションで Agent が動いているか（二重起動を防ぐミューテックスが在るか）．</summary>
    public static bool IsRunning()
    {
        if (Mutex.TryOpenExisting(MutexName, out var mutex))
        {
            mutex.Dispose();
            return true;
        }
        return false;
    }

    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle wait) : IDisposable
    {
        public void Dispose()
        {
            wait.Unregister(null);
            signal.Dispose();
        }
    }
}
