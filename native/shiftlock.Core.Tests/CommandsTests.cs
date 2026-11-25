using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// 見るだけのコマンドの文（status・yotei／day list・kakunin）が Python 版と同じになること
/// （docs/test-vectors/commands.json．pc/tools/export_native.py が書き出す）．
/// </summary>
public sealed class CommandsTests
{
    public static IEnumerable<object[]> StatusCases => Vectors.Cases("commands.json", "status_text");

    public static IEnumerable<object[]> ScheduleCases => Vectors.Cases("commands.json", "schedule_text");

    public static IEnumerable<object[]> KakuninCases => Vectors.Cases("commands.json", "kakunin_text");

    public static IEnumerable<object[]> RecordCases =>
        Vectors.Cases("commands.json", "cmd_keyword").Concat(Vectors.Cases("commands.json", "late_report"));

    public static IEnumerable<object[]> DayCases => Vectors.Cases("commands.json", "days_set");

    [Theory]
    [MemberData(nameof(StatusCases))]
    public void StatusText_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var text = Commands.StatusText(cfg, new Store(folder.Dir), cfg.Msg, Vectors.Time(c.Input["now"]));
        Assert.Equal(c.Expect["text"]!.GetValue<string>(), text);
        AssertState(c, folder);
    }

    [Theory]
    [MemberData(nameof(ScheduleCases))]
    public void ScheduleText_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var today = DateOnly.FromDateTime(Vectors.Time(c.Input["now"]));
        var text = Commands.ScheduleText(cfg, cfg.Msg, today, c.Input["n"]!.GetValue<int>());
        Assert.Equal(c.Expect["text"]!.GetValue<string>(), text);
    }

    [Theory]
    [MemberData(nameof(KakuninCases))]
    public void KakuninText_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var text = Confirm.KakuninText(cfg, new Store(folder.Dir), cfg.Msg, Vectors.Time(c.Input["now"]));
        Assert.Equal(c.Expect["text"]!.GetValue<string>(), text);
        AssertState(c, folder);
    }

    [Theory]
    [MemberData(nameof(RecordCases))]
    public void Record_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var words = c.Input["words"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        var text = Commands.Record(c.Input["cmd"]!.GetValue<string>(), words, cfg, new Store(folder.Dir), cfg.Msg,
            Vectors.Time(c.Input["now"]));
        Assert.Equal(c.Expect["text"]!.GetValue<string>(), text);
        AssertState(c, folder);
        AssertJsonFile(c.Expect["days"], folder, "days.json");
        Assert.Equal(c.Expect["events"]!.AsArray().Select(e => e!.GetValue<string>()), EventLines(folder));
    }

    [Theory]
    [MemberData(nameof(DayCases))]
    public void Day_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var note = c.Input["note"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        var (code, text) = Commands.Day(c.Input["action"]!.GetValue<string>(), c.Input["date"]?.GetValue<string>(), note,
            folder.ShiftsJson, new Store(folder.Dir), cfg.Msg, Vectors.Time(c.Input["now"]));
        Assert.Equal(c.Expect["code"]!.GetValue<int>(), code);
        Assert.Equal(c.Expect["text"]!.GetValue<string>(), text);
        AssertJsonFile(c.Expect["days"], folder, "days.json");
        Assert.False(File.Exists(folder.P("state.json")) || File.Exists(folder.P("events.log")), "例外日のほかに書いている");
    }

    [Fact]
    public void RecordVectors_CoverEveryBranch()
    {
        // 返事の文のキーごとに，その文が出るケースが 1 つはあること（ベクタが痩せたら気づけるように）
        var m = Messages.Load("ja");
        var all = string.Join("\n", RecordCases.Select(c => ((VectorCase)c[0]).Expect["text"]!.GetValue<string>()));
        foreach (var key in new[]
        {
            "RECORDS_NERU", "RECORDS_NERU_FREE", "RECORDS_OKITA", "RECORDS_OKITA_SLEPT", "RECORDS_OKITA_SHORT", "RECORDS_OKITA_NO_GAP",
            "RECORDS_OKITA_KEEP", "RECORDS_OKITA_KEEP_SLEPT", "RECORDS_OKITA_ENFORCE_GATE", "RECORDS_OKITA_FREE",
            "RECORDS_SHUPPATSU", "RECORDS_SHUPPATSU_EARLY", "RECORDS_SHUPPATSU_NO_PLAN",
            "CMD_YASUMI", "CMD_YASUMI_PAID", "CMD_UNDO", "CMD_NO_PLAN", "ABSENCE_OVER",
            "RECORDS_LATE_YASUMI", "RECORDS_LATE_WORKED", "RECORDS_LATE_ALREADY", "RECORDS_LATE_FUTURE", "RECORDS_LATE_TOO_OLD",
            "RECORDS_LATE_NOT_YET", "RECORDS_LATE_OFF_DAY", "RECORDS_PAID_SET", "RECORDS_PAID_UNSET",
        })
        {
            // 文の中の，最初の差し込みより前（無ければ最初の差し込みより後ろ）の部分で探す
            var parts = m[key].Replace(m["PREFIX"], "", StringComparison.Ordinal).Split('{', '}');
            var probe = parts[0].Length > 0 ? parts[0] : parts[^1];
            Assert.True(probe.Length > 0 && all.Contains(probe, StringComparison.Ordinal), key);
        }
        foreach (var what in new[] { "CMD_UNDO_WORKED", "CMD_UNDO_YASUMI" })
        {
            Assert.True(all.Contains(m.Format("CMD_UNDO", ("day", "12/10"), ("what", m[what])), StringComparison.Ordinal), what);
        }
    }

    [Theory]
    [InlineData("有給", true)]
    [InlineData(" 有　休 ", true)]
    [InlineData("有給なし", false)]
    [InlineData("有休無し", false)]
    [InlineData("有給です", null)]
    [InlineData("12/9", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PaidFlag_ReadsOnlyTheFixedWords(string? text, bool? expected) => Assert.Equal(expected, Commands.PaidFlag(text));

    [Theory]
    [InlineData("12/3", "2026-12-03")]    // ちょうど 7 日前は今年
    [InlineData("12/2", "2027-12-02")]    // それより前は来年
    [InlineData("1/4", "2027-01-04")]
    [InlineData("明日", "2026-12-11")]
    [InlineData("2026-12-01", "2026-12-01")]   // 年まで書いた日付は動かさない
    public void ParseDay_Forward_RollsOldDatesToNextYear(string text, string expected) =>
        Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd"),
            LateReport.ParseDay(text, new DateOnly(2026, 12, 10), past: false));

    [Fact]
    public void ParseDay_Forward_Feb29_NeedsLeapYearOnBothSides()
    {
        // 今年に無い日付は読めない．今年にあっても，来年に送った先に無ければ読めない（Python 版は ValueError）
        Assert.Throws<FormatException>(() => LateReport.ParseDay("2/29", new DateOnly(2027, 12, 10), past: false));
        Assert.Throws<FormatException>(() => LateReport.ParseDay("2/29", new DateOnly(2028, 12, 10), past: false));
        Assert.Equal(new DateOnly(2028, 2, 29), LateReport.ParseDay("2/29", new DateOnly(2028, 2, 25), past: false));
    }

    [Fact]
    public void Vectors_CoverEveryStatusLine()
    {
        // 文面のキーごとに，その行が出るケースが 1 つはあること（ベクタが痩せたら気づけるように）
        var m = Messages.Load("ja");
        var all = string.Join("\n", StatusCases.Select(c => ((VectorCase)c[0]).Expect["text"]!.GetValue<string>()));
        foreach (var (key, probe) in new[]
        {
            ("STATUS_NO_SHIFT", m["STATUS_NO_SHIFT"]),
            ("STATUS_ABSENCE_OVER", m["STATUS_ABSENCE_OVER"] + "\n"),
            ("STATUS_CAL_NEVER", m["STATUS_CAL_NEVER"] + "\n"),
            ("STATUS_CAL_ERROR", m["STATUS_CAL_ERROR"].Split('{')[0]),
            ("STATUS_UNCONFIRMED", m["STATUS_UNCONFIRMED"].Split('{')[0]),
            ("STATUS_ENFORCE_RELEASED", m["STATUS_ENFORCE_RELEASED"]),
            ("STATUS_ENFORCE_STOP", m["STATUS_ENFORCE_STOP"]),
            ("DAYS_NOTE", "（通院）"),
            ("DAYS_EXCEPTION_MARK", m["DAYS_OFF"] + m["DAYS_EXCEPTION_MARK"]),
        })
        {
            Assert.True(all.Contains(probe, StringComparison.Ordinal), key);
        }
    }

    [Fact]
    public void StatusText_UnreadableDeclaration_Throws()
    {
        // 宣言の中身が読めないときは，それらしい文を作らずに落とす（Python 版もここで落ちる）
        var files = new JsonObject
        {
            ["shifts.json"] = new JsonObject { ["lang"] = "ja" },
            ["state.json"] = JsonNode.Parse(
                """{"shifts": {"2026-12-10": {"enforce": {"consented_at": "2026-12-10T06:00", "action": "lock", "duration_hours": null}}}}"""),
        };
        using var folder = Vectors.Materialize(files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        Assert.Throws<InvalidDataException>(
            () => Commands.StatusText(cfg, new Store(folder.Dir), cfg.Msg, new DateTime(2026, 12, 10, 12, 0, 0)));
    }

    /// <summary>終わったあとの state.json が Python 版と同じ（書かないケースでは無いまま）．</summary>
    private static void AssertState(VectorCase c, TempFolder folder) => AssertJsonFile(c.Expect["state"], folder, "state.json");

    private static void AssertJsonFile(JsonNode? expected, TempFolder folder, string name)
    {
        var path = folder.P(name);
        if (expected is null)
        {
            Assert.False(File.Exists(path), name + " を書いている");
            return;
        }
        Assert.True(File.Exists(path), name + " が無い");
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllBytes(path))),
            $"{name} が違う: {File.ReadAllText(path)}");
    }

    private static string[] EventLines(TempFolder folder) => File.Exists(folder.P("events.log"))
        ? File.ReadAllText(folder.P("events.log")).Split('\n')[..^1]
        : [];
}
