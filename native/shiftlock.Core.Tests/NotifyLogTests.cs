using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>通知の間引き（notified.json）が pc/shiftlock/notify.py の _once と同じになること．</summary>
public sealed class NotifyLogTests
{
    private static DateTime T(string s) => TimeUtil.ParseIso(s)!.Value;

    private static JsonObject Sent(TempFolder folder) => JsonNode.Parse(File.ReadAllText(folder.P("notified.json")))!.AsObject();

    [Theory]
    [InlineData("lastcall", null)]
    [InlineData("go", null)]
    [InlineData("go_last", null)]
    [InlineData("soft", 30)]
    [InlineData("hard", 10)]
    [InlineData("free", null)]
    public void Every_FollowsPython(string phase, int? minutes)
    {
        Assert.Equal(minutes, NotifyLog.Every(phase));
    }

    [Fact]
    public void Once_WithoutInterval_IsOnlyOnce()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);

        Assert.True(NotifyLog.Once(store, "2026-12-01:lastcall", T("2026-12-01T07:20")));
        Assert.False(NotifyLog.Once(store, "2026-12-01:lastcall", T("2026-12-01T09:00")));
        Assert.Equal("2026-12-01T07:20:00", Sent(folder)["2026-12-01:lastcall"]!.GetValue<string>());   // 断ったときは書き換えない
    }

    [Fact]
    public void Once_WithInterval_RepeatsAfterIt()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);

        Assert.True(NotifyLog.Once(store, "2026-12-01:hard", T("2026-12-01T10:30"), 10));
        Assert.False(NotifyLog.Once(store, "2026-12-01:hard", T("2026-12-01T10:39:59"), 10));
        Assert.True(NotifyLog.Once(store, "2026-12-01:hard", T("2026-12-01T10:40"), 10));
        Assert.Equal("2026-12-01T10:40:00", Sent(folder)["2026-12-01:hard"]!.GetValue<string>());
    }

    [Fact]
    public void Once_DropsKeysOlderThan3Days()
    {
        using var folder = Vectors.Materialize(new JsonObject
        {
            ["notified.json"] = JsonNode.Parse(
                """{"2026-12-01:hard":"2026-12-01T10:30:00","2026-12-02:soft":"2026-12-02T08:00:00","2026-12-04:warn:sound:C:/a.wav":"2026-12-04T08:00:00"}"""),
        });

        Assert.True(NotifyLog.Once(new Store(folder.Dir), "2026-12-05:go", T("2026-12-05T15:45")));

        // 12/05 の 3 日前 = 12/02 より前の日付で始まるキーを捨てる
        Assert.Equal(["2026-12-02:soft", "2026-12-04:warn:sound:C:/a.wav", "2026-12-05:go"], Sent(folder).Select(kv => kv.Key).Order());
    }

    [Theory]
    [InlineData("""{"2026-12-01:hard":"こわれた"}""")]
    [InlineData("""{"2026-12-01:hard":5}""")]
    [InlineData("""{"2026-12-01:hard":["2026-12-01T10:30:00"]}""")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("")]
    public void Once_UnreadableTime_CountsAsNotSent_AndIsRewritten(string notified)
    {
        // Review Focus 2: 読めない時刻があっても止まらず，出して書き直す
        using var folder = Vectors.Materialize(new JsonObject { ["notified.json"] = notified });
        var store = new Store(folder.Dir);

        Assert.True(NotifyLog.Once(store, "2026-12-01:hard", T("2026-12-01T10:40"), 10));
        Assert.Equal("2026-12-01T10:40:00", Sent(folder)["2026-12-01:hard"]!.GetValue<string>());
        Assert.False(NotifyLog.Once(store, "2026-12-01:hard", T("2026-12-01T10:45"), 10));
    }

    [Fact]
    public void Once_UnreadableTime_WithoutInterval_AlsoCountsAsNotSent()
    {
        using var folder = Vectors.Materialize(new JsonObject { ["notified.json"] = """{"2026-12-01:go":"こわれた"}""" });
        var store = new Store(folder.Dir);

        Assert.True(NotifyLog.Once(store, "2026-12-01:go", T("2026-12-01T15:45")));
        Assert.False(NotifyLog.Once(store, "2026-12-01:go", T("2026-12-01T15:50")));
    }

    [Fact]
    public void AttachSounds_NoActions_NoWarnings()
    {
        using var folder = Vectors.Materialize(new JsonObject { ["shifts.json"] = new JsonObject() });
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var actions = new List<TickAction>();

        Assert.Empty(NotifyLog.AttachSounds(cfg, new Store(folder.Dir), cfg.Msg, actions, T("2026-12-01T10:30")));
        Assert.Empty(actions);
        Assert.False(File.Exists(folder.P("notified.json")));
    }

    [Fact]
    public void AttachSounds_WslPath_IsCheckedAsWindowsPath()
    {
        // 設定に /mnt/c/... と書いてあっても，在るファイルなら警告しない（書いてあるとおりのパスを返す）
        using var folder = new TempFolder();
        File.WriteAllBytes(folder.P("hard.wav"), []);
        var root = Path.GetPathRoot(folder.Dir)!;                       // "C:\"
        var wsl = "/mnt/" + char.ToLowerInvariant(root[0]) + "/" + folder.Dir[root.Length..].Replace('\\', '/') + "/hard.wav";
        File.WriteAllText(folder.ShiftsJson, new JsonObject
        {
            ["notify"] = new JsonObject { ["sound"] = new JsonObject { ["hard"] = wsl, ["soft"] = "/mnt/c/none/none.wav" } },
        }.ToJsonString());
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var store = new Store(folder.Dir);

        var hard = new List<TickAction> { new("popup", "hard", "shiftlock", "x", false) };
        Assert.Empty(NotifyLog.AttachSounds(cfg, store, cfg.Msg, hard, T("2026-12-01T10:30")));
        Assert.Equal(wsl, hard[0].Sound);

        var soft = new List<TickAction> { new("toast", "soft", "shiftlock", "x", false) };
        Assert.Single(NotifyLog.AttachSounds(cfg, store, cfg.Msg, soft, T("2026-12-01T08:00")));
        Assert.Equal("", soft[0].Sound);
    }
}
