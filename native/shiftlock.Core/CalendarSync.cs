using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// Google カレンダー（iCal）の解析と同期（pc/shiftlock/calendar_sync.py）．ソースは複数．
/// 1 本失敗しても他は更新し，失敗した分は前回の内容で続ける．結果は calendar.json（新形式）に書く．
/// 乗務日の判定に効くのは，次に設定を読み直したとき（＝次の見張り）から．
/// </summary>
public static class CalendarSync
{
    private const string FileName = "calendar.json";

    /// <summary>1 件の予定から拾う日数の上限．</summary>
    private const int MaxSpanDays = 62;

    /// <summary>これより先の予定は残さない（日）．</summary>
    private const int KeepAheadDays = 180;

    /// <summary>calendar.json に残すエラーの文の長さの上限．</summary>
    private const int ErrorMax = 300;

    /// <summary>旧形式（off・work が直下）から先頭のソースに引き継ぐキー．</summary>
    private static readonly string[] Carried = ["fetched", "error", "off", "work", "recurring_skipped"];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>同期するカレンダー 1 本．キーワードは読む前の値（読めなければそのソースだけ失敗にする）．</summary>
    private sealed record Source(string Name, string Url, JsonNode? OffKeywords, JsonNode? WorkKeywords);

    /// <summary>
    /// SUMMARY にキーワードを含む予定の日付を拾う（calendar_sync.parse_ics）．休みの言葉が先に勝つ．
    /// 戻り値は {"off": {日: 題}, "work": {日: 題}, "recurring_skipped": n}．繰り返し予定（RRULE）は初回だけ拾って n に数える．
    /// Z つきの時刻はローカルの日付にする．拾う予定の日付が読めなければ FormatException
    /// （Python 版の strptime より厳しく，8 桁の日付・6 桁の時刻だけを受ける）．
    /// </summary>
    public static JsonObject ParseIcs(string text, IReadOnlyList<string> offKeywords, IReadOnlyList<string> workKeywords)
    {
        var off = new JsonObject();
        var work = new JsonObject();
        var recurring = 0;
        Dictionary<string, (string Value, string Params)>? ev = null;
        foreach (var line in Unfold(text))
        {
            if (line == "BEGIN:VEVENT")
            {
                ev = new(StringComparer.Ordinal);
                continue;
            }
            if (ev is null)
            {
                continue;
            }
            if (line == "END:VEVENT")
            {
                var summary = (ev.TryGetValue("SUMMARY", out var s) ? s.Value : "")
                    .Replace("\\,", ",", StringComparison.Ordinal)
                    .Replace("\\;", ";", StringComparison.Ordinal)
                    .Replace("\\n", " ", StringComparison.Ordinal);
                var kind = offKeywords.Any(k => summary.Contains(k, StringComparison.Ordinal)) ? off
                    : workKeywords.Any(k => summary.Contains(k, StringComparison.Ordinal)) ? work
                    : null;
                var cancelled = ev.TryGetValue("STATUS", out var status) && status.Value == "CANCELLED";
                if (kind is not null && ev.TryGetValue("DTSTART", out var dtStart) && !cancelled)
                {
                    var (start, _) = Dt(dtStart.Value, dtStart.Params);
                    var end = start;
                    if (ev.TryGetValue("DTEND", out var dtEnd))
                    {
                        var (e, allDay) = Dt(dtEnd.Value, dtEnd.Params);
                        end = allDay ? e.AddDays(-1) : e;   // 終日の DTEND は翌日（その日を含まない）
                    }
                    if (end < start)
                    {
                        end = start;
                    }
                    if (ev.ContainsKey("RRULE"))
                    {
                        recurring++;
                    }
                    for (var d = start; d <= end && d.DayNumber - start.DayNumber < MaxSpanDays; d = d.AddDays(1))
                    {
                        kind[Planner.DayKey(d)] = summary;
                    }
                }
                ev = null;
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }
            var head = line[..colon];
            var semi = head.IndexOf(';');
            var name = semi < 0 ? head : head[..semi];
            if (name is "DTSTART" or "DTEND" or "SUMMARY" or "STATUS" or "RRULE")
            {
                ev[name] = (line[(colon + 1)..], semi < 0 ? "" : head[(semi + 1)..]);
            }
        }
        return new JsonObject { ["off"] = off, ["work"] = work, ["recurring_skipped"] = recurring };
    }

    /// <summary>
    /// ソースごとに同期して calendar.json に書く（calendar_sync.sync_calendar）．戻り値はソースごとに 1 行．
    /// sync_minutes 以内に取ったソースは取りに行かない（force なら必ず取る）．取得・解析に失敗したソースは前回分を残して error を入れる．
    /// 設定や前回の calendar.json の形がおかしければ例外（何も書かない．見張りからは Maintenance.Daily が握る）．
    /// </summary>
    public static string Sync(ShiftConfig cfg, Store store, Messages m, DateTime now, Func<string, string> fetch, bool force = false)
    {
        var cal = cfg.Section("calendar");
        var sources = Sources(cal);
        if (sources.Count == 0)
        {
            return m["CAL_UNSET"];
        }
        var cur = store.ReadJson(FileName);
        var hasSources = cur.TryGetPropertyValue("sources", out var sourcesNode);
        var prev = hasSources ? sourcesNode as JsonObject ?? throw Unreadable("sources") : new JsonObject();
        if (!hasSources && JsonX.Truthy(cur["fetched"]))
        {
            // 旧形式 → 先頭のソースの前回分として引き継ぐ
            var first = new JsonObject();
            foreach (var key in Carried)
            {
                if (cur.TryGetPropertyValue(key, out var v))
                {
                    first[key] = v?.DeepClone();
                }
            }
            prev[sources[0].Name] = first;
        }
        var today = DateOnly.FromDateTime(now);
        var keepFrom = Planner.DayKey(today.AddDays(-Maintenance.WholeDays(cfg, "days_keep", 60)));
        var keepTo = Planner.DayKey(today.AddDays(KeepAheadDays));
        var every = TimeSpan.FromMinutes(cfg.SectionNumber("calendar", "sync_minutes", 60));

        var result = new JsonObject();
        var lines = new List<string>();
        foreach (var s in sources)
        {
            var old = prev.TryGetPropertyValue(s.Name, out var o)
                ? (o as JsonObject ?? throw Unreadable(s.Name)).DeepClone().AsObject()
                : new JsonObject();
            if (!force && JsonX.Truthy(old["fetched"])
                && now - (TimeUtil.ParseIso(JsonX.Str(old["fetched"])) ?? throw Unreadable("fetched")) < every)
            {
                result[s.Name] = old;
                lines.Add(m.Format("CAL_SKIP", ("name", s.Name)));
                continue;
            }
            JsonObject res;
            try
            {
                res = ParseIcs(fetch(s.Url), Keywords(s.OffKeywords), Keywords(s.WorkKeywords));
            }
            catch (Exception e)
            {
                // ネットが落ちていても前回分で動く．error は calendar.json に保存する値（文面の対象外）
                var error = $"{TimeUtil.FmtD(now)} {ErrorName(e)}: {e.Message}";
                old["error"] = error.Length > ErrorMax ? error[..ErrorMax] : error;
                if (!old.ContainsKey("fetched"))
                {
                    old["fetched"] = null;
                }
                result[s.Name] = old;
                lines.Add(m.Format("CAL_FAIL", ("name", s.Name), ("err", e.Message)));
                continue;
            }
            var off = InRange(res["off"]!.AsObject(), keepFrom, keepTo);
            var work = InRange(res["work"]!.AsObject(), keepFrom, keepTo);
            lines.Add(m.Format("CAL_OK", ("name", s.Name), ("off", off.Count), ("work", work.Count)));
            result[s.Name] = new JsonObject
            {
                ["fetched"] = Store.IsoSeconds(now),
                ["error"] = null,
                ["off"] = off,
                ["work"] = work,
                ["recurring_skipped"] = res["recurring_skipped"]!.DeepClone(),
            };
        }
        if (!hasSources || !JsonNode.DeepEquals(result, prev))
        {
            // 全部スキップなら書き直さない
            store.WriteJson(FileName, new JsonObject { ["fetched"] = Store.IsoSeconds(now), ["sources"] = result });
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 既定の取得（calendar_sync._http_get）．タイムアウト 10 秒．UTF-8 で読み，読めないバイトは U+FFFD に置き換える．
    /// 失敗（つながらない・時間切れ・200 番台でない）は例外．
    /// </summary>
    public static string HttpGet(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = Http.Send(request);
        response.EnsureSuccessStatusCode();
        using var body = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        body.CopyTo(buffer);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// 同期するカレンダーの一覧（config.calendar_sources）．旧形式 ics_url（1 本）が先頭，続けて sources．
    /// 同じ URL は 1 本にまとめる．名前が重なれば ' を足す．名前が無ければ「カレンダー＋番号」．
    /// ソース名「カレンダー」は calendar.json のキーとして保存される値なので文面には置かない．
    /// </summary>
    private static List<Source> Sources(JsonObject cal)
    {
        var raw = new List<JsonObject>();
        if (Text(cal["ics_url"], "ics_url").Trim().Length > 0)
        {
            raw.Add(new JsonObject { ["name"] = "カレンダー", ["ics_url"] = cal["ics_url"]!.DeepClone() });
        }
        raw.AddRange((cal["sources"] as JsonArray ?? []).OfType<JsonObject>());

        var found = new List<Source>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            var url = Text(s["ics_url"], "ics_url").Trim();
            if (url.Length == 0 || !seen.Add(url))
            {
                continue;
            }
            var name = Text(s["name"], "name").Trim();
            if (name.Length == 0)
            {
                name = "カレンダー" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            while (found.Any(f => f.Name == name))
            {
                name += "'";
            }
            found.Add(new Source(name, url,
                s.TryGetPropertyValue("off_keywords", out var off) ? off : cal["off_keywords"] ?? new JsonArray(),
                s.TryGetPropertyValue("work_keywords", out var work) ? work : cal["work_keywords"] ?? new JsonArray()));
        }
        return found;
    }

    /// <summary>Python の (値 or "")．偽の値（無い・null・空）は空，文字列はそのまま，それ以外は読めない．</summary>
    private static string Text(JsonNode? v, string key) =>
        !JsonX.Truthy(v) ? "" : JsonX.Str(v) ?? throw Unreadable(key);

    /// <summary>キーワードの並び．配列は要素（文字列だけ），文字列は 1 文字ずつ（Python の list(文字列)）．それ以外は読めない．</summary>
    private static List<string> Keywords(JsonNode? v) => v switch
    {
        JsonArray a => [.. a.Select(k => JsonX.Str(k) ?? throw Unreadable("keywords"))],
        _ when JsonX.Str(v) is { } s => [.. s.Select(c => c.ToString())],
        _ => throw Unreadable("keywords"),
    };

    private static JsonObject InRange(JsonObject days, string from, string to)
    {
        var kept = new JsonObject();
        foreach (var (d, v) in days)
        {
            if (string.CompareOrdinal(from, d) <= 0 && string.CompareOrdinal(d, to) <= 0)
            {
                kept[d] = v?.DeepClone();
            }
        }
        return kept;
    }

    /// <summary>折り返した行（先頭が空白かタブ）を前の行につなぐ．</summary>
    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t' && lines.Count > 0)
            {
                lines[^1] += line[1..];
            }
            else
            {
                lines.Add(line);
            }
        }
        return lines;
    }

    /// <summary>DTSTART・DTEND の値 →（ローカルの日付，終日か）．読めなければ FormatException．</summary>
    private static (DateOnly Day, bool AllDay) Dt(string value, string pars)
    {
        var v = value.Trim();
        if (pars.Contains("VALUE=DATE", StringComparison.Ordinal) || (v.Length == 8 && v.All(char.IsAsciiDigit)))
        {
            return (DateOnly.ParseExact(v.Length > 8 ? v[..8] : v, "yyyyMMdd", CultureInfo.InvariantCulture), true);
        }
        if (v.EndsWith('Z'))
        {
            var utc = DateTime.ParseExact(v, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return (DateOnly.FromDateTime(utc.ToLocalTime()), false);
        }
        var local = DateTime.ParseExact(v.Length > 15 ? v[..15] : v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        return (DateOnly.FromDateTime(local), false);
    }

    /// <summary>
    /// error に書く例外の名前．取得の失敗（IOException）は Python 版と同じ OSError と書く
    /// （同じ calendar.json を両方の版が読み書きするので，よくある失敗の表記を揃える）．ほかは .NET の型の名前．
    /// </summary>
    private static string ErrorName(Exception e) => e.GetType() == typeof(IOException) ? "OSError" : e.GetType().Name;

    private static InvalidDataException Unreadable(string key) => new($"calendar.{key}");
}
