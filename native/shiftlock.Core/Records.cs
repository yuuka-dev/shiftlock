using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>取り消せる記録の目印（state.json の undo.at と undo.what の先頭）．自分が書いた undo かを見分ける．</summary>
public sealed record UndoToken(string At, string What);

/// <summary>ボタン 1 つぶんの結果．Message は画面に出す文（先頭の "[shiftlock] " は外してある）．Recorded は記録したか．</summary>
public sealed record RecordResult(string Message, bool Recorded, UndoToken? Undo);

/// <summary>
/// 画面のボタンの記録（寝る・起きた・行ってきます・帰ってきた）と取り消し．
/// 判断と返事は pc/shiftlock/records.py の do_neru / do_okita / do_shuppatsu / do_kitaku / undo_fuzzy と同じ．
/// Python 版との違い: 出所は "app"．4 つとも，記録すると決まったときだけ取り消し用の undo を残す（形は apply_intents と同じ）．
/// state.json は 1 回の操作で 1 回だけ書き，そのあと events.log に足す．state.json が読めない・書けないときは何も書かずに断る．
/// </summary>
public static class Records
{
    /// <summary>events.log のメモに書く出所．</summary>
    public const string Via = "app";

    /// <summary>乗務の無い日の記録の乗務日．</summary>
    private const string NoDay = "-";

    private static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(60);

    /// <summary>「行ってきます」を受け付ける早さ（出発目安の 2 時間前から）．</summary>
    private static readonly TimeSpan ShuppatsuLead = TimeSpan.FromHours(2);

    /// <summary>「寝る」（records.do_neru）．</summary>
    public static RecordResult Neru(ShiftConfig cfg, Store store, Messages m, DateTime now) => Guard(m, () =>
    {
        var p = Planner.TargetPlan(cfg, now);
        var state = store.ReadStateForWrite();
        if (p is null)
        {
            return Commit(store, state, now, NoDay, "neru", Text(m, "RECORDS_NERU_FREE"), null, NoDay, "neru", "free-day");
        }
        var msg = Text(m, "RECORDS_NERU", ("when", TimeUtil.Fmt(now)), ("hours", cfg.AutoWakeHoursText),
            ("depart", TimeUtil.Fmt(p.DepartTarget)), ("latest", TimeUtil.Fmt(p.DepartLatest)));
        return Commit(store, state, now, p.Day, "neru", msg, shift => shift["sleep_at"] = TimeUtil.Iso(now),
            p.Day, "sleep_at", $"{Via} {TimeUtil.Iso(now)}");
    });

    /// <summary>「帰ってきた」（records.do_kitaku）．</summary>
    public static RecordResult Kitaku(ShiftConfig cfg, Store store, Messages m, DateTime now) => Guard(m, () =>
    {
        var p = Planner.TargetPlan(cfg, now);
        var state = store.ReadStateForWrite();
        if (p is null)
        {
            return Commit(store, state, now, NoDay, "kitaku", Text(m, "RECORDS_KITAKU_FREE"), null, NoDay, "kitaku", "free-day");
        }
        return Commit(store, state, now, p.Day, "kitaku", Text(m, "RECORDS_KITAKU", ("when", TimeUtil.Fmt(now))),
            shift => shift["kitaku"] = TimeUtil.Iso(now), p.Day, "kitaku", $"{Via} {TimeUtil.Iso(now)}");
    });

    /// <summary>「起きた」（records.do_okita）．強制中で寝た記録が無ければ記録しない．休み・出発済みの乗務は状態を変えずに行だけ残す．</summary>
    public static RecordResult Okita(ShiftConfig cfg, Store store, Messages m, DateTime now) => Guard(m, () =>
    {
        var p = Planner.TargetPlan(cfg, now);
        var state = store.ReadStateForWrite();
        if (p is null)
        {
            return Commit(store, state, now, NoDay, "okita", Text(m, "RECORDS_OKITA_FREE"), null, NoDay, "okita", "free-day");
        }
        var me = ShiftOf(state, p.Day) ?? new JsonObject();
        var enforce = me["enforce"] as JsonObject;
        if (JsonX.Truthy(enforce?["consented_at"]) && !JsonX.Truthy(enforce?["released_at"]) && !store.StopActive
            && now >= p.HardClose && !JsonX.Truthy(me["sleep_at"]) && store.RecentLongGap(now) != true)
        {
            // 強制中は「起きた」だけでは解除しない（寝た記録が要る）．断るので undo にも触らない
            return new RecordResult(Text(m, "RECORDS_OKITA_ENFORCE_GATE"), false, null);
        }

        var sleptAt = TimeUtil.ParseIso(JsonX.Str(me["sleep_at"]));
        var sleepNote = sleptAt is { } sa0 ? $"neru {TimeUtil.Fmt(sa0)}-{TimeUtil.Fmt(now)}" : "";
        var status = JsonX.Str(me["status"]);
        if (status is "yasumi" or "worked")
        {
            // 休み／出発済みの乗務は status を触らない（okita にするとレポートで出勤扱いになる）．events.log の行だけ残す
            var day = TimeUtil.FmtMd(p.StartLatest);
            var what = Label(m, status);
            var keep = sleptAt is { } sa1
                ? Text(m, "RECORDS_OKITA_KEEP_SLEPT", ("sleep_at", TimeUtil.Fmt(sa1)), ("when", TimeUtil.Fmt(now)),
                    ("dur", TimeUtil.FmtDur(now - sa1, m)), ("day", day), ("what", what))
                : Text(m, "RECORDS_OKITA_KEEP", ("day", day), ("what", what));
            return Commit(store, state, now, p.Day, "okita", keep, null, p.Day, "okita", sleepNote);
        }

        string msg;
        string note;
        if (sleptAt is { } sa)
        {
            msg = Text(m, "RECORDS_OKITA_SLEPT", ("sleep_at", TimeUtil.Fmt(sa)), ("when", TimeUtil.Fmt(now)),
                ("dur", TimeUtil.FmtDur(now - sa, m)), ("depart", TimeUtil.Fmt(p.DepartTarget)), ("latest", TimeUtil.Fmt(p.DepartLatest)));
            if (now - sa < TimeSpan.FromHours(cfg.SleepMinHours))
            {
                msg += Text(m, "RECORDS_OKITA_SHORT", ("min_h", cfg.SleepMinHoursText));
            }
            note = sleepNote;
        }
        else
        {
            var slept = store.RecentLongGap(now);
            msg = Text(m, "RECORDS_OKITA", ("depart", TimeUtil.Fmt(p.DepartTarget)), ("latest", TimeUtil.Fmt(p.DepartLatest)));
            if (slept == false)
            {
                msg += Text(m, "RECORDS_OKITA_NO_GAP");
            }
            note = slept == false ? "no-gap" : "";
        }
        return Commit(store, state, now, p.Day, "okita", msg, shift => SetStatus(shift, "okita", now), p.Day, "okita", note);
    });

    /// <summary>「行ってきます」（records.do_shuppatsu）．乗務が無い・出発目安の 2 時間より前なら断る．</summary>
    public static RecordResult Shuppatsu(ShiftConfig cfg, Store store, Messages m, DateTime now) => Guard(m, () =>
    {
        var p = Planner.TargetPlan(cfg, now);
        if (p is null)
        {
            return new RecordResult(Text(m, "RECORDS_SHUPPATSU_NO_PLAN"), false, null);
        }
        var day = TimeUtil.FmtMd(p.StartLatest);
        if (now < p.DepartTarget - ShuppatsuLead)
        {
            return new RecordResult(
                Text(m, "RECORDS_SHUPPATSU_EARLY", ("day", day), ("depart", TimeUtil.Fmt(p.DepartTarget))), false, null);
        }
        var state = store.ReadStateForWrite();
        return Commit(store, state, now, p.Day, "worked", Text(m, "RECORDS_SHUPPATSU", ("day", day)),
            shift => SetStatus(shift, "worked", now), p.Day, "worked", "shuppatsu");
    });

    /// <summary>
    /// 「取り消す」を出してよいか: state.json の undo が token のもの（このアプリが最後に書いた記録）のままで，60 分以内．
    /// Python 版が書いた undo や，例外日を足した事後報告の undo はアプリからは戻さない．
    /// </summary>
    /// <remarks>state.json が在るのに読めなかったとき（ほかの書き手が置き換えている最中など）は null（わからない．次の機会に確かめる）．</remarks>
    public static bool? CanUndo(Store store, UndoToken token, DateTime now)
    {
        if (!JsonFiles.TryReadObject(store.P("state.json"), out var state))
        {
            return null;
        }
        return ReadUndo(state["undo"] as JsonObject, now) is { } undo && Matches(undo, token);
    }

    /// <summary>
    /// 直前の記録を取り消す（records.undo_fuzzy）．60 分以内の分だけ．強制モードの宣言・実行・解除は戻さない．
    /// only を渡すと，state.json の undo がその記録のままのときだけ戻す（画面の「取り消す」用．
    /// 押すまでの間にほかの書き手が undo を書き換えていたら，その記録は戻さない）．
    /// </summary>
    public static RecordResult Undo(Store store, Messages m, DateTime now, UndoToken? only = null) => Guard(m, () =>
    {
        var state = store.ReadStateForWrite();
        if (ReadUndo(state["undo"] as JsonObject, now) is not { } u || (only is not null && !Matches(u, only)))
        {
            return new RecordResult(Text(m, "RECORDS_UNDO_NOTHING"), false, null);
        }
        if (u.Day != NoDay)
        {
            var prev = u.Prev;
            prev.Remove("enforce");
            if (ShiftOf(state, u.Day)?["enforce"] is { } currentEnforce)
            {
                prev["enforce"] = currentEnforce.DeepClone();
            }
            if (prev.Count > 0)
            {
                Shifts(state, create: true)![u.Day] = prev;
            }
            else
            {
                Shifts(state, create: false)?.Remove(u.Day);
            }
        }
        state.Remove("undo");
        store.WriteState(state);
        var labels = string.Join(m["INTENT_LABEL_SEP"], u.What.Select(w => Label(m, w)));
        var done = new RecordResult(Text(m, "RECORDS_UNDO_DONE", ("what", labels)), true, null);
        AppendAfterState(store, done, now, u.Day, "undo", string.Join(",", u.What));
        return done;
    });

    // ---- 部品 ----

    private sealed record UndoEntry(string Day, JsonObject Prev, string At, IReadOnlyList<string> What);

    /// <summary>state.json は書けたが events.log に足せなかった．Done は「記録できた」としての返事．</summary>
    private sealed class LogBusyException(RecordResult done, Exception inner) : Exception(inner.Message, inner)
    {
        public RecordResult Done { get; } = done;
    }

    /// <summary>state.json の undo が token の記録（このアプリが書いたもの）のままか．</summary>
    private static bool Matches(UndoEntry u, UndoToken token) =>
        u.At == token.At && u.What.Count > 0 && u.What[0] == token.What;

    /// <summary>
    /// state.json を書いたあとの events.log への追記．ここで失敗しても記録は済んでいるので，
    /// 「記録できなかった」とは返さない（押し直すと二重になる）．履歴に書けなかったことを返事に添える（Guard）．
    /// </summary>
    private static void AppendAfterState(Store store, RecordResult done, DateTime now, string day, string kind, string note)
    {
        try
        {
            store.AppendEvent(now, day, kind, note);
        }
        catch (StateBusyException e)
        {
            throw new LogBusyException(done, e);
        }
    }

    /// <summary>state.json の undo のうち，いま取り消せるもの（60 分以内．例外日を足した事後報告のものは除く）．無ければ null．</summary>
    private static UndoEntry? ReadUndo(JsonObject? u, DateTime now)
    {
        if (u is null || JsonX.Str(u["day"]) is not { } day || JsonX.Str(u["at"]) is not { } at
            || TimeUtil.ParseIso(at) is not { } when || now - when > UndoWindow || JsonX.Truthy(u["days_work_added"]))
        {
            return null;
        }
        var what = (u["what"] as JsonArray)?.Select(JsonX.Str).OfType<string>().ToList() ?? [];
        var prev = (u["prev"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        return new UndoEntry(day, prev, at, what);
    }

    /// <summary>
    /// 記録する: 取り消し用に直前の状態を undo に残し，乗務の記録を変え，state.json を書いてから events.log に 1 行足す．
    /// </summary>
    private static RecordResult Commit(Store store, JsonObject state, DateTime now, string day, string what, string message,
        Action<JsonObject>? mutateShift, string eventDay, string eventKind, string eventNote)
    {
        var at = TimeUtil.Iso(now);
        state["undo"] = new JsonObject
        {
            ["day"] = day,
            ["prev"] = ShiftOf(state, day)?.DeepClone() ?? new JsonObject(),
            ["at"] = at,
            ["what"] = new JsonArray(what),
        };
        if (mutateShift is not null)
        {
            var shifts = Shifts(state, create: true)!;
            if (!shifts.TryGetPropertyValue(day, out var cur))
            {
                cur = new JsonObject();
                shifts[day] = cur;
            }
            mutateShift(cur as JsonObject ?? throw new StateBusyException($"state.json の shifts.{day} が辞書ではない"));
        }
        store.WriteState(state);
        var done = new RecordResult(message, true, new UndoToken(at, what));
        AppendAfterState(store, done, now, eventDay, eventKind, eventNote);
        return done;
    }

    /// <summary>乗務の状態を変える（store.set_shift_status）．休み以外にするときは有給の印を外す．</summary>
    private static void SetStatus(JsonObject shift, string status, DateTime now)
    {
        shift["status"] = status;
        shift["at"] = TimeUtil.Iso(now);
        shift.Remove("paid");
    }

    private static JsonObject? Shifts(JsonObject state, bool create)
    {
        if (!state.TryGetPropertyValue("shifts", out var node))
        {
            if (!create)
            {
                return null;
            }
            var made = new JsonObject();
            state["shifts"] = made;
            return made;
        }
        // 在るのに辞書でないものを作り直すと中身が消えるので，断る
        return node as JsonObject ?? throw new StateBusyException("state.json の shifts が辞書ではない");
    }

    private static JsonObject? ShiftOf(JsonObject state, string day) =>
        (state["shifts"] as JsonObject)?[day] as JsonObject;

    private static string Label(Messages m, string kind) =>
        m.Map("INTENT_LABEL").TryGetValue(kind, out var label) ? label : kind;

    /// <summary>文を引いて埋め，先頭の "[shiftlock] " を外す（画面には付けない）．</summary>
    private static string Text(Messages m, string key, params (string Name, object? Value)[] args) =>
        m.Format(key, args).Replace(m["PREFIX"], "", StringComparison.Ordinal);

    /// <summary>記録のファイルが読み書きできない・設定がおかしいときは，例外を外に出さずにその旨の文を返す．</summary>
    private static RecordResult Guard(Messages m, Func<RecordResult> body)
    {
        try
        {
            return body();
        }
        catch (LogBusyException e)
        {
            return e.Done with { Message = e.Done.Message + m["APP_LOG_BUSY"] };
        }
        catch (StateBusyException)
        {
            return new RecordResult(m["APP_STATE_BUSY"], false, null);
        }
        catch (ConfigException e)
        {
            return new RecordResult(m.Format("APP_BAD_CONFIG", ("err", e.Message)), false, null);
        }
    }
}
