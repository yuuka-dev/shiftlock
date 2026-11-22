using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>減速・一時停止・解除の判断（設計書 §4.5〜4.9・§7 の 1）．S = 12/7 13:30，H = 17:00，StartLatest = 22:30，steps = 75・50・25・10．</summary>
public sealed class LimitPolicyTests
{
    private const string Day = "2026-12-07";
    private const string Phase = "toast";
    private const string Hard = "popup";

    private static readonly Messages M = Messages.FromTexts("ja", new Dictionary<string, string>
    {
        ["AGENT_LIMIT_NOTICE"] = "{at} から対象 {n} 件を {percent}% に下げる",
        ["AGENT_LIMIT_NOTICE_SUSPEND"] = "{at} に対象 {n} 件を一時停止する",
        ["AGENT_LIMIT_SUSPENDED"] = "{n} 件を一時停止した",
        ["AGENT_LIMIT_RELEASED"] = "元に戻した",
        ["AGENT_LIMIT_SKIPPED"] = "見送った",
    });

    private static DateTime At(string hm)
    {
        var parts = hm.Split(':').Select(int.Parse).ToArray();
        return new DateTime(2026, 12, 7, parts[0], parts[1], parts.Length > 2 ? parts[2] : 0);
    }

    private static Plan MakePlan(string soft = "13:30", string hard = "17:00") =>
        new(Day, At("22:00"), At("22:30"), At("22:00"), At("22:30"), At(soft), At(hard), At("13:00"));

    private static readonly DateTime Born = At("10:00");

    private static ProcSample Proc(int pid, string name) => new(pid, 1, Born.AddSeconds(pid), TimeSpan.Zero, name);

    private static LimitApp Game(string exe = "eldenring.exe", LimitVerdict verdict = LimitVerdict.Target, int firstPid = 100, int count = 3) =>
        new(exe, true, [.. Enumerable.Range(firstPid, count).Select(pid => Proc(pid, pid == firstPid ? exe : "child.exe"))], verdict);

    private static JsonObject Decl(string action = "suspend", string? runs = null, string? steps = null, bool released = false)
    {
        var d = new JsonObject
        {
            ["action"] = action,
            ["consented_at"] = "2026-12-07T06:00",
            ["steps"] = JsonNode.Parse(steps ?? "[75,50,25,10]"),
            ["runs"] = JsonNode.Parse(runs ?? "[]"),
        };
        if (released)
        {
            d["released_at"] = "2026-12-07T11:00";
        }
        return d;
    }

    private static LimitState State(double? baseline = 40.0, string day = Day, IEnumerable<(string Exe, double Pct)>? capped = null,
        IEnumerable<SuspendedProc>? suspended = null, IEnumerable<string>? announced = null, IEnumerable<string>? skipped = null,
        IReadOnlyDictionary<string, DateTime>? pending = null) =>
        new(day,
            baseline is { } b ? new Dictionary<string, double> { ["eldenring.exe"] = b } : new Dictionary<string, double>(),
            (capped ?? []).ToDictionary(c => c.Exe, c => new CappedApp(LimitPolicy.JobName(c.Exe), c.Pct)),
            [.. suspended ?? []],
            new HashSet<string>(skipped ?? []),
            new HashSet<string>(announced ?? []),
            pending ?? new Dictionary<string, DateTime>());

    // 実際の行と同じく，ファイルから読んだ形（JSON の文字列を解析したもの）にする
    private static (DateTime, JsonObject) Line(string hm, string exe, int act, double cpu)
    {
        var json = "{\"span\":300,\"apps\":{\"" + exe + "\":{\"cat\":\"game\",\"act\":" + act + ",\"cpu\":"
            + cpu.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "}},\"other\":0}";
        return (At(hm), JsonNode.Parse(json)!.AsObject());
    }

    private static LimitInput In(string hm, string action = "suspend", LimitState? state = null, IReadOnlyList<LimitApp>? apps = null,
        bool appsNull = false, JsonObject? decl = null, string? status = null, bool stop = false, bool busy = false,
        IReadOnlyList<(DateTime, JsonObject)>? lines = null, Plan? plan = null, bool noPlan = false) =>
        new(At(hm), noPlan ? null : plan ?? MakePlan(), decl ?? Decl(action), status, stop, state ?? State(),
            appsNull ? null : apps ?? [Game()], lines ?? [], TimeSpan.FromMinutes(30), busy, Phase, Hard);

    private static LimitDecision Decide(LimitInput i) => LimitPolicy.Decide(i, M);

    private static T[] Of<T>(LimitDecision d) where T : LimitOp => [.. d.Ops.OfType<T>()];

    private static SuspendedProc Susp(int pid, string exe = "eldenring.exe") => new(pid, Born.AddSeconds(pid), exe);

    // ---- 段階と予告 ----

    [Fact]
    public void Before_soft_no_ops()
    {
        var d = Decide(In("13:20"));
        Assert.Empty(d.Ops);
        Assert.Empty(d.StateAfterOps.Announced);
        Assert.Empty(d.StateAfterOps.Capped);
    }

    [Fact]
    public void Notice_five_minutes_before_stage_1()
    {
        var d = Decide(In("13:25"));
        var n = Assert.Single(d.Ops);
        var notice = Assert.IsType<NoticeOp>(n);
        Assert.Equal(Phase, notice.Method);
        Assert.Contains("13:30", notice.Text);
        Assert.Contains("75", notice.Text);
        Assert.DoesNotContain("eldenring", notice.Text);
        Assert.Contains("1", d.StateAfterOps.Announced);
    }

    [Fact]
    public void Notice_not_repeated() => Assert.Empty(Decide(In("13:26", state: State(announced: ["1"]))).Ops);

    [Fact]
    public void No_notice_when_no_target_running() => Assert.Empty(Decide(In("13:25", apps: [])).Ops);

    [Fact]
    public void Suspend_notice_uses_hard_method_and_throttle_has_no_stage_5_notice()
    {
        var suspend = Assert.IsType<NoticeOp>(Assert.Single(Decide(In("16:56", state: State(capped: [("eldenring.exe", 4.0)]))).Ops));
        Assert.Equal(Hard, suspend.Method);
        Assert.Contains("17:00", suspend.Text);

        // throttle の段階 5 は 10% のままなので予告するものが無い
        Assert.Empty(Decide(In("16:56", action: "throttle", state: State(capped: [("eldenring.exe", 4.0)]))).Ops);
    }

    [Fact]
    public void Stage_1_caps_at_75_percent_of_baseline()
    {
        var d = Decide(In("13:30"));
        var cap = Assert.Single(Of<CapOp>(d));
        Assert.Equal("eldenring.exe", cap.Exe);
        Assert.Equal("shiftlock-limit-eldenring.exe", cap.Job);
        Assert.Equal(30.0, cap.Percent);
        Assert.Equal(3, cap.Procs.Count);
        Assert.Equal(new RunOp(Day, "suspend", 1), Assert.Single(Of<RunOp>(d)));
        Assert.Equal(30.0, d.StateAfterOps.Capped["eldenring.exe"].Percent);
        Assert.Equal(30.0, d.StateBeforeOps.Capped["eldenring.exe"].Percent);   // 上限の記録は操作の前に書く（Agent が落ちたとき知らせるため）
    }

    [Theory]
    [InlineData("14:22:30", 20.0)]
    [InlineData("15:15", 10.0)]
    [InlineData("16:07:30", 4.0)]
    public void Stage_boundaries_exact(string hm, double expected) =>
        Assert.Equal(expected, Assert.Single(Of<CapOp>(Decide(In(hm)))).Percent);

    [Fact]
    public void Just_before_stage_boundary_keeps_previous_stage()
    {
        var d = Decide(In("14:22:29", state: State(capped: [("eldenring.exe", 30.0)]), decl: Decl(runs: """[{"step":1}]""")));
        Assert.Empty(Of<CapOp>(d));
    }

    [Fact]
    public void Cap_floor_is_half_percent() =>
        Assert.Equal(0.5, Assert.Single(Of<CapOp>(Decide(In("16:07:30", state: State(baseline: 2.0))))).Percent);

    [Fact]
    public void Same_percent_is_not_capped_again_and_new_percent_is()
    {
        var capped = State(capped: [("eldenring.exe", 30.0)]);
        var runs = Decl(runs: """[{"step":1}]""");
        Assert.Empty(Decide(In("13:40", state: capped, decl: runs)).Ops);

        var d = Decide(In("14:22:30", state: capped, decl: runs));
        Assert.Equal(20.0, Assert.Single(Of<CapOp>(d)).Percent);
        Assert.Equal(new RunOp(Day, "suspend", 2), Assert.Single(Of<RunOp>(d)));
    }

    [Fact]
    public void Throttle_keeps_10_percent_after_hard()
    {
        var capped = Decide(In("17:30", action: "throttle", state: State(capped: [("eldenring.exe", 4.0)])));
        Assert.Empty(capped.Ops);

        var fresh = Decide(In("17:30", action: "throttle"));
        Assert.Equal(4.0, Assert.Single(Of<CapOp>(fresh)).Percent);
        Assert.Empty(Of<SuspendOp>(fresh));
    }

    [Fact]
    public void Suspend_at_hard_suspends_whole_tree()
    {
        var d = Decide(In("17:00", state: State(capped: [("eldenring.exe", 4.0)])));
        Assert.Equal(3, Of<SuspendOp>(d).Length);
        Assert.Empty(Of<CapOp>(d));
        Assert.Equal(3, d.StateBeforeOps.Suspended.Count);   // 止める前に記録する
        Assert.Equal(3, d.StateAfterOps.Suspended.Count);
        Assert.All(d.StateBeforeOps.Suspended, p => Assert.Equal("eldenring.exe", p.Exe));
        Assert.Equal(new RunOp(Day, "suspend", 5), Assert.Single(Of<RunOp>(d)));
        var report = Assert.Single(Of<NoticeOp>(d));
        Assert.Equal(Hard, report.Method);
        Assert.Contains("1", report.Text);
        Assert.DoesNotContain("eldenring", report.Text);
        Assert.Contains("suspended", d.StateAfterOps.Announced);
    }

    [Fact]
    public void Already_suspended_not_suspended_again()
    {
        var d = Decide(In("17:00", state: State(capped: [("eldenring.exe", 4.0)], suspended: [Susp(100), Susp(101)],
            announced: ["suspended"])));
        var op = Assert.Single(Of<SuspendOp>(d));
        Assert.Equal(102, op.Proc.Pid);
        Assert.Equal(3, d.StateAfterOps.Suspended.Count);

        var again = Decide(In("17:00:10", state: d.StateAfterOps, decl: Decl(runs: """[{"step":5}]""")));
        Assert.Empty(again.Ops);
    }

    [Fact]
    public void App_started_after_hard_is_suspended()
    {
        var d = Decide(In("17:30", state: State(baseline: null, announced: ["suspended"])));
        Assert.Equal(3, Of<SuspendOp>(d).Length);   // 基準が無くても止める（止めるのに基準は要らない）
        Assert.Empty(Of<CapOp>(d));
    }

    [Fact]
    public void Hard_not_after_soft_only_stage_5()
    {
        var plan = MakePlan(soft: "17:00", hard: "17:00");
        Assert.Empty(Decide(In("16:50", plan: plan)).Ops);
        var d = Decide(In("17:00", plan: plan));
        Assert.Equal(3, Of<SuspendOp>(d).Length);
        Assert.Equal(new RunOp(Day, "suspend", 5), Assert.Single(Of<RunOp>(d)));

        var inverted = MakePlan(soft: "17:30", hard: "17:00");
        Assert.Equal(3, Of<SuspendOp>(Decide(In("17:00", plan: inverted))).Length);
    }

    [Fact]
    public void Started_mid_stage_applies_current_stage_only()
    {
        var d = Decide(In("15:20"));
        Assert.Equal(10.0, Assert.Single(Of<CapOp>(d)).Percent);
        Assert.Equal(new RunOp(Day, "suspend", 3), Assert.Single(Of<RunOp>(d)));
        Assert.Empty(Of<NoticeOp>(d));   // 過ぎた段階の予告は出さない
    }

    [Fact]
    public void Run_op_once_per_step()
    {
        var d = Decide(In("13:30", decl: Decl(runs: """[{"at":"2026-12-07T13:30:00","action":"suspend","step":1}]""")));
        Assert.Single(Of<CapOp>(d));
        Assert.Empty(Of<RunOp>(d));
    }

    [Fact]
    public void Narrow_stages_only_nearest_notice()
    {
        var d = Decide(In("13:26", plan: MakePlan(soft: "13:30", hard: "13:40")));
        var notice = Assert.IsType<NoticeOp>(Assert.Single(d.Ops));
        Assert.Contains("13:30", notice.Text);
        Assert.Equal(new[] { "1" }, d.StateAfterOps.Announced.ToArray());
    }

    // ---- 基準 ----

    [Fact]
    public void Baseline_is_max_of_active_lines_in_window()
    {
        var lines = new[]
        {
            Line("12:59", "eldenring.exe", 300, 90.0),   // 31 分前は入らない
            Line("13:05", "eldenring.exe", 300, 12.0),
            Line("13:15", "eldenring.exe", 300, 40.0),
            Line("13:25", "eldenring.exe", 300, 25.0),
            Line("13:20", "eldenring.exe", 0, 99.0),     // 動いていない行は，動いた行があれば見ない
        };
        var d = Decide(In("13:30", state: State(baseline: null), lines: lines));
        Assert.Equal(30.0, Assert.Single(Of<CapOp>(d)).Percent);
        Assert.Equal(40.0, d.StateAfterOps.Baseline["eldenring.exe"]);
    }

    [Fact]
    public void Baseline_falls_back_to_lines_with_cpu_when_never_active()
    {
        var lines = new[] { Line("13:10", "eldenring.exe", 0, 8.0), Line("13:20", "eldenring.exe", 0, 20.0), Line("13:25", "eldenring.exe", 0, 0.0) };
        var d = Decide(In("13:30", state: State(baseline: null), lines: lines));
        Assert.Equal(15.0, Assert.Single(Of<CapOp>(d)).Percent);
        Assert.Equal(20.0, d.StateAfterOps.Baseline["eldenring.exe"]);
    }

    [Fact]
    public void Baseline_ignores_zero_cpu_even_when_active()
    {
        // act があっても cpu が 0.0 の行だけなら，基準は 0 にせず待つ（上限が下限 0.5% に張り付かないように）
        var d = Decide(In("13:30", state: State(baseline: null), lines: [Line("13:25", "eldenring.exe", 300, 0.0)]));
        Assert.Empty(d.Ops);
        Assert.Empty(d.StateAfterOps.Baseline);
    }

    [Fact]
    public void Baseline_waits_when_no_line()
    {
        var d = Decide(In("13:30", state: State(baseline: null), lines: [Line("13:25", "apex.exe", 300, 50.0)]));
        Assert.Empty(d.Ops);   // CapOp も RunOp も出さない
        Assert.Empty(d.StateAfterOps.Baseline);
    }

    [Fact]
    public void Baseline_not_recomputed()
    {
        var d = Decide(In("13:30", lines: [Line("13:25", "eldenring.exe", 300, 5.0)]));
        Assert.Equal(30.0, Assert.Single(Of<CapOp>(d)).Percent);
        Assert.Equal(40.0, d.StateAfterOps.Baseline["eldenring.exe"]);
    }

    [Fact]
    public void Baseline_uses_lines_before_soft_when_state_lost()
    {
        var lines = new[] { Line("13:20", "eldenring.exe", 300, 40.0), Line("14:00", "eldenring.exe", 300, 8.0) };
        var d = Decide(In("15:00", state: State(baseline: null), lines: lines));
        Assert.Equal(20.0, Assert.Single(Of<CapOp>(d)).Percent);   // 段階 2: 40 × 50%
        Assert.Equal(40.0, d.StateAfterOps.Baseline["eldenring.exe"]);
    }

    [Fact]
    public void New_day_clears_baseline_and_restores_old_effects()
    {
        var old = State(baseline: 99.0, day: "2026-12-06", capped: [("eldenring.exe", 9.9)], announced: ["1", "suspended"], skipped: ["x.exe"]);
        var d = Decide(In("13:30", state: old, lines: [Line("13:25", "eldenring.exe", 300, 40.0)]));
        Assert.Single(Of<UncapOp>(d));
        Assert.Equal(30.0, Assert.Single(Of<CapOp>(d)).Percent);
        Assert.Equal(40.0, d.StateAfterOps.Baseline["eldenring.exe"]);
        Assert.Equal(Day, d.StateAfterOps.Day);
        Assert.Empty(d.StateAfterOps.Announced);
        Assert.Empty(d.StateAfterOps.Skipped);
        Assert.Empty(Of<NoticeOp>(d));   // 前の日の残りを戻すだけなので報告は出さない
    }

    // ---- 解除 ----

    private static LimitState WithEffects() =>
        State(capped: [("eldenring.exe", 4.0)], suspended: [Susp(100), Susp(101)], announced: ["1", "suspended"]);

    [Theory]
    [InlineData("depart")]
    [InlineData("yasumi")]
    [InlineData("kaijo-stop")]
    [InlineData("kaijo-released")]
    [InlineData("off")]
    [InlineData("timeout")]
    public void Release_conditions_restore_everything(string reason)
    {
        var input = reason switch
        {
            "depart" => In("17:30", status: "worked", state: WithEffects()),
            "yasumi" => In("17:30", status: "yasumi", state: WithEffects()),
            "kaijo-stop" => In("17:30", stop: true, state: WithEffects()),
            "kaijo-released" => In("17:30", decl: Decl(released: true), state: WithEffects()),
            "off" => In("17:30", decl: new JsonObject(), state: WithEffects()),
            _ => In("22:30", state: WithEffects()),
        };
        var expectedReason = reason.StartsWith("kaijo", StringComparison.Ordinal) ? "kaijo" : reason;

        var d = Decide(input);

        Assert.Equal("eldenring.exe", Assert.Single(Of<UncapOp>(d)).Exe);
        Assert.Equal(2, Of<ResumeOp>(d).Length);
        Assert.Equal(new ReleaseEventOp(Day, expectedReason), Assert.Single(Of<ReleaseEventOp>(d)));
        var report = Assert.Single(Of<NoticeOp>(d));
        Assert.Equal(Hard, report.Method);
        Assert.Equal("元に戻した", report.Text);
        Assert.Empty(Of<CapOp>(d));
        Assert.Empty(Of<SuspendOp>(d));
        Assert.Empty(d.StateAfterOps.Capped);
        Assert.Empty(d.StateAfterOps.Suspended);
        Assert.Empty(d.StateAfterOps.Announced);
        Assert.Equal(40.0, d.StateAfterOps.Baseline["eldenring.exe"]);   // 基準は残す
        Assert.Equal(2, d.StateBeforeOps.Suspended.Count);               // 戻せるまで記録は消さない
    }

    [Fact]
    public void Plan_missing_restores_as_off()
    {
        var d = Decide(In("13:30", noPlan: true, state: WithEffects()));
        Assert.Equal(new ReleaseEventOp(Day, "off"), Assert.Single(Of<ReleaseEventOp>(d)));
        Assert.Single(Of<UncapOp>(d));
    }

    [Fact]
    public void Declaration_without_consent_restores_as_off()
    {
        var decl = Decl();
        decl.Remove("consented_at");
        Assert.Equal(new ReleaseEventOp(Day, "off"), Assert.Single(Of<ReleaseEventOp>(Decide(In("13:30", decl: decl, state: WithEffects())))));
    }

    [Fact]
    public void Release_with_nothing_applied_is_silent()
    {
        Assert.Empty(Decide(In("17:30", status: "worked", state: State())).Ops);
        Assert.Empty(Decide(In("17:30", stop: true, state: State(announced: []))).Ops);
    }

    [Fact]
    public void Release_is_reported_once()
    {
        var first = Decide(In("17:30", status: "worked", state: WithEffects()));
        Assert.NotEmpty(first.Ops);
        Assert.Empty(Decide(In("17:30:10", status: "worked", state: first.StateAfterOps)).Ops);
    }

    [Fact]
    public void Lock_declaration_restores_without_event()
    {
        var d = Decide(In("17:30", action: "lock", state: State(capped: [("eldenring.exe", 4.0)])));
        Assert.Single(Of<UncapOp>(d));
        Assert.Empty(Of<ReleaseEventOp>(d));
    }

    [Fact]
    public void Unknown_apps_restores_and_adds_nothing()
    {
        var d = Decide(In("17:00", appsNull: true, state: WithEffects()));
        Assert.Single(Of<UncapOp>(d));
        Assert.Equal(2, Of<ResumeOp>(d).Length);
        Assert.Empty(Of<CapOp>(d));
        Assert.Empty(Of<SuspendOp>(d));
        Assert.Empty(Of<ReleaseEventOp>(d));
        Assert.Empty(Of<RunOp>(d));
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("[75,50,25]")]
    [InlineData("[10,25,50,75]")]
    [InlineData("null")]
    public void Broken_declared_steps_restores(string steps)
    {
        var decl = Decl();
        decl["steps"] = JsonNode.Parse(steps);
        var d = Decide(In("13:30", decl: decl, state: State(capped: [("eldenring.exe", 4.0)])));
        Assert.Single(Of<UncapOp>(d));
        Assert.Empty(Of<CapOp>(d));
        Assert.Empty(Of<ReleaseEventOp>(d));
    }

    [Fact]
    public void Odd_json_types_in_declaration_do_not_throw_and_restore()
    {
        var decl = new JsonObject { ["action"] = 5, ["consented_at"] = JsonValue.Create(true), ["runs"] = "x", ["steps"] = 1 };
        var d = Decide(In("13:30", decl: decl, state: State(capped: [("eldenring.exe", 4.0)])));
        Assert.Single(Of<UncapOp>(d));

        var odd = Decl();
        odd["runs"] = "x";
        var e = Decide(In("13:30", decl: odd));   // runs が壊れていても判断は続く（step は無いものとして足す）
        Assert.Single(Of<CapOp>(e));
    }

    // ---- 対象外・判定の変化 ----

    [Fact]
    public void Protected_app_skipped_and_announced_once()
    {
        var protectedApp = Game(verdict: LimitVerdict.Protected);
        var d = Decide(In("13:30", apps: [protectedApp]));
        Assert.Empty(Of<CapOp>(d));
        var notice = Assert.IsType<NoticeOp>(Assert.Single(d.Ops));
        Assert.Equal(Phase, notice.Method);
        Assert.Equal("見送った", notice.Text);
        Assert.Contains("eldenring.exe", d.StateAfterOps.Skipped);

        Assert.Empty(Decide(In("13:30:10", apps: [protectedApp], state: d.StateAfterOps)).Ops);
    }

    [Fact]
    public void Protected_app_before_soft_is_not_announced() =>
        Assert.Empty(Decide(In("13:10", apps: [Game(verdict: LimitVerdict.Protected)])).Ops);

    [Fact]
    public void App_becoming_protected_is_restored()
    {
        var d = Decide(In("14:00", apps: [Game(verdict: LimitVerdict.Protected)],
            state: State(capped: [("eldenring.exe", 30.0)], suspended: [Susp(100)], skipped: ["eldenring.exe"])));
        Assert.Single(Of<UncapOp>(d));
        Assert.Single(Of<ResumeOp>(d));
        Assert.Empty(d.StateAfterOps.Capped);
        Assert.Empty(d.StateAfterOps.Suspended);
        Assert.Empty(Of<NoticeOp>(d));
    }

    [Theory]
    [InlineData(LimitVerdict.Excluded)]
    [InlineData(LimitVerdict.AntiCheat)]
    [InlineData(LimitVerdict.Unknown)]
    [InlineData(LimitVerdict.NotAdded)]
    public void Non_target_after_cap_is_restored_silently(LimitVerdict verdict)
    {
        var d = Decide(In("14:00", apps: [Game(verdict: verdict)], state: State(capped: [("eldenring.exe", 30.0)], suspended: [Susp(101)])));
        Assert.Single(Of<UncapOp>(d));
        Assert.Single(Of<ResumeOp>(d));
        Assert.Empty(Of<NoticeOp>(d));
        Assert.Empty(Of<CapOp>(d));
        Assert.Empty(d.StateAfterOps.Skipped);
    }

    [Fact]
    public void Excluded_after_cap_is_restored_silently()
    {
        var d = Decide(In("14:00", apps: [Game(verdict: LimitVerdict.Excluded)], state: State(capped: [("eldenring.exe", 30.0)])));
        Assert.Single(Of<UncapOp>(d));
        Assert.Empty(Of<NoticeOp>(d));
    }

    [Fact]
    public void Ended_app_is_dropped_from_records_without_ops()
    {
        var d = Decide(In("14:00", apps: [], state: State(capped: [("eldenring.exe", 30.0)])));
        Assert.Empty(d.Ops);
        Assert.Empty(d.StateAfterOps.Capped);
        Assert.Empty(d.StateBeforeOps.Capped);
    }

    [Fact]
    public void Suspended_record_of_reused_pid_is_dropped_without_resume()
    {
        var stale = new SuspendedProc(100, Born.AddDays(-3), "eldenring.exe");   // 同じ pid・違う Start
        var d = Decide(In("13:30", state: State(suspended: [stale])));
        Assert.Empty(Of<ResumeOp>(d));
        Assert.Empty(d.StateAfterOps.Suspended);
        Assert.Empty(d.StateBeforeOps.Suspended);
    }

    // ---- 処理中の待ち ----

    [Fact]
    public void Notice_waits_while_claude_busy()
    {
        var d = Decide(In("14:20", busy: true));
        Assert.Equal(30.0, Assert.Single(Of<CapOp>(d)).Percent);   // 段階の切り替えは待たない
        Assert.Empty(Of<NoticeOp>(d));
        Assert.Equal(At("14:20"), d.StateAfterOps.Pending["2"]);
        Assert.Contains("2", d.StateAfterOps.Announced);
    }

    [Fact]
    public void Pending_notice_delivered_when_idle()
    {
        var pending = new Dictionary<string, DateTime> { ["2"] = At("14:20") };
        var d = Decide(In("14:21", state: State(capped: [("eldenring.exe", 30.0)], announced: ["1", "2"], pending: pending),
            decl: Decl(runs: """[{"step":1}]""")));
        var notice = Assert.IsType<NoticeOp>(Assert.Single(d.Ops));
        Assert.Contains("14:22", notice.Text);
        Assert.Contains("50", notice.Text);
        Assert.Empty(d.StateAfterOps.Pending);
    }

    [Fact]
    public void Pending_notice_delivered_after_ten_minutes()
    {
        var pending = new Dictionary<string, DateTime> { ["2"] = At("14:20") };
        LimitDecision At14(string hm) => Decide(In(hm, busy: true, state: State(capped: [("eldenring.exe", 20.0)], announced: ["1", "2"], pending: pending),
            decl: Decl(runs: """[{"step":1},{"step":2}]""")));

        var waiting = At14("14:29:59");
        Assert.Empty(waiting.Ops);
        Assert.Single(waiting.StateAfterOps.Pending);

        var due = At14("14:30");
        Assert.IsType<NoticeOp>(Assert.Single(due.Ops));
        Assert.Empty(due.StateAfterOps.Pending);
    }

    [Fact]
    public void Pending_suspended_report_is_rebuilt_from_state()
    {
        var pending = new Dictionary<string, DateTime> { ["suspended"] = At("17:00") };
        var d = Decide(In("17:01", state: State(capped: [("eldenring.exe", 4.0)], suspended: [Susp(100), Susp(101), Susp(102)],
            announced: ["suspended"], pending: pending), decl: Decl(runs: """[{"step":5}]""")));
        var notice = Assert.IsType<NoticeOp>(Assert.Single(d.Ops));
        Assert.Equal(Hard, notice.Method);
        Assert.Equal("1 件を一時停止した", notice.Text);
    }

    [Fact]
    public void Pending_notice_with_nothing_to_say_is_dropped()
    {
        var pending = new Dictionary<string, DateTime> { ["2"] = At("14:20"), ["suspended"] = At("14:20") };
        var d = Decide(In("14:21", apps: [], state: State(announced: ["1", "2"], pending: pending)));
        Assert.Empty(d.Ops);
        Assert.Empty(d.StateAfterOps.Pending);
    }

    // ---- RestoreAll ----

    [Fact]
    public void RestoreAll_for_exit_keeps_baseline_and_pending_and_writes_no_event()
    {
        var pending = new Dictionary<string, DateTime> { ["2"] = At("14:20") };
        var state = State(capped: [("eldenring.exe", 4.0)], suspended: [Susp(100)], announced: ["1"], pending: pending);
        var d = LimitPolicy.RestoreAll(state, Day, null, M, Hard);
        Assert.Single(Of<UncapOp>(d));
        Assert.Single(Of<ResumeOp>(d));
        Assert.Empty(Of<ReleaseEventOp>(d));
        Assert.Single(Of<NoticeOp>(d));
        Assert.Equal(40.0, d.StateAfterOps.Baseline["eldenring.exe"]);
        Assert.Equal(pending, d.StateAfterOps.Pending);
        Assert.Equal(Day, d.StateAfterOps.Day);
    }

    [Fact]
    public void RestoreAll_with_nothing_is_empty() =>
        Assert.Empty(LimitPolicy.RestoreAll(State(), Day, "kaijo", M, Hard).Ops);

    [Fact]
    public void Notices_never_contain_app_names()
    {
        var all = new List<string>();
        foreach (var hm in new[] { "13:25", "14:20", "16:56", "17:00", "17:30" })
        {
            var d = Decide(In(hm, state: State(capped: [("eldenring.exe", 4.0)], announced: hm == "17:30" ? ["suspended"] : [])));
            all.AddRange(Of<NoticeOp>(d).Select(n => n.Text));
        }
        all.AddRange(Of<NoticeOp>(Decide(In("13:30", apps: [Game(verdict: LimitVerdict.Protected)]))).Select(n => n.Text));
        all.AddRange(Of<NoticeOp>(Decide(In("17:30", status: "worked", state: WithEffects()))).Select(n => n.Text));
        Assert.NotEmpty(all);
        Assert.All(all, t => Assert.DoesNotContain("eldenring", t));
    }
}
