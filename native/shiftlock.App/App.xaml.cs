using Microsoft.UI.Xaml;

namespace Shiftlock.App;

/// <summary>アプリ本体．ウィンドウは 1 つだけ（起動は Program.Main から）．</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// ウィンドウを前に出す（2 つ目の起動が来たとき用）．UI スレッド以外から呼んでよい．
    /// ウィンドウがまだ無ければ何もしない．
    /// </summary>
    public void BringToFront()
    {
        var window = _window;
        window?.DispatcherQueue.TryEnqueue(window.BringToFront);
    }
}
