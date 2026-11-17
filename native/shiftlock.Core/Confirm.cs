using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 終わった乗務の確認（「出た？休んだ？」）．乗務の翌日に 1 日 1 件，新しい乗務から聞く（pc/shiftlock/confirm.py）．
/// </summary>
public static class Confirm
{
    /// <summary>ログの 1 行が表す分数（store.TICK_MIN）．</summary>
    private const int TickMinutes = 5;

    /// <summary>今日まだ聞いていなければ，いちばん新しい未確認の乗務と残りの件数（confirm.pick_unconfirmed）．</summary>
    public static (Plan? Pick, int Others) PickUnconfirmed(ShiftConfig cfg, Store store, DateTime now)
    {
        var today = Planner.DayKey(DateOnly.FromDateTime(now));
        if (JsonX.Str(cfg.Section("confirm")["mode"]) != "ask" || JsonX.Str(store.ReadJson("state.json")["confirm_last_ask"]) == today)
        {
            return (null, 0);
        }
        var unconfirmed = Unconfirmed(cfg, store, now);
        return unconfirmed.Count > 0 ? (unconfirmed[^1], unconfirmed.Count - 1) : (null, 0);
    }

    /// <summary>通知の文（confirm.confirm_text のユーザー向け）．乗務の時間帯に PC を触っていたら一言付ける．</summary>
    public static string UserText(ShiftConfig cfg, Store store, Messages m, Plan p, DateTime now, int others)
    {
        var hint = HomeActivityMinutes(cfg, store, p) >= cfg.SectionInt("confirm", "home_activity_minutes")
            ? m["CONFIRM_HOME_HINT"]
            : "";
        var more = others > 0 ? m.Format("CONFIRM_MORE", ("n", others)) : "";
        return m.Format("CONFIRM_USER", ("label", LateReport.Label(Day(p))), ("hint", hint), ("more", more));
    }

    /// <summary>聞いたことを残す（confirm.mark_confirm_asked）．聞くのは 1 日 1 件．</summary>
    public static void MarkAsked(Store store, string day, DateTime now)
    {
        var state = store.ReadStateForWrite();
        Store.ShiftForWrite(Store.ShiftsForWrite(state), day)["confirm_asked"] = TimeUtil.Iso(now);
        state["confirm_last_ask"] = Planner.DayKey(DateOnly.FromDateTime(now));
        store.WriteState(state);
    }

    /// <summary>
    /// 終わった乗務で「出た／休んだ」が無いもの（confirm.unconfirmed_shifts）．記録開始日以降，直近 lookback_days．古い順．
    /// もう聞いた乗務は除く．
    /// </summary>
    private static List<Plan> Unconfirmed(ShiftConfig cfg, Store store, DateTime now)
    {
        var cf = cfg.Section("confirm");
        if (store.FirstLogDate() is not { } since)
        {
            return [];
        }
        var introduced = ConfirmSince(store, now);
        if (introduced > since)
        {
            since = introduced;
        }
        var status = store.ShiftStatus();
        var result = new List<Plan>();
        foreach (var p in Planner.Plans(cfg, DateOnly.FromDateTime(now), back: cfg.SectionInt("confirm", "lookback_days"), fwd: 0))
        {
            var d = Day(p);
            if (d < since || ShiftEnd(cfg, d) > now)
            {
                continue;
            }
            var me = status[p.Day] as JsonObject;
            var st = JsonX.Str(me?["status"]);
            if (st is "yasumi" or "worked" || (st == "okita" && JsonX.Truthy(cf["okita_means_worked"])))
            {
                continue;
            }
            if (JsonX.Truthy(me?["confirm_asked"]))
            {
                continue;
            }
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// 未確認の扱いを始めた日（confirm.confirm_since）．無ければ前日を state.json に書く．
    /// それより前の乗務は従来どおり「出た」扱い（導入前の日が全部「出た？」にならないように）．
    /// </summary>
    private static DateOnly ConfirmSince(Store store, DateTime now)
    {
        if (ParseDay(JsonX.Str(store.ReadJson("state.json")["confirm_since"])) is { } since)
        {
            return since;
        }
        var yesterday = DateOnly.FromDateTime(now).AddDays(-1);
        var state = store.ReadStateForWrite();
        state["confirm_since"] = Planner.DayKey(yesterday);
        store.WriteState(state);
        return yesterday;
    }

    /// <summary>乗務の時間帯（出発目安〜翌朝 shift_end_earliest）に PC を触っていた分数（confirm.home_activity_minutes）．目安．</summary>
    private static int HomeActivityMinutes(ShiftConfig cfg, Store store, Plan p) =>
        store.LogRead("pc", p.DepartTarget, ShiftEnd(cfg, Day(p)))
            .Count(r => JsonX.Truthy(r.Rec["game"]) || (r.Rec.ContainsKey("idle") ? Py.Int(r.Rec["idle"]) ?? int.MaxValue : 0) < TickMinutes * 60)
        * TickMinutes;

    /// <summary>乗務日 d の乗務が終わったとみなす時刻（翌朝の shift_end_earliest）．</summary>
    private static DateTime ShiftEnd(ShiftConfig cfg, DateOnly d) =>
        TimeUtil.At(d.AddDays(1), JsonX.Str(cfg.Raw["shift_end_earliest"]) ?? "07:00");

    private static DateOnly Day(Plan p) => DateOnly.ParseExact(p.Day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDay(string? s) =>
        DateOnly.TryParseExact(s, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
