using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 記録のファイルを読み書きできなかった（state.json が在るのに読めない・書けない，events.log に足せない）．
/// 読めなかったものを空として書き戻すと記録が消えるので，記録を断るために投げる．
/// </summary>
public sealed class StateBusyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 実行フォルダのファイル（pc/shiftlock/store.py の Store）．画面（区切り 1）が書くのは state.json と events.log．見張り（区切り 2）はログや通知の履歴なども書く．
/// 排他はしない（Python 版同士と同じ．書き手が同時に state.json を書くと後勝ちになりうる）．
/// </summary>
public sealed class Store(string dir)
{
    /// <summary>実行フォルダにあれば強制は一切しない（緊急解除）．</summary>
    public const string StopFile = "STOP.txt";

    private const int Attempts = 3;
    private static readonly TimeSpan RetryWait = TimeSpan.FromMilliseconds(50);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string Dir { get; } = dir;

    public string P(string name) => Path.Combine(Dir, name);

    /// <summary>state.json の shifts（乗務日 → 記録）．無い・壊れている → 空．表示用（書き戻す前に読むときは ReadStateForWrite）．</summary>
    public JsonObject ShiftStatus() =>
        JsonFiles.ReadObjectOrEmpty(P("state.json"))["shifts"] as JsonObject ?? new JsonObject();

    /// <summary>書き戻すために state.json を読む．無ければ空．在るのに読めなければ（少し待ってやり直してから）StateBusyException．</summary>
    public JsonObject ReadStateForWrite()
    {
        for (var i = 0; ; i++)
        {
            if (JsonFiles.TryReadObject(P("state.json"), out var state))
            {
                return state;
            }
            if (i + 1 >= Attempts)
            {
                throw new StateBusyException("state.json が読めない");
            }
            Thread.Sleep(RetryWait);
        }
    }

    /// <summary>state.json を書く（.tmp に書いてから置き換え）．だめなら少し待ってやり直し，それでもだめなら StateBusyException．.tmp は残さない．</summary>
    public void WriteState(JsonObject state) =>
        Retry(() => JsonFiles.Write(P("state.json"), state), "state.json が書けない");

    /// <summary>events.log に 1 行足す（タブ区切り・LF．時刻は分まで）．</summary>
    public void AppendEvent(DateTime at, string day, string kind, string note = "")
    {
        var line = $"{TimeUtil.Iso(at)}\t{day}\t{kind}\t{note}\n";
        Retry(() => File.AppendAllText(P("events.log"), line, Utf8NoBom), "events.log に書けない");
    }

    /// <summary>STOP.txt がある（enforce.stop_active）．</summary>
    public bool StopActive => File.Exists(P(StopFile)) || Directory.Exists(P(StopFile));

    /// <summary>
    /// PC の記録（usage-pc.json）上，最近まとまって離席（≒睡眠）していたか（usage.recent_long_gap）．記録なしなら null．
    /// </summary>
    public bool? RecentLongGap(DateTime now, double minHours = 2.5, double withinHours = 4)
    {
        if (JsonFiles.ReadObjectOrEmpty(P("usage-pc.json"))["gaps"] is not JsonArray gaps)
        {
            return null;
        }
        foreach (var gap in gaps)
        {
            if (gap is not JsonArray { Count: 2 } pair
                || TimeUtil.ParseIso(JsonX.Str(pair[0])) is not { } s
                || TimeUtil.ParseIso(JsonX.Str(pair[1])) is not { } e)
            {
                continue;
            }
            if (e - s >= TimeSpan.FromHours(minHours) && now - e <= TimeSpan.FromHours(withinHours))
            {
                return true;
            }
        }
        return false;
    }

    // ---- 見張り（Tick）が使う読み書き．pc/shiftlock/store.py の同名の関数と同じ形 ----

    /// <summary>name の JSON を読む．無い・壊れている・オブジェクトでない → 空（store._read(path, {})）．</summary>
    public JsonObject ReadJson(string name) => JsonFiles.ReadObjectOrEmpty(P(name));

    /// <summary>name に JSON を書く（.tmp に書いてから置き換え）．書けなければ StateBusyException．</summary>
    public void WriteJson(string name, JsonObject obj) => Retry(() => JsonFiles.Write(P(name), obj), name + " が書けない");

    /// <summary>log/&lt;kind&gt;-yyyy-MM.jsonl に {"t": 秒までの時刻, ...rec} を 1 行足す（store.log_append）．</summary>
    public void LogAppend(string kind, DateTime now, JsonObject rec)
    {
        var line = new JsonObject { ["t"] = IsoSeconds(now) };
        foreach (var (k, v) in rec)
        {
            line[k] = v?.DeepClone();
        }
        var text = PyJson.Dumps(line) + "\n";
        var path = LogPath(kind, now);
        Retry(() =>
        {
            Directory.CreateDirectory(P("log"));
            File.AppendAllText(path, text, Utf8NoBom);
        }, Path.GetFileName(path) + " に書けない");
    }

    /// <summary>start &lt;= t &lt; end の行を時刻順で返す（store.log_read）．壊れた行・時刻の読めない行は飛ばす．</summary>
    public IReadOnlyList<(DateTime T, JsonObject Rec)> LogRead(string kind, DateTime start, DateTime end)
    {
        var found = new List<(DateTime T, JsonObject Rec)>();
        for (var m = new DateTime(start.Year, start.Month, 1); m <= end; m = m.AddMonths(1))
        {
            foreach (var (t, rec) in ReadLogFile(LogPath(kind, m)))
            {
                if (start <= t && t < end)
                {
                    found.Add((t, rec));
                }
            }
        }
        return [.. found.OrderBy(r => r.T)];   // OrderBy は安定（同じ時刻はファイルの順のまま）
    }

    /// <summary>記録を始めた日（PC の見張りログの最初に読める行の日付．store.first_log_date）．</summary>
    public DateOnly? FirstLogDate()
    {
        foreach (var file in LogFiles("pc"))
        {
            foreach (var (t, _) in ReadLogFile(file))
            {
                return DateOnly.FromDateTime(t);
            }
        }
        return null;
    }

    /// <summary>
    /// kind のログの最後の行の時刻（最新の月のファイルの，最後に読める行）．無ければ null．
    /// Agent が「前の見張りから 5 分たったか」を見るのに使う（Python 版には無い）．
    /// </summary>
    public DateTime? LastLogTime(string kind)
    {
        foreach (var file in LogFiles(kind).Reverse())
        {
            DateTime? last = null;
            foreach (var (t, _) in ReadLogFile(file))
            {
                last = t;
            }
            if (last is not null)
            {
                return last;
            }
        }
        return null;
    }

    /// <summary>
    /// events.log から「行ってきます」と「ただいま」の履歴（store.departures）．
    /// 出発は (乗務日, 行の時刻)（worked 行でメモが shuppatsu のもの），
    /// 帰宅は時刻（kitaku 行．メモ末尾の時刻，無い古い行は行の時刻．free-day の行も含み，clear は除く）．
    /// </summary>
    public (HashSet<(string Day, string At)> Shuppatsu, HashSet<string> Kitaku) Departures()
    {
        var shuppatsu = new HashSet<(string Day, string At)>();
        var kitaku = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(P("events.log")))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            var memo = parts.Length >= 4 ? parts[3] : "";
            if (parts[2] == "worked" && memo == "shuppatsu")
            {
                shuppatsu.Add((parts[1], parts[0]));
            }
            else if (parts[2] == "kitaku" && memo != "clear")
            {
                kitaku.Add(MemoTime(memo) ?? parts[0]);
            }
        }
        return (shuppatsu, kitaku);
    }

    /// <summary>乗務の状態を変えて events.log に 1 行足す（store.set_shift_status）．status が null なら状態を消す（行の種類は undo）．</summary>
    public void SetShiftStatus(string day, string? status, DateTime now, string note = "")
    {
        var state = ReadStateForWrite();
        var shifts = ShiftsForWrite(state);
        var had = shifts.TryGetPropertyValue(day, out var node);
        var cur = had ? node as JsonObject ?? throw new StateBusyException($"state.json の shifts.{day} が辞書ではない") : new JsonObject();
        ApplyStatus(cur, status, now);
        if (cur.Count == 0)
        {
            shifts.Remove(day);
        }
        else if (!had)
        {
            shifts[day] = cur;
        }
        WriteState(state);
        AppendEvent(now, day, status ?? "undo", note);
    }

    /// <summary>
    /// 乗務日ごとの自己申告（sleep_at・kitaku）を書く（store.set_mark）．t が null なら消す．
    /// events.log のメモは「出所 時刻」．消したときは出所があれば出所，無ければ clear．
    /// </summary>
    public void SetMark(string day, string key, DateTime? t, DateTime now, string note = "")
    {
        var state = ReadStateForWrite();
        var cur = ShiftForWrite(ShiftsForWrite(state), day);
        var when = t is { } at ? TimeUtil.Iso(at) : "clear";
        if (t is null)
        {
            cur.Remove(key);
        }
        else
        {
            cur[key] = when;
        }
        WriteState(state);
        var memo = note.Length > 0 && t is not null ? $"{note} {when}" : (note.Length > 0 ? note : when);
        AppendEvent(now, day, key, memo);
    }

    /// <summary>休みの記録に「有給」の印を付ける・外す（store.set_paid）．events.log は種類 paid，メモ「on 出所」「off 出所」．</summary>
    public void SetPaid(string day, bool on, DateTime now, string note = "")
    {
        var state = ReadStateForWrite();
        var shifts = ShiftsForWrite(state);
        var cur = ShiftForWrite(shifts, day);
        if (on)
        {
            cur["paid"] = true;
        }
        else
        {
            cur.Remove("paid");
        }
        if (cur.Count == 0)
        {
            shifts.Remove(day);
        }
        WriteState(state);
        AppendEvent(now, day, "paid", $"{(on ? "on" : "off")} {note}".TrimEnd());
    }

    /// <summary>乗務の記録 1 件の状態を変える（set_shift_status の中身）．休み以外にするときは有給の印を外す．</summary>
    internal static void ApplyStatus(JsonObject shift, string? status, DateTime now)
    {
        if (status is null)
        {
            shift.Remove("status");
            shift.Remove("at");
            shift.Remove("paid");
            return;
        }
        shift["status"] = status;
        shift["at"] = TimeUtil.Iso(now);
        if (status != "yasumi")
        {
            shift.Remove("paid");
        }
    }

    /// <summary>ログの時刻（秒まで．Python の isoformat(timespec="seconds")）．</summary>
    internal static string IsoSeconds(DateTime t) =>
        t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);

    private string LogPath(string kind, DateTime month) =>
        Path.Combine(P("log"), $"{kind}-{month.ToString("yyyy'-'MM", CultureInfo.InvariantCulture)}.jsonl");

    /// <summary>log/&lt;kind&gt;-yyyy-MM.jsonl を名前順（＝月の順）で．</summary>
    private string[] LogFiles(string kind)
    {
        try
        {
            var files = Directory.GetFiles(P("log"), $"{kind}-????-??.jsonl");
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IEnumerable<(DateTime T, JsonObject Rec)> ReadLogFile(string path)
    {
        foreach (var line in ReadLines(path))
        {
            if (ParseLine(line) is { } rec && TimeUtil.ParseIso(JsonX.Str(rec["t"])) is { } t)
            {
                yield return (t, rec);
            }
        }
    }

    private static JsonObject? ParseLine(string line)
    {
        try
        {
            var rec = JsonNode.Parse(line) as JsonObject;
            JsonFiles.Touch(rec);
            return rec;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>テキストを行で読む（無ければ空）．行末の CR は外す．</summary>
    private static string[] ReadLines(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }
        return lines;
    }

    /// <summary>events.log のメモの末尾にある時刻（store._memo_time）．無ければ null．</summary>
    private static string? MemoTime(string memo)
    {
        var toks = memo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return toks.Length > 0 && TimeUtil.ParseIso(toks[^1]) is not null ? toks[^1] : null;
    }

    internal static JsonObject ShiftsForWrite(JsonObject state)
    {
        if (!state.TryGetPropertyValue("shifts", out var node))
        {
            var made = new JsonObject();
            state["shifts"] = made;
            return made;
        }
        // 在るのに辞書でないものを作り直すと中身が消えるので，断る
        return node as JsonObject ?? throw new StateBusyException("state.json の shifts が辞書ではない");
    }

    internal static JsonObject ShiftForWrite(JsonObject shifts, string day)
    {
        if (!shifts.TryGetPropertyValue(day, out var node))
        {
            var made = new JsonObject();
            shifts[day] = made;
            return made;
        }
        return node as JsonObject ?? throw new StateBusyException($"state.json の shifts.{day} が辞書ではない");
    }

    private static void Retry(Action write, string what)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (i + 1 >= Attempts)
                {
                    throw new StateBusyException(what, e);
                }
                Thread.Sleep(RetryWait);
            }
        }
    }
}
