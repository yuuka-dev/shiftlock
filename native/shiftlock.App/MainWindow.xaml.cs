using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shiftlock.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace Shiftlock.App;

/// <summary>
/// ただ 1 つのウィンドウ．左のメニューでページを切り替える: 「いま」（いまの状態・記録のボタン 4 つ・取り消す・予定）と
/// 「予定」（28 日の表と例外日．SchedulePage）．
/// ここに書くのは表示・ボタン・読み直しだけで，判断は Core（Snapshot・Records）に置く．
/// 強制モードの宣言・解除・設定は置かない．
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>ウィンドウの大きさ（拡大率 100% のときのピクセル）．</summary>
    private const int WidthDip = 700;
    private const int HeightDip = 640;

    /// <summary>予定の 1 行に並べる日数．</summary>
    private const int ScheduleColumns = 7;

    /// <summary>ボタンを押してから次に押せるようになるまでの最短の時間（二度押しで同じ記録を 2 行書かないため）．</summary>
    private static readonly TimeSpan PressHold = TimeSpan.FromMilliseconds(700);

    private readonly string _shiftsJson = Path.Combine(AppPaths.DataDir, "shifts.json");
    private readonly Store _store = new(AppPaths.DataDir);
    private readonly Refresher _refresher;

    /// <summary>いまの文面．設定が読めるまで・読めないときは既定（標準語）．</summary>
    private Messages _m = Messages.Load(null);

    /// <summary>設定が読めているか（読めないときはボタン 4 つを押せなくする）．</summary>
    private bool _configOk;

    /// <summary>記録・取り消しの処理中か．</summary>
    private bool _busy;

    /// <summary>このアプリがこの起動中に最後に書いた記録（「取り消す」の相手）．</summary>
    private UndoToken? _undo;

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

        Nav.SelectedItem = NavNow;
        SchedulePage.Changed += Reload;

        _refresher = new Refresher(AppPaths.DataDir, DispatcherQueue);
        _refresher.Changed += Reload;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                Reload();
            }
        };
        Closed += (_, _) => _refresher.Dispose();

        Reload();
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

    // ---- 読み直し ----

    /// <summary>
    /// 設定と状態を読み直して表示する．設定が無い・読めないときはそれを知らせてボタンを押せなくする．
    /// それ以外の失敗（書き込みの途中に読んだ，など）は前の表示を残し，次の機会にやり直す．
    /// </summary>
    private void Reload()
    {
        var now = DateTime.Now;
        try
        {
            var cfg = ShiftConfig.Load(_shiftsJson);
            var snapshot = Snapshot.Build(cfg, _store, now);
            _m = cfg.Msg;
            _configOk = true;
            ShowStatus(snapshot);
            SchedulePage.Show(cfg, _store, now);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            ShowConfigError("APP_NO_CONFIG", ("path", _shiftsJson));
        }
        catch (ConfigException e)
        {
            ShowConfigError("APP_BAD_CONFIG", ("err", e.Message));
        }
        catch (Exception)
        {
            // 前の表示のまま
        }
        ShowButtons(now);
    }

    private void ShowStatus(StatusSnapshot s)
    {
        ConfigError.IsOpen = false;
        StatusPanel.Visibility = ShiftCard.Visibility = ScheduleCard.Visibility = Visibility.Visible;

        // Lines の並び: 現在時刻，フェーズ，（残り時間），次の乗務の 5 行か「乗務はない」の 1 行
        var lines = Snapshot.Lines(s, _m);
        var shiftLines = s.NextPlan is null ? 1 : 5;
        var hasCountdown = lines.Count > 2 + shiftLines;
        NowText.Text = lines[0];
        PhaseText.Text = lines[1];
        CountdownText.Text = hasCountdown ? lines[2] : "";
        CountdownText.Visibility = hasCountdown ? Visibility.Visible : Visibility.Collapsed;

        ShiftLines.Children.Clear();
        foreach (var (line, i) in lines.Skip(lines.Count - shiftLines).Select((line, i) => (line, i)))
        {
            ShiftLines.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Style = Resource<Style>(i == 0 ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"),
            });
        }

        ScheduleTitle.Text = _m["APP_SCHEDULE"];
        ScheduleGrid.Children.Clear();
        foreach (var (cell, i) in s.Schedule.Select((cell, i) => (cell, i)))
        {
            var view = ScheduleCell(cell);
            Grid.SetRow(view, i / ScheduleColumns);
            Grid.SetColumn(view, i % ScheduleColumns);
            ScheduleGrid.Children.Add(view);
        }
    }

    /// <summary>予定の 1 日ぶん: 上に日付，下に 乗務／休み（理由や印つき）．はみ出す分はツールチップで読める．</summary>
    private FrameworkElement ScheduleCell(DayCell cell)
    {
        var full = Snapshot.DayLabel(cell, _m);
        var date = Snapshot.DateLabel(cell.Date);
        var label = full.StartsWith(date + " ", StringComparison.Ordinal) ? full[(date.Length + 1)..] : full;
        var secondary = Resource<Brush>("TextFillColorSecondaryBrush");

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = date,
            Style = Resource<Style>("CaptionTextBlockStyle"),
            Foreground = secondary,
        });
        var text = new TextBlock
        {
            Text = label,
            Style = Resource<Style>("BodyTextBlockStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        if (cell.Work)
        {
            text.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            text.Foreground = secondary;
        }
        panel.Children.Add(text);
        ToolTipService.SetToolTip(panel, full);
        return panel;
    }

    private void ShowConfigError(string key, params (string Name, object? Value)[] args)
    {
        _m = Messages.Load(null);
        _configOk = false;
        ConfigError.Message = _m.Format(key, args);
        ConfigError.IsOpen = true;
        StatusPanel.Visibility = ShiftCard.Visibility = ScheduleCard.Visibility = Visibility.Collapsed;
        SchedulePage.Clear();
    }

    /// <summary>左のメニューで選んだページだけを出す．</summary>
    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var schedule = ReferenceEquals(args.SelectedItem, NavSchedule);
        NowView.Visibility = schedule ? Visibility.Collapsed : Visibility.Visible;
        SchedulePage.Visibility = schedule ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>ボタンの名前と，押せるか・出すかを今の状態に合わせる．</summary>
    private void ShowButtons(DateTime now)
    {
        NavNow.Content = _m["APP_NAV_NOW"];
        NavSchedule.Content = _m["APP_NAV_SCHEDULE"];
        NeruButton.Content = _m["APP_BTN_NERU"];
        OkitaButton.Content = _m["APP_BTN_OKITA"];
        ShuppatsuButton.Content = _m["APP_BTN_SHUPPATSU"];
        KitakuButton.Content = _m["APP_BTN_KITAKU"];
        UndoButton.Content = _m["APP_BTN_UNDO"];

        var enabled = _configOk && !_busy;
        NeruButton.IsEnabled = OkitaButton.IsEnabled = ShuppatsuButton.IsEnabled = KitakuButton.IsEnabled = enabled;

        // 「取り消す」は，state.json の undo がこのアプリの書いたもののままで 60 分以内の間だけ
        // （state.json が読めなかっただけのとき = null は，出したままにして次の読み直しで確かめる）
        if (_undo is { } token && Records.CanUndo(_store, token, now) == false)
        {
            _undo = null;
        }
        UndoButton.Visibility = _undo is null ? Visibility.Collapsed : Visibility.Visible;
        UndoButton.IsEnabled = !_busy;
        ResultRow.Visibility = ResultText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- ボタン ----

    private void OnNeru(object sender, RoutedEventArgs e) => Record(Records.Neru);

    private void OnOkita(object sender, RoutedEventArgs e) => Record(Records.Okita);

    private void OnShuppatsu(object sender, RoutedEventArgs e) => Record(Records.Shuppatsu);

    private void OnKitaku(object sender, RoutedEventArgs e) => Record(Records.Kitaku);

    /// <summary>記録のボタン．設定は押したときに読み直す（開いたままの古い設定で判断しない）．</summary>
    private void Record(Func<ShiftConfig, Store, Messages, DateTime, RecordResult> op) => Run(() =>
    {
        var cfg = ShiftConfig.Load(_shiftsJson);
        return op(cfg, _store, cfg.Msg, DateTime.Now);
    });

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        var m = _m;
        var token = _undo;
        if (token is not null)
        {
            // 覚えている記録だけを戻す（押すまでの間にほかの書き手が書いた記録は戻さない）
            Run(() => Records.Undo(_store, m, DateTime.Now, token));
        }
    }

    /// <summary>
    /// 記録・取り消しを 1 つ実行する．終わるまで（少なくとも PressHold の間）ボタンを全部押せなくし，
    /// 返事を結果の行に出して読み直す．「取り消す」は記録できた返事にだけ付ける．
    /// </summary>
    private async void Run(Func<RecordResult> work)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        ShowButtons(DateTime.Now);
        var hold = Task.Delay(PressHold);
        try
        {
            RecordResult? result = null;
            try
            {
                result = await Task.Run(work);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ConfigException)
            {
                // 押した瞬間に設定が消えた・壊れた．下の Reload がその状態を出す
            }
            catch (Exception)
            {
                // 想定していない失敗．黙って何も起きなかったように見せない
                ResultText.Text = _m["APP_STATE_BUSY"];
            }
            if (result is not null)
            {
                ResultText.Text = result.Message;
                _undo = result.Recorded ? result.Undo : null;
            }
            Reload();
            await hold;
        }
        finally
        {
            _busy = false;
            ShowButtons(DateTime.Now);
        }
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int SetForegroundWindow(IntPtr hwnd);
}
