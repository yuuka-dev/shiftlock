namespace Shiftlock.Core;

/// <summary>1 回の乗務の時刻（Python: Plan）。</summary>
/// <param name="Day">乗務日 YYYY-MM-DD（出庫する日）</param>
public sealed record Plan(
    string Day,
    DateTime StartTarget,
    DateTime StartLatest,
    DateTime DepartTarget,
    DateTime DepartLatest,
    DateTime SoftClose,
    DateTime HardClose,
    DateTime Lastcall);

/// <summary>フェーズの名前。state.json やログに書く値なので文字列のまま持つ。</summary>
public static class Phase
{
    public const string Free = "free";
    public const string Lastcall = "lastcall";
    public const string Soft = "soft";
    public const string Hard = "hard";
    public const string Go = "go";
    public const string GoLast = "go_last";
}

/// <summary>その日の状態（state.json の shifts[日].status）のうち、フェーズ判定で見る値。</summary>
public static class DayStatus
{
    public const string Okita = "okita";
    public const string Yasumi = "yasumi";
    public const string Worked = "worked";
}

/// <summary>
/// 乗務日と時刻（出発目安・就寝目安・最低ライン）の逆算、いまのフェーズの判定。Python 版 plan.py に対応。
/// 仕様は docs/pc-spec.md、一致の確認は docs/test-vectors/plan.json。
/// </summary>
public static class Planner
{
    private static readonly TimeSpan GoLead = TimeSpan.FromMinutes(15);

    /// <summary>d0〜d1 の寝られない時間帯。終わりが始まり以前なら翌日にまたぐ。</summary>
    public static List<(DateTime Start, DateTime End)> NoSleepIntervals(PlanConfig cfg, DateOnly d0, DateOnly d1)
    {
        var res = new List<(DateTime, DateTime)>();
        for (var d = d0; d <= d1; d = d.AddDays(1))
        {
            var iso = TimeUtil.Iso(d);
            var wd = TimeUtil.Weekday(d);
            foreach (var r in cfg.NoSleep)
            {
                var hit = (r.Date is not null && r.Date == iso)
                    || (r.Weekday is not null && Head3(r.Weekday.ToLowerInvariant()) == wd);
                if (!hit)
                    continue;
                var s = TimeUtil.At(d, r.From);
                var e = TimeUtil.At(d, r.To);
                if (e <= s)
                    e = e.AddDays(1);
                res.Add((s, e));
            }
        }
        return res;
    }

    /// <summary>wake までに、寝られない時間帯を除いて hours 時間寝られる最も遅い就寝時刻。</summary>
    public static DateTime LatestBedtime(PlanConfig cfg, DateTime wake, double hours)
    {
        var need = TimeSpan.FromHours(hours);
        var wakeDay = DateOnly.FromDateTime(wake);
        // 終わりが遅い順。同じ終わりは元の順のまま（Python の sort と同じく安定）
        var blocks = NoSleepIntervals(cfg, wakeDay.AddDays(-2), wakeDay).OrderByDescending(b => b.End);
        var t = wake;
        foreach (var (s, e) in blocks)
        {
            if (s >= t)
                continue;
            if (e < t)
            {
                var free = t - e;
                if (free >= need)
                    return t - need;
                need -= free;
            }
            if (s < t)
                t = s;
        }
        return t - need;
    }

    /// <summary>乗務日か。休みの例外 → 出勤の例外 → 旧形式の shifts → 曜日 の順。</summary>
    public static bool IsWorkday(PlanConfig cfg, DateOnly d)
    {
        var iso = TimeUtil.Iso(d);
        if (cfg.DaysOff.Contains(iso))
            return false;
        if (cfg.DaysWork.Contains(iso))
            return true;
        if (cfg.LegacyShiftDays.Contains(iso))
            return true;
        return cfg.WorkWeekdays.Contains(TimeUtil.Weekday(d));
    }

    /// <summary>その日の乗務の時刻を逆算する（乗務日かどうかは見ない）。</summary>
    public static Plan MakePlan(PlanConfig cfg, DateOnly d)
    {
        var iso = TimeUtil.Iso(d);
        var s = cfg.TimesFor(iso);
        var prep = TimeSpan.FromMinutes(cfg.PrepMinutes);
        var lastcall = TimeSpan.FromMinutes(cfg.LastcallMinutes);
        var st = TimeUtil.At(d, s.StartTarget ?? cfg.StartTarget);
        var sl = TimeUtil.At(d, s.StartLatest ?? cfg.StartLatest);
        if (sl < st)
            sl = st;
        var departTarget = st - prep;
        var departLatest = sl - prep;
        var soft = LatestBedtime(cfg, departTarget, s.SleepTargetHours ?? cfg.SleepTargetHours);
        var hard = LatestBedtime(cfg, departLatest, s.SleepMinHours ?? cfg.SleepMinHours);
        if (hard < soft)
            hard = soft;
        return new Plan(iso, st, sl, departTarget, departLatest, soft, hard, soft - lastcall);
    }

    /// <summary>around の back 日前〜 fwd 日後の乗務日を古い順に展開する。</summary>
    public static List<Plan> Plans(PlanConfig cfg, DateOnly around, int back = 40, int fwd = 14)
    {
        var res = new List<Plan>();
        for (var i = -back; i <= fwd; i++)
        {
            var d = around.AddDays(i);
            if (IsWorkday(cfg, d))
                res.Add(MakePlan(cfg, d));
        }
        return res;
    }

    /// <summary>
    /// いまのフェーズと、その対象の乗務。status は 乗務日 → 状態（okita / yasumi / worked。記録が無い日は入れない）。
    /// 休み・出発済みの乗務は飛ばす。起きた後は出発の 15 分前から go / go_last だけ。
    /// </summary>
    public static (string Phase, Plan? Plan) Current(PlanConfig cfg, IReadOnlyDictionary<string, string> status, DateTime now)
    {
        foreach (var p in Plans(cfg, DateOnly.FromDateTime(now), back: 2, fwd: 2))
        {
            if (!(p.Lastcall <= now && now < p.StartLatest))
                continue;
            var s = status.GetValueOrDefault(p.Day);
            if (s is DayStatus.Yasumi or DayStatus.Worked)
                continue;
            if (s == DayStatus.Okita)
            {
                if (now >= p.DepartLatest - GoLead)
                    return (Phase.GoLast, p);
                if (now >= p.DepartTarget - GoLead)
                    return (Phase.Go, p);
                continue;
            }
            if (now < p.SoftClose)
                return (Phase.Lastcall, p);
            if (now < p.HardClose)
                return (Phase.Soft, p);
            return (Phase.Hard, p);
        }
        return (Phase.Free, null);
    }

    /// <summary>休む／起きた の対象 = 最終出庫がまだ来ていない直近の乗務（24 時間以内）。</summary>
    public static Plan? TargetPlan(PlanConfig cfg, DateTime now)
    {
        foreach (var p in Plans(cfg, DateOnly.FromDateTime(now), back: 1, fwd: 2))
        {
            if (now < p.StartLatest && p.StartLatest <= now.AddHours(24))
                return p;
        }
        return null;
    }

    /// <summary>直近 lookbackDays 日で、出庫最終がもう過ぎた最後の乗務。</summary>
    public static Plan? LastPastPlan(PlanConfig cfg, DateTime now, int lookbackDays) =>
        Plans(cfg, DateOnly.FromDateTime(now), back: lookbackDays, fwd: 0).LastOrDefault(p => p.StartLatest <= now);

    private static string Head3(string s) => s.Length <= 3 ? s : s[..3];
}
