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

    public void Sleep() => Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false);

    public void ScheduleShutdown(int seconds) =>
        RunShutdown($"/s /t {seconds} /c \"{ShutdownComment}\"", wait: false);

    /// <summary>予約済みのシャットダウンを中止する．予約が無いとき shutdown.exe は失敗を返すが，それは気にしない．</summary>
    public void AbortShutdown() => RunShutdown("/a", wait: true);

    private static void RunShutdown(string arguments, bool wait)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("shutdown.exe を起動できない");
        if (wait)
        {
            p.WaitForExit(5000);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
