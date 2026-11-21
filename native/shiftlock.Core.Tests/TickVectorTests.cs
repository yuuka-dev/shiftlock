using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// 見張り（Tick.Run）が Python 版の run_tick と同じ結果・同じファイルになること．
/// docs/test-vectors/tick.json の節ごとに，置いたファイルと見張りの列を通して比べる．
/// </summary>
public sealed class TickVectorTests
{
    public static IEnumerable<object[]> Cases(string section) => Vectors.Cases("tick.json", section);

    [Theory]
    [MemberData(nameof(Cases), "usage")]
    public void Usage(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "notify")]
    public void Notify(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "sound")]
    public void Sound(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "autowake")]
    public void AutoWake(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "phone")]
    public void Phone(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "confirm")]
    public void Confirm(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "enforce")]
    public void Enforce(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "calendar")]
    public void Calendar(VectorCase c) => TickRunner.RunCase(c);

    [Theory]
    [MemberData(nameof(Cases), "maintenance")]
    public void Maintenance(VectorCase c) => TickRunner.RunCase(c);

    /// <summary>緊急解除（tick を通らない）．返事・state.json・events.log・STOP.txt を比べる．</summary>
    [Theory]
    [MemberData(nameof(Cases), "release")]
    public void Release(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);

        var message = Shiftlock.Core.Enforce.Release(new Store(folder.Dir), cfg.Msg, Vectors.Time(c.Input["now"]),
            c.Input["reason"]!.GetValue<string>());

        Assert.Equal(c.Expect["message"]!.GetValue<string>(), message);
        TickRunner.AssertSame(c.Expect["state"], TickRunner.Snapshot(folder, "state.json"), "state.json");
        TickRunner.AssertSame(c.Expect["events"], TickRunner.Snapshot(folder, "events.log") ?? new JsonArray(), "events.log");
        TickRunner.AssertSame(c.Expect["stop"], TickRunner.Snapshot(folder, "STOP.txt"), "STOP.txt");
    }
}

/// <summary>tick.json の 1 ケースを通す共通の手順．</summary>
public static class TickRunner
{
    /// <summary>files を置く → steps を順に Tick.Run → 各回の結果と watch のファイルを expect と比べる．</summary>
    public static void RunCase(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var fetchTable = c.Input["fetch"] as JsonObject;
        string Fetch(string url) =>
            fetchTable is not null && fetchTable[url] is { } body
                ? body.GetValue<string>()
                : throw new IOException("fetch failed");

        var results = new JsonArray();
        foreach (var step in c.Input["steps"]!.AsArray())
        {
            // Python の run_tick は毎回設定を読み直す
            var cfg = ShiftConfig.Load(folder.ShiftsJson);
            var input = new TickInput(
                step!["idle"]!.GetValue<double>(),
                step["game"]!.GetValue<int>() != 0,
                step["uptime"]?.GetValue<double>(),
                Remote: false,
                DryRun: step["dry_run"]?.GetValue<bool>() ?? false);
            var result = Tick.Run(cfg, new Store(folder.Dir), cfg.Msg, input, Vectors.Time(step["now"]), Fetch);
            results.Add(ToJson(result, folder));
        }

        var files = new JsonObject();
        foreach (var name in c.Input["watch"]!.AsArray())
        {
            files[name!.GetValue<string>()] = Snapshot(folder, name.GetValue<string>());
        }
        var actual = new JsonObject { ["results"] = results, ["files"] = files };

        AssertSame(c.Expect["results"], actual["results"], "results");
        foreach (var (name, expected) in c.Expect["files"]!.AsObject())
        {
            AssertSame(expected, files[name], name);
        }
    }

    public static void AssertSame(JsonNode? expected, JsonNode? actual, string what) =>
        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"{what}\nexpected: {expected?.ToJsonString(Relaxed) ?? "null"}\nactual:   {actual?.ToJsonString(Relaxed) ?? "null"}");

    private static readonly System.Text.Json.JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>run_tick の戻り値と同じ形にする．一時フォルダのパスは {DIR} に戻す．</summary>
    public static JsonObject ToJson(TickResult r, TempFolder folder)
    {
        string Scrub(string s) => s.Replace(folder.Dir.Replace('\\', '/'), Vectors.DirMark).Replace(folder.Dir, Vectors.DirMark);

        var actions = new JsonArray();
        foreach (var a in r.Actions)
        {
            var o = new JsonObject
            {
                ["method"] = a.Method,
                ["kind"] = a.Kind,
                ["title"] = a.Title,
                ["text"] = Scrub(a.Text),
                ["speak"] = a.Speak,
                ["sound"] = Scrub(a.Sound),
            };
            if (a.Action is not null)
            {
                o["action"] = a.Action;
            }
            actions.Add(o);
        }
        return new JsonObject
        {
            ["actions"] = actions,
            ["warnings"] = new JsonArray([.. r.Warnings.Select(w => (JsonNode?)Scrub(w))]),
            ["sound"] = new JsonObject { ["volume"] = r.SoundVolume, ["max_seconds"] = r.SoundMaxSeconds },
        };
    }

    /// <summary>*.json は読み込んだ値，それ以外は行の配列，無ければ null（export_native._snapshot_file と同じ）．</summary>
    public static JsonNode? Snapshot(TempFolder folder, string name)
    {
        var path = folder.P(name);
        if (!File.Exists(path))
        {
            return null;
        }
        // ファイルの中に出た一時フォルダのパス（通知音の警告のキーなど）も {DIR} に戻す
        var text = File.ReadAllText(path)
            .Replace(folder.Dir.Replace('\\', '/'), Vectors.DirMark)
            .Replace(folder.Dir.Replace(@"\", @"\\"), Vectors.DirMark)
            .Replace(folder.Dir, Vectors.DirMark);
        if (name.EndsWith(".json", StringComparison.Ordinal))
        {
            return JsonNode.Parse(text);
        }
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return new JsonArray([.. lines.Select(l => (JsonNode?)l)]);
    }
}
