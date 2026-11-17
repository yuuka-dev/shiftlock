using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// 画面のボタンの記録と取り消しが，Python 版の部品で組んだ同じ手順（export_native.app_record / records.undo_fuzzy）と
/// 同じ返事・同じ state.json・同じ events.log になること．
/// </summary>
public sealed class RecordsTests
{
    private const string Base =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"lastcall_minutes":40}""";

    private static readonly DateTime Morning = new(2026, 12, 1, 8, 10, 0);

    public static IEnumerable<object[]> StepCases => Vectors.Cases("records.json", "steps");

    private sealed record Env(TempFolder Folder, ShiftConfig Cfg, Store Store, Messages M) : IDisposable
    {
        public void Dispose() => Folder.Dispose();

        public string[] Events() => File.Exists(Folder.P("events.log"))
            ? File.ReadAllText(Folder.P("events.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

        public string[] FileNames() => [.. Directory.GetFiles(Folder.Dir).Select(p => Path.GetFileName(p)!).Order()];
    }

    private static Env Open(JsonObject files)
    {
        var folder = Vectors.Materialize(files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        return new Env(folder, cfg, new Store(folder.Dir), Messages.Load(cfg.Lang));
    }

    private static Env OpenBase(string? state = null)
    {
        var files = new JsonObject { ["shifts.json"] = JsonNode.Parse(Base) };
        if (state is not null)
        {
            files["state.json"] = state;   // 文字列はそのままの中身として置かれる
        }
        return Open(files);
    }

    private static RecordResult Run(Env env, string op, DateTime now) => op switch
    {
        "neru" => Records.Neru(env.Cfg, env.Store, env.M, now),
        "okita" => Records.Okita(env.Cfg, env.Store, env.M, now),
        "shuppatsu" => Records.Shuppatsu(env.Cfg, env.Store, env.M, now),
        "kitaku" => Records.Kitaku(env.Cfg, env.Store, env.M, now),
        "undo" => Records.Undo(env.Store, env.M, now),
        _ => throw new ArgumentException(op),
    };

    [Theory]
    [MemberData(nameof(StepCases))]
    public void Steps_MatchPython(VectorCase c)
    {
        using var env = Open(c.Files);
        var steps = c.Input["steps"]!.AsArray();
        var results = c.Expect["results"]!.AsArray();
        for (var i = 0; i < steps.Count; i++)
        {
            var op = steps[i]!["op"]!.GetValue<string>();
            var r = Run(env, op, Vectors.Time(steps[i]!["now"]));
            Assert.Equal(results[i]!["message"]!.GetValue<string>(), r.Message);
            Assert.Equal(results[i]!["recorded"]!.GetValue<bool>(), r.Recorded);
            // 取り消しの目印は「記録したボタン」にだけ付く
            Assert.Equal(r.Recorded && op != "undo", r.Undo is not null);
        }

        var statePath = env.Folder.P("state.json");
        if (c.Expect["state"] is { } expectedState)
        {
            var actual = JsonNode.Parse(File.ReadAllBytes(statePath));
            Assert.True(JsonNode.DeepEquals(expectedState, actual), $"state.json が違う:\n{actual}");
        }
        else
        {
            Assert.False(File.Exists(statePath));
        }
        Assert.Equal(c.Expect["events"]!.AsArray().Select(e => e!.GetValue<string>()), env.Events());
        Assert.DoesNotContain(env.FileNames(), n => n.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void CanUndo_OnlyForOwnLatestRecord()
    {
        using var env = OpenBase();
        var r = Records.Neru(env.Cfg, env.Store, env.M, Morning);
        var token = Assert.IsType<UndoToken>(r.Undo);
        Assert.Equal(new UndoToken("2026-12-01T08:10", "neru"), token);
        Assert.True(Records.CanUndo(env.Store, token, Morning.AddMinutes(60)));
        Assert.False(Records.CanUndo(env.Store, token, Morning.AddMinutes(61)));

        // ほかの書き手（Python 版）が undo を書き換えたら，アプリからは戻さない
        var path = env.Folder.P("state.json");
        var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        state["undo"]!["what"] = new JsonArray("kitaku");
        File.WriteAllText(path, state.ToJsonString());
        Assert.False(Records.CanUndo(env.Store, token, Morning.AddMinutes(1)));

        // 例外日を足した事後報告の undo はアプリからは扱わない
        state["undo"]!["what"] = new JsonArray("neru");
        state["undo"]!["days_work_added"] = true;
        File.WriteAllText(path, state.ToJsonString());
        Assert.False(Records.CanUndo(env.Store, token, Morning.AddMinutes(1)));
        var undo = Records.Undo(env.Store, env.M, Morning.AddMinutes(1));
        Assert.False(undo.Recorded);
        Assert.Single(env.Events());

        // undo が無くなれば戻せない
        state.Remove("undo");
        File.WriteAllText(path, state.ToJsonString());
        Assert.False(Records.CanUndo(env.Store, token, Morning.AddMinutes(1)));
    }

    [Fact]
    public void CanUndo_NewerRecordReplacesTheToken()
    {
        using var env = OpenBase();
        var first = Records.Neru(env.Cfg, env.Store, env.M, Morning).Undo!;
        var second = Records.Kitaku(env.Cfg, env.Store, env.M, Morning.AddMinutes(5)).Undo!;
        Assert.False(Records.CanUndo(env.Store, first, Morning.AddMinutes(6)));
        Assert.True(Records.CanUndo(env.Store, second, Morning.AddMinutes(6)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"shifts":[]}""")]
    public void BrokenStateFile_RefusesAndWritesNothing(string broken)
    {
        // 読めなかったものを空として書き戻すと記録が消える．記録を断り，何も書かない
        using var env = OpenBase(broken);
        foreach (var op in new[] { "neru", "okita", "kitaku" })
        {
            var r = Run(env, op, Morning);
            Assert.Equal(env.M["APP_STATE_BUSY"], r.Message);
            Assert.False(r.Recorded);
            Assert.Null(r.Undo);
        }
        Assert.False(Run(env, "undo", Morning).Recorded);
        var late = Run(env, "shuppatsu", Morning.AddHours(7));
        Assert.Equal(env.M["APP_STATE_BUSY"], late.Message);
        Assert.Equal(broken, File.ReadAllText(env.Folder.P("state.json")));
        Assert.Equal(["shifts.json", "state.json"], env.FileNames());
    }

    [Theory]
    [InlineData(FileShare.None)]   // 読めない
    [InlineData(FileShare.Read)]   // 読めるが置き換えられない
    public void LockedStateFile_RefusesAndLeavesNoTmp(FileShare share)
    {
        const string before = """{"shifts":{"2026-12-01":{"kitaku":"2026-12-01T07:00"}}}""";
        using var env = OpenBase(before);
        using (new FileStream(env.Folder.P("state.json"), FileMode.Open, FileAccess.Read, share))
        {
            var r = Records.Neru(env.Cfg, env.Store, env.M, Morning);
            Assert.Equal(env.M["APP_STATE_BUSY"], r.Message);
            Assert.False(r.Recorded);
        }
        Assert.Equal(before, File.ReadAllText(env.Folder.P("state.json")));
        Assert.Equal(["shifts.json", "state.json"], env.FileNames());

        // 掴んでいたプロセスが離せば，もう一度押して記録できる
        Assert.True(Records.Neru(env.Cfg, env.Store, env.M, Morning).Recorded);
    }

    [Fact]
    public void Message_HasNoPrefix()
    {
        using var env = OpenBase();
        foreach (var op in new[] { "neru", "okita", "kitaku", "shuppatsu", "undo" })
        {
            Assert.DoesNotContain("[shiftlock]", Run(env, op, Morning).Message);
        }
    }

    [Fact]
    public void Seconds_AreDroppedInFiles()
    {
        using var env = OpenBase();
        var now = Morning.AddSeconds(45);
        Records.Neru(env.Cfg, env.Store, env.M, now);
        var state = JsonNode.Parse(File.ReadAllBytes(env.Folder.P("state.json")))!;
        Assert.Equal("2026-12-01T08:10", state["shifts"]!["2026-12-01"]!["sleep_at"]!.GetValue<string>());
        Assert.Equal(["2026-12-01T08:10\t2026-12-01\tsleep_at\tapp 2026-12-01T08:10"], env.Events());
    }

    [Fact]
    public void StateFile_IsWrittenInPythonFormat()
    {
        // Python 版の _write と同じ形（インデント 1・LF・日本語はそのまま・末尾に改行なし）
        using var env = OpenBase("""{"メモ":"あ"}""");
        Records.Kitaku(env.Cfg, env.Store, env.M, Morning);
        var expected = "{\n \"メモ\": \"あ\",\n \"undo\": {\n  \"day\": \"2026-12-01\",\n  \"prev\": {},\n  \"at\": \"2026-12-01T08:10\",\n"
            + "  \"what\": [\n   \"kitaku\"\n  ]\n },\n \"shifts\": {\n  \"2026-12-01\": {\n   \"kitaku\": \"2026-12-01T08:10\"\n  }\n }\n}";
        Assert.Equal(expected, File.ReadAllText(env.Folder.P("state.json")));
    }

    [Fact]
    public void Kansai_MessagesFollowTheConfigLang()
    {
        using var env = Open(new JsonObject { ["shifts.json"] = JsonNode.Parse(Base.Replace("\"ja\"", "\"ja_kansai\"")) });
        var r = Records.Kitaku(env.Cfg, env.Store, env.M, Morning);
        Assert.Equal(Messages.Load("ja_kansai").Format("RECORDS_KITAKU", ("when", "08:10")).Replace("[shiftlock] ", ""), r.Message);
        Assert.NotEqual(Messages.Load("ja").Format("RECORDS_KITAKU", ("when", "08:10")).Replace("[shiftlock] ", ""), r.Message);
    }
}
