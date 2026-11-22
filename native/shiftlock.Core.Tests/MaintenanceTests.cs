using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// 1 日 1 回の整理のうち，ベクタ（tick.json の maintenance）で見られないところ．
/// 壊れたファイルを空で書き直さないこと，失敗しても見張りが続くこと．
/// </summary>
public sealed class MaintenanceTests
{
    private const string Shifts =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","confirm":{"mode":"off"}}""";

    /// <summary>土曜（通知の出ない日）．</summary>
    private static readonly DateTime Now = new(2027, 12, 4, 10, 0, 0);

    private const string OldAndNewEvents = "2026-12-03T09:00\t2026-12-03\tyasumi\tapp\n2026-12-04T09:00\t2026-12-04\tyasumi\tapp\n";

    private static TempFolder Folder(string shifts = Shifts, params (string Name, string Body)[] files)
    {
        var o = new JsonObject { ["shifts.json"] = JsonNode.Parse(shifts) };
        foreach (var (name, body) in files)
        {
            o[name] = body;
        }
        return Vectors.Materialize(o);
    }

    private static TickResult Tick1(TempFolder folder, DateTime? now = null)
    {
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        return Tick.Run(cfg, new Store(folder.Dir), cfg.Msg, new TickInput(30, false, 7200), now ?? Now,
            _ => throw new IOException("no fetch"));
    }

    private static JsonObject Result(TempFolder folder) =>
        JsonNode.Parse(File.ReadAllText(folder.P("maintenance.json")))!["result"]!.AsObject();

    [Fact]
    public void Cleanup_ReturnsCounts()
    {
        using var folder = Folder(Shifts, ("events.log", OldAndNewEvents));
        var cfg = ShiftConfig.Load(folder.ShiftsJson);

        var removed = Maintenance.Cleanup(cfg, new Store(folder.Dir), Now);

        Assert.Equal("""{"days":0,"state":0,"logs":0,"events":1,"reports":0}""", removed.ToJsonString());
    }

    [Fact]
    public void UnreadableDaysJson_IsNotOverwritten_AndTheRestIsCleaned()
    {
        // Python 版は読めない days.json を空として書き直す（手で入れた例外日が消える）．C# は触らない
        using var folder = Folder(Shifts, ("days.json", """{"off": {"2027-12-06": "公休"},"""), ("events.log", OldAndNewEvents));

        Tick1(folder);

        Assert.Equal("""{"off": {"2027-12-06": "公休"},""", File.ReadAllText(folder.P("days.json")));
        Assert.Equal("""{"days":0,"state":0,"logs":0,"events":1,"reports":0}""", Result(folder).ToJsonString());
    }

    [Fact]
    public void UnreadableStateJson_IsLeftAlone()
    {
        using var folder = Folder(Shifts, ("state.json", "{"), ("events.log", OldAndNewEvents));

        Tick1(folder);

        Assert.Equal("{", File.ReadAllText(folder.P("state.json")));
        Assert.Equal(1, Result(folder)["events"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("days.json", """{"off": ["2027-10-04"]}""")]
    [InlineData("state.json", """{"shifts": ["2027-10-04"]}""")]
    public void WrongShape_FailsThatDay_ButTickGoesOn(string name, string body)
    {
        // 形が違うものを勝手に直さない．その日の整理は失敗として記録し（翌日また試す），見張りは続ける
        using var folder = Folder(Shifts, (name, body), ("events.log", OldAndNewEvents));

        var result = Tick1(folder);

        Assert.Empty(result.Warnings);
        Assert.Equal(body, File.ReadAllText(folder.P(name)));
        Assert.True(Result(folder).ContainsKey("error"));
        var m = JsonNode.Parse(File.ReadAllText(folder.P("maintenance.json")))!;
        Assert.Equal("2027-12-04", m["last_cleanup"]!.GetValue<string>());
    }

    [Fact]
    public void BrokenRetention_FailsCleanup_ButTickGoesOn()
    {
        var shifts = Shifts[..^1] + ""","retention":{"days_keep":"長め"}}""";
        using var folder = Folder(shifts, ("events.log", OldAndNewEvents));

        Tick1(folder);

        Assert.True(Result(folder).ContainsKey("error"));
        Assert.Equal(OldAndNewEvents, File.ReadAllText(folder.P("events.log")));
    }

    [Fact]
    public void CalendarSyncFailure_DoesNotStopCleanup()
    {
        var shifts = Shifts[..^1] + ""","calendar":{"ics_url":"https://cal.example/a.ics","sync_minutes":"毎時"}}""";
        using var folder = Folder(shifts, ("events.log", OldAndNewEvents));

        Tick1(folder);

        Assert.Equal(1, Result(folder)["events"]!.GetValue<int>());
        Assert.False(File.Exists(folder.P("calendar.json")));
    }

    [Fact]
    public void EventsLog_WithCrLf_IsReadByLine()
    {
        using var folder = Folder(Shifts, ("events.log", OldAndNewEvents.Replace("\n", "\r\n")));

        Tick1(folder);

        Assert.Equal("2026-12-04T09:00\t2026-12-04\tyasumi\tapp\n", File.ReadAllText(folder.P("events.log")));
    }

    [Fact]
    public void UnreadableMaintenanceJson_CleansAgain()
    {
        using var folder = Folder(Shifts, ("maintenance.json", "{"), ("events.log", OldAndNewEvents));

        Tick1(folder);

        Assert.Equal(1, Result(folder)["events"]!.GetValue<int>());
    }

    [Fact]
    public void LogFileNames_ThatAreNotMonths_AreKept()
    {
        using var folder = Folder(Shifts, ("log/pc-2027-00.jsonl", "x\n"), ("log/pc-2027-13.jsonl", "x\n"), ("log/pc-0000-01.jsonl", "x\n"),
            ("log/pc-2027-05.jsonl.bak", "x\n"), ("log/pc-2027-05.jsonl", "x\n"));

        Tick1(folder);

        Assert.Equal(["pc-0000-01.jsonl", "pc-2027-00.jsonl", "pc-2027-05.jsonl.bak", "pc-2027-12.jsonl", "pc-2027-13.jsonl"],
            Directory.GetFiles(folder.P("log")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}
