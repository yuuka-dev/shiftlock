using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 1 回ぶんの判断の入力（設計書 §4.5〜4.9）．Plan は見張りと同じ決め方で選んだ乗務（Planner.TargetPlan）で，無ければ null．
/// Apps が null は「判断できない」（設定・計測が読めない，親の PID が取れない経路）．
/// Alive は今のサンプルに居る全プロセスの (pid, 開始時刻)（null なら Apps のツリーだけを生きているとみなす）．
/// RecentLines は log/app の直近の行（基準の計算用）．PhaseMethod は notify.phase，HardMethod は notify.hard．
/// </summary>
public sealed record LimitInput(
    DateTime Now,
    Plan? Plan,
    JsonObject Declaration,
    string? ShiftStatus,
    bool StopActive,
    LimitState State,
    IReadOnlyList<LimitApp>? Apps,
    IReadOnlySet<ProcKey>? Alive,
    IReadOnlyList<(DateTime T, JsonObject Rec)> RecentLines,
    TimeSpan BaselineWindow,
    bool ClaudeBusy,
    string PhaseMethod,
    string HardMethod);

public abstract record LimitOp;

/// <summary>ジョブに入れて上限をかける（既に入っていれば上限だけ変える）．Procs はツリーの全プロセス．Percent は PC 全体に対する %．</summary>
public sealed record CapOp(string Exe, string Job, IReadOnlyList<ProcKey> Procs, double Percent) : LimitOp;

public sealed record UncapOp(string Exe, string Job) : LimitOp;

public sealed record SuspendOp(SuspendedProc Proc) : LimitOp;

public sealed record ResumeOp(SuspendedProc Proc) : LimitOp;

/// <summary>通知を出す．アプリの名前は入れない．</summary>
public sealed record NoticeOp(string Method, string Text) : LimitOp;

/// <summary>宣言の runs に足す（Enforce.AppendLimitRun）．</summary>
public sealed record RunOp(string Day, string Action, int Step) : LimitOp;

/// <summary>events.log の limit_release（メモは Reason）．</summary>
public sealed record ReleaseEventOp(string Day, string Reason) : LimitOp;

/// <summary>
/// 判断の結果．StateBeforeOps は操作の前に書く記録（止めるプロセス・かけた上限を含み，まだ戻していないものも残す）．
/// StateAfterOps は全部成功したときの記録（戻したものを除く）．
/// </summary>
public sealed record LimitDecision(IReadOnlyList<LimitOp> Ops, LimitState StateBeforeOps, LimitState StateAfterOps);

/// <summary>
/// 減速・一時停止・解除の判断（設計書 §4.5〜4.9）．今この瞬間に何をするかを，合成できる入力から決める純粋な関数．
/// Windows API・乱数・現在時刻・ファイルの読み書きは使わない（now は入力）．
/// </summary>
public static class LimitPolicy
{
    /// <summary>上限の下限（PC 全体に対する %）．これより低いと上限を外す操作にも応答しにくくなる．</summary>
    public const double MinPercent = 0.5;

    /// <summary>段階の始まりのこれだけ前に予告する．</summary>
    public static readonly TimeSpan NoticeLead = TimeSpan.FromMinutes(5);

    /// <summary>Claude Code の処理中に通知を待たせる最大．</summary>
    public static readonly TimeSpan BusyWaitMax = TimeSpan.FromMinutes(10);

    // Announced・Pending の印．段階の予告は "1"〜"5"，一時停止の報告は "suspended"，見送りの知らせは "skipped"
    private const string KeySuspended = "suspended";
    private const string KeySkipped = "skipped";

    // 解除の理由（events.log の limit_release のメモ）
    private const string ReasonOff = "off";
    private const string ReasonKaijo = "kaijo";
    private const string ReasonDepart = "depart";
    private const string ReasonYasumi = "yasumi";
    private const string ReasonTimeout = "timeout";

    /// <summary>ジョブの名前（接頭辞は付けない．Agent が落ちると名前では開けないので，名前は識別のためだけ）．</summary>
    public static string JobName(string exe) => "shiftlock-limit-" + exe;

    /// <summary>
    /// 判断する．戻す条件に当たれば RestoreAll と同じ結果を返す．例外は出さない（宣言の JSON の型が変でも「戻す側」に倒れる）．
    /// </summary>
    public static LimitDecision Decide(LimitInput input, Messages m)
    {
        var state = input.State;
        var plan = input.Plan;
        var decl = input.Declaration;
        var hard = input.HardMethod;

        LimitDecision Restore(string? reason) => RestoreAll(state, plan?.Day, reason, m, hard);

        if (input.Apps is null)
        {
            return Restore(null);   // 判断できない → 新しくかけず，かけたものを戻す
        }
        // 出庫の時刻を過ぎると TargetPlan は次の乗務（か null）を返すので，記録の日が今の乗務の日と違い，効いているものがあれば
        // 前の乗務の timeout として戻す（理由と日を前の乗務のものにする．次の乗務の判断は次の回でよい）
        if (state.Day is { } recorded && (plan is null || plan.Day != recorded) && state.HasEffects)
        {
            return RestoreAll(state, recorded, ReasonTimeout, m, hard);
        }
        if (plan is null || decl.Count == 0 || !JsonX.Truthy(decl["consented_at"]))
        {
            return Restore(ReasonOff);
        }
        var action = JsonX.Str(decl["action"]);
        if (!LimitStages.IsLimitAction(action))
        {
            return Restore(null);   // 動作が違う（ロック系）
        }
        if (input.StopActive || JsonX.Truthy(decl["released_at"]))
        {
            return Restore(ReasonKaijo);
        }
        if (input.ShiftStatus == "worked")
        {
            return Restore(ReasonDepart);
        }
        if (input.ShiftStatus == "yasumi")
        {
            return Restore(ReasonYasumi);
        }
        if (input.Now >= plan.StartLatest)
        {
            return Restore(ReasonTimeout);
        }
        // 宣言に写した steps が壊れている → 判断できない（理由を残すものを先に見たので，ここは理由なし）
        if (LimitStages.CheckSteps(decl["steps"]) is not { } steps)
        {
            return Restore(null);
        }
        return Apply(input, plan, action!, steps, m);
    }

    /// <summary>
    /// 全部を元に戻す（終了時・緊急解除用．reason が null なら events.log に書かない）．
    /// 戻すものが何も無ければ Ops は空．StateAfterOps は Baseline・Day・Pending だけ残す（別の日のものは捨てる）．
    /// </summary>
    public static LimitDecision RestoreAll(LimitState state, string? day, string? reason, Messages m, string hardMethod)
    {
        var ops = new List<LimitOp>();
        foreach (var (exe, c) in state.Capped)
        {
            ops.Add(new UncapOp(exe, c.Job));
        }
        foreach (var p in state.Suspended)
        {
            ops.Add(new ResumeOp(p));
        }
        if (state.HasEffects || state.Announced.Count > 0)
        {
            if (reason is not null)
            {
                ops.Add(new ReleaseEventOp(day ?? state.Day ?? "-", reason));
            }
            ops.Add(new NoticeOp(hardMethod, m["AGENT_LIMIT_RELEASED"]));
        }
        var sameDay = day is null || state.Day == day;
        var after = new LimitState(
            day ?? state.Day,
            sameDay ? state.Baseline : new Dictionary<string, double>(),
            new Dictionary<string, CappedApp>(), [], new HashSet<string>(), new HashSet<string>(),
            sameDay ? state.Pending : new Dictionary<string, DateTime>());
        return new LimitDecision(ops, state, after);
    }

    // 戻す条件に当たらなかったときの判断（設計書 §4.5〜4.7）
    private static LimitDecision Apply(LimitInput input, Plan plan, string action, IReadOnlyList<int> steps, Messages m)
    {
        var now = input.Now;
        var apps = input.Apps!;
        var state = input.State;

        var capped = new Dictionary<string, CappedApp>(state.Capped);
        var suspended = new List<SuspendedProc>(state.Suspended);
        var baseline = new Dictionary<string, double>(state.Baseline);
        var announced = new HashSet<string>(state.Announced);
        var skipped = new HashSet<string>(state.Skipped);
        var pending = new Dictionary<string, DateTime>(state.Pending);
        var removedCapped = new Dictionary<string, CappedApp>();   // 戻す操作をするもの（成功するまで記録に残す）
        var resumed = new List<SuspendedProc>();
        var restoreOps = new List<LimitOp>();
        var actOps = new List<LimitOp>();

        // 記録の掃除．止めた記録: ツリーに有れば残す（Verdict は後で見る）．ツリーに無くて生きている（対象のアプリでなくなった）なら
        // 再開して消す．生きていない（終わった・PID が使い回されて開始時刻が違う）なら再開せず消す．
        // 上限: その exe が Apps に無ければ外す（終わっていれば空のジョブへの無害な呼び出し．ジョブのハンドルを閉じるのは Agent）
        var live = apps.SelectMany(a => a.Tree).Select(p => p.Key).ToHashSet();
        var alive = input.Alive ?? live;
        foreach (var p in suspended.Where(p => !live.Contains(p.Key)).ToList())
        {
            suspended.Remove(p);
            if (alive.Contains(p.Key))
            {
                restoreOps.Add(new ResumeOp(p));
                resumed.Add(p);
            }
        }
        var runningExes = apps.Select(a => a.Exe).ToHashSet(StringComparer.Ordinal);
        foreach (var (exe, c) in capped.Where(kv => !runningExes.Contains(kv.Key)).ToList())
        {
            capped.Remove(exe);
            restoreOps.Add(new UncapOp(exe, c.Job));
            removedCapped[exe] = c;
        }

        // 乗務日が変わった → 前の日のものを戻し，基準・予告・見送りを捨てる
        if (state.Day != plan.Day)
        {
            foreach (var (exe, c) in capped)
            {
                restoreOps.Add(new UncapOp(exe, c.Job));
                removedCapped[exe] = c;
            }
            capped.Clear();
            foreach (var p in suspended)
            {
                restoreOps.Add(new ResumeOp(p));
                resumed.Add(p);
            }
            suspended.Clear();
            baseline.Clear();
            announced.Clear();
            skipped.Clear();
            pending.Clear();
        }

        var stages = LimitStages.Of(plan.SoftClose, plan.HardClose, steps, action);
        var current = stages.LastOrDefault(s => s.At <= now);
        var next = stages.FirstOrDefault(s => s.At > now);
        var targets = apps.Where(a => a.Verdict == LimitVerdict.Target).ToList();

        // 対象でなくなったアプリ（守る・外した・アンチチート・調べられない・足していない）は戻す．守るものは見送りを 1 回だけ知らせる
        var skippedNew = false;
        foreach (var app in apps.Where(a => a.Verdict != LimitVerdict.Target))
        {
            if (capped.Remove(app.Exe, out var c))
            {
                restoreOps.Add(new UncapOp(app.Exe, c.Job));
                removedCapped[app.Exe] = c;
            }
            foreach (var p in suspended.Where(p => p.Exe == app.Exe).ToList())
            {
                restoreOps.Add(new ResumeOp(p));
                resumed.Add(p);
                suspended.Remove(p);
            }
            if (app.Verdict == LimitVerdict.Protected && current is not null && skipped.Add(app.Exe))
            {
                skippedNew = true;
            }
        }
        if (skippedNew)
        {
            pending.TryAdd(KeySkipped, now);
        }

        // 予告: 次の段階の 5 分前から 1 回．対象が動いていなければ出さない．過ぎた段階の予告は出さない（次の段階だけを見る）．
        // throttle の段階 5 は 10% のままで変わるものが無いので予告しない
        if (next is not null && targets.Count > 0 && now >= next.At - NoticeLead
            && !(next.Step == 5 && next.Percent is not null && stages.Count > 1))
        {
            var key = StageKey(next);
            if (announced.Add(key))
            {
                pending.TryAdd(key, now);
            }
        }

        // 今の段階の上限・一時停止
        if (current is not null)
        {
            var suspendedKeys = suspended.Select(p => p.Key).ToHashSet();
            var acted = false;
            var suspendedNow = false;
            foreach (var app in targets)
            {
                if (current.Percent is { } percent)
                {
                    if (!baseline.TryGetValue(app.Exe, out var b))
                    {
                        if (BaselineOf(app.Exe, now, plan.SoftClose, input.RecentLines, input.BaselineWindow) is not { } fresh)
                        {
                            continue;   // 当たる行が無い → このアプリは今回何もしない
                        }
                        baseline[app.Exe] = b = fresh;
                    }
                    var limit = Math.Max(MinPercent, Math.Round(b * percent / 100.0, 2, MidpointRounding.AwayFromZero));
                    capped.TryGetValue(app.Exe, out var cur);
                    if (cur is null || Math.Abs(cur.Percent - limit) > 1e-6)
                    {
                        var job = cur?.Job ?? JobName(app.Exe);
                        actOps.Add(new CapOp(app.Exe, job, [.. app.Tree.Select(p => p.Key)], limit));
                        capped[app.Exe] = new CappedApp(job, limit);
                        acted = true;
                    }
                }
                else
                {
                    // 一時停止．上限は段階 4 のまま残す．記録にあるプロセスは止めない（止めた回数だけ再開が要るため）
                    foreach (var p in app.Tree.Where(p => !suspendedKeys.Contains(p.Key)))
                    {
                        var proc = new SuspendedProc(p.Pid, p.Start, app.Exe);
                        actOps.Add(new SuspendOp(proc));
                        suspended.Add(proc);
                        suspendedKeys.Add(p.Key);
                        acted = suspendedNow = true;
                    }
                }
            }
            if (acted && !RunsHaveStep(input.Declaration, current.Step))
            {
                actOps.Add(new RunOp(plan.Day, action, current.Step));
            }
            if (suspendedNow && announced.Add(KeySuspended))
            {
                pending.TryAdd(KeySuspended, now);
            }
        }

        // 通知を出すか待つか．処理中は待つ（段階の切り替えは待たない）．待ち始めから BusyWaitMax を過ぎたら出す
        var notices = new List<LimitOp>();
        foreach (var (key, since) in pending.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            if (input.ClaudeBusy)
            {
                if (since > now)
                {
                    pending[key] = now;   // 時計が戻って未来の時刻が残っている．今から数え直す
                    continue;
                }
                if (now - since < BusyWaitMax)
                {
                    continue;
                }
            }
            pending.Remove(key);
            if (NoticeFor(key, stages, targets.Count, suspended, m, input) is { } notice)
            {
                notices.Add(notice);
            }
        }

        var after = new LimitState(plan.Day, baseline, capped, suspended, skipped, announced, pending);
        var before = after with
        {
            Capped = new Dictionary<string, CappedApp>(removedCapped.Where(kv => !capped.ContainsKey(kv.Key)).Concat(capped)),
            Suspended = [.. suspended, .. resumed],
        };
        return new LimitDecision([.. restoreOps, .. actOps, .. notices], before, after);
    }

    private static string StageKey(LimitStage s) => s.Step.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 印から通知を作り直す（待たせている間に状況が変わっても，今の状況で言える形にする）．言うことが無くなったもの
    /// （対象が動いていない・止めたものが無い・知らない印）は null（捨てる）．アプリの名前は入れない．
    /// </summary>
    private static NoticeOp? NoticeFor(string key, IReadOnlyList<LimitStage> stages, int targetCount,
        IReadOnlyList<SuspendedProc> suspended, Messages m, LimitInput input)
    {
        switch (key)
        {
            case KeySuspended:
                var n = suspended.Select(p => p.Exe).Distinct(StringComparer.Ordinal).Count();
                return n == 0 ? null : new NoticeOp(input.HardMethod, m.Format("AGENT_LIMIT_SUSPENDED", ("n", n)));
            case KeySkipped:
                return new NoticeOp(input.PhaseMethod, m["AGENT_LIMIT_SKIPPED"]);
        }
        if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var step)
            || stages.FirstOrDefault(s => s.Step == step) is not { } stage || targetCount == 0)
        {
            return null;
        }
        var at = TimeUtil.Fmt(stage.At);
        return stage.Percent is { } percent
            ? new NoticeOp(input.PhaseMethod, m.Format("AGENT_LIMIT_NOTICE", ("at", at), ("percent", percent), ("n", targetCount)))
            : new NoticeOp(input.HardMethod, m.Format("AGENT_LIMIT_NOTICE_SUSPEND", ("at", at), ("n", targetCount)));
    }

    /// <summary>宣言の runs にその step があるか．runs が配列でない・要素が変でも例外は出さない（無いものとして扱う）．</summary>
    private static bool RunsHaveStep(JsonObject decl, int step) =>
        decl["runs"] is JsonArray runs && runs.Any(r => r is JsonObject o && Py.Int(o["step"]) == step);

    /// <summary>
    /// 基準の使用率（設計書 §4.2）．窓の終わりは，SoftClose 以前の行にその exe があれば SoftClose，無ければ now．
    /// 窓（終わりの手前 window）の行で act が 1 以上かつ cpu が 0 より大きい行の cpu の最大，無ければ cpu が 0 より大きい行の最大，無ければ null．
    /// </summary>
    private static double? BaselineOf(string exe, DateTime now, DateTime soft,
        IReadOnlyList<(DateTime T, JsonObject Rec)> lines, TimeSpan window)
    {
        var mine = new List<(DateTime T, double Act, double Cpu)>();
        foreach (var (t, rec) in lines)
        {
            if (rec["apps"] is JsonObject apps && apps[exe] is JsonObject e
                && Py.Float(e["cpu"]) is { } cpu && double.IsFinite(cpu))
            {
                mine.Add((t, Py.Float(e["act"]) ?? 0, cpu));
            }
        }
        var end = mine.Any(l => l.T <= soft) ? soft : now;
        var inWindow = mine.Where(l => l.T >= end - window && l.T <= end && l.Cpu > 0).ToList();
        var active = inWindow.Where(l => l.Act >= 1).ToList();
        var pool = active.Count > 0 ? active : inWindow;
        return pool.Count > 0 ? pool.Max(l => l.Cpu) : null;
    }
}
