using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>limit.json・limit-games.json・claude-busy.json の読み書き．</summary>
public sealed class LimitFilesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 12, 7, 23, 0, 0);

    private readonly TempFolder _folder = new();
    private readonly Store _store;

    public LimitFilesTests() => _store = new Store(_folder.Dir);

    public void Dispose() => _folder.Dispose();

    private void Put(string name, string text) => File.WriteAllText(_folder.P(name), text);

    // ---- limit.json ----

    [Fact]
    public void State_RoundTrips()
    {
        var start = new DateTime(2026, 12, 7, 20, 15, 3).AddTicks(1234567);
        var state = new LimitState(
            "2026-12-07",
            new Dictionary<string, double> { ["eldenring.exe"] = 42.5 },
            new Dictionary<string, CappedApp> { ["eldenring.exe"] = new("job-1", 31.9) },
            [new SuspendedProc(1234, start, "eldenring.exe")],
            new HashSet<string> { "photoshop.exe" },
            new HashSet<string> { "1", "2" },
            new Dictionary<string, DateTime> { ["phase-1"] = new DateTime(2026, 12, 7, 22, 40, 5) });

        LimitFiles.WriteState(_store, state);
        var got = LimitFiles.ReadState(_store);

        Assert.Equal("2026-12-07", got.Day);
        Assert.Equal(state.Baseline, got.Baseline);
        Assert.Equal(state.Capped, got.Capped);
        Assert.Equal(state.Suspended, got.Suspended);
        Assert.Equal(state.Skipped.Order(), got.Skipped.Order());
        Assert.Equal(state.Announced.Order(), got.Announced.Order());
        Assert.Equal(state.Pending, got.Pending);
        Assert.True(got.HasEffects);
        Assert.False(File.Exists(_folder.P("limit.json.tmp")));
    }

    [Fact]
    public void State_Missing_IsEmpty()
    {
        var got = LimitFiles.ReadState(_store);
        Assert.Null(got.Day);
        Assert.Empty(got.Capped);
        Assert.Empty(got.Suspended);
        Assert.False(got.HasEffects);
    }

    [Fact]
    public void State_EmptyWritesEmptyObject()
    {
        LimitFiles.WriteState(_store, LimitState.Empty);
        Assert.Equal("{}", File.ReadAllText(_folder.P("limit.json")).Trim());
        Assert.False(LimitFiles.ReadState(_store).HasEffects);
    }

    [Fact]
    public void State_BaselineOnly_HasNoEffects()
    {
        var s = LimitState.Empty with { Day = "2026-12-07", Baseline = new Dictionary<string, double> { ["apex.exe"] = 10 } };
        Assert.False(s.HasEffects);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("""{"suspended":[{"pid":"1","start":"2026-12-07T20:00:00","exe":"eldenring.exe"}]}""")]
    [InlineData("""{"suspended":[{"pid":1,"start":"x","exe":"eldenring.exe"}]}""")]
    [InlineData("""{"suspended":{"pid":1}}""")]
    [InlineData("""{"capped":{"eldenring.exe":{"job":"j","percent":"5"}}}""")]
    [InlineData("""{"baseline":{"eldenring.exe":"x"}}""")]
    [InlineData("""{"skipped":[1]}""")]
    [InlineData("""{"announced":"1"}""")]
    [InlineData("""{"pending":{"a":5}}""")]
    [InlineData("""{"day":5}""")]
    public void State_Broken_Throws(string text)
    {
        Put("limit.json", text);
        Assert.Throws<InvalidDataException>(() => LimitFiles.ReadState(_store));
    }

    [Fact]
    public void Quarantine_RenamesToBad()
    {
        Put("limit.json", "{");
        Put("limit.json.bad", "old");

        LimitFiles.QuarantineState(_store);

        Assert.False(File.Exists(_folder.P("limit.json")));
        Assert.Equal("{", File.ReadAllText(_folder.P("limit.json.bad")));
    }

    [Fact]
    public void Quarantine_NoFile_DoesNothing()
    {
        LimitFiles.QuarantineState(_store);
        Assert.False(File.Exists(_folder.P("limit.json.bad")));
    }

    // ---- limit-games.json ----

    [Fact]
    public void ReadGames_DropsOldAndBadEntries()
    {
        Put("limit-games.json", """
            {
             "eldenring.exe": {"verdict": "target", "seen": "2026-12-07T20:00:00"},
             "apex.exe": {"verdict": "anticheat", "seen": "2026-11-07T23:00:00"},
             "old.exe": {"verdict": "target", "seen": "2026-11-07T22:59:59"},
             "bad1.exe": {"verdict": "ok", "seen": "2026-12-07T20:00:00"},
             "bad2.exe": {"verdict": "target"},
             "bad3.exe": 5
            }
            """);

        var got = LimitFiles.ReadGames(_store, Now);

        Assert.Equal(new Dictionary<string, string> { ["eldenring.exe"] = "target", ["apex.exe"] = "anticheat" }, got);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    public void ReadGames_MissingOrBroken_IsEmpty(string? text)
    {
        if (text is not null)
        {
            Put("limit-games.json", text);
        }
        Assert.Empty(LimitFiles.ReadGames(_store, Now));
    }

    [Fact]
    public void UpdateGames_WritesAndReadsBack()
    {
        LimitFiles.UpdateGames(_store, new Dictionary<string, string> { ["EldenRing"] = "target", ["apex.exe"] = "unknown" }, Now);

        Assert.Equal(
            new Dictionary<string, string> { ["eldenring.exe"] = "target", ["apex.exe"] = "unknown" },
            LimitFiles.ReadGames(_store, Now));
    }

    [Fact]
    public void UpdateGames_SameContent_DoesNotWrite()
    {
        var seen = new Dictionary<string, string> { ["eldenring.exe"] = "target" };
        LimitFiles.UpdateGames(_store, seen, Now);
        var path = _folder.P("limit-games.json");
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, past);

        LimitFiles.UpdateGames(_store, seen, Now.AddMinutes(10));

        Assert.Equal(past, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void UpdateGames_VerdictChanged_Writes()
    {
        LimitFiles.UpdateGames(_store, new Dictionary<string, string> { ["apex.exe"] = "unknown" }, Now);
        LimitFiles.UpdateGames(_store, new Dictionary<string, string> { ["apex.exe"] = "anticheat" }, Now.AddMinutes(1));

        Assert.Equal("anticheat", LimitFiles.ReadGames(_store, Now.AddMinutes(1))["apex.exe"]);
    }

    [Fact]
    public void UpdateGames_RefreshesSeenOncePerDay()
    {
        var seen = new Dictionary<string, string> { ["eldenring.exe"] = "target" };
        LimitFiles.UpdateGames(_store, seen, Now);

        LimitFiles.UpdateGames(_store, seen, Now.AddDays(2));

        var raw = JsonNode.Parse(File.ReadAllText(_folder.P("limit-games.json")))!.AsObject();
        Assert.Equal("2026-12-09T23:00:00", raw["eldenring.exe"]!["seen"]!.GetValue<string>());
    }

    [Fact]
    public void UpdateGames_DropsEntriesNotSeenFor30Days_KeepsOthers()
    {
        Put("limit-games.json", """
            {
             "old.exe": {"verdict": "target", "seen": "2026-11-01T00:00:00"},
             "apex.exe": {"verdict": "anticheat", "seen": "2026-12-01T00:00:00"}
            }
            """);

        LimitFiles.UpdateGames(_store, new Dictionary<string, string>(), Now);

        var got = LimitFiles.ReadGames(_store, Now);
        Assert.Equal(new Dictionary<string, string> { ["apex.exe"] = "anticheat" }, got);
        Assert.DoesNotContain("old.exe", File.ReadAllText(_folder.P("limit-games.json")));
    }

    [Fact]
    public void UpdateGames_IgnoresUnknownVerdictAndBadName()
    {
        LimitFiles.UpdateGames(_store, new Dictionary<string, string> { ["a.exe"] = "weird", [@"C:\x.exe"] = "target" }, Now);
        Assert.Empty(LimitFiles.ReadGames(_store, Now));
    }

    // ---- claude-busy.json ----

    private void Busy(DateTime t) => Put("claude-busy.json", "{\"s1\": \"" + t.ToString("yyyy-MM-ddTHH:mm:ss") + "\"}");

    [Fact]
    public void ClaudeBusy_Within30Minutes_True()
    {
        Busy(Now.AddMinutes(-29));
        Assert.True(LimitFiles.ClaudeBusy(_store, Now));
    }

    [Fact]
    public void ClaudeBusy_Over30Minutes_False()
    {
        Busy(Now.AddMinutes(-31));
        Assert.False(LimitFiles.ClaudeBusy(_store, Now));
    }

    [Fact]
    public void ClaudeBusy_FutureMark_False()
    {
        Busy(Now.AddMinutes(5));
        Assert.False(LimitFiles.ClaudeBusy(_store, Now));
    }

    [Fact]
    public void ClaudeBusy_OneFreshAmongStale_True()
    {
        Put("claude-busy.json", """{"old": "2026-12-06T10:00:00", "bad": "x", "n": 5, "new": "2026-12-07T22:50:00"}""");
        Assert.True(LimitFiles.ClaudeBusy(_store, Now));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("""{"s1": 5}""")]
    public void ClaudeBusy_BrokenFile_False(string text)
    {
        Put("claude-busy.json", text);
        Assert.False(LimitFiles.ClaudeBusy(_store, Now));
    }

    [Fact]
    public void ClaudeBusy_NoFile_False() => Assert.False(LimitFiles.ClaudeBusy(_store, Now));
}
