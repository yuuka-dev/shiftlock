using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Shiftlock.App;

/// <summary>
/// 入口．同時に動かすのは 1 つだけにする: 2 つ目の起動は 1 つ目に知らせて終わり，1 つ目がウィンドウを前に出す．
/// （XAML が作る Main の代わり．csproj の DISABLE_XAML_GENERATED_MAIN）
/// </summary>
public static class Program
{
    /// <summary>1 つ目の起動を見分ける名前．</summary>
    private const string InstanceKey = "shiftlock";

    private static App? _app;

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var first = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!first.IsCurrent)
        {
            // 1 つ目がウィンドウを前面に出せるよう許可を渡してから知らせる
            AllowSetForegroundWindow(first.ProcessId);
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            // STA の上で待つと戻ってこないことがあるので，別スレッドで知らせて終わるのを待つ
            Task.Run(() => first.RedirectActivationToAsync(args).AsTask()).Wait();
            return 0;
        }

        // 2 つ目の起動が来たとき（UI スレッド以外で呼ばれる）
        first.Activated += (_, _) => _app?.BringToFront();

        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _app = new App();
        });
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern int AllowSetForegroundWindow(uint processId);
}
