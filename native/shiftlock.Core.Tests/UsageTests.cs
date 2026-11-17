using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>稼働・離席の判定（pc/shiftlock/usage.py）と，壊れたファイルがあっても見張りが止まらないこと．</summary>
public sealed class UsageTests
{
    private const string Base =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"lastcall_minutes":40,"confirm":{"mode":"off"}}""";

    private static readonly DateTime Sat = new(2026, 12, 5, 10, 0, 0);

    private static DateTime T(string s) => TimeUtil.ParseIso(s)!.Value;

    private static TempFolder Folder(string? usage = null, string over = "{}")
    {
        var shifts = JsonNode.Parse(Base)!.AsObject();
        foreach (var (k, v) in JsonNode.Parse(over)!.AsObject())
        {
            shifts[k] = v?.DeepClone();
        }
        var files = new JsonObject { ["shifts.json"] = shifts };
        if (usage is not null)
        {
            files["usage-pc.json"] = usage;
        }
        return Vectors.Materialize(files);
    }

    private static TickResult Run(TempFolder folder, DateTime now, double idle = 30, bool game = false)
    {
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        return Tick.Run(cfg, new Store(folder.Dir), cfg.Msg, new TickInput(idle, game), now, _ => throw new IOException("no fetch"));
    }

    private static JsonObject UsageFile(TempFolder folder) => JsonNode.Parse(File.ReadAllText(folder.P("usage-pc.json")))!.AsObject();

    [Theory]
    [InlineData(19 * 60 + 59, false, true)]     // reset（20 分）未満なら触っている
    [InlineData(20 * 60, false, false)]
    [InlineData(20 * 60, true, true)]           // ゲーム中は game_idle まで
    [InlineData(180 * 60 - 1, true, true)]
    [InlineData(180 * 60, true, false)]
    public void PcActive_FollowsPython(int idleSeconds, bool game, bool expected)
    {
        Assert.Equal(expected,
            Usage.PcActive(TimeSpan.FromSeconds(idleSeconds), game, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(180)));
    }

    [Fact]
    public void PcActive_NoGameIdleLimit_IsAlwaysActiveInGame()
    {
        Assert.True(Usage.PcActive(TimeSpan.FromDays(3), true, TimeSpan.FromMinutes(20), null));
        Assert.False(Usage.PcActive(TimeSpan.FromDays(3), false, TimeSpan.FromMinutes(20), null));
    }

    [Theory]
    [InlineData("{}", 180.0)]
    [InlineData("""{"usage":{"game_idle_minutes":90}}""", 90.0)]
    [InlineData("""{"usage":{"game_idle_minutes":"45"}}""", 45.0)]
    [InlineData("""{"usage":{"game_idle_minutes":"長め"}}""", 180.0)]
    [InlineData("""{"usage":{"game_idle_minutes":null}}""", 180.0)]
    [InlineData("""{"usage":{"game_idle_minutes":"inf"}}""", 180.0)]
    [InlineData("""{"usage":{"game_idle_minutes":"nan"}}""", 180.0)]
    [InlineData("""{"usage":{"game_idle_minutes":[1]}}""", 180.0)]
    [InlineData("""{"usage":{"game_idle_minutes":0}}""", null)]
    [InlineData("""{"usage":{"game_idle_minutes":-5}}""", null)]
    public void GameIdleLimit_FollowsPython(string over, double? minutes)
    {
        using var folder = Folder(over: over);
        var limit = Usage.GameIdleLimit(ShiftConfig.Load(folder.ShiftsJson));
        Assert.Equal(minutes is { } m ? TimeSpan.FromMinutes(m) : null, limit);
    }

    [Fact]
    public void DueNag_After120Minutes_ThenEvery30()
    {
        using var folder = Folder();
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var u = new JsonObject { ["start"] = "2026-12-05T08:00:00" };

        Assert.Null(Usage.DueNag(u, T("2026-12-05T09:59:59"), cfg));
        Assert.False(u.ContainsKey("last_nag"));
        Assert.Equal(TimeSpan.FromMinutes(120), Usage.DueNag(u, T("2026-12-05T10:00:00"), cfg));
        Assert.Equal("2026-12-05T10:00:00", u["last_nag"]!.GetValue<string>());
        Assert.Null(Usage.DueNag(u, T("2026-12-05T10:29:59"), cfg));
        Assert.Equal(TimeSpan.FromMinutes(150), Usage.DueNag(u, T("2026-12-05T10:30:00"), cfg));
        Assert.Null(Usage.DueNag(new JsonObject(), Sat, cfg));                               // start が無い
        Assert.Null(Usage.DueNag(new JsonObject { ["start"] = "こわれた" }, Sat, cfg));     // 読めない start は無いものとして扱う
    }

    [Fact]
    public void PcGaps_IncludesTheOngoingGap()
    {
        using var folder = Folder("""{"gaps":[["2026-12-05T01:00","2026-12-05T07:00"],["こわれた","2026-12-05T08:00"],[1]],"gap_start":"2026-12-05T09:00:00"}""");
        var store = new Store(folder.Dir);

        Assert.Equal([(T("2026-12-05T01:00"), T("2026-12-05T07:00")), (T("2026-12-05T09:00"), Sat)], Usage.PcGaps(store, Sat));

        File.WriteAllText(folder.P("usage-pc.json"), """{"start":"2026-12-05T09:00:00"}""");
        Assert.Null(Usage.PcGaps(store, Sat));                                               // gaps が無い → 記録なし
        File.Delete(folder.P("usage-pc.json"));
        Assert.Null(Usage.PcGaps(store, Sat));
    }

    [Fact]
    public void ShiftAway_IgnoresBrokenTimes_AndLateReports()
    {
        using var folder = Folder();
        var store = new Store(folder.Dir);
        Assert.False(Usage.ShiftAway(store, Sat));

        File.WriteAllText(folder.P("events.log"),
            "2026-12-04T16:05\t2026-12-04\tworked\tshuppatsu\n2026-12-03T16:00\t2026-12-03\tworked\tlate claude\n");
        File.WriteAllText(folder.P("state.json"),
            """{"shifts":{"2026-12-04":{"status":"worked","at":"2026-12-04T16:05","kitaku":"こわれた"},"2026-12-03":{"status":"worked","at":"2026-12-03T16:00"},"x":5}}""");

        Assert.True(Usage.ShiftAway(store, Sat));                           // 出発から 24 時間以内で，帰宅がまだ無い
        Assert.False(Usage.ShiftAway(store, T("2026-12-05T16:05")));        // ちょうど 24 時間で外れる
        Assert.False(Usage.ShiftAway(store, T("2026-12-04T16:04")));        // 出発より前

        File.AppendAllText(folder.P("events.log"), "2026-12-05T07:10\t-\tkitaku\tfree-day\n");
        Assert.False(Usage.ShiftAway(store, Sat));                          // 乗務日に紐づかない帰宅でも戻った扱い
        Assert.True(Usage.ShiftAway(store, T("2026-12-05T07:09")));
    }

    // ---- 手で壊れたファイル（Review Focus 2）----

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"gaps":"x"}""")]
    [InlineData("""{"gaps":{"a":1},"gap_start":5}""")]
    [InlineData("""{"start":"こわれた"}""")]
    [InlineData("""{"start":"2026-12-05T09:00:00","last":"こわれた"}""")]
    [InlineData("""{"gaps":[],"gap_start":"こわれた"}""")]
    [InlineData("""{"gaps":[["a"],5,["2026-12-05T01:00","2026-12-05T02:00"]],"start":123,"last":[],"last_nag":{}}""")]
    public void BrokenUsageFile_DoesNotStopTheTick(string usage)
    {
        using var folder = Folder(usage);

        Run(folder, Sat);
        Run(folder, Sat.AddMinutes(5));

        // 2 回触ったあとは普通の形: 起点は 1 回目，最後は 2 回目
        var u = UsageFile(folder);
        Assert.Equal("2026-12-05T10:00:00", u["start"]!.GetValue<string>());
        Assert.Equal("2026-12-05T10:05:00", u["last"]!.GetValue<string>());
        Assert.IsType<JsonArray>(u["gaps"]);
        Assert.All(u["gaps"]!.AsArray(), g => Assert.Equal(2, Assert.IsType<JsonArray>(g).Count));
    }

    [Fact]
    public void BrokenUsageFile_AwayThenBack_StillRecordsTheGap()
    {
        using var folder = Folder("""{"gaps":"x","gap_start":"こわれた"}""");

        Run(folder, Sat, idle: 1500);                    // 離席中: 読めない gap_start は付け直す
        Assert.Equal("2026-12-05T09:35:00", UsageFile(folder)["gap_start"]!.GetValue<string>());
        Run(folder, Sat.AddMinutes(30), idle: 10);       // 戻った

        var gap = Assert.Single(UsageFile(folder)["gaps"]!.AsArray())!.AsArray();
        Assert.Equal(["2026-12-05T09:35", "2026-12-05T10:29"], gap.Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void BrokenLogLines_DoNotStopAppending()
    {
        using var folder = Folder();
        Directory.CreateDirectory(folder.P("log"));
        File.WriteAllText(folder.P(Path.Combine("log", "pc-2026-12.jsonl")), "こわれた行\n{\"t\": 5}\n");

        Run(folder, Sat);

        var lines = File.ReadAllText(folder.P(Path.Combine("log", "pc-2026-12.jsonl"))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["こわれた行", "{\"t\": 5}", "{\"t\": \"2026-12-05T10:00:00\", \"idle\": 30, \"game\": 0}"], lines);
    }
}
