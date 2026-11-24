using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// コマンドの返事の文（pc/shiftlock/commands.py と days.py の schedule_text）．
/// shl.exe の見るだけのコマンド（status・yotei・day list）が使う．Python 版と同じ文になることをテストベクタ（commands.json）で確かめる．
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

    /// <summary>実行の回数（len(e.get("runs", []))）．</summary>
    private static int Runs(JsonObject e) =>
        !e.TryGetPropertyValue("runs", out var runs) ? 0
        : runs is JsonArray a ? a.Count
        : throw new InvalidDataException("enforce.runs");

    /// <summary>"12/01"．</summary>
    private static string Md(DateOnly d) => d.ToString("MM'/'dd", CultureInfo.InvariantCulture);
}
