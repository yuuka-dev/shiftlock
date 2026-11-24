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
    private static void AssertState(VectorCase c, TempFolder folder)
    {
        var path = folder.P("state.json");
        if (c.Expect["state"] is not { } expected)
        {
            Assert.False(File.Exists(path), "state.json を書いている");
            return;
        }
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllBytes(path))),
            $"state.json が違う: {File.ReadAllText(path)}");
    }
}
