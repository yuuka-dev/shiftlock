using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>1 乗務ぶんの時刻（pc/shiftlock/plan.py の Plan）．Day は出庫する日（yyyy-MM-dd）．</summary>
public sealed record Plan(
    string Day,
    DateTime StartTarget,
    DateTime StartLatest,
    DateTime DepartTarget,
    DateTime DepartLatest,
    DateTime SoftClose,
    DateTime HardClose,
    DateTime Lastcall);

/// <summary>
/// 乗務日と時刻（出発目安・就寝目安・最低ライン）の逆算，いまのフェーズの判定（pc/shiftlock/plan.py）．
/// 現在時刻は自分で取らず，引数で受ける．
/// </summary>
public static class Planner
{
    /// <summary>曜日の名前．月曜が 0．</summary>
    public static IReadOnlyList<string> Weekdays => ShiftConfig.WeekdayNames;

    /// <summary>日付の表記（yyyy-MM-dd．Python の date.isoformat()）．</summary>
    public static string DayKey(DateOnly d) => d.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

    /// <summary>乗務日か（plan.is_workday）．休みの例外 → 出勤の例外 → 旧形式 shifts → 曜日 の順に見る．</summary>
    public static bool IsWorkday(ShiftConfig cfg, DateOnly d)
    {
        var ds = DayKey(d);
        if (cfg.DaysOff.ContainsKey(ds))
        {
            return false;
        }
        if (cfg.DaysWork.ContainsKey(ds) || cfg.IsLegacyShiftDay(ds))
        {
            return true;
        }
        return cfg.WorkWeekdays.Contains(WeekdayName(d));
    }

    /// <summary>その日の乗務の時刻を逆算する（plan.make_plan）．その日だけの上書きの形がおかしければ ConfigException．</summary>
    public static Plan MakePlan(ShiftConfig cfg, DateOnly d)
    {
        var day = DayKey(d);
        var s = cfg.TimesFor(day);
        var prep = Span(cfg.PrepMinutes, MicrosPerMinute);
        var lastcall = Span(cfg.LastcallMinutes, MicrosPerMinute);
        var st = TimeUtil.At(d, s.StartTarget ?? cfg.StartTarget);
        var sl = TimeUtil.At(d, s.StartLatest ?? cfg.StartLatest);
        if (sl < st)
        {
            sl = st;
        }
        var departTarget = st - prep;
        var departLatest = sl - prep;
        var soft = LatestBedtime(cfg, departTarget, s.SleepTargetHours ?? cfg.SleepTargetHours);
        var hard = LatestBedtime(cfg, departLatest, s.SleepMinHours ?? cfg.SleepMinHours);
        if (hard < soft)
        {
            hard = soft;
        }
        return new Plan(day, st, sl, departTarget, departLatest, soft, hard, soft - lastcall);
    }

    /// <summary>around を中心に back 日前〜 fwd 日後の乗務日を展開する（plan.plans）．</summary>
    public static IReadOnlyList<Plan> Plans(ShiftConfig cfg, DateOnly around, int back = 40, int fwd = 14)
    {
        var result = new List<Plan>();
        for (var i = -back; i <= fwd; i++)
        {
            var d = around.AddDays(i);
            if (IsWorkday(cfg, d))
            {
                result.Add(MakePlan(cfg, d));
            }
        }
        return result;
    }

    /// <summary>
    /// いまのフェーズと，その乗務（plan.current）．フェーズは "free" "lastcall" "soft" "hard" "go" "go_last"．
    /// shiftStatus は state.json の shifts（乗務日 → 記録）．
    /// </summary>
    public static (string Phase, Plan? Plan) Current(ShiftConfig cfg, JsonObject shiftStatus, DateTime now)
    {
        foreach (var p in Plans(cfg, DateOnly.FromDateTime(now), back: 2, fwd: 2))
        {
            if (!(p.Lastcall <= now && now < p.StartLatest))
            {
                continue;
            }
            var status = JsonX.Str((shiftStatus[p.Day] as JsonObject)?["status"]);
            if (status is "yasumi" or "worked")
            {
                continue;   // 休み / 出発済み → その乗務についてはもう何も言わない
            }
            if (status == "okita")
            {
                if (now >= p.DepartLatest - GoLead)
                {
                    return ("go_last", p);
                }
                if (now >= p.DepartTarget - GoLead)
                {
                    return ("go", p);
                }
                continue;
            }
            if (now < p.SoftClose)
            {
                return ("lastcall", p);
            }
            return (now < p.HardClose ? "soft" : "hard", p);
        }
        return ("free", null);
    }

    /// <summary>記録の対象 = 最終出庫がまだ来ていない直近の乗務（24 時間以内）（plan.target_plan）．無ければ null．</summary>
    public static Plan? TargetPlan(ShiftConfig cfg, DateTime now)
    {
        foreach (var p in Plans(cfg, DateOnly.FromDateTime(now), back: 1, fwd: 2))
        {
            if (now < p.StartLatest && p.StartLatest <= now.AddHours(24))
            {
                return p;
            }
        }
        return null;
    }

    // ---- 逆算の部品 ----

    private const long MicrosPerMinute = 60_000_000;
    private const long MicrosPerHour = 3_600_000_000;

    /// <summary>出発のリマインドを始める早さ（出発の 15 分前から）．</summary>
    private static readonly TimeSpan GoLead = TimeSpan.FromMinutes(15);

    private static string WeekdayName(DateOnly d) => ShiftConfig.WeekdayNames[((int)d.DayOfWeek + 6) % 7];

    /// <summary>Python の timedelta(minutes=x) / timedelta(hours=x) と同じ丸め（マイクロ秒に，偶数丸め）．</summary>
    private static TimeSpan Span(double value, long microsPerUnit) =>
        TimeSpan.FromTicks((long)Math.Round(value * microsPerUnit, MidpointRounding.ToEven) * 10);

    /// <summary>d0〜d1 の各日の寝られない時間帯（plan.no_sleep_intervals）．終わりが始まり以前なら翌日にまたぐ．</summary>
    internal static List<(DateTime Start, DateTime End)> NoSleepIntervals(ShiftConfig cfg, DateOnly d0, DateOnly d1)
    {
        var result = new List<(DateTime, DateTime)>();
        for (var d = d0; d <= d1; d = d.AddDays(1))
        {
            var ds = DayKey(d);
            var wd = WeekdayName(d);
            foreach (var r in cfg.NoSleep)
            {
                if (r.Date != ds && r.Weekday != wd)
                {
                    continue;
                }
                var s = TimeUtil.At(d, r.From);
                var e = TimeUtil.At(d, r.To);
                if (e <= s)
                {
                    e = e.AddDays(1);
                }
                result.Add((s, e));
            }
        }
        return result;
    }

    /// <summary>wake までに，no_sleep を除いて hours 時間寝られる最も遅い就寝時刻（plan.latest_bedtime）．</summary>
    private static DateTime LatestBedtime(ShiftConfig cfg, DateTime wake, double hours)
    {
        var need = Span(hours, MicrosPerHour);
        // 終わりが遅い区間から見る．同じ終わりの区間は書いてある順のまま（Python の sort と同じく安定）
        var blocks = NoSleepIntervals(cfg, DateOnly.FromDateTime(wake.AddDays(-2)), DateOnly.FromDateTime(wake))
            .OrderByDescending(b => b.End);
        var t = wake;
        foreach (var (s, e) in blocks)
        {
            if (s >= t)
            {
                continue;
            }
            if (e < t)
            {
                var free = t - e;
                if (free >= need)
                {
                    return t - need;
                }
                need -= free;
            }
            if (s < t)
            {
                t = s;
            }
        }
        return t - need;
    }

    /// <summary>直近 lookbackDays 日で，出庫最終がもう過ぎた最後の乗務（plan.last_past_plan）．無ければ null．</summary>
    public static Plan? LastPastPlan(ShiftConfig cfg, DateTime now, int lookbackDays) =>
        Plans(cfg, DateOnly.FromDateTime(now), back: lookbackDays, fwd: 0).LastOrDefault(p => p.StartLatest <= now);
}
