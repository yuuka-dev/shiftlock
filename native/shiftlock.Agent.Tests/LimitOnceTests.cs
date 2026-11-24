using System.Text.Json.Nodes;
using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>
/// 確かめ用の入口（shiftlock-agent.exe --limit-once）: 判断を 1 回だけ行って「すること」を JSON で出す．
/// プロセスには手を加えず（control は 1 回も呼ばれない），ファイルも書かない．本番の実行フォルダは断る．
/// </summary>
[Collection("UserProfileEnv")]
public sealed class LimitOnceTests : IDisposable
{
    private static readonly DateTime Born = new(2026, 12, 7, 6, 0, 0);

    private readonly TempDir _dir = new();
    private readonly FakeSource _source = new();
    private readonly FakeMeterProbe _meterProbe = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeFolders _folders = new();
    private readonly FakeLimitControl _control = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly string? _userProfile = Environment.GetEnvironmentVariable("USERPROFILE");

    public LimitOnceTests()
    {
        _dir.Put("shifts.json",
            WatcherTests.Shifts[..^1] + ",\"apps\":{\"registered\":{\"code.exe\":\"dev\"},\"active_input_minutes\":5,\"active_cpu_percent\":5}}");
        _dir.Put("state.json",
            "{\"shifts\":{\"2026-12-07\":{\"enforce\":{\"action\":\"suspend\",\"consented_at\":\"2026-12-07T06:00\","
            + "\"steps\":[75,50,25,10],\"limit_apps\":[],\"runs\":[]}}}}");
        _dir.Put("limit.json", "{\"day\":\"2026-12-07\",\"baseline\":{\"eldenring.exe\":40.0}}");
        _source.Procs.Add(new ProcSample(100, 1, Born, TimeSpan.Zero, "eldenring.exe"));
        _meterProbe.Paths[100] = @"C:\Games\Elden\eldenring.exe";
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", _userProfile);
        _dir.Dispose();
    }

    private int Run(string dir, DateTime now) =>
        LimitOnce.Run(dir, _source, _meterProbe, _probe, _folders, _control, now, _out, _err);

    private string[] Files() => [.. Directory.GetFileSystemEntries(_dir.Dir, "*", SearchOption.AllDirectories)
        .Select(f => f + "|" + File.GetLastWriteTimeUtc(f).Ticks + "|" + (File.Exists(f) ? new FileInfo(f).Length : -1)).Order()];

    [Fact]
    public void ProductionFolder_IsRefused_WithCode2_AndNothingRuns()
    {
        var prod = _dir.P("shift-lock");
        Directory.CreateDirectory(prod);
        Environment.SetEnvironmentVariable("USERPROFILE", _dir.Dir);

        Assert.Equal(2, Run(prod, new DateTime(2026, 12, 7, 8, 5, 0)));
        Assert.Equal(2, Run(prod.ToUpperInvariant() + "\\", new DateTime(2026, 12, 7, 8, 5, 0)));

        Assert.Equal("", _out.ToString());
        Assert.NotEqual("", _err.ToString());
        Assert.Empty(_control.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(prod));
        Assert.Equal(0, _source.Calls);   // 一覧も取らない
    }

    [Fact]
    public void SyntheticFolder_PrintsTheOps_CallsNoControl_WritesNothing()
    {
        var before = Files();

        var code = Run(_dir.Dir, new DateTime(2026, 12, 7, 8, 5, 0));

        Assert.Equal(0, code);
        Assert.Equal("", _err.ToString());
        var json = JsonNode.Parse(_out.ToString())!;
        var op = Assert.Single(json["ops"]!.AsArray(), o => (string?)o!["op"] == "cap")!;
        Assert.Equal("eldenring.exe", (string?)op["exe"]);
        Assert.Equal(30, (double?)op["percent"]);
        Assert.Empty(_control.Calls);
        Assert.Equal(before, Files());   // limit-games.json も agent.log も作らない
    }

    [Fact]
    public void BrokenRecord_IsTreatedAsEmpty_AndNotQuarantined()
    {
        _dir.Put("limit.json", "{");

        var code = Run(_dir.Dir, new DateTime(2026, 12, 7, 8, 5, 0));

        Assert.Equal(0, code);
        Assert.True(File.Exists(_dir.P("limit.json")));
        Assert.False(File.Exists(_dir.P("limit.json.bad")));
    }

    [Fact]
    public void NoConfig_Returns1()
    {
        File.Delete(_dir.P("shifts.json"));

        Assert.Equal(1, Run(_dir.Dir, new DateTime(2026, 12, 7, 8, 5, 0)));
        Assert.Equal("", _out.ToString());
    }
}
