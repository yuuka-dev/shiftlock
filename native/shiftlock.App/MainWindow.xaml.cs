using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Shiftlock.App;

/// <summary>ただ 1 つのウィンドウ．中身は MainWindow.xaml．</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>ウィンドウの大きさ（拡大率 100% のときのピクセル）．</summary>
    private const int WidthDip = 520;
    private const int HeightDip = 560;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 作業フォルダがどこでも見つかるように，実行ファイルの場所から引く
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // AppWindow.Resize は物理ピクセルなので，画面の拡大率を掛ける
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));
    }

    /// <summary>最小化を戻して前に出す．UI スレッドから呼ぶ．</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
        // Activate だけでは他のアプリの後ろに残ることがある．前面にする許可は 2 つ目の起動が渡してくる（Program.Main）
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int SetForegroundWindow(IntPtr hwnd);
}
