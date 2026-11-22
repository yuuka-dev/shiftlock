using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shiftlock.Agent;

/// <summary>PC を止める側の Windows 実装（pc/guard.ps1 の Invoke-Enforce と -Release と同じ操作）．</summary>
public sealed class WindowsPower : IPowerControl
{
    /// <summary>シャットダウンの予告に出る文（guard.ps1 と同じ．cmd に渡すので ASCII）．</summary>
    private const string ShutdownComment = "shiftlock: kyousei mode. cancel = kaijo.cmd (60s)";

    public void Lock()
    {
        if (!LockWorkStation())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>スリープに入れなければ例外（呼ぶ側が agent.log に失敗を書く）．入れたときは復帰してから戻る．</summary>
    public void Sleep()
    {
        if (!Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false))
        {
            throw new InvalidOperationException("SetSuspendState returned false");
        }
    }

    /// <summary>
    /// シャットダウンを予約する．shutdown.exe が断ったら例外（権限やポリシーで予約できなかったのを，予約できたことにしない）．
    /// 既に予約があるとき（1190）は，予約されているので成功として扱う．
    /// </summary>
    public void ScheduleShutdown(int seconds)
    {
        var code = RunShutdown($"/s /t {seconds} /c \"{ShutdownComment}\"");
        if (code is not (null or 0 or ErrorShutdownIsScheduled))
        {
            throw new InvalidOperationException($"shutdown.exe exited with {code}");
        }
    }

    /// <summary>予約済みのシャットダウンを中止する．予約が無いとき shutdown.exe は失敗を返すが，それは気にしない．</summary>
    public void AbortShutdown() => RunShutdown("/a");

    /// <summary>ERROR_SHUTDOWN_IS_SCHEDULED．</summary>
    private const int ErrorShutdownIsScheduled = 1190;

    /// <summary>shutdown.exe を動かして終了コードを返す．5 秒で終わらなければ null（結果は分からない）．</summary>
    private static int? RunShutdown(string arguments)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("shutdown.exe を起動できない");
        return p.WaitForExit(5000) ? p.ExitCode : null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
