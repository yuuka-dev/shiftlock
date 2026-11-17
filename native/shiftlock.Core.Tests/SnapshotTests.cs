using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>画面に出す値が，Python 版の部品で組んだ期待値（export_native._expect_snapshot）と同じになること．</summary>
public sealed class SnapshotTests
{
    private const string Base =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"lastcall_minutes":40}""";

    public static IEnumerable<object[]> BuildCases => Vectors.Cases("snapshot.json", "build");

    private static (TempFolder Folder, StatusSnapshot Snapshot, Messages M) Build(
        DateTime now, string shifts = Base, string? state = null, string? days = null)
    {
        var files = new JsonObject { ["shifts.json"] = JsonNode.Parse(shifts) };
        if (state is not null)
        {
            files["state.json"] = JsonNode.Parse(state);
        }
        if (days is not null)
        {
            files["days.json"] = JsonNode.Parse(days);
        }
        var folder = Vectors.Materialize(files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        return (folder, Snapshot.Build(cfg, new Store(folder.Dir), now), Messages.Load(cfg.Lang));
    }

    private static DateTime? TimeOrNull(JsonNode? node) => node is null ? null : Vectors.Time(node);

    [Theory]
    [MemberData(nameof(BuildCases))]
    public void Build_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var now = Vectors.Time(c.Input["now"]);
        var s = Snapshot.Build(cfg, new Store(folder.Dir), now);
        var e = c.Expect;

        Assert.Equal(now, s.Now);
        Assert.Equal(e["phase"]!.GetValue<string>(), s.Phase);
        Assert.Equal(e["countdown_key"]?.GetValue<string>(), s.CountdownKey);
        Assert.Equal(TimeOrNull(e["countdown_to"]), s.CountdownTo);
        Assert.Equal(e["next_day"]?.GetValue<string>(), s.NextPlan?.Day);
        Assert.Equal(e["record_status"]?.GetValue<string>(), s.RecordStatus);
        Assert.Equal(TimeOrNull(e["sleep_at"]), s.SleepAt);
        Assert.Equal(TimeOrNull(e["kitaku"]), s.Kitaku);

        var expected = e["schedule"]!.AsArray()
            .Select(x => new DayCell(Vectors.Day(x!["date"]), x["work"]!.GetValue<bool>(), x["why"]?.GetValue<string>(),
                x["exception"]!.GetValue<bool>()));
        Assert.Equal(expected, s.Schedule);
    }

    [Fact]
    public void Lines_UsesMessages()
    {
        var (folder, s, m) = Build(new DateTime(2026, 12, 1, 7, 30, 0));
        using var _ = folder;
        Assert.Equal(
        [
            "12/01(火) 07:30",
            "就寝準備の時間",
            "就寝目安まで あと 30分",
            "次の乗務  12/01(火)",
            "就寝準備 07:20 → 就寝目安 08:00 → 最低ライン 10:30",
            "出発 16:00（遅くとも 16:30）",
            "出庫 17:00（遅くとも 17:30）",
            "記録: まだなし",
        ], Snapshot.Lines(s, m));
    }

    [Fact]
    public void Lines_Free_HasNoCountdown()
    {
        var (folder, s, m) = Build(new DateTime(2026, 12, 1, 18, 0, 0));
        using var _ = folder;
        var lines = Snapshot.Lines(s, m);
        Assert.Equal(["12/01(火) 18:00", "いまは自由時間", "次の乗務  12/02(水)"], lines.Take(3));
        Assert.Equal(7, lines.Count);
    }

    [Fact]
    public void Lines_NoShift()
    {
        var (folder, s, m) = Build(new DateTime(2026, 12, 1, 9, 0, 0), Base.Replace("""["mon","tue","wed","thu","fri"]""", "[]"));
        using var _ = folder;
        Assert.Equal(["12/01(火) 09:00", "いまは自由時間", "14 日以内に乗務はないよ"], Snapshot.Lines(s, m));
    }

    [Fact]
    public void Lines_RecordCombines()
    {
        var (folder, s, m) = Build(new DateTime(2026, 12, 1, 15, 0, 0),
            state: """{"shifts":{"2026-12-01":{"status":"okita","at":"2026-12-01T14:30","sleep_at":"2026-12-01T08:10","kitaku":"2026-12-01T07:05"}}}""");
        using var _ = folder;
        Assert.Equal("記録: 起きた / 08:10 就寝 / 07:05 帰宅", Snapshot.Lines(s, m)[^1]);
    }

    [Fact]
    public void Lines_Kansai()
    {
        var (folder, s, m) = Build(new DateTime(2026, 12, 1, 7, 30, 0), Base.Replace("\"ja\"", "\"ja_kansai\""));
        using var _ = folder;
        var kansai = Messages.Load("ja_kansai");
        Assert.Equal("ja_kansai", m.Lang);
        Assert.Equal(kansai.Map("APP_PHASE")["lastcall"], Snapshot.Lines(s, m)[1]);
    }

    [Fact]
    public void DayLabel_Variants()
    {
        var (folder, s, m) = Build(new DateTime(2026, 12, 1, 9, 0, 0),
            days: """{"off":{"2026-12-03":"用事"},"work":{"2026-12-05":""}}""");
        using var _ = folder;
        Assert.Equal(14, s.Schedule.Count);
        Assert.Equal("12/01(火) 乗務", Snapshot.DayLabel(s.Schedule[0], m));
        Assert.Equal("12/03(木) 休み（用事）", Snapshot.DayLabel(s.Schedule[2], m));
        Assert.Equal("12/05(土) 乗務*", Snapshot.DayLabel(s.Schedule[4], m));
        Assert.Equal("12/06(日) 休み", Snapshot.DayLabel(s.Schedule[5], m));
    }
}
