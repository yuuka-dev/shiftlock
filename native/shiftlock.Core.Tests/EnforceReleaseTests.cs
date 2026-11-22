using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// 強制モードの発動判定と緊急解除のうち，ベクタ（tick.json の enforce・release）で見られないところ．
/// 迷ったら発動しない側に倒れること，解除は途中で失敗しても進めるところまで進むこと．
/// </summary>
public sealed class EnforceReleaseTests
{
    private const string Base =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"lastcall_minutes":40,"confirm":{"mode":"off"}}""";

    private const string Day = "2026-12-07";

    private static readonly DateTime Hard = new(2026, 12, 7, 10, 30, 0);

    private static DateTime T(string s) => TimeUtil.ParseIso(s)!.Value;

    /// <summary>同意済みの宣言（Python 版の shl enforce on が state.json に書く形）に over を重ねたものを置いたフォルダ．</summary>
    private static TempFolder Folder(string over = "{}", string cfgOver = "{}")
    {
        var enforce = JsonNode.Parse(
            """{"action":"lock","duration_hours":2,"declared_at":"2026-12-07T06:00","consented_at":"2026-12-07T06:00","via":"shl","runs":[],"grace_minutes":15,"relock_minutes":5,"max_runs":6,"boot_grace_minutes":15}""")!.AsObject();
        foreach (var (k, v) in JsonNode.Parse(over)!.AsObject())
        {
            enforce[k] = v?.DeepClone();
        }
        var shifts = JsonNode.Parse(Base)!.AsObject();
        foreach (var (k, v) in JsonNode.Parse(cfgOver)!.AsObject())
        {
            shifts[k] = v?.DeepClone();
        }
        return Vectors.Materialize(new JsonObject
        {
            ["shifts.json"] = shifts,
            ["state.json"] = new JsonObject { ["shifts"] = new JsonObject { [Day] = new JsonObject { ["enforce"] = enforce } } },
        });
    }

    private static TickResult Run(TempFolder folder, DateTime now, bool remote = false, double? uptime = 7200, double idle = 30)
    {
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        return Tick.Run(cfg, new Store(folder.Dir), cfg.Msg, new TickInput(idle, false, uptime, remote), now,
            _ => throw new IOException("no fetch"));
    }

    private static JsonObject EnforceState(TempFolder folder) =>
        JsonNode.Parse(File.ReadAllText(folder.P("state.json")))!["shifts"]![Day]!["enforce"]!.AsObject();

    private static IEnumerable<TickAction> EnforceActions(TickResult r) => r.Actions.Where(a => a.Kind == "enforce");

    [Fact]
    public void Remote_SkipsByDefault_ButNotWhenSkipIsOff()
    {
        using var folder = Folder();
        Assert.Empty(EnforceActions(Run(folder, Hard, remote: true)));
        Assert.False(EnforceState(folder).ContainsKey("warned_at"));     // リモート操作中は予告も始めない

        using var noSkip = Folder(cfgOver: """{"enforce":{"skip_if_remote":false}}""");
        Assert.Single(EnforceActions(Run(noSkip, Hard, remote: true)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    [InlineData(899.0)]
    public void OddUptime_DoesNotFire(double uptime)
    {
        using var folder = Folder();
        Assert.Empty(EnforceActions(Run(folder, Hard, uptime: uptime)));
    }

    [Theory]
    [InlineData("""{"warned_at":"こわれた"}""")]
    [InlineData("""{"seen_at":"こわれた"}""")]
    [InlineData("""{"grace_minutes":"長め"}""")]
    [InlineData("""{"max_runs":"たくさん"}""")]
    [InlineData("""{"boot_grace_minutes":null}""")]
    [InlineData("""{"runs":[{"at":"こわれた","action":"lock"}]}""")]
    [InlineData("""{"runs":"x"}""")]
    [InlineData("""{"action":5}""")]
    public void BrokenDeclaration_DoesNotFire_AndWarns(string over)
    {
        // 宣言の中身が読めないときは発動せず，通知だけ続ける（run_tick が例外を握るのと同じ）
        using var folder = Folder(over);

        var result = Run(folder, Hard);
        var later = Run(folder, Hard.AddMinutes(5));   // 12 分以内（空くと予告からやり直しになり，読めない warned_at は捨てられる）

        Assert.Empty(EnforceActions(result));
        Assert.Empty(EnforceActions(later));
        Assert.Contains(result.Actions, a => a.Kind == "hard");
        Assert.StartsWith("強制モードの判断に失敗（発動せず）: ", Assert.Single(result.Warnings));
        Assert.False(File.Exists(folder.P("events.log")));
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("throttle")]
    public void LimitActions_DueReturnsNothing_AndLeavesStateUntouched(string action)
    {
        // 減速・一時停止は Agent の段階の処理が持つ．見張りは予告も実行もせず，seen_at・warned_at も書かない
        using var folder = Folder($$"""{"action":"{{action}}","steps":[75,50,25,10]}""");
        var before = EnforceState(folder).ToJsonString();

        foreach (var min in new[] { 0, 5, 15, 30 })
        {
            Assert.Empty(EnforceActions(Run(folder, Hard.AddMinutes(min))));
        }

        var e = EnforceState(folder);
        Assert.False(e.ContainsKey("seen_at"));
        Assert.False(e.ContainsKey("warned_at"));
        Assert.Empty(e["runs"]!.AsArray());
        Assert.Equal(before, e.ToJsonString());   // 宣言は 1 バイトも変わらない
        Assert.False(File.Exists(folder.P("events.log")));
    }

    [Theory]
    [InlineData("suspend", "アプリの一時停止")]
    [InlineData("throttle", "アプリの減速")]
    public void LimitActions_SuffixHasNoHours(string action, string label)
    {
        using var folder = Folder($$"""{"action":"{{action}}","steps":[75,50,25,10]}""");

        var hard = Run(folder, Hard).Actions.Single(a => a.Kind == "hard");

        Assert.Contains($" 今日は強制モード（{label}）だよ。解除は kaijo.cmd。", hard.Text);
        Assert.DoesNotContain("時間", hard.Text.Split("今日は強制モード")[1]);
    }

    [Fact]
    public void Run_IsLastInTheList_AndSpeaks()
    {
        using var folder = Folder("""{"warned_at":"2026-12-07T10:30:00","seen_at":"2026-12-07T10:40:00"}""");

        var result = Run(folder, T("2026-12-07T10:45"));

        var last = result.Actions[^1];
        Assert.Equal(("enforce", "enforce", "lock", true), (last.Method, last.Kind, last.Action, last.Speak));
        Assert.All(result.Actions.Take(result.Actions.Count - 1), a => Assert.Null(a.Action));
    }

    [Fact]
    public void State_IsACopy()
    {
        using var folder = Folder();
        var store = new Store(folder.Dir);

        var e = Enforce.State(store, Day);
        e["released_at"] = "2026-12-07T09:00";

        Assert.False(Enforce.State(store, Day).ContainsKey("released_at"));
        Assert.Empty(Enforce.State(store, "2026-12-08"));
    }

    // ---- 緊急解除 ----

    [Fact]
    public void Release_StateUnreadable_StillWritesStopFile_ThenThrows()
    {
        using var folder = Folder();
        File.WriteAllText(folder.P("state.json"), "{");
        var store = new Store(folder.Dir);
        var m = Messages.Load("ja");

        Assert.Throws<StateBusyException>(() => Enforce.Release(store, m, T("2026-12-07T11:00"), "tray"));

        Assert.Equal("2026-12-07 11:00 tray\n", File.ReadAllText(folder.P("STOP.txt")));   // STOP.txt があれば強制は止まる
    }

    [Fact]
    public void Release_StopFileUnwritable_StillMarksDeclarations_ThenThrows()
    {
        using var folder = Folder();
        Directory.CreateDirectory(folder.P("STOP.txt"));     // フォルダがあると追記できない（それでも「STOP.txt がある」扱いで強制は止まる）
        var store = new Store(folder.Dir);

        Assert.Throws<StateBusyException>(() => Enforce.Release(store, Messages.Load("ja"), T("2026-12-07T11:00"), "tray"));

        Assert.Equal("2026-12-07T11:00", EnforceState(folder)["released_at"]!.GetValue<string>());
        Assert.Equal("2026-12-07T11:00\t2026-12-07\tenforce_release\ttray\n", File.ReadAllText(folder.P("events.log")));
    }

    [Fact]
    public void Release_ThenTick_DoesNothing()
    {
        using var folder = Folder("""{"warned_at":"2026-12-07T10:30:00","seen_at":"2026-12-07T10:40:00"}""");
        Enforce.Release(new Store(folder.Dir), Messages.Load("ja"), T("2026-12-07T10:44"), "tray");

        Assert.Empty(EnforceActions(Run(folder, T("2026-12-07T10:45"))));

        File.Delete(folder.P("STOP.txt"));                   // STOP.txt を消しても，解除した宣言は戻らない
        Assert.Empty(EnforceActions(Run(folder, T("2026-12-07T10:50"))));
    }
}
