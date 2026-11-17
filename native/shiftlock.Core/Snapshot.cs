using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>予定の 1 日ぶん．Why は例外日の理由（無ければ null）．IsException は手動かカレンダーの例外日か．</summary>
public sealed record DayCell(DateOnly Date, bool Work, string? Why, bool IsException);

/// <summary>
/// 画面に出す値．CountdownKey は文面 APP_COUNTDOWN のキー（残り時間を出さないときは null），CountdownTo はその相手の時刻．
/// NextPlan は「次の乗務」（無ければ null）．RecordStatus・SleepAt・Kitaku はその乗務の記録．
/// </summary>
public sealed record StatusSnapshot(
    DateTime Now,
    string Phase,
    string? CountdownKey,
    DateTime? CountdownTo,
    Plan? NextPlan,
    string? RecordStatus,
    DateTime? SleepAt,
    DateTime? Kitaku,
    IReadOnlyList<DayCell> Schedule);

/// <summary>
/// 画面に出す値をまとめる入口（設定・状態・時刻 → 表示用の値）．画面（App）はこれを表示するだけにして，判断を持たない．
/// </summary>
public static class Snapshot
{
    /// <summary>予定に出す日数（今日から）．</summary>
    public const int ScheduleDays = 14;

    /// <summary>
    /// いまの状態をまとめる．「次の乗務」の選び方は Python 版の commands.status_text と同じ:
    /// フェーズの乗務 → 記録の対象（24 時間以内）→ これから来る最初の乗務（14 日以内）．
    /// </summary>
    public static StatusSnapshot Build(ShiftConfig cfg, Store store, DateTime now)
    {
        var status = store.ShiftStatus();
        var today = DateOnly.FromDateTime(now);
        var (phase, cur) = Planner.Current(cfg, status, now);
        var next = cur
            ?? Planner.TargetPlan(cfg, now)
            ?? Planner.Plans(cfg, today, back: 0).FirstOrDefault(p => p.StartLatest > now);
        var (key, to) = Countdown(phase, cur, now);
        var rec = next is null ? null : status[next.Day] as JsonObject;

        var schedule = new List<DayCell>(ScheduleDays);
        for (var i = 0; i < ScheduleDays; i++)
        {
            var d = today.AddDays(i);
            var ds = Planner.DayKey(d);
            var work = Planner.IsWorkday(cfg, d);
            var why = (work ? cfg.DaysWork : cfg.DaysOff).GetValueOrDefault(ds);
            schedule.Add(new DayCell(d, work, string.IsNullOrEmpty(why) ? null : why,
                cfg.DaysOff.ContainsKey(ds) || cfg.DaysWork.ContainsKey(ds)));
        }

        return new StatusSnapshot(now, phase, key, to, next,
            JsonX.Str(rec?["status"]),
            TimeUtil.ParseIso(JsonX.Str(rec?["sleep_at"])),
            TimeUtil.ParseIso(JsonX.Str(rec?["kitaku"])),
            schedule);
    }

    /// <summary>
    /// 画面の上から順の文: 現在時刻，フェーズ，残り時間（あれば），次の乗務（見出し・就寝の時刻・出発・出庫・記録）．
    /// 乗務が無ければ見出しの代わりに「14 日以内に乗務はない」．
    /// </summary>
    public static IReadOnlyList<string> Lines(StatusSnapshot s, Messages m)
    {
        var lines = new List<string>
        {
            $"{DateLabel(DateOnly.FromDateTime(s.Now))} {TimeUtil.Fmt(s.Now)}",
            m.Map("APP_PHASE").TryGetValue(s.Phase, out var phase) ? phase : s.Phase,
        };
        if (s.CountdownKey is { } key && s.CountdownTo is { } to && m.Map("APP_COUNTDOWN").TryGetValue(key, out var countdown))
        {
            lines.Add(Messages.Fill(countdown, ("remain", TimeUtil.FmtDur(to - s.Now, m))));
        }
        if (s.NextPlan is not { } p)
        {
            lines.Add(m["APP_NO_SHIFT"]);
            return lines;
        }
        lines.Add(m.Format("APP_NEXT_SHIFT", ("day", DateLabel(DateOnly.FromDateTime(p.StartLatest)))));
        lines.Add(m.Format("APP_TIMES_SLEEP", ("lastcall", TimeUtil.Fmt(p.Lastcall)), ("soft", TimeUtil.Fmt(p.SoftClose)),
            ("hard", TimeUtil.Fmt(p.HardClose))));
        lines.Add(m.Format("APP_TIMES_DEPART", ("target", TimeUtil.Fmt(p.DepartTarget)), ("latest", TimeUtil.Fmt(p.DepartLatest))));
        lines.Add(m.Format("APP_TIMES_START", ("target", TimeUtil.Fmt(p.StartTarget)), ("latest", TimeUtil.Fmt(p.StartLatest))));

        var parts = new List<string>();
        if (s.RecordStatus is { } status && m.Map("APP_RECORD_STATUS").TryGetValue(status, out var statusText))
        {
            parts.Add(statusText);
        }
        if (s.SleepAt is { } sleepAt)
        {
            parts.Add(m.Format("APP_RECORD_SLEEP_AT", ("when", TimeUtil.Fmt(sleepAt))));
        }
        if (s.Kitaku is { } kitaku)
        {
            parts.Add(m.Format("APP_RECORD_KITAKU", ("when", TimeUtil.Fmt(kitaku))));
        }
        lines.Add(m.Format("APP_RECORD", ("text", parts.Count > 0 ? string.Join(m["APP_RECORD_SEP"], parts) : m["APP_RECORD_NONE"])));
        return lines;
    }

    /// <summary>予定の 1 日ぶんの文（"12/01(火) 乗務"）．理由があれば添え，理由の無い例外日には印を付ける．</summary>
    public static string DayLabel(DayCell c, Messages m)
    {
        var extra = c.Why is not null ? m.Format("DAYS_NOTE", ("why", c.Why))
            : c.IsException ? m["DAYS_EXCEPTION_MARK"]
            : "";
        return $"{DateLabel(c.Date)} {m[c.Work ? "APP_DAY_WORK" : "APP_DAY_OFF"]}{extra}";
    }

    /// <summary>"12/01(火)"．日付の表記は文面ではなくここで決める（Python 版の timeutil と同じ扱い）．</summary>
    public static string DateLabel(DateOnly d) =>
        $"{d.Month:00}/{d.Day:00}({TimeUtil.WdJa[((int)d.DayOfWeek + 6) % 7]})";

    /// <summary>
    /// 「〜まで あと …」の相手．相手の時刻を過ぎている・ちょうどのときは出さない（負の残り時間を出さない）．
    /// 決まりは pc/tools/export_native.py の app_countdown と同じ（テストベクタで確かめる）．
    /// </summary>
    private static (string? Key, DateTime? To) Countdown(string phase, Plan? p, DateTime now)
    {
        if (p is null)
        {
            return (null, null);
        }
        (string Key, DateTime To)? pair = phase switch
        {
            "lastcall" => ("lastcall", p.SoftClose),
            "soft" => ("soft", p.HardClose),
            "hard" => ("hard", p.DepartLatest),
            "go" => now < p.DepartTarget ? ("go", p.DepartTarget) : ("go_late", p.DepartLatest),
            "go_last" => ("go_last", p.StartLatest),
            _ => null,
        };
        return pair is { } c && c.To > now ? (c.Key, c.To) : (null, null);
    }
}
