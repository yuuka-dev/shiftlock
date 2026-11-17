using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>寝られない時間帯（shifts.json の no_sleep の 1 件）。date か weekday のどちらかが合えば当たり。</summary>
public sealed record NoSleepRule(string? Date, string? Weekday, string From, string To);

/// <summary>その日だけの時刻（days.json の times、旧形式の shifts）。無い項目は設定の値を使う。</summary>
public sealed record DayTimes(string? StartTarget, string? StartLatest, double? SleepTargetHours, double? SleepMinHours)
{
    public static readonly DayTimes Empty = new(null, null, null, null);

    /// <summary>this に無い項目だけ other から埋める（this が優先）。</summary>
    public DayTimes Or(DayTimes other) => new(
        StartTarget ?? other.StartTarget,
        StartLatest ?? other.StartLatest,
        SleepTargetHours ?? other.SleepTargetHours,
        SleepMinHours ?? other.SleepMinHours);
}

/// <summary>
/// 乗務日と時刻の逆算に使う設定。shifts.json と、例外日をまとめたもの（Python 版の cfg["_days"]）から作る。
/// 既定値は Python 版 config.py の DEFAULTS と同じ（テストベクタの defaults シナリオで確かめている）。
/// </summary>
public sealed class PlanConfig
{
    public IReadOnlySet<string> WorkWeekdays { get; init; } = new HashSet<string> { "mon", "tue", "wed", "thu", "fri", "sat" };
    public string StartTarget { get; init; } = "20:00";
    public string StartLatest { get; init; } = "22:30";
    public double PrepMinutes { get; init; } = 90;
    public double SleepTargetHours { get; init; } = 5;
    public double SleepMinHours { get; init; } = 4;
    public double LastcallMinutes { get; init; } = 40;
    public IReadOnlyList<NoSleepRule> NoSleep { get; init; } = [];

    /// <summary>旧形式 shifts の日付（文字列でも { "date": ... } でも、ここに入った日は乗務日）。</summary>
    public IReadOnlySet<string> LegacyShiftDays { get; init; } = new HashSet<string>();
    /// <summary>旧形式 shifts の { "date": ..., "start_target": ... }。同じ日が複数あれば先に書いたほうが優先（まとめ済み）。</summary>
    public IReadOnlyDictionary<string, DayTimes> LegacyShiftTimes { get; init; } = new Dictionary<string, DayTimes>();

    /// <summary>休みの例外日（手動＋カレンダー）。</summary>
    public IReadOnlySet<string> DaysOff { get; init; } = new HashSet<string>();
    /// <summary>出勤の例外日（手動＋カレンダー）。</summary>
    public IReadOnlySet<string> DaysWork { get; init; } = new HashSet<string>();
    /// <summary>その日だけの時刻（days.json の times）。旧形式 shifts より優先。</summary>
    public IReadOnlyDictionary<string, DayTimes> DaysTimes { get; init; } = new Dictionary<string, DayTimes>();

    /// <summary>その日の時刻の上書き（Python: day_times）。</summary>
    public DayTimes TimesFor(string day)
    {
        var t = DaysTimes.GetValueOrDefault(day, DayTimes.Empty);
        return LegacyShiftTimes.TryGetValue(day, out var legacy) ? t.Or(legacy) : t;
    }

    /// <summary>shifts.json の内容と、まとめた例外日（{ off, work, times }）から作る。どちらも null なら既定値。</summary>
    public static PlanConfig FromJson(JsonNode? shifts, JsonNode? days = null)
    {
        var o = shifts as JsonObject;
        var def = new PlanConfig();

        var legacyDays = new HashSet<string>();
        var legacyTimes = new Dictionary<string, DayTimes>();
        if (o?["shifts"] is JsonArray arr)
        {
            foreach (var x in arr)
            {
                if (x is JsonValue && Str(x) is { } s)
                {
                    legacyDays.Add(s);
                }
                else if (x is JsonObject xo && Str(xo["date"]) is { } d)
                {
                    legacyDays.Add(d);
                    var t = ReadTimes(xo);
                    legacyTimes[d] = legacyTimes.TryGetValue(d, out var prev) ? prev.Or(t) : t;
                }
            }
        }

        var noSleep = new List<NoSleepRule>();
        if (o?["no_sleep"] is JsonArray rules)
        {
            foreach (var r in rules.OfType<JsonObject>())
            {
                noSleep.Add(new NoSleepRule(Str(r["date"]), Str(r["weekday"]),
                    Str(r["from"]) ?? throw new FormatException("no_sleep に from が無い"),
                    Str(r["to"]) ?? throw new FormatException("no_sleep に to が無い")));
            }
        }

        var dd = days as JsonObject;
        var times = new Dictionary<string, DayTimes>();
        if (dd?["times"] is JsonObject to)
        {
            foreach (var (k, v) in to)
            {
                if (v is JsonObject vo)
                    times[k] = ReadTimes(vo);
            }
        }

        return new PlanConfig
        {
            WorkWeekdays = o?["work_weekdays"] is JsonArray ww
                ? ww.Select(Str).OfType<string>().ToHashSet()
                : def.WorkWeekdays,
            StartTarget = Str(o?["start_target"]) ?? def.StartTarget,
            StartLatest = Str(o?["start_latest"]) ?? def.StartLatest,
            PrepMinutes = Num(o?["prep_minutes"]) ?? def.PrepMinutes,
            SleepTargetHours = Num(o?["sleep_target_hours"]) ?? def.SleepTargetHours,
            SleepMinHours = Num(o?["sleep_min_hours"]) ?? def.SleepMinHours,
            LastcallMinutes = Num(o?["lastcall_minutes"]) ?? def.LastcallMinutes,
            NoSleep = noSleep,
            LegacyShiftDays = legacyDays,
            LegacyShiftTimes = legacyTimes,
            DaysOff = Keys(dd?["off"]),
            DaysWork = Keys(dd?["work"]),
            DaysTimes = times,
        };
    }

    private static DayTimes ReadTimes(JsonObject o) => new(
        Str(o["start_target"]), Str(o["start_latest"]),
        Num(o["sleep_target_hours"]), Num(o["sleep_min_hours"]));

    private static HashSet<string> Keys(JsonNode? n) =>
        n is JsonObject o ? o.Select(kv => kv.Key).ToHashSet() : [];

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>数値。Python 版が float() で読むので、数字の文字列（"90"）も通す。</summary>
    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v)
            return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<double>(),
            JsonValueKind.String => double.Parse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
