using System.Text.Json.Nodes;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// アプリの減速・一時停止の 10 秒の仕事（設計書 §4.5〜4.9，§6.2）．判断は Core の LimitPolicy が行い，ここは
/// 入力を集めて（設定・宣言・直近のサンプル・ゲームのフォルダの印・記録），「すること」を ILimitControl で実行し，記録を書く．
/// 守ること:
/// ・止める・上限をかける前に，記録（limit.json）を書く．書けなければ止めない・かけない（戻す操作と通知は行う）．
/// ・判断できない（設定・計測が読めない，サンプルが古い，親の PID が取れない経路）ときは新しくかけず，かけたものを戻す．
/// ・失敗しても外に例外を出さない．同じ失敗は 1 日 1 回だけ agent.log に書く（アプリの名前は書かない）．
/// ・全体を 1 つの排他で包む．RunOnce は前の回が動いていれば飛ばす（Scheduler は仕事の重なりを防がない）．
///   RestoreAll・Startup は少し待つ（トレイの緊急解除・終了を取りこぼさない）．
/// ・通知は排他を手放した後に出す（ポップアップは数十秒止まるので，その間に緊急解除を待たせない）．
/// </summary>
public sealed class Limiter(string dir, Func<MeterSnapshot?> latest, ILimitControl control, IFolderLister folders,
    Action<IReadOnlyList<TickAction>> notify, Func<DateTime> clock, OncePerDay once)
{
    /// <summary>仕事の間隔．</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>これより古いサンプルは「計測が止まっている」とみなして，新しくかけず戻す．</summary>
    public static readonly TimeSpan StaleSample = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 一時的に判断できない状態（計測のサンプルが無い・古い・親の PID が取れない，state.json・shifts.json が掴まれている）が
    /// これだけ続くまでは，何もせず飛ばす（スリープ復帰などで一瞬止まっただけの回に，戻してかけ直さない）．
    /// 続いたら新しくかけず，戻す側に倒す．設定が壊れている・宣言が無いなどの確かな判断にはかけない（すぐ判断する）．
    /// </summary>
    public static readonly TimeSpan UnknownGrace = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);
    private const string Title = "shiftlock";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Dir, int Depth), ScanResult> _scans = [];
    private LimitState? _known;
    private string? _failedNoticeDay;

    // 終了のための RestoreAll(null) が立てる．以後 Startup・RunOnce は何もせず，進行中の RunOnce も Cap・Suspend の前で打ち切る
    // （戻した後に遅れた回がかけ直すと，戻す仕事が無いまま Agent が終わる）．排他の外から立てる
    private volatile bool _closed;

    // 判断できない状態が始まった時刻と，前の回の時刻（排他の中だけで触る）．前の回から Interval の 3 倍より空いた
    // （スリープ・時計の飛び）ときは数え直す．連続した回だけで数えるため
    private DateTime? _unknownSince;
    private DateTime? _lastRun;

    // この Agent が上限をかけたジョブ（control がハンドルを持っている）と，外すのに続けて失敗した回数（排他の中だけで触る）
    private const int UncapRetries = 3;
    private readonly HashSet<string> _heldJobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _uncapFailures = new(StringComparer.Ordinal);

    /// <summary>ゲームの根のフォルダの探索結果（Agent が生きている間，根ごとに 1 回だけ探す）．Names が null は読めなかった．</summary>
    private sealed record ScanResult(IReadOnlyList<string>? Names);

    /// <summary>1 回ぶんの入力のうち，設定から決まるもの．Cfg が null は判断できない（設定が無い・読めない・乗務の逆算に失敗）．</summary>
    // ConfigBusy は設定（shifts.json）が掴まれていて開けない（一時的．壊れている・無いとは分ける）
    private sealed record Env(ShiftConfig? Cfg, Plan? Plan, Messages M, string Phase, string Hard, bool Speak, bool ConfigBusy = false);

    // ---- 入口 ----

    /// <summary>起動時に 1 回．記録の一時停止を再開し，残っていた上限の知らせを出す．失敗しても Agent の起動を止めない．</summary>
    public void Startup()
    {
        var notices = new List<TickAction>();
        if (!_gate.Wait(GateWait))
        {
            return;
        }
        try
        {
            if (_closed)
            {
                return;
            }
            StartupLocked(notices);
        }
        catch (Exception e)
        {
            once.Write("limit-startup", "limit startup failed: " + e.GetType().Name, Today());
        }
        finally
        {
            _gate.Release();
        }
        Deliver(notices);
    }

    /// <summary>10 秒の仕事．前の回が動いていたら何もしない．例外は外に出さない．</summary>
    public void RunOnce()
    {
        if (!_gate.Wait(0))
        {
            return;
        }
        var notices = new List<TickAction>();
        try
        {
            if (_closed)
            {
                return;
            }
            RunLocked(notices);
        }
        catch (Exception e)
        {
            once.Write("limit-run", "limit failed: " + e.GetType().Name, Today());
        }
        finally
        {
            _gate.Release();
        }
        Deliver(notices);
    }

    /// <summary>
    /// 全部を元に戻す（上限を外す → 再開する → 記録を空にする）．終了時は reason を null（events.log に書かない），
    /// トレイの緊急解除は "kaijo"．通知は出さない（トレイが結果を知らせる．終了時は出す相手がいない）．
    /// 記録が読めない・排他が取れないときは何もしない（記録が壊れていれば次の起動の Startup が知らせる）．
    /// wait は排他を待つ最大（既定 10 秒．未処理の例外の受け口からは短くする）．
    /// </summary>
    public void RestoreAll(string? reason, TimeSpan? wait = null)
    {
        if (reason is null)
        {
            _closed = true;   // 排他を待つ前に立てる（取れずに諦める道でも，進行中の回を止める）
        }
        if (!_gate.Wait(wait ?? GateWait))
        {
            once.Write("limit-restore", "limit restore skipped: busy", Today());
            return;
        }
        try
        {
            var store = new Store(dir);
            LimitState state;
            try
            {
                state = LimitFiles.ReadState(store);
            }
            catch (InvalidDataException)
            {
                once.Write("limit-restore", "limit restore skipped: bad record", Today());
                return;
            }
            catch (IOException) when (_known is not null)
            {
                state = _known;   // 掴まれて読めない．最後に読んだ・書いた記録で戻す
            }
            var m = Messages.Load(null);
            var decision = LimitPolicy.RestoreAll(state, null, reason, m, "popup");
            Execute(store, MakeEnv(null, m), decision, state, clock(), [], collectNotices: false);
        }
        catch (Exception e)
        {
            once.Write("limit-restore", "limit restore failed: " + e.GetType().Name, Today());
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- 起動時 ----

    private void StartupLocked(List<TickAction> notices)
    {
        var now = clock();
        var store = new Store(dir);
        var env = LoadEnv(now);
        LimitState state;
        try
        {
            state = LimitFiles.ReadState(store);
        }
        catch (InvalidDataException)
        {
            Quarantine(store, env, notices);
            return;
        }
        catch (IOException)
        {
            once.Write("limit-state", "limit: cannot read the record", Today());
            return;
        }

        // 一時停止: 開始時刻が同じプロセスが今も在るものだけ再開する．PID が使い回された別のプロセスには触らない（記録から消す）
        var stillSuspended = new List<SuspendedProc>();
        foreach (var p in state.Suspended)
        {
            if (Exists(p.Key) && !Resume(p.Key))
            {
                stillSuspended.Add(p);   // 再開できなかった．記録に残して次の RunOnce の判断に任せる
                once.Write("limit-resume", "limit: resume failed", Today());
            }
        }

        // 上限: 新しい Agent はジョブのハンドルを持たないので，外せない（名前で開き直さない．本人の決定）．アプリを起動し直すと戻る
        var snap = latest();
        var stale = state.Capped.Where(c => !UncapQuiet(c.Value.Job))
            .Any(c => snap is null || snap.Sample.Procs.Any(p => p.Name == c.Key));
        if (stale)
        {
            notices.Add(Notice(env, env.Phase, env.M["AGENT_LIMIT_STALE_CAP"]));
        }

        var cleared = new LimitState(state.Day, state.Baseline, new Dictionary<string, CappedApp>(), stillSuspended,
            new HashSet<string>(), new HashSet<string>(), state.Pending);
        if (!Same(cleared, state))
        {
            TryWrite(store, cleared);
        }
        _known = cleared;
    }

    // ---- 10 秒の仕事 ----

    private void RunLocked(List<TickAction> notices)
    {
        var now = clock();
        // 前の回から大きく空いた（スリープ復帰・時計の飛び）なら，未判断の時間を数え直す
        if (_lastRun is not { } last || now < last || now - last > Interval * 3)
        {
            _unknownSince = null;
        }
        _lastRun = now;

        var store = new Store(dir);
        var env = LoadEnv(now);
        LimitState state;
        try
        {
            state = LimitFiles.ReadState(store);
        }
        catch (InvalidDataException)
        {
            Quarantine(store, env, notices);
            state = LimitState.Empty;
        }
        catch (IOException)
        {
            once.Write("limit-state", "limit: cannot read the record", Today());
            return;   // 記録が読めない回は何もしない（記録なしで止める・かけることはしない．戻す記録も読めない）
        }
        _known = state;

        // state.json は「読めない（掴まれている）」と「読めて空」を分ける（ShiftStatus は両方を空にしてしまう）
        JsonObject? shifts;
        try
        {
            shifts = store.ReadJson("state.json")["shifts"] as JsonObject ?? new JsonObject();
        }
        catch (StateBusyException)
        {
            shifts = null;
        }
        var snap = latest();

        // 一時的に判断できない原因: 設定・state.json が掴まれている，計測が無い・古い・親の PID が取れない．
        // 設定が無い・壊れている（Cfg が null で，掴まれてはいない）は確かな判断なので，待たずに戻す側へ進む
        var unknown = env.ConfigBusy || (env.Cfg is not null && (shifts is null || !Usable(snap, now)));
        if (unknown)
        {
            _unknownSince ??= now;
            if (now - _unknownSince.Value < UnknownGrace)
            {
                return;   // 猶予の間は何もしない（判断も記録も通知もしない．かけたものは残る．緊急解除は state.json が読めた回に効く）
            }
        }
        else
        {
            _unknownSince = null;
        }

        var decision = Decide(env, snap, now, state, store, shifts ?? new JsonObject(), forceUnknown: unknown, persist: true);
        Execute(store, env, decision, state, now, notices, collectNotices: true);
    }

    /// <summary>計測のサンプルが判断に使えるか（在る・親の PID が取れている・30 秒以内）．</summary>
    private static bool Usable(MeterSnapshot? snap, DateTime now) =>
        snap is not null && snap.Sample.HasParents && Math.Abs((now - snap.Sample.Now).TotalSeconds) <= StaleSample.TotalSeconds;

    /// <summary>判断に要る入力を集めて LimitPolicy.Decide を呼ぶ．persist が偽なら何も書かない（--limit-once）．</summary>
    private LimitDecision Decide(Env env, MeterSnapshot? snap, DateTime now, LimitState state, Store store, JsonObject shifts,
        bool forceUnknown, bool persist)
    {
        var plan = env.Plan;
        var day = plan is null ? null : shifts[plan.Day] as JsonObject;
        var declaration = day?["enforce"] is JsonObject decl ? (JsonObject)decl.DeepClone() : new JsonObject();
        var status = day?["status"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;

        IReadOnlyList<LimitApp>? apps = null;
        IReadOnlySet<ProcKey>? alive = null;
        var window = TimeSpan.FromMinutes(30);
        if (!forceUnknown && env.Cfg is not null && snap is not null && Usable(snap, now))
        {
            try
            {
                var settings = LimitSettings.Read(env.Cfg);
                window = settings.BaselineWindow;
                var verdicts = Classify(snap, settings);
                if (persist && verdicts.Count > 0)
                {
                    TryUpdateGames(store, verdicts, now);
                }
                apps = LimitTargets.Resolve(snap.Sample, snap.Settings.Registered, settings, DeclaredList(declaration, "limit_apps"),
                    verdicts, DeclaredList(declaration, "limit_exclude"));
                alive = snap.Sample.Procs.Select(p => p.Key).ToHashSet();
            }
            catch (ConfigException)
            {
                apps = null;   // limit 節が壊れている → 判断できない
            }
        }

        // 基準の行は，宣言があり，まだ基準が無いアプリがあるとき（か乗務日が変わったとき）だけ読む（10 秒ごとに月のログを読まない）
        var limitDeclared = declaration["consented_at"] is not null
            && declaration["action"] is JsonValue av && av.TryGetValue<string>(out var act) && LimitStages.IsLimitAction(act);
        IReadOnlyList<(DateTime T, JsonObject Rec)> lines = [];
        if (apps is not null && plan is not null && limitDeclared && now >= plan.SoftClose
            && (state.Day != plan.Day || apps.Any(a => a.Verdict == LimitVerdict.Target && !state.Baseline.ContainsKey(a.Exe))))
        {
            try
            {
                lines = store.LogRead("app", (now < plan.SoftClose ? now : plan.SoftClose) - window, now.AddSeconds(1));
            }
            catch (Exception)
            {
                lines = [];   // 読めなければ基準が決まらない（上限をかけずに待つ）
            }
        }

        var input = new LimitInput(now, plan, declaration, status, store.StopActive, state, apps, alive, lines, window,
            LimitFiles.ClaudeBusy(store, now), env.Phase, env.Hard);
        return LimitPolicy.Decide(input, env.M);
    }

    /// <summary>
    /// 「すること」を実行して記録を書く．止める・上限をかける前に，その分を入れた記録を書く（書けなければ止めない・かけない）．
    /// 失敗したものは記録から外す（上限は外しておく）．再開に失敗して今も在るもの・外すのに失敗した上限は記録に残す．
    /// </summary>
    private void Execute(Store store, Env env, LimitDecision d, LimitState prior, DateTime now, List<TickAction> notices, bool collectNotices)
    {
        var wrote = Same(d.StateBeforeOps, prior) || TryWrite(store, d.StateBeforeOps);
        var lastWritten = wrote ? d.StateBeforeOps : prior;
        var capped = new Dictionary<string, CappedApp>(d.StateAfterOps.Capped);
        var suspended = new List<SuspendedProc>(d.StateAfterOps.Suspended);
        var acted = false;
        var failed = false;

        foreach (var op in d.Ops)
        {
            switch (op)
            {
                case CapOp c:
                    if (!wrote || _closed)
                    {
                        Revert(capped, prior, c.Exe);
                        failed |= !wrote;
                    }
                    else if (Try("limit-cap", () => control.Cap(c.Job, c.Procs, c.Percent)))
                    {
                        acted = true;
                        _heldJobs.Add(c.Job);
                    }
                    else
                    {
                        Try("limit-cap", () => control.Uncap(c.Job));   // 一部だけ入っているかもしれないので外しておく
                        capped.Remove(c.Exe);
                        failed = true;
                    }
                    break;
                case SuspendOp s:
                    if (wrote && !_closed && Try("limit-suspend", () => control.Suspend(s.Proc.Key)))
                    {
                        acted = true;
                    }
                    else
                    {
                        suspended.Remove(s.Proc);
                        failed |= !_closed || !wrote;
                    }
                    break;
                case UncapOp u:
                    if (Try("limit-uncap", () => control.Uncap(u.Job)))
                    {
                        _uncapFailures.Remove(u.Job);
                    }
                    else
                    {
                        failed = true;
                        KeepForRetry(capped, prior, u);
                    }
                    break;
                case ResumeOp r:
                    if (Exists(r.Proc.Key) && !Resume(r.Proc.Key))
                    {
                        suspended.Add(r.Proc);   // 次の回にやり直す
                        failed = true;
                    }
                    break;
                case RunOp r when acted || !d.Ops.Any(o => o is CapOp or SuspendOp):
                    Guard("limit-run", () => Enforce.AppendLimitRun(store, r.Day, r.Action, r.Step, now));
                    break;
                case ReleaseEventOp e:
                    Guard("limit-event", () => store.AppendEvent(now, e.Day, "limit_release", e.Reason));
                    break;
                case NoticeOp n when collectNotices:
                    notices.Add(Notice(env, n.Method, n.Text));
                    break;
            }
        }

        var final = d.StateAfterOps with { Capped = capped, Suspended = suspended };
        if (!Same(final, lastWritten))
        {
            TryWrite(store, final);
        }
        _known = final;

        if (failed && collectNotices && _failedNoticeDay != (final.Day ?? "-"))
        {
            _failedNoticeDay = final.Day ?? "-";
            notices.Add(Notice(env, env.Phase, env.M["AGENT_LIMIT_FAILED"]));
        }
    }

    // ---- 部品 ----

    private Env LoadEnv(DateTime now, bool quiet = false)
    {
        ShiftConfig? cfg = null;
        Plan? plan = null;
        var busy = false;
        try
        {
            cfg = Watcher.LoadConfig(dir).Config;
            plan = cfg is null ? null : Planner.TargetPlan(cfg, now);
        }
        catch (Exception e)
        {
            cfg = null;   // 判断できない（読めない・乗務の逆算に失敗）．文は設定の値を含みうるので型だけ書く
            plan = null;
            // 無い・壊れているは LoadConfig が値で返す．ここまで IO の例外が来るのは開けない（掴まれている・権限）＝一時的
            busy = e is IOException or UnauthorizedAccessException;
            if (!quiet)
            {
                once.Write("limit-config", "limit: cannot read the config: " + e.GetType().Name, DateOnly.FromDateTime(now));
            }
        }
        return MakeEnv(cfg, Messages.Load(null), plan) with { ConfigBusy = busy };
    }

    private static Env MakeEnv(ShiftConfig? cfg, Messages fallback, Plan? plan = null)
    {
        var notify = cfg?.Section("notify");
        return new Env(cfg, plan, cfg?.Msg ?? fallback, Method(notify, "phase", "toast"), Method(notify, "hard", "popup"),
            notify?["speak"] is JsonValue v && v.TryGetValue<bool>(out var b) && b);
    }

    /// <summary>通知の方法は popup か toast だけ（enforce など別の道に流さない）．</summary>
    private static string Method(JsonObject? notify, string key, string fallback) =>
        notify is null ? fallback : NotifyLog.Method(notify, key, fallback) == "popup" ? "popup" : "toast";

    private static TickAction Notice(Env env, string method, string text) =>
        new(method == "popup" ? "popup" : "toast", "limit", Title, text, env.Speak);

    private void Quarantine(Store store, Env env, List<TickAction> notices)
    {
        Guard("limit-state", () => LimitFiles.QuarantineState(store));
        notices.Add(Notice(env, env.Hard, env.M["AGENT_LIMIT_BAD_RECORD"]));
    }

    /// <summary>今見えているゲームの exe ごとの判定（target｜anticheat｜unknown）．1 つでも anticheat なら anticheat，次に unknown．</summary>
    private Dictionary<string, string> Classify(MeterSnapshot snap, LimitSettings settings)
    {
        var sample = snap.Sample;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in sample.Procs.Where(p => sample.Games.Contains(p.Key)))
        {
            var verdict = VerdictOf(p, snap, settings);
            result[p.Name] = result.TryGetValue(p.Name, out var old) ? Worse(old, verdict) : verdict;
        }
        return result;
    }

    private string VerdictOf(ProcSample p, MeterSnapshot snap, LimitSettings settings)
    {
        var sample = snap.Sample;
        var markers = settings.AntiCheatMarkers;
        if (ProcTree.OfName(sample.Procs, p.Name).Any(t => AntiCheat.Matches(t.Name, markers)))
        {
            return LimitTargets.VerdictAntiCheat;
        }
        snap.Paths.TryGetValue(p.Key, out var path);
        if (AntiCheat.RootOf(snap.Rules, path) is not { } root)
        {
            return LimitTargets.VerdictUnknown;
        }
        if (!_scans.TryGetValue((root.Dir, root.Depth), out var scan))
        {
            IReadOnlyList<string>? names = null;
            try
            {
                names = folders.Names(root.Dir, root.Depth);
            }
            catch (Exception)
            {
                names = null;   // 読めなければ調べられない（対象外にする）
            }
            _scans[(root.Dir, root.Depth)] = scan = new ScanResult(names);
        }
        if (scan.Names is null)
        {
            return LimitTargets.VerdictUnknown;
        }
        return scan.Names.Any(n => AntiCheat.Matches(n, markers)) ? LimitTargets.VerdictAntiCheat : LimitTargets.VerdictTarget;
    }

    private static string Worse(string a, string b) =>
        a == LimitTargets.VerdictAntiCheat || b == LimitTargets.VerdictAntiCheat ? LimitTargets.VerdictAntiCheat
        : a == LimitTargets.VerdictUnknown || b == LimitTargets.VerdictUnknown ? LimitTargets.VerdictUnknown
        : LimitTargets.VerdictTarget;

    /// <summary>
    /// 宣言に固定した exe の一覧（limit_apps・limit_exclude）．配列でない・文字列でない要素は無いものとして扱う
    /// （limit_apps なら足していないアプリは手を加えない側．limit_exclude が無い古い宣言は今の設定だけで外す）．
    /// </summary>
    private static HashSet<string> DeclaredList(JsonObject declaration, string key) =>
        declaration[key] is JsonArray a
            ? [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? MeterSettings.NormalizeExe(s) : null).OfType<string>()]
            : [];

    private void TryUpdateGames(Store store, IReadOnlyDictionary<string, string> verdicts, DateTime now) =>
        Guard("limit-games", () => LimitFiles.UpdateGames(store, verdicts, now));

    private bool TryWrite(Store store, LimitState state)
    {
        try
        {
            LimitFiles.WriteState(store, state);
            _known = state;
            return true;
        }
        catch (Exception e) when (e is StateBusyException or IOException or UnauthorizedAccessException)
        {
            once.Write("limit-write", "limit: cannot write the record: " + e.GetType().Name, Today());
            return false;
        }
    }

    /// <summary>control の呼び出し．例外も失敗（false）として扱う．</summary>
    private bool Try(string key, Func<bool> call)
    {
        try
        {
            if (call())
            {
                return true;
            }
            once.Write(key, "limit: " + key[6..] + " failed", Today());
        }
        catch (Exception e)
        {
            once.Write(key, "limit: " + key[6..] + " failed: " + e.GetType().Name, Today());
        }
        return false;
    }

    private bool Exists(ProcKey key)
    {
        try
        {
            return control.Exists(key);
        }
        catch (Exception)
        {
            return true;   // 分からなければ在るとして，再開を試す（Resume が開始時刻を確かめる）
        }
    }

    private bool Resume(ProcKey key) => Try("limit-resume", () => control.Resume(key));

    /// <summary>起動時の上限の取り直し．新しい Agent はハンドルを持たないので false が普通（ログに書かない）．</summary>
    private bool UncapQuiet(string job)
    {
        try
        {
            return control.Uncap(job);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>失敗しても続ける操作（記録・ログの書き込み）．型だけ agent.log に書く．</summary>
    private void Guard(string key, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            once.Write(key, "limit: " + key[6..] + " failed: " + e.GetType().Name, Today());
        }
    }

    /// <summary>
    /// 外せなかった上限は，記録に残して次の回にやり直す（記録から消すと，上限が効いたまま二度と外しに行かない）．
    /// 残すのは，この Agent がかけたジョブ（ハンドルを持っている）だけ．持っていないジョブは何度やっても外せないので残さない
    /// （起動時の知らせに任せる）．続けて UncapRetries 回だめなら諦めて記録から外す（10 秒ごとに解除の通知を出し続けない）．
    /// </summary>
    private void KeepForRetry(Dictionary<string, CappedApp> capped, LimitState prior, UncapOp u)
    {
        if (!_heldJobs.Contains(u.Job) || !prior.Capped.TryGetValue(u.Exe, out var old))
        {
            return;
        }
        var n = _uncapFailures.GetValueOrDefault(u.Job) + 1;
        if (n >= UncapRetries)
        {
            _uncapFailures.Remove(u.Job);
            return;
        }
        _uncapFailures[u.Job] = n;
        capped.TryAdd(u.Exe, old);   // 同じ回にかけ直していれば（乗務日が変わった），新しい記録を残す
    }

    /// <summary>skip した上限は，前の記録にあればそれに戻し，無ければ記録から外す．</summary>
    private static void Revert(Dictionary<string, CappedApp> capped, LimitState prior, string exe)
    {
        if (prior.Capped.TryGetValue(exe, out var old))
        {
            capped[exe] = old;
        }
        else
        {
            capped.Remove(exe);
        }
    }

    private void Deliver(List<TickAction> notices)
    {
        if (notices.Count == 0)
        {
            return;
        }
        try
        {
            notify(notices);
        }
        catch (Exception e)
        {
            once.Write("limit-notify", "limit: notify failed: " + e.GetType().Name, Today());
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock());

    /// <summary>記録の中身が同じか（書き直すかの判断．10 秒ごとに同じ内容を書かない）．</summary>
    internal static bool Same(LimitState a, LimitState b) =>
        a.Day == b.Day
        && a.Baseline.Count == b.Baseline.Count && a.Baseline.All(kv => b.Baseline.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && a.Capped.Count == b.Capped.Count && a.Capped.All(kv => b.Capped.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && a.Suspended.Count == b.Suspended.Count && a.Suspended.All(b.Suspended.Contains)
        && a.Skipped.SetEquals(b.Skipped) && a.Announced.SetEquals(b.Announced)
        && a.Pending.Count == b.Pending.Count && a.Pending.All(kv => b.Pending.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>--limit-once 用．判断だけ行い，何も書かず・実行しない．記録が壊れていれば空として扱う．記録が掴まれていれば IOException．</summary>
    internal (LimitDecision Decision, Messages M) Preview(MeterSnapshot snap, DateTime now)
    {
        var store = new Store(dir);
        var env = LoadEnv(now, quiet: true);
        LimitState state;
        try
        {
            state = LimitFiles.ReadState(store);
        }
        catch (InvalidDataException)
        {
            state = LimitState.Empty;
        }
        return (Decide(env, snap, now, state, store, store.ShiftStatus(), forceUnknown: false, persist: false), env.M);
    }
}
