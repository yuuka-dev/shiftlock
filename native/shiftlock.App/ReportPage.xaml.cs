using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shiftlock.Core;

namespace Shiftlock.App;

/// <summary>
/// 「休み・報告」のページ: 終わった乗務の事後報告（出た・休んだ・休んだ（有給））と，次の乗務の「今日休む」「やっぱ出る」．
/// shl yasumi／deru と同じ Core の処理（LateReport.Report・Commands.YasumiNext・UndoNext）を呼ぶだけで，判断は持たない．
/// 事後報告の押し間違いは「取り消す」（Records.Undo）で戻す．
/// </summary>
public sealed partial class ReportPage : UserControl
{
    /// <summary>ボタンを押してから次に押せるようになるまでの最短の時間（二度押しで同じ記録を 2 回書かないため）．</summary>
    private static readonly TimeSpan PressHold = TimeSpan.FromMilliseconds(700);

    private Messages _m = Messages.Load(null);
    private Store? _store;

    /// <summary>記録・取り消しの処理中か．</summary>
    private bool _busy;

    /// <summary>このページがこの起動中に最後に書いた事後報告（「取り消す」の相手）．</summary>
    private UndoToken? _undo;

    /// <summary>いま並べている乗務日（同じなら行を作り直さない．フォーカスが飛ぶため）．</summary>
    private List<string>? _days;

    public ReportPage()
    {
        InitializeComponent();
    }

    /// <summary>shifts.json の場所．押したときに読み直す（開いたままの古い設定で判断しない）．</summary>
    public string ShiftsJson { get; set; } = "";

    /// <summary>記録を書いたあと（ウィンドウに全体の読み直しを頼む）．</summary>
    public event Action? Changed;

    /// <summary>
    /// 設定を読めたときの表示．返り値は未確認の乗務の件数（メニューの印に使う）．
    /// 記録のファイルをいま読めなかったら null（前の表示のまま）．
    /// </summary>
    public int? Show(ShiftConfig cfg, Store store, DateTime now)
    {
        _m = cfg.Msg;
        _store = store;
        Body.Visibility = Visibility.Visible;
        ListTitle.Text = _m["APP_REPORT_TITLE"];
        NextTitle.Text = _m["APP_NEXT_TITLE"];
        NextHint.Text = _m["APP_NEXT_HINT"];
        YasumiButton.Content = _m["APP_BTN_YASUMI_NEXT"];
        YasumiPaidButton.Content = _m["APP_BTN_YASUMI_NEXT_PAID"];
        DeruButton.Content = _m["APP_BTN_DERU"];
        UndoButton.Content = _m["APP_BTN_UNDO"];

        int? count = null;
        try
        {
            // 新しい乗務から並べる（聞いた乗務も含む．shl kakunin と同じ集まり）
            var days = Confirm.Unconfirmed(cfg, store, now, includeAsked: true).Select(p => p.Day).Reverse().ToList();
            count = days.Count;
            if (_days is null || !_days.SequenceEqual(days))
            {
                _days = days;
                Rows.Children.Clear();
                foreach (var day in days)
                {
                    Rows.Children.Add(Row(day));
                }
                if (days.Count == 0)
                {
                    Rows.Children.Add(new TextBlock
                    {
                        Text = _m["CONFIRM_KAKUNIN_NONE"],
                        Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
                    });
                }
            }
            ListHint.Text = _m["APP_REPORT_HINT"];
            ListHint.Visibility = days.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            // 記録のファイルをいま読めなかった．前の行のまま，次の読み直しでやり直す
        }
        ShowButtons(now);
        return count;
    }

    /// <summary>設定が無い・読めないとき（知らせはウィンドウが出す）．</summary>
    public void Clear()
    {
        _store = null;
        _days = null;
        Rows.Children.Clear();
        Body.Visibility = Visibility.Collapsed;
    }

    /// <summary>1 件ぶん: 左に乗務日，右に 出た・休んだ・休んだ（有給）．</summary>
    private Grid Row(string day)
    {
        var label = Snapshot.DateLabel(DateOnly.ParseExact(day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Style = Resource<Style>("BodyStrongTextBlockStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Add("APP_BTN_WORKED", "worked", false);
        Add("APP_BTN_YASUMI", "yasumi", false);
        Add("APP_BTN_YASUMI_PAID", "yasumi", true);
        return row;

        void Add(string key, string kind, bool paid)
        {
            var b = new Button { Content = _m[key], IsEnabled = !_busy };
            AutomationProperties.SetName(b, label + " " + _m[key]);
            b.Click += (_, _) => Report(day, kind, paid);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(b, row.ColumnDefinitions.Count - 1);
            row.Children.Add(b);
        }
    }

    // ---- ボタン ----

    /// <summary>事後報告．記録できたら「取り消す」の相手として覚える（LateReport.Report が state.json の undo に残したもの）．</summary>
    private void Report(string day, string kind, bool paid) => Run((cfg, store, now) =>
    {
        var (ok, text) = LateReport.Report(cfg, store, cfg.Msg, now, kind, day, via: Records.Via, paid: paid ? true : null);
        return (text, ok ? new UndoToken(TimeUtil.Iso(now), kind) : null);
    });

    private void OnYasumi(object sender, RoutedEventArgs e) => Next(paid: false);

    private void OnYasumiPaid(object sender, RoutedEventArgs e) => Next(paid: true);

    private void Next(bool paid) => Run((cfg, store, now) =>
        (Commands.YasumiNext(cfg, store, cfg.Msg, now, paid, Records.Via), null));

    private void OnDeru(object sender, RoutedEventArgs e) => Run((cfg, store, now) =>
        (Commands.UndoNext(cfg, store, cfg.Msg, now), null));

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo is { } token)
        {
            // 覚えている報告だけを戻す（押すまでの間にほかの書き手が書いた記録は戻さない）
            Run((cfg, store, now) => (Records.Undo(store, cfg.Msg, now, token).Message, null));
        }
    }

    /// <summary>
    /// 記録・取り消しを 1 つ実行する．終わるまで（少なくとも PressHold の間）ボタンを全部押せなくし，
    /// 返事を結果の行に出して読み直しを頼む．
    /// </summary>
    private async void Run(Func<ShiftConfig, Store, DateTime, (string Text, UndoToken? Undo)> work)
    {
        if (_busy || _store is not { } store)
        {
            return;
        }
        _busy = true;
        ShowButtons(DateTime.Now);
        var hold = Task.Delay(PressHold);
        var m = _m;
        var path = ShiftsJson;
        try
        {
            try
            {
                var (text, undo) = await Task.Run(() =>
                {
                    var cfg = ShiftConfig.Load(path);
                    var r = work(cfg, store, DateTime.Now);
                    // 画面には先頭の "[shiftlock] " を付けない
                    return (r.Text.Replace(cfg.Msg["PREFIX"], "", StringComparison.Ordinal), r.Undo);
                });
                ResultText.Text = text;
                _undo = undo;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ConfigException)
            {
                // 押した瞬間に設定が消えた・壊れた．読み直しがその状態を出す
            }
            catch (Exception)
            {
                // 黙って何も起きなかったように見せない
                ResultText.Text = m["APP_STATE_BUSY"];
                _undo = null;
            }
            Changed?.Invoke();
            await hold;
        }
        finally
        {
            _busy = false;
            ShowButtons(DateTime.Now);
        }
    }

    /// <summary>押せるか・出すかを今の状態に合わせる．</summary>
    private void ShowButtons(DateTime now)
    {
        var enabled = !_busy && _store is not null;
        YasumiButton.IsEnabled = YasumiPaidButton.IsEnabled = DeruButton.IsEnabled = UndoButton.IsEnabled = enabled;
        foreach (var b in Rows.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<Button>()))
        {
            b.IsEnabled = enabled;
        }

        // 「取り消す」は，state.json の undo がこのページの書いた報告のままで 60 分以内の間だけ
        // （state.json が読めなかっただけのとき = null は，出したままにして次の読み直しで確かめる）
        if (_undo is { } token && _store is { } store && Records.CanUndo(store, token, now) == false)
        {
            _undo = null;
        }
        UndoButton.Visibility = _undo is null ? Visibility.Collapsed : Visibility.Visible;
        ResultRow.Visibility = ResultText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
