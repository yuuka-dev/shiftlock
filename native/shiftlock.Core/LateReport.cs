using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>当日欠勤の目安の数（attendance.absence_stats）．Limit が 0 なら目安なし．</summary>
public sealed record AbsenceCount(int Limit, int Shifts, int Absent, int Paid, bool Over);

/// <summary>
/// 終わった乗務の事後報告（「休んだ」「出た」）と，それが使う部品（pc/shiftlock/records.py の _late_report・
/// resolve_past_day・month_yasumi_count，timeutil.parse_day，days.days_set，attendance.absence_note，
/// confirm.last_asked_unconfirmed）．見張りでは iPhone の記録の取り込みから呼ばれる．
/// </summary>
public static class LateReport
{
    /// <summary>休みの日に「出た」を記録したとき，days.json の出勤の例外日に付ける理由（ファイルに書く値）．</summary>
    private const string WorkNote = "事後報告";

    /// <summary>「出た？休んだ？」と聞いてからこの時間は，日付なしの返事をその乗務のものとして扱う．</summary>
    private static readonly TimeSpan AskedWindow = TimeSpan.FromHours(36);

    /// <summary>
    /// 事後報告を記録する（records._late_report）．kind は "yasumi"（休んだ）か "worked"（出た）．返り値は (記録したか, 表示文)．
    /// when は報告した時刻（iPhone の記録時刻．無ければ now）．取り消し用に直前の状態を state.json の undo に残す．
    /// paid: 休んだに添えた「有給」（true）／「有給なし」（false）．既に休みになっている日なら印の付け外しだけする．
    /// </summary>
    public static (bool Ok, string Text) Report(ShiftConfig cfg, Store store, Messages m, DateTime now, string kind,
        string? dayText = null, DateTime? when = null, string via = "claude", bool? paid = null)
    {
        var at = when ?? now;
        var today = DateOnly.FromDateTime(at);
        var lookback = cfg.SectionInt("confirm", "lookback_days");
        DateOnly? resolved;
        try
        {
            resolved = ResolvePastDay(cfg, store, at, dayText, lookback);
        }
        catch (FormatException e)
        {
            return (false, m.Format("RECORDS_LATE_BAD_DATE", ("err", e.Message)));
        }
        if (resolved is not { } d)
        {
            return (false, m.Format("RECORDS_LATE_NONE", ("days", lookback)));
        }
        var label = Label(d);
        if (d > today)
        {
            return (false, m.Format("RECORDS_LATE_FUTURE", ("label", label)));
        }
        var keep = cfg.SectionInt("retention", "days_keep");
        if (d < today.AddDays(-keep))
        {
            return (false, m.Format("RECORDS_LATE_TOO_OLD", ("label", label),
                ("days", ShiftConfig.PyText(cfg.Section("retention")["days_keep"]))));
        }
        var ds = Planner.DayKey(d);
        if (JsonX.Str((store.ShiftStatus()[ds] as JsonObject)?["status"]) == kind)
        {
            if (kind == "yasumi" && paid is { } on)
            {
                store.SetPaid(ds, on, now, via);
                return (true, m.Format(on ? "RECORDS_PAID_SET" : "RECORDS_PAID_UNSET", ("label", label)) + AbsenceNote(cfg, store, m, now));
            }
            return (false, m.Format("RECORDS_LATE_ALREADY", ("label", label),
                ("what", m[kind == "yasumi" ? "RECORDS_LATE_KIND_YASUMI" : "RECORDS_LATE_KIND_WORKED"])));
        }
        var workAdded = false;
        if (Planner.IsWorkday(cfg, d))
        {
            if (Planner.MakePlan(cfg, d).StartLatest > at)
            {
                return (false, m.Format("RECORDS_LATE_NOT_YET", ("label", label)));
            }
        }
        else if (kind == "yasumi")
        {
            return (false, m.Format("RECORDS_LATE_OFF_DAY", ("label", label)));
        }
        else
        {
            DaysSet(store, d, "work", WorkNote);   // 休みの日に出た → 出勤の例外日にもする（取り消しで戻す）
            workAdded = true;
        }

        var state = store.ReadStateForWrite();
        state["undo"] = new JsonObject
        {
            ["day"] = ds,
            ["prev"] = ((state["shifts"] as JsonObject)?[ds] as JsonObject)?.DeepClone() ?? new JsonObject(),
            ["at"] = TimeUtil.Iso(now),
            ["what"] = new JsonArray(kind),
            ["days_work_added"] = workAdded,
        };
        store.WriteState(state);
        store.SetShiftStatus(ds, kind, now, $"late {via}");
        if (kind != "yasumi")
        {
            return (true, m.Format("RECORDS_LATE_WORKED", ("label", label)));
        }
        if (paid == true)
        {
            store.SetPaid(ds, true, now, via);
        }
        return (true, m.Format("RECORDS_LATE_YASUMI", ("label", label), ("n", MonthYasumiCount(store, now)))
            + (paid == true ? m["RECORDS_YASUMI_PAID"] : "") + AbsenceNote(cfg, store, m, now));
    }

    /// <summary>
    /// 日付の書き方を読む（timeutil.parse_day）: "12/2"・"2026-12-02"・今日・昨日・一昨日・明日．読めなければ FormatException．
    /// past が true（事後報告）: 月日だけで未来になる場合，年始（1〜2 月）に 11〜12 月を書いたときは去年とみなす．
    /// past が false（例外日の登録）: 月日だけで 7 日より前になる場合は来年とみなす．
    /// </summary>
    public static DateOnly ParseDay(string text, DateOnly today, bool past = true)
    {
        var t = text.Trim().Replace('／', '/');
        switch (t)
        {
            case "今日" or "きょう":
                return today;
            case "明日" or "あした":
                return today.AddDays(1);
            case "昨日" or "きのう":
                return today.AddDays(-1);
            case "一昨日" or "おととい":
                return today.AddDays(-2);
        }
        if (t.Contains('-'))
        {
            return DateOnly.TryParseExact(t, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)
                ? iso
                : throw new FormatException($"日付として読めない: {text}");
        }
        var parts = t.Split('/');
        const NumberStyles style = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (parts.Length != 2
            || !int.TryParse(parts[0], style, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[1], style, CultureInfo.InvariantCulture, out var day)
            || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(today.Year, month))
        {
            throw new FormatException($"日付として読めない: {text}");
        }
        var cand = new DateOnly(today.Year, month, day);
        var year = today.Year;
        if (past && cand > today && today.Month <= 2 && month >= 11)
        {
            year--;
        }
        else if (!past && cand < today.AddDays(-7))
        {
            year++;
        }
        if (year == today.Year)
        {
            return cand;
        }
        return day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : throw new FormatException($"日付として読めない: {text}");
    }

    /// <summary>手動の例外日（days.json）を書く（days.days_set）．kind は "off"・"work"・null（手動の例外を消す）．</summary>
    public static void DaysSet(Store store, DateOnly d, string? kind, string note = "")
    {
        var data = store.ReadJson("days.json");
        var off = Dict(data, "off");
        var work = Dict(data, "work");
        var ds = Planner.DayKey(d);
        off.Remove(ds);
        work.Remove(ds);
        if (kind == "off")
        {
            off[ds] = note;
        }
        else if (kind == "work")
        {
            work[ds] = note;
        }
        store.WriteJson("days.json", data);

        static JsonObject Dict(JsonObject data, string key)
        {
            if (data[key] is JsonObject o)
            {
                return o;
            }
            var made = new JsonObject();
            data[key] = made;
            return made;
        }
    }

    /// <summary>
    /// その日の手動の例外日（days.json）．Kind は "off"・"work"，Note はメモ．手動の例外が無ければ null
    /// （カレンダーから来た例外日は入らない）．画面が「例外を取り消す」を出すか決めるのに使う．
    /// </summary>
    public static (string Kind, string Note)? ManualDay(Store store, DateOnly d)
    {
        var data = store.ReadJson("days.json");
        var ds = Planner.DayKey(d);
        foreach (var kind in (string[])["off", "work"])
        {
            if (data[kind] is JsonObject o && o.TryGetPropertyValue(ds, out var note))
            {
                return (kind, JsonX.Str(note) ?? "");
            }
        }
        return null;
    }

    /// <summary>今月（now の月）に休みにした乗務の数（records.month_yasumi_count）．</summary>
    public static int MonthYasumiCount(Store store, DateTime now)
    {
        var ym = now.ToString("yyyy'-'MM", CultureInfo.InvariantCulture);
        return store.ShiftStatus().Count(kv =>
            kv.Key.StartsWith(ym, StringComparison.Ordinal) && JsonX.Str((kv.Value as JsonObject)?["status"]) == "yasumi");
    }

    /// <summary>
    /// 当日欠勤の目安に達していれば注意の一文（attendance.absence_note）．達していなければ空．
    /// 直近 window_shifts 乗務（記録開始日以降）のうち，有給の印が無い休みが warn_at 回以上．
    /// </summary>
    public static string AbsenceNote(ShiftConfig cfg, Store store, Messages m, DateTime now, DateOnly? upto = null)
    {
        var s = AbsenceStats(cfg, store, now, upto);
        return s.Over ? m.Format("ABSENCE_OVER", ("n", s.Absent), ("shifts", s.Shifts), ("limit", s.Limit)) : "";
    }

    /// <summary>
    /// upto（既定は今日）までの直近 window_shifts 乗務日の数（attendance.absence_stats）．記録開始日より前の乗務日は数えない．
    /// Absent は有給の印が無い休み，Paid は有給の印がある休み．Over は Limit が 1 以上で Absent がそれ以上．
    /// </summary>
    public static AbsenceCount AbsenceStats(ShiftConfig cfg, Store store, DateTime now, DateOnly? upto = null)
    {
        var a = cfg.Section("absence");
        var window = Num(a["window_shifts"], 24, 1, 365);
        var limit = Num(a["warn_at"], 5, 0, 100);
        var since = store.FirstLogDate();
        var status = store.ShiftStatus();
        // 週 1 乗務でも窓が埋まるように，乗務日の 7 倍の日数をさかのぼる
        var days = Planner.Plans(cfg, upto ?? DateOnly.FromDateTime(now), back: window * 7 + 14, fwd: 0)
            .Select(p => p.Day)
            .Where(day => since is not { } s || string.CompareOrdinal(day, Planner.DayKey(s)) >= 0)
            .TakeLast(window)
            .ToList();
        var yasumi = days.Select(day => status[day] as JsonObject).Where(v => JsonX.Str(v?["status"]) == "yasumi").ToList();
        var absent = yasumi.Count(v => !JsonX.Truthy(v?["paid"]));
        return new AbsenceCount(limit, days.Count, absent, yasumi.Count - absent, limit > 0 && absent >= limit);

        // 無い・壊れている（文字列・inf・nan）・範囲外のときは既定（任意機能の設定ミスで記録を止めない）
        static int Num(JsonNode? v, int fallback, int lo, int hi) =>
            v is not null && Py.IntOfFloat(v) is { } n && lo <= n && n <= hi ? n : fallback;
    }

    /// <summary>
    /// 直近 36 時間に「出た？休んだ？」と聞いて，まだ返事の無い乗務日（lookbackDays 内．confirm.last_asked_unconfirmed）．
    /// 複数あれば最後に聞いたもの．無ければ null．
    /// </summary>
    public static string? LastAskedUnconfirmed(Store store, DateTime now, int lookbackDays)
    {
        (string Day, string Asked)? best = null;
        var oldest = Planner.DayKey(DateOnly.FromDateTime(now).AddDays(-lookbackDays));
        foreach (var (day, node) in store.ShiftStatus())
        {
            if (node is not JsonObject v || JsonX.Str(v["confirm_asked"]) is not { } asked || JsonX.Truthy(v["status"])
                || TimeUtil.ParseIso(asked) is not { } askedAt)
            {
                continue;
            }
            if (now - askedAt > AskedWindow || string.CompareOrdinal(day, oldest) < 0)
            {
                continue;
            }
            if (best is not { } b || string.CompareOrdinal(asked, b.Asked) > 0)
            {
                best = (day, asked);
            }
        }
        return best?.Day;
    }

    /// <summary>"MM/dd(曜)" の表記．</summary>
    internal static string Label(DateOnly d) =>
        d.ToString("MM'/'dd", CultureInfo.InvariantCulture) + "(" + TimeUtil.WdJa[((int)d.DayOfWeek + 6) % 7] + ")";

    /// <summary>
    /// 事後報告の対象日（records.resolve_past_day）．日付あり → その日．無し → 直近に聞いた未確認の乗務 → 直近の終わった乗務．
    /// 見つからなければ null．日付が読めなければ FormatException．
    /// </summary>
    private static DateOnly? ResolvePastDay(ShiftConfig cfg, Store store, DateTime when, string? dayText, int lookback)
    {
        if (!string.IsNullOrEmpty(dayText))
        {
            return ParseDay(dayText, DateOnly.FromDateTime(when));
        }
        var day = LastAskedUnconfirmed(store, when, lookback) ?? Planner.LastPastPlan(cfg, when, lookback)?.Day;
        return day is null ? null : DateOnly.ParseExact(day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
    }
}
