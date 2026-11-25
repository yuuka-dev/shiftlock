using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shiftlock.Core;

namespace Shiftlock.App;

/// <summary>
/// 「予定」のページ: 今日から 28 日の出／休の表と，その日だけの例外日（休みにする・乗務にする・例外を取り消す）．
/// shl day off|work|rm と同じ Core の処理（LateReport.DaysSet）を呼ぶだけで，判断は持たない．
/// </summary>
public sealed partial class SchedulePage : UserControl
{
    /// <summary>表に出す日数（今日から）．</summary>
    private const int Days = 28;

    /// <summary>1 行に並べる日数．</summary>
    private const int Columns = 7;

    /// <summary>メモの長さの上限（表やツールチップが崩れない程度）．</summary>
    private const int MemoMax = 60;

    private Messages _m = Messages.Load(null);
    private Store? _store;

    /// <summary>例外日を書いている途中か．</summary>
    private bool _busy;

    /// <summary>小窓が開いているか（開いている間は表を作り直さない．入力中のメモが消えるため）．</summary>
    private bool _editing;

    /// <summary>小窓が開いている間に来た読み直し（閉じたら反映する）．</summary>
    private (ShiftConfig Cfg, Store Store, DateTime Now)? _pending;

    public SchedulePage()
    {
        InitializeComponent();
    }

    /// <summary>例外日を書いたあと（ウィンドウに全体の読み直しを頼む）．</summary>
    public event Action? Changed;

    /// <summary>設定を読めたときの表示．</summary>
    public void Show(ShiftConfig cfg, Store store, DateTime now)
    {
        if (_editing)
        {
            _pending = (cfg, store, now);
            return;
        }
        _m = cfg.Msg;
        _store = store;
        Body.Visibility = Visibility.Visible;
        TitleText.Text = _m["APP_SCHEDULE_LONG"];
        HintText.Text = _m["APP_SCHEDULE_HINT"];

        DaysGrid.Children.Clear();
        var cells = Snapshot.Cells(cfg, DateOnly.FromDateTime(now), Days);
        foreach (var (cell, i) in cells.Select((cell, i) => (cell, i)))
        {
            var view = CellButton(cell);
            Grid.SetRow(view, i / Columns);
            Grid.SetColumn(view, i % Columns);
            DaysGrid.Children.Add(view);
        }
    }

    /// <summary>設定が無い・読めないとき（知らせはウィンドウが出す）．</summary>
    public void Clear()
    {
        _pending = null;
        _store = null;
        Body.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 1 日ぶん: 上から 日付，乗務／休み（理由の無い例外日には印），理由（あれば）．押すと例外日を変える小窓が出る．
    /// はみ出す理由はツールチップと小窓で読める．
    /// </summary>
    private Button CellButton(DayCell cell)
    {
        var full = Snapshot.DayLabel(cell, _m);
        var date = Snapshot.DateLabel(cell.Date);
        var label = _m[cell.Work ? "APP_DAY_WORK" : "APP_DAY_OFF"]
            + (cell.Why is null && cell.IsException ? _m["DAYS_EXCEPTION_MARK"] : "");
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
        if (cell.Why is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = cell.Why,
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }

        var button = new Button
        {
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, full);
        ToolTipService.SetToolTip(button, full);

        var flyout = new Flyout();
        flyout.Opening += (_, _) =>
        {
            _editing = true;
            flyout.Content = Editor(cell, full, flyout);
        };
        flyout.Closed += (_, _) =>
        {
            _editing = false;
            if (_pending is { } p)
            {
                _pending = null;
                Show(p.Cfg, p.Store, p.Now);
            }
        };
        button.Flyout = flyout;
        return button;
    }

    /// <summary>
    /// 小窓の中身: その日の文，メモ，ボタン．ボタンは同じ見た目で並べる（どれかを勧めない）．
    /// 「例外を取り消す」は手動の例外日（days.json）があるときだけ出す．
    /// </summary>
    private StackPanel Editor(DayCell cell, string full, Flyout flyout)
    {
        (string Kind, string Note)? manual = null;
        try
        {
            manual = _store is null ? null : LateReport.ManualDay(_store, cell.Date);
        }
        catch (Exception)
        {
            // days.json をいま読めなかった．取り消しは出さない（書くときにもう一度確かめる）
        }

        var panel = new StackPanel { Spacing = 8, Width = 280 };
        panel.Children.Add(new TextBlock
        {
            Text = full,
            Style = Resource<Style>("BodyStrongTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (cell.IsException && manual is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _m["APP_DAY_FROM_CALENDAR"],
                Style = Resource<Style>("CaptionTextBlockStyle"),
                Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        var memo = new TextBox
        {
            PlaceholderText = _m["APP_DAY_MEMO"],
            Text = manual?.Note ?? "",
            MaxLength = MemoMax,
        };
        panel.Children.Add(memo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Action("APP_BTN_DAY_OFF", "off"));
        buttons.Children.Add(Action("APP_BTN_DAY_WORK", "work"));
        panel.Children.Add(buttons);
        if (manual is not null)
        {
            panel.Children.Add(Action("APP_BTN_DAY_RM", null));
        }
        return panel;

        Button Action(string key, string? kind)
        {
            var b = new Button { Content = _m[key] };
            b.Click += (_, _) =>
            {
                var note = memo.Text.Trim();
                flyout.Hide();
                Apply(cell.Date, kind, note);
            };
            return b;
        }
    }

    /// <summary>例外日を書く（kind は "off"・"work"・null＝手動の例外を消す）．終わったら返事を出して読み直しを頼む．</summary>
    private async void Apply(DateOnly day, string? kind, string note)
    {
        if (_busy || _store is not { } store)
        {
            return;
        }
        _busy = true;
        var m = _m;
        try
        {
            string text;
            try
            {
                await Task.Run(() => LateReport.DaysSet(store, day, kind, kind is null ? "" : note));
                var label = Snapshot.DateLabel(day);
                text = kind is null
                    ? m.Format("APP_DAY_RM_DONE", ("day", label))
                    : m.Format("APP_DAY_SET", ("day", label), ("kind", m[kind == "work" ? "APP_DAY_WORK" : "APP_DAY_OFF"]));
            }
            catch (Exception)
            {
                // 黙って何も起きなかったように見せない
                text = m["APP_STATE_BUSY"];
            }
            ResultText.Text = text;
            ResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
        }
        Changed?.Invoke();
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
