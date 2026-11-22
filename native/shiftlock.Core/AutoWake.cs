using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// PC の無操作や「ねる」の記録から，寝ていたことを控えめに推定して自動で「起きた」にする（pc/shiftlock/autowake.py）．
/// </summary>
public static class AutoWake
{
    /// <summary>
    /// 乗務日 p の出庫前に，最低睡眠ぶんまとまって寝ていた区間があれば返す（autowake.slept_before．控えめに数える）．
    /// - 前日が乗務なら，出発〜帰宅の最遅時刻（home_by_latest）は外出扱いで数えない
    /// - no_sleep（訪問など）の時間帯は数えない
    /// - Claude Code にプロンプトを送っていた時刻があれば，そこで区切る
    /// </summary>
    public static (DateTime S, DateTime E)? SleptBefore(ShiftConfig cfg, Store store, Plan p, DateTime now)
    {
        var mode = Mode(cfg);
        if (mode == "off")
        {
            return null;
        }
        var need = TimeSpan.FromHours(cfg.AutoWakeHours);
        var d = DateOnly.ParseExact(p.Day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
        var day0 = d.ToDateTime(TimeOnly.MinValue);
        var status = store.ShiftStatus();
        var me = status[p.Day] as JsonObject ?? new JsonObject();
        var gaps = Usage.PcGaps(store, now) ?? [];
        var cuts = Planner.NoSleepIntervals(cfg, d.AddDays(-1), d);
        var prompts = store.LogRead("claude", day0.AddHours(-6), now.AddMinutes(1))
            .Where(r => JsonX.Str(r.Rec["k"]) == "p")
            .Select(r => r.T)
            .ToList();
        cuts.AddRange(prompts.Select(t => (t, t.AddMinutes(1))));

        // (区間, その区間に効かせるカット)
        var cands = new List<((DateTime S, DateTime E) Gap, List<(DateTime Start, DateTime End)> Cuts)>();
        if (TimeUtil.ParseIso(JsonX.Str(me["sleep_at"])) is { } sleepAt)
        {
            // 「ねる」の記録あり → そこから，PC を触る／Claude を使うまで（どちらも無ければ now）
            var end = now;
            foreach (var (gs, ge) in gaps)
            {
                if (gs <= sleepAt.AddMinutes(20) && ge >= sleepAt && ge < end)
                {
                    end = ge;
                }
            }
            foreach (var t in prompts)
            {
                if (t > sleepAt.AddMinutes(2) && t < end)
                {
                    end = t;
                }
            }
            cands.Add(((sleepAt, end), cuts));   // 「ねる」を打った時点で家にいるので外出のカットは要らない
        }
        if (mode == "auto" && gaps.Count > 0)
        {
            var prev = d.AddDays(-1);
            var gapCuts = new List<(DateTime Start, DateTime End)>(cuts);
            if (Planner.IsWorkday(cfg, prev) && JsonX.Str((status[Planner.DayKey(prev)] as JsonObject)?["status"]) != "yasumi")
            {
                var home = TimeUtil.ParseIso(JsonX.Str(me["kitaku"])) ?? cfg.HomeByLatest(d);
                gapCuts.Add((Planner.MakePlan(cfg, prev).DepartTarget, home));
            }
            cands.AddRange(gaps.Select(g => (g, gapCuts)));
        }

        (DateTime S, DateTime E)? best = null;
        foreach (var (gap, cc) in cands)
        {
            foreach (var (s0, e0) in TimeUtil.Subtract(gap, cc))
            {
                var s = Max(s0, day0.AddHours(-3));
                var e = Min(Min(e0, now), p.DepartLatest);
                if (e - s >= need && (best is not { } b || e - s > b.E - b.S))
                {
                    best = (s, e);
                }
            }
        }
        return best;
    }

    /// <summary>
    /// 直近の乗務で，もう寝ていたことが記録から分かれば自動で「起きた」にする（autowake.maybe_auto_okita）．
    /// 返り値はお知らせの文（何もしなければ空）．
    /// </summary>
    public static string MaybeAutoOkita(ShiftConfig cfg, Store store, Messages m, DateTime now)
    {
        var p = Planner.TargetPlan(cfg, now);
        if (p is null || now < DateOnly.ParseExact(p.Day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue))
        {
            return "";
        }
        if (JsonX.Truthy((store.ShiftStatus()[p.Day] as JsonObject)?["status"]))
        {
            return "";
        }
        if (SleptBefore(cfg, store, p, now) is not { } slept)
        {
            return "";
        }
        store.SetShiftStatus(p.Day, "okita", now, $"auto {TimeUtil.Fmt(slept.S)}-{TimeUtil.Fmt(slept.E)}");
        return m.Format("AUTOWAKE_OKITA", ("start", TimeUtil.Fmt(slept.S)), ("end", TimeUtil.Fmt(slept.E)),
            ("dur", TimeUtil.FmtDur(slept.E - slept.S, m)), ("depart", TimeUtil.Fmt(p.DepartTarget)),
            ("latest", TimeUtil.Fmt(p.DepartLatest)));
    }

    /// <summary>auto_wake: true/"auto" は PC の無操作でも判定，"neru" は「ねる」の記録があるときだけ，false はしない．</summary>
    private static string Mode(ShiftConfig cfg)
    {
        var v = cfg.Raw.TryGetPropertyValue("auto_wake", out var node) ? node : JsonValue.Create(true);
        return v?.GetValueKind() switch
        {
            JsonValueKind.True => "auto",
            JsonValueKind.False => "off",
            _ => JsonX.Str(v) ?? "",
        };
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
