using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 通知領域のアイコンとメニュー（状態を開く・緊急解除・終了）．見た目とホバーの 1 行は TrayState が決め，ここは描くだけ．
/// 強制モードの宣言・再開・設定は置かない（置くのは解除だけ．同意は Python 版の対話入力だけ）．
/// UI のスレッドで作り，UI のスレッドから使う（ほかのスレッドからは RequestRefresh）．
/// </summary>
public sealed class Tray : IDisposable
{
    /// <summary>ウィンドウの実行ファイル．</summary>
    private const string AppExe = "shiftlock.exe";

    private const string Title = "shiftlock";

    private readonly string _dir;
    private readonly Watcher _watcher;
    private readonly Func<DateTime> _clock;
    private readonly INotifier _notifier;
    private readonly ReleaseFlow _release;
    private readonly AgentLog _log;
    private readonly Action _quit;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _releaseItem;
    private readonly ToolStripMenuItem _exit;
    private readonly System.Windows.Forms.Timer _minute;
    private readonly Dictionary<TrayLook, Icon> _icons = [];
    private Messages _m = Messages.Load(null);
    private bool _disposed;

    /// <summary>quit は「終了」が選ばれたときに呼ぶ（出している途中の通知を打ち切って，メッセージの輪を止める）．</summary>
    public Tray(string dir, Watcher watcher, Func<DateTime> clock, INotifier notifier, ReleaseFlow release, AgentLog log, Action quit)
    {
        _dir = dir;
        _watcher = watcher;
        _clock = clock;
        _notifier = notifier;
        _release = release;
        _log = log;
        _quit = quit;

        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        _ui = SynchronizationContext.Current!;

        _open = new ToolStripMenuItem { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        _open.Click += (_, _) => OpenApp();
        _releaseItem = new ToolStripMenuItem();
        _releaseItem.Click += (_, _) => AskRelease();
        _exit = new ToolStripMenuItem();
        _exit.Click += (_, _) => _quit();

        // メニューは常に 3 つとも有効
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_open, _releaseItem, _exit]);

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenApp();
            }
        };

        _minute = new System.Windows.Forms.Timer { Interval = 60_000 };
        _minute.Tick += (_, _) => Refresh();
        _minute.Start();
        Refresh();
    }

    /// <summary>見た目を更新してほしい（どのスレッドからでも．見張りの後に呼ぶ）．</summary>
    public void RequestRefresh() => _ui.Post(_ => Refresh(), null);

    /// <summary>設定と状態を読み直して，アイコン・ホバー・メニューの文を更新する（UI のスレッド）．</summary>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            StatusSnapshot? snapshot = null;
            var error = _watcher.LastError;
            var (cfg, _, configError) = Watcher.LoadConfig(_dir);
            if (cfg is null)
            {
                error = configError;
            }
            else
            {
                _m = cfg.Msg;
                snapshot = Snapshot.Build(cfg, new Store(_dir), _clock());
            }
            var (look, tip) = TrayState.From(_watcher.LastOutcome, error, snapshot, _m);
            _icon.Icon = IconFor(look);
            _icon.Text = tip;
            _open.Text = _m["AGENT_MENU_OPEN"];
            _releaseItem.Text = _m["AGENT_MENU_RELEASE"];
            _exit.Text = _m["AGENT_MENU_EXIT"];
        }
        catch (Exception e)
        {
            // 設定をほかのプロセスが書いている最中など．前の表示のままにして次の回に任せる
            _log.Write($"tray refresh failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ウィンドウの実行ファイルの場所: Agent と同じフォルダ，無ければ 1 つ上（置いた形は &lt;場所&gt;\shiftlock.exe と
    /// &lt;場所&gt;\agent\shiftlock-agent.exe．native/deploy.ps1）．どちらにも無ければ，同じフォルダのパスを返す（知らせる文に出す）．
    /// </summary>
    public static string FindApp(string agentDir)
    {
        var here = Path.Combine(agentDir, AppExe);
        if (File.Exists(here))
        {
            return here;
        }
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(agentDir));
        var above = parent is null ? null : Path.Combine(parent, AppExe);
        return above is not null && File.Exists(above) ? above : here;
    }

    /// <summary>状態を開く: shiftlock.exe を起動する（起動済みならウィンドウの側が前に出す）．</summary>
    private void OpenApp()
    {
        var exe = FindApp(AppContext.BaseDirectory);
        try
        {
            if (File.Exists(exe))
            {
                using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
                return;
            }
        }
        catch (Exception e)
        {
            _log.Write($"open failed: {e.GetType().Name}: {e.Message}");
        }
        Tell(_m.Format("AGENT_OPEN_FAILED", ("path", exe)));
    }

    /// <summary>緊急解除: はい／いいえ（既定は「いいえ」）で確かめてから解除し，結果を 1 回知らせる．</summary>
    private void AskRelease()
    {
        var m = _m;
        var answer = MessageBox.Show(m["AGENT_RELEASE_ASK"], m["AGENT_RELEASE_TITLE"], MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, MessageBoxOptions.DefaultDesktopOnly);
        if (answer != DialogResult.Yes)
        {
            return;
        }
        // ファイルの書き込みと shutdown /a を UI のスレッドで待たない
        Task.Run(() =>
        {
            var text = _release.Run(m);
            Tell(text);
            RequestRefresh();
        });
    }

    /// <summary>見張りとは別に 1 件知らせる（解除の報告など）．トーストが出せなければポップアップ．</summary>
    private void Tell(string text) => Task.Run(() =>
    {
        try
        {
            _notifier.Toast(Title, text, silent: false);
        }
        catch (Exception e)
        {
            _log.Write($"toast failed, fallback popup: {e.GetType().Name}: {e.Message}");
            try
            {
                _notifier.Popup(Title, text);
            }
            catch (Exception e2)
            {
                _log.Write($"popup failed: {e2.GetType().Name}: {e2.Message}");
            }
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _minute.Dispose();
        _icon.Visible = false;   // 消しておかないと，マウスを乗せるまでアイコンが残る
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private Icon IconFor(TrayLook look)
    {
        if (!_icons.TryGetValue(look, out var icon))
        {
            icon = Draw(look);
            _icons[look] = icon;
        }
        return icon;
    }

    /// <summary>
    /// アイコンを描く（絵のファイルは持たない）．色だけに頼らないよう，丸の中の印も変える:
    /// 平常は無地，注意は横棒，警告は「!」，設定エラーは「×」，見張りを止めているときは縦棒 2 本．
    /// </summary>
    private static Icon Draw(TrayLook look)
    {
        var fill = look switch
        {
            TrayLook.Normal => Color.FromArgb(0x2E, 0x9E, 0x6B),
            TrayLook.Caution => Color.FromArgb(0xE0, 0x9A, 0x10),
            TrayLook.Warning => Color.FromArgb(0xD0, 0x30, 0x30),
            _ => Color.FromArgb(0x80, 0x80, 0x80),
        };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(Color.White, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillEllipse(brush, 1, 1, 30, 30);
            switch (look)
            {
                case TrayLook.Caution:
                    g.DrawLine(pen, 9, 16, 23, 16);
                    break;
                case TrayLook.Warning:
                    g.DrawLine(pen, 16, 7, 16, 18);
                    g.DrawLine(pen, 16, 24, 16, 24.5f);
                    break;
                case TrayLook.ConfigError:
                    g.DrawLine(pen, 10, 10, 22, 22);
                    g.DrawLine(pen, 22, 10, 10, 22);
                    break;
                case TrayLook.Paused:
                    g.DrawLine(pen, 12, 9, 12, 23);
                    g.DrawLine(pen, 20, 9, 20, 23);
                    break;
            }
        }
        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
