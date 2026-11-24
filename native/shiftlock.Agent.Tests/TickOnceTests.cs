using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Agent.Tests;

/// <summary>
/// 突き合わせ用の入口（shiftlock-agent.exe --tick-once）．Python 版の pc/tools/tick_once.py と同じ引数・同じ形の出力．
/// 通知は出さず，強制も実行しない．本番の実行フォルダは断る．
/// </summary>
[Collection("UserProfileEnv")]
public sealed class TickOnceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly string? _userProfile = Environment.GetEnvironmentVariable("USERPROFILE");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", _userProfile);
        _dir.Dispose();
    }

    /// <summary>tick.json の節 notify の最初のケース（lastcall は 1 回だけ）．</summary>
    private static JsonObject FirstNotifyCase()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "tick.json");
        return JsonNode.Parse(File.ReadAllBytes(path))!["notify"]![0]!.AsObject();
    }

    private static void PutFiles(string dir, JsonObject files)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), body!.ToJsonString(), new UTF8Encoding(false));
        }
    }

    private int Run(string dir, JsonNode step, params string[] extra) => TickOnce.Run(
        ["--dir", dir, "--now", step["now"]!.GetValue<string>(), "--idle", step["idle"]!.ToJsonString(),
            "--game", step["game"]!.ToJsonString(), "--uptime", step["uptime"]!.ToJsonString(), .. extra],
        _out, _err);

    [Fact]
    public void SameResultAsTheVector_InTheSameShape()
    {
        var c = FirstNotifyCase();
        PutFiles(_dir.Dir, c["files"]!.AsObject());

        var code = Run(_dir.Dir, c["input"]!["steps"]![0]!);

        Assert.Equal(0, code);
        var text = _out.ToString();
        Assert.True(JsonNode.DeepEquals(c["expect"]!["results"]![0], JsonNode.Parse(text)), text);
        // Python の json.dumps(sort_keys=True, ensure_ascii=False, indent=1) と同じ形
        Assert.StartsWith("{\n \"actions\": [\n  {\n   \"kind\": \"lastcall\",\n   \"method\": \"toast\",\n   \"sound\": \"\",\n   \"speak\": false,\n   \"text\": \"", text);
        Assert.EndsWith("\n \"sound\": {\n  \"max_seconds\": 20,\n  \"volume\": 0.8\n },\n \"warnings\": []\n}\n", text);
        Assert.DoesNotContain("\\u", text);
        Assert.True(File.Exists(_dir.P("notified.json")));   // 渡したフォルダのファイルは書き換える
    }

    [Fact]
    public void ProductionFolder_IsRefused_AndNothingIsWritten()
    {
        var c = FirstNotifyCase();
        var prod = _dir.P("shift-lock");
        PutFiles(prod, c["files"]!.AsObject());
        Environment.SetEnvironmentVariable("USERPROFILE", _dir.Dir);

        Assert.Equal(2, Run(prod, c["input"]!["steps"]![0]!));
        Assert.Equal(2, Run(prod + "\\", c["input"]!["steps"]![0]!));
        Assert.Equal(2, Run(prod.ToUpperInvariant(), c["input"]!["steps"]![0]!));

        Assert.Equal("", _out.ToString());
        Assert.Equal(["shifts.json"], Directory.GetFileSystemEntries(prod).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("--now", "きのう")]
    [InlineData("--idle", "たくさん")]
    [InlineData("--game", "2")]
    [InlineData("--uptime", "x")]
    public void UnreadableValue_IsRefused(string name, string value)
    {
        var c = FirstNotifyCase();
        PutFiles(_dir.Dir, c["files"]!.AsObject());
        var args = new List<string> { "--dir", _dir.Dir, "--now", "2026-12-01T07:20", "--idle", "30", "--game", "0", "--uptime", "7200" };
        args[args.IndexOf(name) + 1] = value;

        Assert.Equal(2, TickOnce.Run(args, _out, _err));

        Assert.Equal("", _out.ToString());
        Assert.NotEqual("", _err.ToString());
        Assert.Equal(["shifts.json"], Directory.GetFileSystemEntries(_dir.Dir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("--dir")]
    [InlineData("--now")]
    [InlineData("--idle")]
    [InlineData("--game")]
    public void MissingRequiredArgument_IsRefused(string missing)
    {
        var args = new List<string> { "--dir", _dir.Dir, "--now", "2026-12-01T07:20", "--idle", "30", "--game", "0" };
        var i = args.IndexOf(missing);
        args.RemoveRange(i, 2);

        Assert.Equal(2, TickOnce.Run(args, _out, _err));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--now")]            // 値が無い
    public void OddArguments_AreRefused(string last)
    {
        Assert.Equal(2, TickOnce.Run(["--dir", _dir.Dir, "--idle", "30", "--game", "0", "--now", "2026-12-01T07:20", last], _out, _err));
    }

    [Fact]
    public void NoConfig_FailsWithOne_NotACrash()
    {
        Assert.Equal(1, TickOnce.Run(["--dir", _dir.Dir, "--now", "2026-12-01T07:20", "--idle", "30", "--game", "0"], _out, _err));

        Assert.Equal("", _out.ToString());
        Assert.NotEqual("", _err.ToString());
    }

    [Fact]
    public void UptimeIsOptional_AndDryRunDoesNotAdvanceEnforce()
    {
        // 宣言済みの強制の実行に当たる回: --dry-run なら記録しない（runs が増えない・events.log が無い）．実行もしない（結果を出すだけ）
        _dir.Put("shifts.json", WatcherTests.Shifts);
        _dir.Put("state.json",
            """{"shifts": {"2026-12-07": {"enforce": {"action": "lock", "duration_hours": 2, "declared_at": "2026-12-07T06:00", "consented_at": "2026-12-07T06:00", "via": "shl", "runs": [], "grace_minutes": 15, "relock_minutes": 5, "max_runs": 6, "boot_grace_minutes": 15, "warned_at": "2026-12-07T10:30:00", "seen_at": "2026-12-07T10:40:00"}}}}""");

        Assert.Equal(0, TickOnce.Run(["--dir", _dir.Dir, "--now", "2026-12-07T10:45", "--idle", "30", "--game", "0", "--uptime", "7200", "--dry-run"], _out, _err));
        Assert.Contains("\"action\": \"lock\"", _out.ToString());
        Assert.Contains("\"runs\": []", File.ReadAllText(_dir.P("state.json")));
        Assert.False(File.Exists(_dir.P("events.log")));

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, TickOnce.Run(["--dir", _dir.Dir, "--now", "2026-12-07T10:50", "--idle", "30", "--game", "0"], _out, _err));
        Assert.DoesNotContain("\"method\": \"enforce\"", _out.ToString());   // uptime が無ければ強制は発動しない
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.8, "0.8")]
    [InlineData(0.25, "0.25")]
    public void Render_WritesFloatsLikePython(double volume, string expected)
    {
        var text = TickOnce.Render(new Shiftlock.Core.TickResult([], ["警告 <1>"], volume, 20));

        Assert.Contains($"\"volume\": {expected}\n", text);
        Assert.Contains("\"warnings\": [\n  \"警告 <1>\"\n ]", text);
    }
}
