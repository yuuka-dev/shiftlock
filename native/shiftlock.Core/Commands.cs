using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// コマンドの返事の文（pc/shiftlock/commands.py と days.py の schedule_text）．
/// shl.exe の見るだけのコマンド（status・yotei・day list）と，記録のコマンド（okita・neru・itte・yasumi・deru・day off|work|rm．
/// cli.py の record_cmd・day_cmd）が使う．Python 版と同じ文・同じファイルになることをテストベクタ（commands.json）で確かめる．
/// </summary>
public static class Commands
{
    /// <summary>減速・一時停止（limit.LIMIT_ACTIONS）．宣言に「N 時間」が無い動作．</summary>
    private static readonly string[] LimitActions = ["throttle", "suspend"];

    /// <summary>
    /// 『状況』の文（commands.status_text）．いまのフェーズ，次の乗務の時刻，今月の休み，当日欠勤の目安，
    /// カレンダーの同期，未確認の乗務，強制モードの宣言，この先 2 週間の予定．
    /// 未確認の扱いを始めた日が無ければ state.json に書く（Python 版と同じ）．
    /// </summary>
    public static string StatusText(ShiftConfig cfg, Store store, Messages m, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var status = store.ShiftStatus();
        var (phase, cur) = Planner.Current(cfg, status, now);
        var p = cur
            ?? Planner.TargetPlan(cfg, now)
            ?? Planner.Plans(cfg, today, back: 0).FirstOrDefault(x => x.StartLatest > now);

        var lines = new List<string> { m.Format("STATUS_HEAD", ("now", TimeUtil.FmtD(now)), ("phase", phase)) };
        if (p is not null)
        {
            // 記録が無ければ "-"．キーがあれば Python の str(値)
            var st = status[p.Day] is JsonObject rec && rec.TryGetPropertyValue("status", out var node) ? ShiftConfig.PyText(node) : "-";
            lines.Add(m.Format("STATUS_SHIFT", ("day", TimeUtil.FmtMd(p.StartLatest)), ("status", st),
                ("start_target", TimeUtil.Fmt(p.StartTarget)), ("start_latest", TimeUtil.Fmt(p.StartLatest)),
                ("depart_target", TimeUtil.Fmt(p.DepartTarget)), ("depart_latest", TimeUtil.Fmt(p.DepartLatest))));
            lines.Add(m.Format("STATUS_TIMES", ("soft", TimeUtil.FmtD(p.SoftClose)), ("target_h", cfg.SleepTargetHoursText),
                ("hard", TimeUtil.FmtD(p.HardClose)), ("min_h", cfg.SleepMinHoursText), ("lastcall", TimeUtil.Fmt(p.Lastcall))));
        }
        else
        {
            lines.Add(m["STATUS_NO_SHIFT"]);
        }
        lines.Add(m.Format("STATUS_YASUMI_COUNT", ("n", LateReport.MonthYasumiCount(store, now))));

        var ab = LateReport.AbsenceStats(cfg, store, now);
        if (ab.Limit > 0)
        {
            lines.Add(m.Format("STATUS_ABSENCE", ("shifts", ab.Shifts), ("n", ab.Absent), ("paid", ab.Paid), ("limit", ab.Limit),
                ("over", ab.Over ? m["STATUS_ABSENCE_OVER"] : "")));
        }

        lines.AddRange(CalendarLines(cfg, store, m));

        var unconfirmed = Confirm.Unconfirmed(cfg, store, now, includeAsked: true);
        if (unconfirmed.Count > 0)
        {
            lines.Add(m.Format("STATUS_UNCONFIRMED",
                ("days", string.Join(m["STATUS_UNCONFIRMED_SEP"], unconfirmed.Select(x => Md(Confirm.Day(x)))))));
        }

        if (EnforceLine(store, m, today) is { } enforce)
        {
            lines.Add(enforce);
        }
        lines.Add(m["STATUS_SCHEDULE_HEAD"]);
        lines.Add(ScheduleText(cfg, m, today));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// today から n 日ぶんの出／休の表（days.schedule_text）．1 行に 7 日．理由のある例外日は理由を，理由の無い例外日は印を添える．
    /// </summary>
    public static string ScheduleText(ShiftConfig cfg, Messages m, DateOnly today, int n = Snapshot.ScheduleDays)
    {
        var cells = Snapshot.Cells(cfg, today, n).Select(c => m.Format("DAYS_CELL",
            ("md", Md(c.Date)),
            ("wd", TimeUtil.WdJa[((int)c.Date.DayOfWeek + 6) % 7]),
            ("mark", m[c.Work ? "DAYS_WORK" : "DAYS_OFF"]),
            ("extra", c.Why is not null ? m.Format("DAYS_NOTE", ("why", c.Why)) : c.IsException ? m["DAYS_EXCEPTION_MARK"] : "")));
        return string.Join("\n", cells.Chunk(7).Select(week => "  " + string.Join("  ", week)));
    }

    /// <summary>カレンダーごとの同期の状況（設定にあるソースの順）．calendar.json が旧形式（sources が無い）なら直下を見る．</summary>
    private static IEnumerable<string> CalendarLines(ShiftConfig cfg, Store store, Messages m)
    {
        var names = CalendarSync.SourceNames(cfg);
        if (names.Count == 0)
        {
            yield break;
        }
        var saved = store.ReadJson("calendar.json");
        var hasSources = saved.TryGetPropertyValue("sources", out var sources);
        foreach (var name in names)
        {
            var cs = (hasSources ? (sources as JsonObject)?[name] : saved) as JsonObject;
            var fetchedNode = cs?["fetched"];
            var fetched = JsonX.Truthy(fetchedNode)
                ? JsonX.Str(fetchedNode) ?? throw new InvalidDataException("calendar.json: fetched")
                : m["STATUS_CAL_NEVER"];
            fetched = (fetched.Length > 16 ? fetched[..16] : fetched).Replace("T", " ", StringComparison.Ordinal);
            var error = JsonX.Truthy(cs?["error"]) ? m.Format("STATUS_CAL_ERROR", ("error", ShiftConfig.PyText(cs?["error"]))) : "";
            yield return m.Format("STATUS_CAL", ("name", name), ("fetched", fetched), ("error", error));
        }
    }

    /// <summary>
    /// 強制モードの行．同意済みの宣言のうち乗務日が昨日以降のもの（解除済みも並べる）と，STOP.txt の有無．どちらも無ければ null．
    /// 宣言の中身が読めなければ InvalidDataException．
    /// </summary>
    private static string? EnforceLine(Store store, Messages m, DateOnly today)
    {
        var since = Planner.DayKey(today.AddDays(-1));
        var labels = m.Map("ENFORCE_LABEL");
        var parts = new List<string>();
        foreach (var (day, node) in store.ShiftStatus().OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if ((node as JsonObject)?["enforce"] is not JsonObject e || !JsonX.Truthy(e["consented_at"]))
            {
                continue;
            }
            if (!DateOnly.TryParseExact(day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                throw new InvalidDataException($"state.json: shifts.{day}");
            }
            if (string.CompareOrdinal(day, since) < 0)
            {
                continue;
            }
            var state = JsonX.Truthy(e["released_at"])
                ? m["STATUS_ENFORCE_RELEASED"]
                : m.Format("STATUS_ENFORCE_RUNS", ("n", Runs(e)));
            var action = JsonX.Str(e["action"]);
            var label = action is not null && labels.TryGetValue(action, out var known) ? known : ShiftConfig.PyText(e["action"]);
            if (action is not null && Array.IndexOf(LimitActions, action) >= 0)
            {
                // 減速・一時停止には「N 時間」が無い
                parts.Add(m.Format("STATUS_ENFORCE_ITEM_LIMIT", ("day", Md(d)), ("label", label), ("state", state)));
            }
            else
            {
                var hours = e.TryGetPropertyValue("duration_hours", out var h)
                    ? Py.Float(h) ?? throw new InvalidDataException("enforce.duration_hours")
                    : 2;
                parts.Add(m.Format("STATUS_ENFORCE_ITEM", ("day", Md(d)), ("label", label), ("hours", Enforce.G(hours)), ("state", state)));
            }
        }
        if (!store.StopActive && parts.Count == 0)
        {
            return null;
        }
        return m.Format("STATUS_ENFORCE", ("stop", store.StopActive ? m["STATUS_ENFORCE_STOP"] : ""),
            ("items", string.Join(m["STATUS_ENFORCE_SEP"], parts)));
    }

    // ---- 記録のコマンド（shl okita / neru / itte / yasumi / deru，shl day off|work|rm）----

    /// <summary>events.log のメモに書く出所（yasumi・deru）．okita・neru は Python 版と同じく "claude" になる（Records の Cli*）．</summary>
    public const string Via = "shl";

    // メモ・引数の「有給」「有給なし」（keywords.PAID_ON／PAID_OFF．入力の語彙）
    private static readonly string[] PaidOn = ["有給", "有休"];
    private static readonly string[] PaidOff = ["有給なし", "有休なし", "有給無し", "有休無し"];

    /// <summary>
    /// shl okita / neru / itte（語なし）と shl yasumi [12/3] [有給|有給なし] / shl deru [12/3] の返事（cli.record_cmd）．
    /// yasumi・deru は日付があれば事後報告，無ければ「今日休む」「やっぱ出る」．「有給」は休みに有給の印（deru には関係ない）．
    /// 記録のファイルが読み書きできなければ StateBusyException．
    /// </summary>
    public static string Record(string cmd, IReadOnlyList<string> words, ShiftConfig cfg, Store store, Messages m, DateTime now)
    {
        switch (cmd)
        {
            case "okita":
                return Records.CliOkita(cfg, store, m, now).Message;
            case "neru":
                return Records.CliNeru(cfg, store, m, now).Message;
            case "itte":
                return Records.CliShuppatsu(cfg, store, m, now).Message;
            case "yasumi" or "deru":
                break;
            default:
                throw new ArgumentException(cmd, nameof(cmd));
        }
        bool? paid = null;
        string? date = null;
        foreach (var w in words)
        {
            if (string.IsNullOrEmpty(w))
            {
                continue;
            }
            if (PaidFlag(w) is { } flag)
            {
                paid ??= flag;
            }
            else
            {
                date ??= w;
            }
        }
        var yasumi = cmd == "yasumi";
        if (date is not null)
        {
            return LateReport.Report(cfg, store, m, now, yasumi ? "yasumi" : "worked", date, via: Via, paid: yasumi ? paid : null).Text;
        }
        return yasumi ? YasumiNext(cfg, store, m, now, paid == true) : UndoNext(cfg, store, m, now);
    }

    /// <summary>
    /// shl day off|work|rm &lt;日付&gt; [メモ]（cli.day_cmd）: 例外日を days.json に書き，書いたあとの 2 週間の表を返す．
    /// 返り値は (終了コード, 表示文)．日付が無ければ 1．日付が読めなければ FormatException．
    /// </summary>
    public static (int Code, string Text) Day(string action, string? date, IReadOnlyList<string> note, string configPath,
        Store store, Messages m, DateTime now)
    {
        if (string.IsNullOrEmpty(date))
        {
            return (1, m["CLI_DAY_NEED_DATE"]);
        }
        var kind = action switch
        {
            "off" => "off",
            "work" => "work",
            "rm" => null,
            _ => throw new ArgumentException(action, nameof(action)),
        };
        var today = DateOnly.FromDateTime(now);
        LateReport.DaysSet(store, LateReport.ParseDay(date, today, past: false), kind, string.Join(" ", note));
        var cfg = ShiftConfig.Load(configPath);   // 書いた例外日を読み直す
        return (0, ScheduleText(cfg, cfg.Msg, today));
    }

    /// <summary>「有給」「有休」→ true，「有給なし」など → false，どちらでもなければ null（records.paid_flag）．</summary>
    public static bool? PaidFlag(string? text)
    {
        var t = (text ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("　", "", StringComparison.Ordinal).Trim();
        return Array.IndexOf(PaidOn, t) >= 0 ? true : Array.IndexOf(PaidOff, t) >= 0 ? false : null;
    }

    /// <summary>
    /// 「今日休む」（cmd_keyword）: 次の乗務（24 時間以内）を休みにする．
    /// 今日の乗務の出庫の最終を過ぎていたら，翌日ではなく今日の乗務の事後報告にする．
    /// via は events.log のメモに書く出所（画面からは Records.Via）．
    /// </summary>
    public static string YasumiNext(ShiftConfig cfg, Store store, Messages m, DateTime now, bool paid, string via = Via)
    {
        var today = DateOnly.FromDateTime(now);
        if (Planner.IsWorkday(cfg, today) && Planner.MakePlan(cfg, today).StartLatest <= now)
        {
            return LateReport.Report(cfg, store, m, now, "yasumi", "今日", via: via, paid: paid ? true : null).Text;
        }
        if (Planner.TargetPlan(cfg, now) is not { } p)
        {
            return m.Format("CMD_NO_PLAN");
        }
        store.SetShiftStatus(p.Day, "yasumi", now);
        if (paid)
        {
            store.SetPaid(p.Day, true, now, via);
        }
        var n = LateReport.MonthYasumiCount(store, now);
        var upto = DateOnly.ParseExact(p.Day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
        return m.Format("CMD_YASUMI", ("day", TimeUtil.FmtMd(p.StartLatest)), ("n", n))
            + (paid ? m.Format("CMD_YASUMI_PAID") : "")
            + LateReport.AbsenceNote(cfg, store, m, now, upto);
    }

    /// <summary>「やっぱ出る」（cmd_keyword）: 次の乗務（24 時間以内）の休み・出発済みを取り消す．</summary>
    public static string UndoNext(ShiftConfig cfg, Store store, Messages m, DateTime now)
    {
        if (Planner.TargetPlan(cfg, now) is not { } p)
        {
            return m.Format("CMD_NO_PLAN");
        }
        var prev = JsonX.Str((store.ShiftStatus()[p.Day] as JsonObject)?["status"]);
        store.SetShiftStatus(p.Day, null, now);
        return m.Format("CMD_UNDO", ("day", TimeUtil.FmtMd(p.StartLatest)),
            ("what", m[prev == "worked" ? "CMD_UNDO_WORKED" : "CMD_UNDO_YASUMI"]));
    }

    /// <summary>実行の回数（len(e.get("runs", []))）．</summary>
    private static int Runs(JsonObject e) =>
        !e.TryGetPropertyValue("runs", out var runs) ? 0
        : runs is JsonArray a ? a.Count
        : throw new InvalidDataException("enforce.runs");

    /// <summary>"12/01"．</summary>
    private static string Md(DateOnly d) => d.ToString("MM'/'dd", CultureInfo.InvariantCulture);
}
