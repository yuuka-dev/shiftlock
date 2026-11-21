using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// ほかのプロセスが掴んでいて読めないファイルを「空」として扱わないこと（空として書き戻すと，稼働の記録や通知の履歴が消える）．
/// 壊れた中身（JSON でない）は今までどおり空として進む．
/// </summary>
public sealed class BusyFileTests
{
    private const string Shifts =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","confirm":{"mode":"off"}}""";

    private static readonly DateTime Sat = new(2026, 12, 5, 10, 0, 0);

    private static TempFolder Folder(params (string Name, string Body)[] files)
    {
        var o = new JsonObject { ["shifts.json"] = JsonNode.Parse(Shifts) };
        foreach (var (name, body) in files)
        {
            o[name] = body;
        }
        return Vectors.Materialize(o);
    }

    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void ReadJson_HeldFile_ThrowsBusy()
    {
        using var folder = Folder(("notified.json", """{"2026-12-05:soft": "2026-12-05T08:00:00"}"""));
        var store = new Store(folder.Dir);

        using (Hold(folder.P("notified.json")))
        {
            Assert.Throws<StateBusyException>(() => store.ReadJson("notified.json"));
        }
        Assert.Single(store.ReadJson("notified.json"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("[]")]
    public void ReadJson_BrokenOrMissing_IsEmpty(string body)
    {
        using var folder = Folder(("notified.json", body));
        var store = new Store(folder.Dir);

        Assert.Empty(store.ReadJson("notified.json"));
        Assert.Empty(store.ReadJson("無い.json"));
    }

    [Fact]
    public void Tick_UsageHeld_FailsThatRun_AndKeepsTheRecord()
    {
        const string usage = """{"gaps": [["2026-12-05T01:00", "2026-12-05T08:00"]], "start": "2026-12-05T08:00:00", "last": "2026-12-05T09:55:00", "last_nag": "2026-12-05T09:30:00"}""";
        using var folder = Folder(("usage-pc.json", usage));
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        TickResult Run(DateTime now) => Tick.Run(cfg, new Store(folder.Dir), cfg.Msg, new TickInput(30, false, 7200), now,
            _ => throw new IOException("no fetch"));

        using (Hold(folder.P("usage-pc.json")))
        {
            Assert.Throws<StateBusyException>(() => Run(Sat));
        }
        Assert.Equal(usage, File.ReadAllText(folder.P("usage-pc.json")));

        Run(Sat.AddMinutes(5));   // 離したら普通に動き，前の記録の続きになる
        var u = JsonNode.Parse(File.ReadAllText(folder.P("usage-pc.json")))!;
        Assert.Equal("2026-12-05T08:00:00", u["start"]!.GetValue<string>());
        Assert.Single(u["gaps"]!.AsArray());
    }

    [Fact]
    public void NotifyOnce_HistoryHeld_Throws_InsteadOfNotifyingAgain()
    {
        using var folder = Folder(("notified.json", """{"2026-12-05:lastcall": "2026-12-05T07:20:00"}"""));
        var store = new Store(folder.Dir);

        using (Hold(folder.P("notified.json")))
        {
            Assert.Throws<StateBusyException>(() => NotifyLog.Once(store, "2026-12-05:lastcall", Sat));
        }
        Assert.False(NotifyLog.Once(store, "2026-12-05:lastcall", Sat));   // 出した記録は残っている
    }
}
