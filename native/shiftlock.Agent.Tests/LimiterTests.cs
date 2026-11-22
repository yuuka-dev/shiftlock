using System.Text.Json.Nodes;
using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>プロセスに触る操作の偽物．呼ばれた操作を記録し，結果と例外を差し替えられる（実物の WindowsLimitControl は使わない）．</summary>
public sealed class FakeLimitControl : ILimitControl
{
    public List<string> Calls { get; } = [];
    public HashSet<string> CapFailsFor { get; } = [];
    public bool SuspendResult { get; set; } = true;
    public bool ResumeResult { get; set; } = true;
    public bool UncapResult { get; set; } = true;
    public bool Throws { get; set; }
    public HashSet<ProcKey>? Missing { get; set; }
    public Action<ProcKey>? OnSuspend { get; set; }
    public ManualResetEventSlim? CapEntered { get; set; }
    public ManualResetEventSlim? CapHold { get; set; }

    private void Check()
    {
        if (Throws)
        {
            throw new InvalidOperationException("boom");
        }
    }

    public bool Cap(string jobName, IReadOnlyList<ProcKey> procs, double percent)
    {
        lock (Calls)
        {
            Calls.Add($"cap {jobName} {percent:0.##} n={procs.Count}");
        }
        CapEntered?.Set();
        CapHold?.Wait();
        Check();
        return !CapFailsFor.Contains(jobName);
    }

    public bool Uncap(string jobName)
    {
        lock (Calls)
        {
            Calls.Add($"uncap {jobName}");
        }
        Check();
        return UncapResult;
    }

    public bool Suspend(ProcKey proc)
    {
        lock (Calls)
        {
            Calls.Add($"suspend {proc.Pid}");
        }
        OnSuspend?.Invoke(proc);
        Check();
        return SuspendResult;
    }

    public bool Resume(ProcKey proc)
    {
        lock (Calls)
        {
            Calls.Add($"resume {proc.Pid}");
        }
        Check();
        return ResumeResult;
    }

    public bool Exists(ProcKey proc) => Missing is null || !Missing.Contains(proc);

    public int Count(string prefix)
    {
        lock (Calls)
        {
            return Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
        }
    }
}

/// <summary>ゲームのフォルダの中身の偽物（探した回数を数える）．</summary>
public sealed class FakeFolders : IFolderLister
{
    public Dictionary<string, IReadOnlyList<string>?> ByDir { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Asked { get; } = [];

    public IReadOnlyList<string>? Names(string dir, int depth, int maxEntries = 20000)
    {
        Asked.Add(dir);
        return ByDir.TryGetValue(dir, out var names) ? names : ["game.exe", "data"];
    }
}

/// <summary>
/// アプリの減速・一時停止の 10 秒の仕事（Limiter）．偽の control・偽のフォルダ・合成の実行フォルダ（一時フォルダ）で，
/// 記録が先・失敗は記録から外す・判断できなければ戻す・例外を外に出さない，を確かめる．時刻は 2026-12-07（合成）．
/// 乗務は WatcherTests.Shifts（就寝目安 08:00・最低ライン 10:30）．
/// </summary>
public sealed class LimiterTests : IDisposable
{
    private const string Day = "2026-12-07";
    private const string Elden = "eldenring.exe";
    private const string Apex = "apex.exe";
    private const string Apps = ",\"apps\":{\"registered\":{\"code.exe\":\"dev\"},\"active_input_minutes\":5,\"active_cpu_percent\":5}";

    private static readonly DateTime Born = new(2026, 12, 7, 6, 0, 0);

    private readonly TempDir _dir = new();
    private readonly FakeLimitControl _control = new();
    private readonly FakeFolders _folders = new();
    private readonly List<TickAction> _notices = [];
    private readonly Messages _m = Messages.Load("ja");
    private readonly Plan _plan;
    private readonly GameRules _rules;
    private readonly List<ProcSample> _procs = [];
    private readonly Dictionary<ProcKey, string?> _paths = [];
    private DateTime _now;
    private DateTime? _sampleAt;
    private bool _parents = true;
    private bool _noSnapshot;

    public LimiterTests()
    {
        _dir.Put("shifts.json", WatcherTests.Shifts[..^1] + Apps + "}");
        var cfg = ShiftConfig.Load(_dir.P("shifts.json"));
        _plan = Planner.TargetPlan(cfg, new DateTime(2026, 12, 7, 5, 0, 0))!;
        _rules = Games.Rules(cfg, []);
        Declare("suspend");
        AddGame(100, Elden, @"C:\Games\Elden\eldenring.exe", children: 1);
        _now = Stage(1).AddMinutes(5);
    }

    public void Dispose() => _dir.Dispose();

    // ---- 部品 ----

    private DateTime Stage(int step) => LimitStages.Of(_plan.SoftClose, _plan.HardClose, [75, 50, 25, 10], "suspend")[step - 1].At;

    private void Declare(string action, string extra = "")
    {
        _dir.Put("state.json",
            "{\"shifts\":{\"" + Day + "\":{\"enforce\":{\"action\":\"" + action
            + "\",\"consented_at\":\"2026-12-07T06:00\",\"steps\":[75,50,25,10],\"limit_apps\":[],\"runs\":[]" + extra + "}}}}");
    }

    private void AddGame(int pid, string exe, string path, int children = 0)
    {
        var root = new ProcSample(pid, 1, Born.AddSeconds(pid), TimeSpan.Zero, exe);
        _procs.Add(root);
        _paths[root.Key] = path;
        for (var i = 1; i <= children; i++)
        {
            _procs.Add(new ProcSample(pid + i, pid, Born.AddSeconds(pid + i), TimeSpan.Zero, "child.exe"));
        }
    }

    private MeterSnapshot? Snapshot()
    {
        if (_noSnapshot)
        {
            return null;
        }
        var games = _procs.Where(p => _paths.GetValueOrDefault(p.Key) is { } path && path.StartsWith(@"C:\Games\", StringComparison.Ordinal))
            .Select(p => p.Key).ToHashSet();
        var sample = new MeterSample(_sampleAt ?? _now, TimeSpan.Zero, [.. _procs], null, 0, games, 8, _parents);
        var settings = new MeterSettings(new Dictionary<string, string> { ["code.exe"] = "dev" }, TimeSpan.FromMinutes(5), 5, null);
        return new MeterSnapshot(sample, settings, _rules, _paths);
    }

    private Limiter NewLimiter() =>
        new(_dir.Dir, Snapshot, _control, _folders, a => _notices.AddRange(a), () => _now, new OncePerDay(new AgentLog(_dir.P("agent.log"))));

    private void PutState(LimitState state) => LimitFiles.WriteState(new Store(_dir.Dir), state);

    private static LimitState State(string? day = Day, IEnumerable<string>? baseline = null, IEnumerable<string>? capped = null,
        IEnumerable<SuspendedProc>? suspended = null) =>
        new(day, (baseline ?? []).ToDictionary(e => e, _ => 40.0),
            (capped ?? []).ToDictionary(e => e, e => new CappedApp(LimitPolicy.JobName(e), 30)),
            [.. suspended ?? []], new HashSet<string>(), new HashSet<string>(), new Dictionary<string, DateTime>());

    private LimitState ReadState() => LimitFiles.ReadState(new Store(_dir.Dir));

    private SuspendedProc Susp(int pid, string exe = Elden) => new(pid, Born.AddSeconds(pid), exe);

    private string[] AgentLogLines => _dir.Lines("agent.log");

    // ---- 起動時 ----

    [Fact]
    public void Startup_resumes_recorded_suspended()
    {
        PutState(State(suspended: [Susp(100), Susp(101)]));

        NewLimiter().Startup();

        Assert.Equal(2, _control.Count("resume"));
        Assert.Empty(ReadState().Suspended);
    }

    [Fact]
    public void Startup_skips_reused_pid()
    {
        PutState(State(suspended: [Susp(100)]));
        _control.Missing = [Susp(100).Key];   // PID が使い回された別のプロセス（開始時刻が違う）

        NewLimiter().Startup();

        Assert.Equal(0, _control.Count("resume"));
        Assert.Empty(ReadState().Suspended);
    }

    [Fact]
    public void Startup_keeps_recorded_suspended_when_resume_fails_but_process_exists()
    {
        PutState(State(suspended: [Susp(100)]));
        _control.ResumeResult = false;

        NewLimiter().Startup();

        Assert.Equal([Susp(100)], ReadState().Suspended);
    }

    [Fact]
    public void Startup_quarantines_broken_record()
    {
        _dir.Put("limit.json", "{");

        NewLimiter().Startup();

        Assert.True(File.Exists(_dir.P("limit.json.bad")));
        Assert.False(File.Exists(_dir.P("limit.json")));
        var notice = Assert.Single(_notices);
        Assert.Equal(_m["AGENT_LIMIT_BAD_RECORD"], notice.Text);
        Assert.Equal("popup", notice.Method);
        Assert.Equal("", notice.Sound);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public void Startup_keeps_baseline()
    {
        PutState(State(baseline: [Elden], suspended: [Susp(100)]) with { Announced = new HashSet<string> { "1" } });

        NewLimiter().Startup();

        var after = ReadState();
        Assert.Equal(Day, after.Day);
        Assert.Equal(40.0, after.Baseline[Elden]);
        Assert.Empty(after.Announced);
    }

    [Fact]
    public void Startup_announces_stale_cap_once_and_clears_it()
    {
        PutState(State(capped: [Elden]));
        _control.UncapResult = false;   // 新しい Agent はジョブのハンドルを持たない

        NewLimiter().Startup();

        var notice = Assert.Single(_notices);
        Assert.Equal(_m["AGENT_LIMIT_STALE_CAP"], notice.Text);
        Assert.Empty(ReadState().Capped);
    }

    [Fact]
    public void Startup_without_record_does_nothing()
    {
        NewLimiter().Startup();

        Assert.Empty(_control.Calls);
        Assert.Empty(_notices);
        Assert.False(File.Exists(_dir.P("limit.json")));
    }

    // ---- 記録が先 ----

    [Fact]
    public void RunOnce_writes_record_before_suspending()
    {
        _now = Stage(5).AddMinutes(1);
        PutState(State(baseline: [Elden]));
        var seen = new List<string>();
        _control.OnSuspend = k => seen.Add(File.ReadAllText(_dir.P("limit.json")));

        NewLimiter().RunOnce();

        Assert.Equal(2, _control.Count("suspend"));
        Assert.All(seen, text => Assert.Contains("\"pid\": 100", text, StringComparison.Ordinal));
        Assert.Equal(2, ReadState().Suspended.Count);
    }

    [Fact]
    public void RunOnce_does_not_suspend_when_record_cannot_be_written()
    {
        _now = Stage(5).AddMinutes(1);
        PutState(State(baseline: [Elden]));
        // 読めるが，置き換えられない（別のハンドルが開いている）
        using var hold = new FileStream(_dir.P("limit.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

        NewLimiter().RunOnce();

        Assert.Equal(0, _control.Count("suspend"));
        Assert.Equal(0, _control.Count("cap"));
        Assert.Contains(AgentLogLines, l => l.Contains("cannot write", StringComparison.Ordinal));
    }

    [Fact]
    public void RunOnce_does_not_cap_when_record_cannot_be_written()
    {
        PutState(State(baseline: [Elden]));
        using var hold = new FileStream(_dir.P("limit.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

        NewLimiter().RunOnce();

        Assert.Equal(0, _control.Count("cap"));
    }

    [Fact]
    public void RunOnce_skips_the_round_when_record_cannot_be_read()
    {
        _now = Stage(5).AddMinutes(1);
        PutState(State(baseline: [Elden]));
        using var hold = new FileStream(_dir.P("limit.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        NewLimiter().RunOnce();

        Assert.Empty(_control.Calls);
    }

    [Fact]
    public void RunOnce_quarantines_broken_record_and_goes_on()
    {
        _dir.Put("limit.json", "{\"suspended\": [");

        NewLimiter().RunOnce();

        Assert.True(File.Exists(_dir.P("limit.json.bad")));
        Assert.Contains(_notices, n => n.Text == _m["AGENT_LIMIT_BAD_RECORD"]);
    }

    // ---- 失敗 ----

    [Fact]
    public void RunOnce_failed_suspend_is_removed_from_record()
    {
        _now = Stage(5).AddMinutes(1);
        PutState(State(baseline: [Elden]));
        _control.SuspendResult = false;

        NewLimiter().RunOnce();

        Assert.Empty(ReadState().Suspended);
        Assert.Contains(_notices, n => n.Text == _m["AGENT_LIMIT_FAILED"]);
        Assert.Single(AgentLogLines, l => l.Contains("suspend failed", StringComparison.Ordinal));
    }

    [Fact]
    public void RunOnce_failed_cap_continues_with_other_apps()
    {
        AddGame(200, Apex, @"C:\Games\Apex\apex.exe");
        PutState(State(baseline: [Elden, Apex]));
        _control.CapFailsFor.Add(LimitPolicy.JobName(Elden));

        NewLimiter().RunOnce();

        Assert.Equal(2, _control.Count("cap"));   // 1 つ目が偽でも 2 つ目を呼ぶ
        var capped = ReadState().Capped;
        Assert.DoesNotContain(Elden, capped.Keys);
        Assert.Contains(Apex, capped.Keys);
        Assert.Equal(1, _control.Count("uncap " + LimitPolicy.JobName(Elden)));   // 一部入ったままにしない
    }

    [Fact]
    public void RunOnce_failed_notice_is_once_per_day()
    {
        _now = Stage(5).AddMinutes(1);
        PutState(State(baseline: [Elden]));
        _control.SuspendResult = false;
        var limiter = NewLimiter();

        limiter.RunOnce();
        _now = _now.AddSeconds(10);
        limiter.RunOnce();

        Assert.Single(_notices, n => n.Text == _m["AGENT_LIMIT_FAILED"]);
    }

    [Fact]
    public void RunOnce_control_throws_does_not_escape()
    {
        _now = Stage(5).AddMinutes(1);
        PutState(State(baseline: [Elden]));
        _control.Throws = true;
        var limiter = NewLimiter();

        limiter.RunOnce();
        limiter.RunOnce();

        Assert.Empty(ReadState().Suspended);
    }

    [Fact]
    public void RunOnce_agent_log_never_contains_app_names()
    {
        _now = Stage(5).AddMinutes(1);
        PutState(State(baseline: [Elden]));
        _control.SuspendResult = false;
        _control.ResumeResult = false;
        _control.Throws = true;

        NewLimiter().RunOnce();

        Assert.NotEmpty(AgentLogLines);
        Assert.DoesNotContain(AgentLogLines, l => l.Contains("eldenring", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_notices, n => n.Text.Contains("eldenring", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 判断できないとき ----

    [Fact]
    public void RunOnce_stale_sample_restores()
    {
        PutState(State(baseline: [Elden], capped: [Elden]));
        _sampleAt = _now.AddSeconds(-35);

        NewLimiter().RunOnce();

        Assert.Equal(1, _control.Count("uncap"));
        Assert.Equal(0, _control.Count("cap"));
        Assert.Empty(ReadState().Capped);
    }

    [Fact]
    public void RunOnce_no_snapshot_restores()
    {
        PutState(State(baseline: [Elden], capped: [Elden]));
        _noSnapshot = true;

        NewLimiter().RunOnce();

        Assert.Equal(1, _control.Count("uncap"));
    }

    [Fact]
    public void RunOnce_no_parents_restores_and_never_applies()
    {
        PutState(State(baseline: [Elden], capped: [Elden]));
        _parents = false;

        NewLimiter().RunOnce();

        Assert.Equal(1, _control.Count("uncap"));
        Assert.Equal(0, _control.Count("cap"));
        Assert.Empty(ReadState().Capped);
    }

    [Fact]
    public void RunOnce_bad_config_restores()
    {
        PutState(State(baseline: [Elden], capped: [Elden]));
        _dir.Put("shifts.json", "{");

        NewLimiter().RunOnce();

        Assert.Equal(1, _control.Count("uncap"));
        Assert.Equal(0, _control.Count("cap"));
    }

    [Fact]
    public void RunOnce_without_declaration_touches_nothing()
    {
        File.Delete(_dir.P("state.json"));

        NewLimiter().RunOnce();

        Assert.Empty(_control.Calls);
        Assert.Empty(_notices);
    }

    // ---- ゲームの判定 ----

    [Fact]
    public void RunOnce_scans_game_folder_once()
    {
        PutState(State(baseline: [Elden]));
        var limiter = NewLimiter();

        limiter.RunOnce();
        _now = _now.AddSeconds(10);
        limiter.RunOnce();
        _now = _now.AddSeconds(10);
        limiter.RunOnce();

        Assert.Equal([@"C:\Games\Elden\"], _folders.Asked);
    }

    [Fact]
    public void RunOnce_marks_anticheat_game_and_writes_limit_games()
    {
        PutState(State(baseline: [Elden]));
        _folders.ByDir[@"C:\Games\Elden\"] = ["EasyAntiCheat", "game.exe"];

        NewLimiter().RunOnce();

        Assert.Equal(0, _control.Count("cap"));
        var games = JsonNode.Parse(File.ReadAllText(_dir.P("limit-games.json")))!.AsObject();
        Assert.Equal("anticheat", (string?)games[Elden]!["verdict"]);
    }

    [Fact]
    public void RunOnce_unreadable_game_folder_is_unknown_and_left_alone()
    {
        PutState(State(baseline: [Elden]));
        _folders.ByDir[@"C:\Games\Elden\"] = null;

        NewLimiter().RunOnce();

        Assert.Equal(0, _control.Count("cap"));
        Assert.Equal("unknown", (string?)JsonNode.Parse(File.ReadAllText(_dir.P("limit-games.json")))![Elden]!["verdict"]);
    }

    [Fact]
    public void RunOnce_anticheat_process_in_tree_marks_game_anticheat()
    {
        PutState(State(baseline: [Elden]));
        _procs.Add(new ProcSample(150, 100, Born.AddSeconds(150), TimeSpan.Zero, "easyanticheat_eos.exe"));

        NewLimiter().RunOnce();

        Assert.Equal(0, _control.Count("cap"));
        Assert.Equal("anticheat", (string?)JsonNode.Parse(File.ReadAllText(_dir.P("limit-games.json")))![Elden]!["verdict"]);
    }

    [Fact]
    public void RunOnce_target_game_is_capped_with_baseline_times_percent()
    {
        PutState(State(baseline: [Elden]));

        NewLimiter().RunOnce();

        Assert.Equal(["cap shiftlock-limit-eldenring.exe 30 n=2"], _control.Calls);
        Assert.Equal(30, ReadState().Capped[Elden].Percent);
        Assert.Equal("target", (string?)JsonNode.Parse(File.ReadAllText(_dir.P("limit-games.json")))![Elden]!["verdict"]);
    }

    [Fact]
    public void RunOnce_baseline_is_read_from_the_app_log()
    {
        _dir.Put("log/app-2026-12.jsonl",
            "{\"t\":\"2026-12-07T07:50:00\",\"span\":300,\"apps\":{\"eldenring.exe\":{\"cat\":\"game\",\"act\":300,\"cpu\":20.0}},\"other\":0}\n"
            + "{\"t\":\"2026-12-07T07:55:00\",\"span\":300,\"apps\":{\"eldenring.exe\":{\"cat\":\"game\",\"act\":300,\"cpu\":40.0}},\"other\":0}\n");

        NewLimiter().RunOnce();

        Assert.Equal(["cap shiftlock-limit-eldenring.exe 30 n=2"], _control.Calls);   // 最大の 40 × 75%
        Assert.Equal(40, ReadState().Baseline[Elden]);
    }

    // ---- 記録（runs・events） ----

    [Fact]
    public void RunOnce_appends_run_and_event()
    {
        PutState(State(baseline: [Elden]));
        var limiter = NewLimiter();

        limiter.RunOnce();
        _now = _now.AddSeconds(10);
        limiter.RunOnce();   // 同じ段階の 2 回目は足さない

        var runs = JsonNode.Parse(File.ReadAllText(_dir.P("state.json")))!["shifts"]![Day]!["enforce"]!["runs"]!.AsArray();
        var run = Assert.Single(runs);
        Assert.Equal(1, (int?)run!["step"]);
        Assert.Equal("suspend", (string?)run["action"]);
        Assert.Contains(_dir.Lines("events.log"), l => l.EndsWith("\tenforce_run\tsuspend 1", StringComparison.Ordinal));
    }

    [Fact]
    public void RunOnce_does_not_append_run_when_every_cap_failed()
    {
        PutState(State(baseline: [Elden]));
        _control.CapFailsFor.Add(LimitPolicy.JobName(Elden));

        NewLimiter().RunOnce();

        Assert.Empty(JsonNode.Parse(File.ReadAllText(_dir.P("state.json")))!["shifts"]![Day]!["enforce"]!["runs"]!.AsArray());
    }

    [Fact]
    public void RunOnce_depart_restores_and_writes_release_event()
    {
        PutState(State(baseline: [Elden], capped: [Elden]));
        _dir.Put("state.json",
            "{\"shifts\":{\"" + Day + "\":{\"status\":\"worked\",\"enforce\":{\"action\":\"suspend\",\"consented_at\":\"2026-12-07T06:00\","
            + "\"steps\":[75,50,25,10],\"limit_apps\":[],\"runs\":[]}}}}");

        NewLimiter().RunOnce();

        Assert.Equal(1, _control.Count("uncap"));
        Assert.Contains(_dir.Lines("events.log"), l => l.EndsWith("\tlimit_release\tdepart", StringComparison.Ordinal));
        Assert.Contains(_notices, n => n.Text == _m["AGENT_LIMIT_RELEASED"]);
    }

    // ---- 排他 ----

    [Fact]
    public async Task RunOnce_skips_while_the_previous_round_is_running()
    {
        PutState(State(baseline: [Elden]));
        _control.CapEntered = new ManualResetEventSlim();
        _control.CapHold = new ManualResetEventSlim();
        var limiter = NewLimiter();

        var first = Task.Run(limiter.RunOnce);
        Assert.True(_control.CapEntered.Wait(TimeSpan.FromSeconds(10)));
        limiter.RunOnce();   // 重ならない（飛ばす）
        _control.CapHold.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _control.Count("cap"));
    }

    [Fact]
    public void Notices_are_delivered_after_the_lock_is_released()
    {
        PutState(State(baseline: [Elden]));
        Limiter? limiter = null;
        var restoredInside = false;
        limiter = new Limiter(_dir.Dir, Snapshot, _control, _folders, a =>
        {
            _notices.AddRange(a);
            // 通知（ポップアップは長く止まる）の最中にトレイの緊急解除が入っても，排他を待たされない
            var watch = System.Diagnostics.Stopwatch.StartNew();
            limiter!.RestoreAll("kaijo");
            restoredInside = watch.Elapsed < TimeSpan.FromSeconds(3);
        }, () => _now, new OncePerDay(new AgentLog(_dir.P("agent.log"))));
        _now = Stage(5).AddMinutes(-3);   // 予告が出る時刻

        limiter.RunOnce();

        Assert.NotEmpty(_notices);
        Assert.True(restoredInside);
    }

    // ---- 戻す ----

    [Fact]
    public void RestoreAll_uncaps_resumes_and_clears()
    {
        PutState(State(baseline: [Elden], capped: [Elden], suspended: [Susp(100), Susp(101)]));

        NewLimiter().RestoreAll("kaijo");

        Assert.Equal(1, _control.Count("uncap"));
        Assert.Equal(2, _control.Count("resume"));
        var after = ReadState();
        Assert.Empty(after.Capped);
        Assert.Empty(after.Suspended);
        Assert.Equal(40.0, after.Baseline[Elden]);   // 基準は残す
        Assert.Contains(_dir.Lines("events.log"), l => l.EndsWith("\tlimit_release\tkaijo", StringComparison.Ordinal));
        Assert.Empty(_notices);   // トレイが結果を知らせる
    }

    [Fact]
    public void RestoreAll_null_reason_writes_no_event()
    {
        PutState(State(capped: [Elden], suspended: [Susp(100)]));

        NewLimiter().RestoreAll(null);

        Assert.Equal(1, _control.Count("uncap"));
        Assert.Equal(1, _control.Count("resume"));
        Assert.False(File.Exists(_dir.P("events.log")));
    }

    [Fact]
    public void RestoreAll_does_not_touch_a_reused_pid()
    {
        PutState(State(suspended: [Susp(100)]));
        _control.Missing = [Susp(100).Key];

        NewLimiter().RestoreAll(null);

        Assert.Equal(0, _control.Count("resume"));
        Assert.Empty(ReadState().Suspended);
    }

    [Fact]
    public void RestoreAll_keeps_unresumed_process_in_the_record()
    {
        PutState(State(suspended: [Susp(100)]));
        _control.ResumeResult = false;

        NewLimiter().RestoreAll(null);

        Assert.Equal([Susp(100)], ReadState().Suspended);
    }

    [Fact]
    public void RestoreAll_with_nothing_recorded_is_quiet()
    {
        NewLimiter().RestoreAll("kaijo");

        Assert.Empty(_control.Calls);
        Assert.False(File.Exists(_dir.P("events.log")));
        Assert.False(File.Exists(_dir.P("limit.json")));
    }

    [Fact]
    public void RestoreAll_with_broken_record_does_not_throw_and_leaves_it_for_startup()
    {
        _dir.Put("limit.json", "{");

        NewLimiter().RestoreAll(null);

        Assert.True(File.Exists(_dir.P("limit.json")));
    }

    [Fact]
    public void RunOnce_unexpected_exception_is_logged_once_and_does_not_escape()
    {
        var limiter = new Limiter(_dir.Dir, () => throw new InvalidOperationException("boom"), _control, _folders,
            a => _notices.AddRange(a), () => _now, new OncePerDay(new AgentLog(_dir.P("agent.log"))));

        limiter.RunOnce();
        limiter.RunOnce();

        Assert.Single(AgentLogLines, l => l.Contains("limit failed: InvalidOperationException", StringComparison.Ordinal));
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public void Notify_throwing_does_not_escape()
    {
        PutState(State(baseline: [Elden]));
        var limiter = new Limiter(_dir.Dir, Snapshot, _control, _folders, _ => throw new InvalidOperationException("boom"),
            () => _now, new OncePerDay(new AgentLog(_dir.P("agent.log"))));
        _now = Stage(5).AddMinutes(1);

        limiter.RunOnce();

        Assert.Equal(2, _control.Count("suspend"));
        Assert.Contains(AgentLogLines, l => l.Contains("notify failed", StringComparison.Ordinal));
    }
}
