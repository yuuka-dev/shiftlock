using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 強制モードの発動判定と緊急解除（pc/shiftlock/enforce.py の enforce_due・enforce_suffix・enforce_release）．
/// 強制モードは，本人が Python 版の shl enforce on を対話的に実行して同意した乗務にだけ効く．
/// ここには宣言・取り消し・再開の入口を置かない（同意は Python 版の対話入力だけ．CLAUDE.md）．
/// 判断に使う値が読めないときは例外を投げ，呼ぶ側（Tick）が「発動せず」として扱う．
/// </summary>
public static class Enforce
{
    private const string Title = "shiftlock";

    /// <summary>前に見た回からこれより空いたら離席をまたいだとみなし，予告からやり直す．</summary>
    private static readonly TimeSpan SeenGap = TimeSpan.FromMinutes(12);

    /// <summary>実行までの残りがこれ以下になったら「あと N 分」を 1 回出す．</summary>
    private static readonly TimeSpan SoonWindow = TimeSpan.FromMinutes(5);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>乗務日 day の宣言の写し（enforce.enforce_state）．無ければ空．書き換えても state.json は変わらない．</summary>
    public static JsonObject State(Store store, string day) =>
        ((store.ShiftStatus()[day] as JsonObject)?["enforce"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();

    /// <summary>
    /// 強制モードの判断（enforce.enforce_due）．戻り値は actions に足す要素（予告の通知か，Method が "enforce" の実行）．
    /// 猶予は最低ラインからではなく，PC を触っている状態で出した最初の予告（warned_at）から数える．
    /// 離席をまたいだら（前に見た回から 12 分以上）予告からやり直す．dryRun なら状態を進めない．
    /// 宣言の中身が読めなければ InvalidDataException（発動しない）．
    /// </summary>
    public static IReadOnlyList<TickAction> Due(ShiftConfig cfg, Store store, Messages m, DateTime now, string phase, Plan? p,
        bool active, double? uptimeSeconds, bool remote, double idleSeconds, bool dryRun)
    {
        if (phase != "hard" || p is null || !active)
        {
            return [];
        }
        var e = State(store, p.Day);
        if (!JsonX.Truthy(e["consented_at"]) || JsonX.Truthy(e["released_at"]) || store.StopActive)
        {
            return [];
        }
        if (LimitStages.IsLimitAction(JsonX.Str(e["action"])))
        {
            return [];   // 減速・一時停止は Agent の段階の処理が持つ．ここは何も返さず，seen_at・warned_at も書かない
        }
        var ec = cfg.Section("enforce");
        if (remote && (!ec.TryGetPropertyValue("skip_if_remote", out var skip) || JsonX.Truthy(skip)))
        {
            return [];
        }
        var bootGrace = Number(e, ec, "boot_grace_minutes");
        if (uptimeSeconds is not { } uptime || !(uptime > 0) || uptime < bootGrace * 60)
        {
            return [];   // 起動直後・起動からの秒数が不明（0・NaN）は発動しない
        }
        var action = JsonX.Str(Pick(e, ec, "action")) ?? throw Unreadable("action");
        var label = m.Map("ENFORCE_LABEL").TryGetValue(action, out var known) ? known : action;
        var grace = Number(e, ec, "grace_minutes");
        var relock = Number(e, ec, "relock_minutes");
        var maxRuns = Py.Int(Pick(e, ec, "max_runs")) ?? throw Unreadable("max_runs");
        var runs = e.TryGetPropertyValue("runs", out var runsNode)
            ? (runsNode as JsonArray)?.DeepClone() as JsonArray ?? throw Unreadable("runs")
            : [];
        var lastInput = now - TimeSpan.FromSeconds(double.IsNaN(idleSeconds) ? 0 : Math.Clamp(idleSeconds, 0, 1e9));
        var hard = NotifyLog.Method(cfg.Section("notify"), "hard", "popup");

        TickAction Notice(string text) => new(hard, "enforce", Title, text, Speak: true);

        void Save()
        {
            if (!dryRun)
            {
                SetEnforce(store, p.Day, e, now, "enforce_state", "");
            }
        }

        if (JsonX.Truthy(e["seen_at"]) && now - Time(e, "seen_at") > SeenGap)
        {
            e.Remove("warned_at");   // 離席をまたいだ → 戻ってきたら必ず予告からやり直す
            e.Remove("rewarned_at");
        }
        e["seen_at"] = Store.IsoSeconds(now);
        Save();

        if (runs.Count > 0)
        {
            var first = JsonX.Truthy(e["first_run_at"]) ? Time(e, "first_run_at") : RunTime(runs[0]);
            if (now >= first + TimeSpan.FromHours(Number(e, ec, "duration_hours")))
            {
                return [];   // 時間切れ．最低ラインを過ぎたままでももう何もしない
            }
            if (runs.Count >= maxRuns)
            {
                return [];
            }
            var last = RunTime(runs[^1]);
            if (lastInput <= last)
            {
                return [];   // ロック直後で誰も触っていない（最後の入力が実行より前）→ 無人の画面に再予告しない
            }
            if (!JsonX.Truthy(e["rewarned_at"]) || Time(e, "rewarned_at") < last)
            {
                e["rewarned_at"] = Store.IsoSeconds(now);
                Save();
                return [Notice(m.Format("ENFORCE_REWARN", ("relock", G(relock)), ("label", label)))];
            }
            if (now < Time(e, "rewarned_at") + TimeSpan.FromMinutes(relock))
            {
                return [];
            }
        }
        else
        {
            if (!JsonX.Truthy(e["warned_at"]))
            {
                e["warned_at"] = Store.IsoSeconds(now);
                Save();
                return [Notice(m.Format("ENFORCE_WARN", ("grace", G(grace)), ("label", label)))];
            }
            var remaining = Time(e, "warned_at") + TimeSpan.FromMinutes(grace) - now;
            if (remaining > TimeSpan.Zero)
            {
                var mins = (long)Math.Ceiling(remaining.TotalSeconds / 60);
                var warned = JsonX.Str(e["warned_at"]);
                if (remaining <= SoonWindow && (dryRun || NotifyLog.Once(store, $"{p.Day}:enforce:warn5:{warned}", now)))
                {
                    return [Notice(m.Format("ENFORCE_WARN_SOON", ("mins", mins), ("label", label)))];
                }
                return [];
            }
        }

        runs.Add(new JsonObject { ["at"] = Store.IsoSeconds(now), ["action"] = action });
        e["runs"] = runs;
        if (!e.ContainsKey("first_run_at"))
        {
            e["first_run_at"] = runs[0]!["at"]!.DeepClone();
        }
        if (!dryRun)
        {
            SetEnforce(store, p.Day, e, now, "enforce_run", action);
        }
        return [new TickAction("enforce", "enforce", Title, m.Format("ENFORCE_RUN", ("label", label)), Speak: true, Action: action)];
    }

    /// <summary>
    /// 宣言済みの乗務の通知に添える一言（enforce.enforce_suffix）．予約の内容を知らせるだけで，
    /// 宣言していない乗務・解除済みの宣言には何も出さない．
    /// </summary>
    public static string Suffix(Store store, Messages m, Plan p, string phase)
    {
        if (phase is not ("lastcall" or "soft" or "hard"))
        {
            return "";
        }
        var e = State(store, p.Day);
        if (!JsonX.Truthy(e["consented_at"]) || JsonX.Truthy(e["released_at"]))
        {
            return "";
        }
        var action = ShiftConfig.PyText(e["action"]);
        var label = m.Map("ENFORCE_LABEL").TryGetValue(action, out var known) ? known : action;
        if (LimitStages.IsLimitAction(action))
        {
            return m.Format("ENFORCE_SUFFIX_LIMIT", ("label", label), ("stop", store.StopActive ? m["ENFORCE_SUFFIX_STOP"] : ""));
        }
        var hours = !e.ContainsKey("duration_hours") ? "2"
            : Py.Float(e["duration_hours"]) is { } h ? G(h)
            : ShiftConfig.PyText(e["duration_hours"]);   // 読めない値はそのまま見せる（通知を止めない）
        return m.Format("ENFORCE_SUFFIX", ("label", label), ("hours", hours),
            ("stop", store.StopActive ? m["ENFORCE_SUFFIX_STOP"] : ""));
    }

    /// <summary>
    /// 緊急解除（enforce.enforce_release）．STOP.txt に 1 行足し，同意済みで未解除の宣言に released_at を付け，
    /// events.log に enforce_release（メモは reason）を書く．宣言が無くても解除の操作は記録に残す（乗務日は "-"）．
    /// 返り値は知らせる文．途中の手順が失敗しても残りは行い，最後に StateBusyException を投げる
    /// （STOP.txt か released_at のどちらかが書けていれば強制は止まる）．
    /// </summary>
    public static string Release(Store store, Messages m, DateTime now, string reason)
    {
        var already = store.StopActive;
        Exception? failure = null;
        try
        {
            var line = now.ToString("yyyy'-'MM'-'dd' 'HH':'mm", CultureInfo.InvariantCulture) + " " + reason + "\n";
            File.AppendAllText(store.P(Store.StopFile), line, Utf8NoBom);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failure = e;
        }

        var released = 0;
        try
        {
            var shifts = store.ReadStateForWrite()["shifts"] as JsonObject ?? new JsonObject();
            foreach (var day in shifts.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList())
            {
                if ((shifts[day] as JsonObject)?["enforce"] is not JsonObject e
                    || !JsonX.Truthy(e["consented_at"]) || JsonX.Truthy(e["released_at"]))
                {
                    continue;
                }
                var updated = (JsonObject)e.DeepClone();
                updated["released_at"] = TimeUtil.Iso(now);
                SetEnforce(store, day, updated, now, "enforce_release", reason);
                released++;
            }
            if (released == 0)
            {
                store.AppendEvent(now, "-", "enforce_release", reason);
            }
        }
        catch (StateBusyException e)
        {
            failure ??= e;
        }

        if (failure is not null)
        {
            throw new StateBusyException("緊急解除の一部を記録できなかった", failure);
        }
        return m[already && released == 0 ? "ENFORCE_RELEASE_ALREADY" : "ENFORCE_RELEASED"];
    }

    /// <summary>
    /// 宣言を state.json に書く（enforce.set_enforce）．kind が enforce_state 以外なら events.log にも 1 行足す．
    /// 新しい宣言を作る入口ではない（呼ぶのは発動の経過と解除の記録だけ）．
    /// </summary>
    private static void SetEnforce(Store store, string day, JsonObject e, DateTime now, string kind, string note)
    {
        var state = store.ReadStateForWrite();
        Store.ShiftForWrite(Store.ShiftsForWrite(state), day)["enforce"] = e.DeepClone();
        store.WriteState(state);
        if (kind != "enforce_state")
        {
            store.AppendEvent(now, day, kind, note);
        }
    }

    /// <summary>宣言に書いてある値．無ければ設定の値（同意した内容を固定してあるので，普通は宣言のほうにある）．</summary>
    private static JsonNode? Pick(JsonObject e, JsonObject ec, string key) =>
        e.TryGetPropertyValue(key, out var own) ? own : ec[key];

    private static double Number(JsonObject e, JsonObject ec, string key) =>
        Py.Float(Pick(e, ec, key)) is { } v && double.IsFinite(v) && Math.Abs(v) <= 1_000_000 ? v : throw Unreadable(key);

    private static DateTime Time(JsonObject e, string key) =>
        TimeUtil.ParseIso(JsonX.Str(e[key])) ?? throw Unreadable(key);

    private static DateTime RunTime(JsonNode? run) =>
        TimeUtil.ParseIso(JsonX.Str((run as JsonObject)?["at"])) ?? throw Unreadable("runs");

    private static InvalidDataException Unreadable(string key) => new($"enforce.{key}");

    /// <summary>Python の f"{x:g}"（有効 6 桁．15.0 → "15"，2.5 → "2.5"）．</summary>
    private static string G(double v) =>
        v.ToString("G6", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
}
