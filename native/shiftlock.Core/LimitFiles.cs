using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>上限をかけたアプリ 1 つ．Job はジョブの名前，Percent は PC 全体に対する上限（%）．</summary>
public sealed record CappedApp(string Job, double Percent);

/// <summary>止めたプロセス 1 つ．(Pid, Start) で同じプロセスかを確かめる（PID は使い回される）．</summary>
public sealed record SuspendedProc(int Pid, DateTime Start, string Exe)
{
    public ProcKey Key => new(Pid, Start);
}

/// <summary>
/// limit.json の中身（設計書 §5.3．data-format.md）．Day は乗務日（変わったら Baseline を捨てる）．
/// Announced は予告・報告を出した段階の印，Pending は処理中で待たせている通知（印 → 待ち始めた時刻）．
/// </summary>
public sealed record LimitState(
    string? Day, IReadOnlyDictionary<string, double> Baseline, IReadOnlyDictionary<string, CappedApp> Capped,
    IReadOnlyList<SuspendedProc> Suspended, IReadOnlySet<string> Skipped, IReadOnlySet<string> Announced,
    IReadOnlyDictionary<string, DateTime> Pending)
{
    public static LimitState Empty { get; } = new(
        null, new Dictionary<string, double>(), new Dictionary<string, CappedApp>(), [],
        new HashSet<string>(), new HashSet<string>(), new Dictionary<string, DateTime>());

    /// <summary>上限か一時停止がかかっている（元に戻すものがある）．</summary>
    public bool HasEffects => Capped.Count > 0 || Suspended.Count > 0;
}

/// <summary>
/// 減速・一時停止が使う実行フォルダのファイル（limit.json・limit-games.json・claude-busy.json）の読み書き．
/// 書き込みは Store の道（.tmp に書いてから置き換え）．Python 側が同じファイルを読み書きする（pc/shiftlock/limit.py・hook.py）．
/// </summary>
public static class LimitFiles
{
    public const string StateFile = "limit.json";
    public const string GamesFile = "limit-games.json";
    public const string BusyFile = "claude-busy.json";

    private const string TimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff";   // Start が 100ns 単位で戻るように
    private static readonly TimeSpan GamesKeep = TimeSpan.FromDays(30);
    private static readonly TimeSpan GamesRefresh = TimeSpan.FromDays(1);
    private static readonly TimeSpan BusyKeep = TimeSpan.FromMinutes(30);
    private static readonly string[] Verdicts = [LimitTargets.VerdictTarget, LimitTargets.VerdictAntiCheat, LimitTargets.VerdictUnknown];

    // ---- limit.json ----

    /// <summary>
    /// limit.json を読む．無い → Empty．壊れている（途中まで書かれた・形が違う）→ InvalidDataException
    /// （止めたプロセスの記録を読み違えて空にしないため，呼び出し側が QuarantineState で退避して知らせる）．
    /// 開けない（ほかのプロセスが掴んでいる）→ IOException のまま．
    /// </summary>
    public static LimitState ReadState(Store store)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(store.P(StateFile));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return LimitState.Empty;
        }
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject root)
            {
                throw new InvalidDataException("limit.json がオブジェクトでない");
            }
            return Parse(root);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("limit.json の形がおかしい", e);
        }
    }

    /// <summary>limit.json を書く．空のものは書かない（{} になる）．</summary>
    public static void WriteState(Store store, LimitState s)
    {
        var root = new JsonObject();
        if (s.Day is not null)
        {
            root["day"] = s.Day;
        }
        if (s.Baseline.Count > 0)
        {
            var baseline = new JsonObject();
            foreach (var (exe, pct) in s.Baseline)
            {
                baseline[exe] = pct;
            }
            root["baseline"] = baseline;
        }
        if (s.Capped.Count > 0)
        {
            var capped = new JsonObject();
            foreach (var (exe, c) in s.Capped)
            {
                capped[exe] = new JsonObject { ["job"] = c.Job, ["percent"] = c.Percent };
            }
            root["capped"] = capped;
        }
        if (s.Suspended.Count > 0)
        {
            root["suspended"] = new JsonArray([.. s.Suspended.Select(p => (JsonNode)new JsonObject
            {
                ["pid"] = p.Pid,
                ["start"] = p.Start.ToString(TimeFormat, CultureInfo.InvariantCulture),
                ["exe"] = p.Exe,
            })]);
        }
        if (s.Skipped.Count > 0)
        {
            root["skipped"] = new JsonArray([.. s.Skipped.Order(StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!)]);
        }
        if (s.Announced.Count > 0)
        {
            root["announced"] = new JsonArray([.. s.Announced.Order(StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)!)]);
        }
        if (s.Pending.Count > 0)
        {
            var pending = new JsonObject();
            foreach (var (key, at) in s.Pending)
            {
                pending[key] = Store.IsoSeconds(at);
            }
            root["pending"] = pending;
        }
        store.WriteJson(StateFile, root);
    }

    /// <summary>読めない limit.json を limit.json.bad に名前を変える（あれば上書き）．limit.json が無ければ何もしない．</summary>
    public static void QuarantineState(Store store)
    {
        var path = store.P(StateFile);
        if (File.Exists(path))
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
    }

    private static LimitState Parse(JsonObject root)
    {
        var day = root["day"] is { } d ? JsonX.Str(d) ?? throw Bad("day") : null;

        var baseline = new Dictionary<string, double>();
        foreach (var (exe, v) in ObjectOf(root, "baseline"))
        {
            baseline[exe] = Number(v, "baseline");
        }

        var capped = new Dictionary<string, CappedApp>();
        foreach (var (exe, v) in ObjectOf(root, "capped"))
        {
            capped[exe] = v is JsonObject c
                ? new CappedApp(JsonX.Str(c["job"]) ?? throw Bad("capped.job"), Number(c["percent"], "capped.percent"))
                : throw Bad("capped");
        }

        var suspended = new List<SuspendedProc>();
        foreach (var item in ArrayOf(root, "suspended"))
        {
            if (item is not JsonObject p)
            {
                throw Bad("suspended");
            }
            var pid = p["pid"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.Number && pv.TryGetValue<int>(out var n) ? n : throw Bad("suspended.pid");
            var start = TimeUtil.ParseIso(JsonX.Str(p["start"])) ?? throw Bad("suspended.start");
            suspended.Add(new SuspendedProc(pid, start, JsonX.Str(p["exe"]) ?? throw Bad("suspended.exe")));
        }

        var pending = new Dictionary<string, DateTime>();
        foreach (var (key, v) in ObjectOf(root, "pending"))
        {
            pending[key] = TimeUtil.ParseIso(JsonX.Str(v)) ?? throw Bad("pending");
        }

        return new LimitState(day, baseline, capped, suspended, Strings(root, "skipped"), Strings(root, "announced"), pending);
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> ObjectOf(JsonObject root, string key) =>
        root[key] switch
        {
            null => [],
            JsonObject o => o,
            _ => throw Bad(key),
        };

    private static IEnumerable<JsonNode?> ArrayOf(JsonObject root, string key) =>
        root[key] switch
        {
            null => [],
            JsonArray a => a,
            _ => throw Bad(key),
        };

    private static HashSet<string> Strings(JsonObject root, string key) =>
        [.. ArrayOf(root, key).Select(x => JsonX.Str(x) ?? throw Bad(key))];

    private static double Number(JsonNode? n, string key) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : throw Bad(key);

    private static InvalidDataException Bad(string key) => new("limit.json の " + key + " の形がおかしい");

    // ---- limit-games.json ----

    /// <summary>
    /// 見たゲームの判定（exe 名 → target｜anticheat｜unknown）．seen が 30 日より古い・形がおかしい項目は捨てる．
    /// 無い・壊れている → 空（pc/shiftlock/limit.py の read_games と同じ．exe 名は書いてあるままで，正規化しない）．
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadGames(Store store, DateTime now) =>
        ReadGameEntries(store).Where(e => !Expired(e.Value.Seen, now)).ToDictionary(e => e.Key, e => e.Value.Verdict);

    /// <summary>
    /// 今の回に見たゲームの判定を書き足す．exe 名は NormalizeExe でそろえる．判定が変わった・新しいときと，
    /// 同じ判定の seen が 1 日以上古いときだけ書き直し，変わらなければファイルに触らない．30 日見ないものは消す．
    /// </summary>
    public static void UpdateGames(Store store, IReadOnlyDictionary<string, string> seen, DateTime now)
    {
        var all = ReadGameEntries(store);
        var next = all.Where(e => !Expired(e.Value.Seen, now)).ToDictionary(e => e.Key, e => e.Value);
        var stamp = TimeUtil.ParseIso(Store.IsoSeconds(now))!.Value;
        foreach (var (name, verdict) in seen)
        {
            if (MeterSettings.NormalizeExe(name) is not { } exe || !Verdicts.Contains(verdict))
            {
                continue;
            }
            if (next.TryGetValue(exe, out var old) && old.Verdict == verdict && now - old.Seen < GamesRefresh)
            {
                continue;
            }
            next[exe] = (verdict, stamp);
        }

        if (next.Count == all.Count && next.All(e => all.TryGetValue(e.Key, out var o) && o == e.Value))
        {
            return;
        }
        var root = new JsonObject();
        foreach (var (exe, (verdict, at)) in next.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            root[exe] = new JsonObject { ["verdict"] = verdict, ["seen"] = Store.IsoSeconds(at) };
        }
        store.WriteJson(GamesFile, root);
    }

    private static bool Expired(DateTime seen, DateTime now) => now - seen > GamesKeep;

    /// <summary>形の合う項目だけ（年齢は見ない）．読めないファイルは空．</summary>
    private static Dictionary<string, (string Verdict, DateTime Seen)> ReadGameEntries(Store store)
    {
        var entries = new Dictionary<string, (string Verdict, DateTime Seen)>();
        var raw = JsonFiles.ReadObjectOrEmpty(store.P(GamesFile));
        foreach (var (exe, node) in raw)
        {
            if (node is JsonObject v
                && JsonX.Str(v["verdict"]) is { } verdict && Verdicts.Contains(verdict)
                && TimeUtil.ParseIso(JsonX.Str(v["seen"])) is { } seen)
            {
                entries[exe] = (verdict, seen);
            }
        }
        return entries;
    }

    // ---- claude-busy.json ----

    /// <summary>
    /// Claude Code が処理中か．30 分以内の印が 1 つでもあれば真．30 分より古い印（Stop が来なかった）と
    /// 今より後の印（時計が戻った）は処理中でない．読めない・無い → 偽．例外は出さない．
    /// </summary>
    public static bool ClaudeBusy(Store store, DateTime now)
    {
        try
        {
            foreach (var (_, node) in JsonFiles.ReadObjectOrEmpty(store.P(BusyFile)))
            {
                if (TimeUtil.ParseIso(JsonX.Str(node)) is { } at && at <= now && now - at <= BusyKeep)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            // 読めなければ処理中でないとして進む（通知を待たせ続けない）
        }
        return false;
    }
}
