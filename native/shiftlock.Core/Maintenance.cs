using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shiftlock.Core;

/// <summary>
/// 見張りのたびに呼ぶ手入れ（pc/shiftlock/maintenance.py）．カレンダー同期（間隔ごと）と，古い記録の整理（1 日 1 回）．
/// 失敗しても見張りは止めない．
/// </summary>
public static partial class Maintenance
{
    private const string FileName = "maintenance.json";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>log/&lt;種類&gt;-yyyy-MM.jsonl．</summary>
    [GeneratedRegex(@"^.*-([0-9]{4})-([0-9]{2})\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex LogName();

    /// <summary>reports/report_開始_終了.html（pdf）と，旧形式 report-終了.html（pdf）．1 つ目の組が期間の終わり．</summary>
    [GeneratedRegex(@"^report(?:_[0-9]{4}-[0-9]{2}-[0-9]{2}_|-)([0-9]{4}-[0-9]{2}-[0-9]{2})\.(?:html|pdf)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReportName();

    /// <summary>
    /// 見張りから呼ぶ（maintenance.daily_maintenance）．カレンダー同期の失敗は握る．
    /// 整理はその日の最初の 1 回だけ．失敗したら maintenance.json の result に {"error": ...} を書き，翌日また試す．
    /// fetch が null なら既定の取得（CalendarSync.HttpGet）．
    /// </summary>
    public static void Daily(ShiftConfig cfg, Store store, Messages m, DateTime now, Func<string, string>? fetch)
    {
        try
        {
            CalendarSync.Sync(cfg, store, m, now, fetch ?? CalendarSync.HttpGet);
        }
        catch (Exception)
        {
            // 同期できなくても前回の calendar.json で動く
        }
        var state = store.ReadJson(FileName);
        var today = Planner.DayKey(DateOnly.FromDateTime(now));
        if (JsonX.Str(state["last_cleanup"]) == today)
        {
            return;
        }
        try
        {
            state["result"] = Cleanup(cfg, store, now);
        }
        catch (Exception e)
        {
            state["result"] = new JsonObject { ["error"] = e.Message };
        }
        state["last_cleanup"] = today;
        store.WriteJson(FileName, state);
    }

    /// <summary>
    /// 古い記録を消す（maintenance.cleanup）．days.json・state.json は days_keep 日，log/*.jsonl は月ごとに log_keep_days 日，
    /// events.log は events_keep_days 日，レポートは keep_months か月．戻り値は消した件数（days・state・logs・events・reports）．
    /// Python 版との違い: 在るのに読めない days.json は書き直さない（Python 版は空で上書きする）．
    /// 月として読めない名前のログ（-2027-13 など）は消さない．
    /// </summary>
    public static JsonObject Cleanup(ShiftConfig cfg, Store store, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var cutDays = Planner.DayKey(today.AddDays(-WholeDays(cfg, "days_keep", 60)));
        var removed = new JsonObject { ["days"] = 0, ["state"] = 0, ["logs"] = 0, ["events"] = 0 };

        if (JsonFiles.TryReadObject(store.P("days.json"), out var days))
        {
            var n = 0;
            foreach (var key in (string[])["off", "work", "times"])
            {
                var kept = Since(days.TryGetPropertyValue(key, out var node) ? node : new JsonObject(), cutDays, "days.json " + key, out var dropped);
                days[key] = kept;
                n += dropped;
            }
            store.WriteJson("days.json", days);   // 無くても空の 3 項目で作る（Python 版と同じ）
            removed["days"] = n;
        }

        if (JsonFiles.TryReadObject(store.P("state.json"), out var st) && st.TryGetPropertyValue("shifts", out var shifts))
        {
            st["shifts"] = Since(shifts, cutDays, "state.json shifts", out var dropped);
            store.WriteState(st);
            removed["state"] = dropped;
        }

        var logDir = store.P("log");
        if (Directory.Exists(logDir))
        {
            var cutLog = today.AddDays(-WholeDays(cfg, "log_keep_days", 180));
            var n = 0;
            foreach (var file in Directory.GetFiles(logDir))
            {
                if (MonthEnd(Path.GetFileName(file)) is { } monthEnd && monthEnd < cutLog)
                {
                    File.Delete(file);
                    n++;
                }
            }
            removed["logs"] = n;
        }

        removed["reports"] = CleanupReports(cfg, store, now);

        var events = store.P("events.log");
        if (File.Exists(events))
        {
            var cutEvents = Planner.DayKey(today.AddDays(-WholeDays(cfg, "events_keep_days", 365)));
            var lines = SplitLines(File.ReadAllText(events, Utf8NoBom));
            var keep = lines.Where(ln => string.CompareOrdinal(ln.Length > 10 ? ln[..10] : ln, cutEvents) >= 0).ToList();
            if (keep.Count != lines.Count)
            {
                File.WriteAllText(events, string.Concat(keep.Select(ln => ln + "\n")), Utf8NoBom);
                removed["events"] = lines.Count - keep.Count;
            }
        }
        return removed;
    }

    /// <summary>締め期間の終わりが keep_months か月より前のレポート（HTML・PDF）を消す（report.cleanup_reports）．戻り値は消した数．</summary>
    private static int CleanupReports(ShiftConfig cfg, Store store, DateTime now)
    {
        var report = cfg.Section("report");
        var keep = report.TryGetPropertyValue("keep_months", out var v) ? Py.Int(v) ?? throw Unreadable("report.keep_months") : 3;
        var months = (now.Year * 12L) + now.Month - 1 - keep;   // 0 年 1 月からの月数
        var (year, month) = ((int)Math.DivRem(months, 12, out var rest), (int)rest + 1);
        if (keep < 0 || year < 1)
        {
            throw Unreadable("report.keep_months");
        }
        var cutoff = Planner.DayKey(new DateOnly(year, month, Math.Min(now.Day, DateTime.DaysInMonth(year, month))));
        var dir = store.P("reports");
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        var n = 0;
        foreach (var file in Directory.GetFiles(dir))
        {
            var match = ReportName().Match(Path.GetFileName(file));
            if (match.Success && string.CompareOrdinal(match.Groups[1].Value, cutoff) < 0)
            {
                File.Delete(file);
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// retention の日数（Python の 日付 - timedelta(days=値)．小数は切り捨て）．数でなければ ConfigException．
    /// </summary>
    internal static int WholeDays(ShiftConfig cfg, string key, double fallback) =>
        (int)Math.Floor(cfg.SectionNumber("retention", key, fallback));

    /// <summary>日付がキーの辞書から cut 以降だけ残す．辞書でなければ InvalidDataException（勝手に直さない）．</summary>
    private static JsonObject Since(JsonNode? node, string cut, string what, out int dropped)
    {
        var all = node as JsonObject ?? throw Unreadable(what);
        var kept = new JsonObject();
        foreach (var (day, v) in all)
        {
            if (string.CompareOrdinal(day, cut) >= 0)
            {
                kept[day] = v?.DeepClone();
            }
        }
        dropped = all.Count - kept.Count;
        return kept;
    }

    /// <summary>ログのファイル名の月の末日．月として読めなければ null．</summary>
    private static DateOnly? MonthEnd(string name)
    {
        var match = LogName().Match(name);
        if (!match.Success)
        {
            return null;
        }
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return year is >= 1 and < 9999 && month is >= 1 and <= 12
            ? new DateOnly(year, month, DateTime.DaysInMonth(year, month))
            : null;
    }

    /// <summary>行に分ける（Python の splitlines．行末は LF・CRLF・CR．最後の改行の後ろに空の行は作らない）．</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    private static InvalidDataException Unreadable(string what) => new(what);
}
