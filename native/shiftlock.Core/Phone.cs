using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shiftlock.Core;

/// <summary>
/// iPhone のショートカットが書く mood.csv の印（ねる／おきた／ただいま／休んだ／出た）の読み取りと取り込み
/// （pc/shiftlock/phone.py の read_phone_marks・import_phone_marks）．気分の行は見張りでは使わない．
/// </summary>
public static partial class Phone
{
    /// <summary>mood.csv に書く印 → 記録の種類（入力の語彙．phone.PHONE_MARKS）．yasumi:2026-12-02 のように日付付きも可．</summary>
    private static readonly Dictionary<string, string> Marks = new(StringComparer.Ordinal)
    {
        ["neru"] = "neru", ["ねる"] = "neru", ["寝る"] = "neru",
        ["okita"] = "okita", ["おきた"] = "okita", ["起きた"] = "okita",
        ["kitaku"] = "kitaku", ["ただいま"] = "kitaku", ["帰宅"] = "kitaku",
        ["yasumi"] = "yasumi", ["休んだ"] = "yasumi",
        ["deta"] = "worked", ["出た"] = "worked",
    };

    /// <summary>適用済みの事後報告として覚えておく件数．</summary>
    private const int AppliedKeep = 300;

    private static readonly UTF8Encoding Lenient = new(false, false);

    [GeneratedRegex(@"^(\d{4})-(\d{1,2})-(\d{1,2})\s+(\d{1,2}):(\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();

    /// <summary>
    /// mood.csv（設定の mood_file．/mnt/c/... と書いてあっても Windows のパスとして読む）から印を拾う．
    /// 時刻・種類・付いていた乗務日の順に並べ，同じ行の重複は 1 件にする．ファイルが無い・読めない・設定が無ければ空．
    /// </summary>
    public static IReadOnlyList<(DateTime T, string Kind, string? Extra)> ReadMarks(ShiftConfig cfg)
    {
        if (JsonX.Str(cfg.Raw["mood_file"]) is not { Length: > 0 } path)
        {
            return [];
        }
        string text;
        try
        {
            text = Lenient.GetString(File.ReadAllBytes(LocalPath.ToLocal(path))).TrimStart('﻿');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
        var found = new HashSet<(DateTime T, string Kind, string? Extra)>();
        foreach (var line in text.Split(['\r', '\n']))
        {
            var parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }
            var mark = parts[1].Trim();
            var colon = mark.IndexOf(':');
            var key = colon < 0 ? mark : mark[..colon];
            var extra = colon < 0 ? "" : mark[(colon + 1)..].Trim();
            if (!Marks.TryGetValue(key, out var kind) || ParseStamp(parts[0].Trim()) is not { } t)
            {
                continue;
            }
            found.Add((t, kind, extra.Length > 0 ? extra : null));   // extra = 乗務日（yasumi / deta のとき）
        }
        return [.. found.OrderBy(x => x.T).ThenBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Extra ?? "", StringComparer.Ordinal)];
    }

    /// <summary>
    /// iPhone の記録を state に取り込む（phone.import_phone_marks．何回呼んでも同じ結果）．返り値はお知らせの文（無ければ空）．
    /// yasumi / deta（事後報告）は 1 件につき 1 回だけ適用し，適用済みを state.json の phone_applied に残す（訂正が巻き戻らない）．
    /// neru / okita / kitaku は直近の乗務（TargetPlan）に対して．
    /// </summary>
    public static string ImportMarks(ShiftConfig cfg, Store store, Messages m, DateTime now)
    {
        var all = ReadMarks(cfg);
        if (all.Count == 0)
        {
            return "";
        }
        var msg = "";
        var lookback = cfg.SectionInt("confirm", "lookback_days");
        var applied = (store.ReadJson("state.json")["phone_applied"] as JsonArray ?? [])
            .Select(JsonX.Str).OfType<string>().ToList();
        var added = new List<string>();
        foreach (var (t, kind, extra) in all)
        {
            if (kind is not ("yasumi" or "worked") || t > now || t < now.AddDays(-lookback))
            {
                continue;
            }
            var key = $"{TimeUtil.Iso(t)}|{kind}|{extra ?? ""}";
            if (applied.Contains(key))
            {
                continue;
            }
            added.Add(key);
            // 日付が無ければ，記録した時点で直近に終わった乗務
            var dayText = extra ?? Planner.LastPastPlan(cfg, t, lookback)?.Day;
            if (dayText is null)
            {
                continue;
            }
            var (ok, text) = LateReport.Report(cfg, store, m, now, kind, dayText, when: t, via: "phone");
            if (ok)
            {
                msg = m["PHONE_LATE_PREFIX"] + text.Replace(m["PREFIX"], "", StringComparison.Ordinal);
            }
        }
        if (added.Count > 0)
        {
            var state = store.ReadStateForWrite();
            state["phone_applied"] = new JsonArray([.. applied.Concat(added).TakeLast(AppliedKeep).Select(k => (JsonNode?)k)]);
            store.WriteState(state);
        }

        var p = Planner.TargetPlan(cfg, now);
        if (p is null)
        {
            return msg;
        }
        var lo = DateOnly.ParseExact(p.Day, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue).AddHours(-12);
        JsonObject Me() => store.ShiftStatus()[p.Day] as JsonObject ?? new JsonObject();
        var me = Me();
        foreach (var (t, kind, _) in all)
        {
            if (t < lo || t > now || kind is not ("kitaku" or "neru" or "okita"))
            {
                continue;
            }
            var sleepAt = TimeUtil.ParseIso(JsonX.Str(me["sleep_at"]));
            if (kind == "kitaku")
            {
                if (JsonX.Str(me["kitaku"]) != TimeUtil.Iso(t))
                {
                    store.SetMark(p.Day, "kitaku", t, now, "phone");
                }
            }
            else if (kind == "neru")
            {
                if (sleepAt is not { } before || before < t)
                {
                    store.SetMark(p.Day, "sleep_at", t, now, "phone");
                }
            }
            else if (sleepAt is { } sa && t > sa && OkitaPending(me))
            {
                // at と events.log の時刻は iPhone の記録時刻 t（取り込みが遅れても起床時刻がずれない）
                store.SetShiftStatus(p.Day, "okita", t, $"phone {TimeUtil.Fmt(sa)}-{TimeUtil.Fmt(t)}");
                msg = m.Format("PHONE_OKITA", ("sleep_at", TimeUtil.Fmt(sa)), ("when", TimeUtil.Fmt(t)),
                    ("dur", TimeUtil.FmtDur(t - sa, m)), ("depart", TimeUtil.Fmt(p.DepartTarget)));
            }
            me = Me();
        }
        return msg;
    }

    /// <summary>
    /// iPhone の「おきた」をまだ当てていない状態か（phone._phone_okita_pending）．
    /// 状態なし → まだ．「起きた」済みでも，そのあとに寝た記録（二度寝）があれば再び当てる．休み・出発済みは触らない．
    /// </summary>
    private static bool OkitaPending(JsonObject me)
    {
        if (!JsonX.Truthy(me["status"]))
        {
            return true;
        }
        if (JsonX.Str(me["status"]) != "okita")
        {
            return false;
        }
        return TimeUtil.ParseIso(JsonX.Str(me["at"])) is { } at
            && TimeUtil.ParseIso(JsonX.Str(me["sleep_at"])) is { } sleepAt
            && at < sleepAt;
    }

    /// <summary>"2026-12-07 08:10" / "2026-12-07T08:10:30"（分まで読む）．読めなければ null．</summary>
    private static DateTime? ParseStamp(string s)
    {
        s = s.Replace('T', ' ');
        var match = Stamp().Match(s.Length > 16 ? s[..16] : s);
        if (!match.Success)
        {
            return null;
        }
        int N(int i) => int.Parse(match.Groups[i].ValueSpan, CultureInfo.InvariantCulture);
        try
        {
            return new DateTime(N(1), N(2), N(3), N(4), N(5), 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
