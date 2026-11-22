using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>ログ・印・履歴の読み書きが pc/shiftlock/store.py と同じ形になること．</summary>
public sealed class StoreLogTests
{
    private static DateTime T(string s) => TimeUtil.ParseIso(s)!.Value;

    private static string[] Lines(string path) =>
        File.Exists(path) ? File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    [Fact]
    public void LogAppend_WritesTheLinePythonWrites()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        store.LogAppend("pc", T("2026-12-01T09:00:05"), new JsonObject { ["idle"] = 30, ["game"] = 0 });
        store.LogAppend("pc", T("2026-12-01T09:05:00"), new JsonObject { ["note"] = "あ\"b\\c\n" });
        // json.dumps の既定の区切り（", " と ": "）・日本語はそのまま・LF
        Assert.Equal(
            "{\"t\": \"2026-12-01T09:00:05\", \"idle\": 30, \"game\": 0}\n"
            + "{\"t\": \"2026-12-01T09:05:00\", \"note\": \"あ\\\"b\\\\c\\n\"}\n",
            File.ReadAllText(folder.P(Path.Combine("log", "pc-2026-12.jsonl"))));
    }

    [Fact]
    public void LogRead_CrossesMonths_AndRespectsRangeEnds()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        // 書く順は時刻順でなくてよい（読むときに並べ直す）
        store.LogAppend("pc", T("2027-01-01T00:00:00"), new JsonObject { ["idle"] = 3 });
        store.LogAppend("pc", T("2026-12-31T23:50:00"), new JsonObject { ["idle"] = 1 });
        store.LogAppend("pc", T("2026-12-31T23:55:00"), new JsonObject { ["idle"] = 2 });
        store.LogAppend("pc", T("2027-01-01T00:05:00"), new JsonObject { ["idle"] = 4 });
        store.LogAppend("claude", T("2026-12-31T23:56:00"), new JsonObject { ["k"] = "p" });

        var got = store.LogRead("pc", T("2026-12-31T23:55:00"), T("2027-01-01T00:05:00"));

        Assert.Equal([T("2026-12-31T23:55:00"), T("2027-01-01T00:00:00")], got.Select(r => r.T));   // start は含み end は含まない
        Assert.Equal([2, 3], got.Select(r => r.Rec["idle"]!.GetValue<int>()));
        Assert.Empty(store.LogRead("pc", T("2026-11-01T00:00:00"), T("2026-11-30T00:00:00")));
        Assert.Empty(new Store(folder.P("none")).LogRead("pc", T("2026-12-01T00:00:00"), T("2026-12-02T00:00:00")));
    }

    [Fact]
    public void LogRead_SkipsBrokenLines()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.P("log"));
        File.WriteAllText(folder.P(Path.Combine("log", "pc-2026-12.jsonl")),
            "こわれた\n[1]\n{\"t\": 5}\n{\"idle\": 1}\n{\"t\": \"きのう\"}\n\n{\"t\": \"2026-12-01T09:00:00\", \"idle\": 7}\n{\"t\": \"2026-12-01T09:05:00\"");
        var store = new Store(folder.Dir);

        var got = store.LogRead("pc", T("2026-12-01T00:00:00"), T("2026-12-02T00:00:00"));

        Assert.Equal(7, Assert.Single(got).Rec["idle"]!.GetValue<int>());
        Assert.Equal(new DateOnly(2026, 12, 1), store.FirstLogDate());
        Assert.Equal(T("2026-12-01T09:00:00"), store.LastLogTime("pc"));   // 最後の行が壊れていたらその前の行
    }

    [Fact]
    public void LastLogTime_And_FirstLogDate_PickAcrossFiles()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        Assert.Null(store.LastLogTime("pc"));
        Assert.Null(store.FirstLogDate());

        store.LogAppend("pc", T("2026-12-30T10:00:00"), new JsonObject());
        store.LogAppend("pc", T("2026-12-31T10:00:00"), new JsonObject());
        store.LogAppend("pc", T("2027-01-02T08:00:00"), new JsonObject());
        store.LogAppend("claude", T("2027-02-01T08:00:00"), new JsonObject());
        File.WriteAllText(folder.P(Path.Combine("log", "pc-2027-02.jsonl")), "x\n");   // 読める行が無い月は飛ばす

        Assert.Equal(T("2027-01-02T08:00:00"), store.LastLogTime("pc"));
        Assert.Equal(T("2027-02-01T08:00:00"), store.LastLogTime("claude"));
        Assert.Equal(new DateOnly(2026, 12, 30), store.FirstLogDate());
    }

    [Fact]
    public void Departures_ReadsTheThreeShapes()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.P("events.log"), string.Join("\n",
            "2026-12-01T16:05\t2026-12-01\tworked\tshuppatsu",
            "2026-12-01T16:10\t2026-12-01\tworked\tlate claude",          // 事後報告は出発に数えない
            "2026-12-02T07:30\t2026-12-02\tkitaku\tphone 2026-12-02T07:05",   // メモの末尾の時刻
            "2026-12-03T07:40\t2026-12-03\tkitaku",                       // メモの無い古い行は行の時刻
            "2026-12-05T18:02\t-\tkitaku\tfree-day",                      // 乗務の無い日
            "2026-12-06T08:00\t2026-12-07\tkitaku\tclear",                // 消した印は除く
            "こわれた行",
            ""));
        var (shuppatsu, kitaku) = new Store(folder.Dir).Departures();

        Assert.Equal([("2026-12-01", "2026-12-01T16:05")], shuppatsu);
        Assert.Equal(["2026-12-02T07:05", "2026-12-03T07:40", "2026-12-05T18:02"], kitaku.Order());

        var none = new Store(folder.P("none")).Departures();
        Assert.Empty(none.Shuppatsu);
        Assert.Empty(none.Kitaku);
    }

    [Fact]
    public void SetMark_WritesStateAndEventMemo()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        var now = T("2026-12-01T08:30");

        store.SetMark("2026-12-01", "sleep_at", T("2026-12-01T08:10"), now, "phone");
        store.SetMark("2026-12-01", "kitaku", T("2026-12-01T07:05"), now);
        Assert.Equal("2026-12-01T08:10", store.ShiftStatus()["2026-12-01"]!["sleep_at"]!.GetValue<string>());

        store.SetMark("2026-12-01", "sleep_at", null, now);
        store.SetMark("2026-12-01", "kitaku", null, now, "undo");
        Assert.Empty(store.ShiftStatus()["2026-12-01"]!.AsObject());   // Python 版と同じく空の辞書が残る

        Assert.Equal(
        [
            "2026-12-01T08:30\t2026-12-01\tsleep_at\tphone 2026-12-01T08:10",
            "2026-12-01T08:30\t2026-12-01\tkitaku\t2026-12-01T07:05",
            "2026-12-01T08:30\t2026-12-01\tsleep_at\tclear",
            "2026-12-01T08:30\t2026-12-01\tkitaku\tundo",
        ], Lines(folder.P("events.log")));
    }

    [Fact]
    public void SetShiftStatus_And_SetPaid_FollowPython()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        var now = T("2026-12-01T09:00");
        const string day = "2026-12-01";

        store.SetShiftStatus(day, "yasumi", now, "claude");
        store.SetPaid(day, true, now, "claude");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"status":"yasumi","at":"2026-12-01T09:00","paid":true}"""),
            store.ShiftStatus()[day]));

        store.SetShiftStatus(day, "worked", now, "late");   // 休み以外にしたら有給の印は外れる
        Assert.False(store.ShiftStatus()[day]!.AsObject().ContainsKey("paid"));

        store.SetPaid(day, true, now);
        store.SetPaid(day, false, now);
        store.SetShiftStatus(day, null, now);               // 空になった乗務日は消える
        Assert.False(store.ShiftStatus().ContainsKey(day));

        Assert.Equal(
        [
            "2026-12-01T09:00\t2026-12-01\tyasumi\tclaude",
            "2026-12-01T09:00\t2026-12-01\tpaid\ton claude",
            "2026-12-01T09:00\t2026-12-01\tworked\tlate",
            "2026-12-01T09:00\t2026-12-01\tpaid\ton",
            "2026-12-01T09:00\t2026-12-01\tpaid\toff",
            "2026-12-01T09:00\t2026-12-01\tundo\t",
        ], File.ReadAllText(folder.P("events.log")).Split('\n')[..^1]);
    }

    [Fact]
    public void SetShiftStatus_KeepsOtherKeys()
    {
        using var folder = Vectors.Materialize(new JsonObject
        {
            ["state.json"] = JsonNode.Parse("""{"confirm_since":"2026-12-01","shifts":{"2026-12-01":{"sleep_at":"2026-12-01T08:10"}}}"""),
        });
        var store = new Store(folder.Dir);
        store.SetShiftStatus("2026-12-01", "okita", T("2026-12-01T14:00"));
        store.SetShiftStatus("2026-12-01", null, T("2026-12-01T14:01"));

        var state = store.ReadJson("state.json");
        Assert.Equal("2026-12-01", state["confirm_since"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"sleep_at":"2026-12-01T08:10"}"""), state["shifts"]!["2026-12-01"]));
    }

    [Fact]
    public void ReadJson_WriteJson_RoundTrip()
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        Assert.Empty(store.ReadJson("notified.json"));          // 無い → 空

        store.WriteJson("notified.json", new JsonObject { ["a"] = "あ" });
        Assert.Equal("{\n \"a\": \"あ\"\n}", File.ReadAllText(folder.P("notified.json")));
        Assert.Equal("あ", store.ReadJson("notified.json")["a"]!.GetValue<string>());
        Assert.False(File.Exists(folder.P("notified.json.tmp")));

        File.WriteAllText(folder.P("notified.json"), "{");      // 壊れている → 空
        Assert.Empty(store.ReadJson("notified.json"));
        File.WriteAllText(folder.P("notified.json"), "[1]");
        Assert.Empty(store.ReadJson("notified.json"));
    }
}
