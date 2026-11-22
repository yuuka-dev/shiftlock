using System.Text.Json.Nodes;
using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>次に返すプロセスの一覧を差し替えられる偽物．Throw を立てると例外．Gate を置くと Snapshot の中で止まる．</summary>
public sealed class FakeSource : IProcessSource
{
    public List<ProcSample> Procs { get; } = [];
    public bool Throw { get; set; }
    public bool HasParents { get; set; } = true;
    public ManualResetEventSlim? Entered { get; set; }
    public ManualResetEventSlim? Hold { get; set; }
    public int Calls;

    public ProcessSnapshot Snapshot()
    {
        Interlocked.Increment(ref Calls);
        Entered?.Set();
        Hold?.Wait();
        if (Throw)
        {
            throw new InvalidOperationException("boom");
        }
        return new ProcessSnapshot([.. Procs], HasParents);
    }
}

public sealed class FakeMeterProbe : IMeterProbe
{
    public int? Foreground { get; set; }
    public Dictionary<int, string?> Paths { get; } = [];
    public Dictionary<int, int> ImagePathCalls { get; } = [];

    public int? ForegroundPid() => Foreground;

    public string? ImagePath(int pid)
    {
        ImagePathCalls[pid] = ImagePathCalls.GetValueOrDefault(pid) + 1;
        return Paths.GetValueOrDefault(pid);
    }
}

/// <summary>稼働計測の 10 秒の仕事（Meter）と --meter-once（MeterOnce）．時刻は 2026-12-07 の 21 時台（合成）．</summary>
public sealed class MeterTests : IDisposable
{
    private const string Apps = ",\"apps\":{\"registered\":{\"code.exe\":\"dev\"},\"active_input_minutes\":5,\"active_cpu_percent\":5}";
    private const string LogName = "log/app-2026-12.jsonl";

    private static readonly DateTime Started = new(2026, 12, 7, 8, 0, 0);

    private readonly TempDir _dir = new();
    private readonly FakeSource _source = new();
    private readonly FakeMeterProbe _meterProbe = new();
    private readonly FakeProbe _probe = new();
    private DateTime _now = new(2026, 12, 7, 21, 0, 0);
    private TimeSpan _mono = TimeSpan.FromHours(1);

    public MeterTests()
    {
        _source.Procs.Add(new ProcSample(100, 1, Started, TimeSpan.Zero, "code.exe"));
        _source.Procs.Add(new ProcSample(200, 1, Started, TimeSpan.Zero, "notepad.exe"));
        _meterProbe.Foreground = 100;
    }

    public void Dispose() => _dir.Dispose();

    private static string ConfigWith(string apps) => WatcherTests.Shifts[..^1] + apps + "}";

    private void PutConfig(string? apps = null) => _dir.Put("shifts.json", ConfigWith(apps ?? Apps));

    private Meter NewMeter() =>
        new(_dir.Dir, _source, _meterProbe, _probe, () => _now, () => _mono, new OncePerDay(new AgentLog(_dir.P("agent.log"))));

    /// <summary>from から to まで（含む）を 10 秒おきに回す．</summary>
    private void RunFrom(Meter meter, string from, string to)
    {
        _now = Time(from);
        var end = Time(to);
        var last = _now - Meter.Interval;
        while (_now <= end)
        {
            _mono += _now - last;
            last = _now;
            meter.RunOnce();
            _now += Meter.Interval;
        }
        _now -= Meter.Interval;
    }

    private static DateTime Time(string hms) => DateTime.Parse("2026-12-07T" + hms, System.Globalization.CultureInfo.InvariantCulture);

    private string[] AgentLogLines => _dir.Lines("agent.log");

    [Fact]
    public void FiveMinutes_WritesOneLine()
    {
        PutConfig();
        RunFrom(NewMeter(), "21:00:00", "21:05:00");

        var line = Assert.Single(_dir.Lines(LogName));
        var rec = JsonNode.Parse(line)!;
        Assert.Equal("2026-12-07T21:05:00", (string?)rec["t"]);
        Assert.Equal("dev", (string?)rec["apps"]!["code.exe"]!["cat"]);
        Assert.Equal(300, (int?)rec["apps"]!["code.exe"]!["act"]);
    }

    [Fact]
    public void DoesNotTouchWatchLog()
    {
        PutConfig();
        RunFrom(NewMeter(), "21:00:00", "21:05:00");

        Assert.False(File.Exists(_dir.P("log/pc-2026-12.jsonl")));
        Assert.False(File.Exists(_dir.P("usage-pc.json")));
        Assert.False(File.Exists(_dir.P("state.json")));
    }

    [Fact]
    public void GameByFolder_IsRecorded()
    {
        PutConfig();
        _source.Procs.Add(new ProcSample(300, 1, Started, TimeSpan.Zero, "eldenring.exe"));
        _meterProbe.Paths[300] = @"C:\Games\Elden\eldenring.exe";
        _meterProbe.Foreground = 300;

        RunFrom(NewMeter(), "21:00:00", "21:05:00");

        Assert.Contains("\"eldenring.exe\": {\"cat\": \"game\"", Assert.Single(_dir.Lines(LogName)));
    }

    [Fact]
    public void PathIsAskedOncePerProcess()
    {
        PutConfig();
        _meterProbe.Paths[100] = @"C:\Tools\code.exe";   // 200 は null（取れない）

        RunFrom(NewMeter(), "21:00:00", "21:05:00");

        Assert.Equal(1, _meterProbe.ImagePathCalls[100]);
        Assert.Equal(1, _meterProbe.ImagePathCalls[200]);
    }

    [Fact]
    public void UnregisteredNames_NeverAppear()
    {
        PutConfig();
        _meterProbe.Foreground = 200;   // 登録していない notepad.exe が前面

        RunFrom(NewMeter(), "21:00:00", "21:05:00");

        Assert.NotEmpty(_dir.Lines(LogName));
        Assert.DoesNotContain(_dir.Lines(LogName), l => l.Contains("notepad", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(AgentLogLines, l => l.Contains("notepad", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SourceThrows_DoesNotThrow_LogsOnce()
    {
        PutConfig();
        var meter = NewMeter();
        _source.Throw = true;

        RunFrom(meter, "21:00:00", "21:00:20");

        var logged = Assert.Single(AgentLogLines);
        Assert.Contains("meter failed", logged);

        _source.Throw = false;
        RunFrom(meter, "21:00:30", "21:05:00");
        Assert.Single(_dir.Lines(LogName));
    }

    [Fact]
    public void NoConfig_DoesNotMeasure_ThenRecovers()
    {
        var meter = NewMeter();
        RunFrom(meter, "21:00:00", "21:04:50");

        Assert.Empty(_dir.Lines(LogName));
        Assert.Contains("not measuring", Assert.Single(AgentLogLines));
        Assert.Equal(0, _source.Calls);

        PutConfig();
        RunFrom(meter, "21:05:00", "21:10:00");

        var line = Assert.Single(_dir.Lines(LogName));
        Assert.Equal("2026-12-07T21:10:00", (string?)JsonNode.Parse(line)!["t"]);
    }

    [Fact]
    public void BadAppsNumber_DoesNotMeasure()
    {
        PutConfig("""
            ,"apps":{"registered":{"code.exe":"dev"},"active_input_minutes":5,"active_cpu_percent":"x"}
            """.Trim());

        RunFrom(NewMeter(), "21:00:00", "21:10:00");

        Assert.Empty(_dir.Lines(LogName));
        Assert.Contains("not measuring", Assert.Single(AgentLogLines));
    }

    [Fact]
    public void RegistrationChange_AppliesFromNextInterval()
    {
        PutConfig();
        var meter = NewMeter();
        RunFrom(meter, "21:00:00", "21:02:00");

        PutConfig("""
            ,"apps":{"registered":{"notepad.exe":"dev"},"active_input_minutes":5,"active_cpu_percent":5}
            """.Trim());
        RunFrom(meter, "21:02:10", "21:10:00");

        var lines = _dir.Lines(LogName);
        Assert.Equal(2, lines.Length);
        var first = JsonNode.Parse(lines[0])!;
        var second = JsonNode.Parse(lines[1])!;
        Assert.Equal("2026-12-07T21:05:00", (string?)first["t"]);
        Assert.NotNull(first["apps"]!["code.exe"]);
        Assert.Null(first["apps"]!["notepad.exe"]);
        Assert.Equal("2026-12-07T21:10:00", (string?)second["t"]);
        Assert.NotNull(second["apps"]!["notepad.exe"]);
        Assert.Null(second["apps"]!["code.exe"]);
    }

    [Fact]
    public void Flush_WritesPending()
    {
        PutConfig();
        var meter = NewMeter();
        RunFrom(meter, "21:00:00", "21:01:00");

        _now = Time("21:01:03");
        meter.Flush();

        var line = Assert.Single(_dir.Lines(LogName));
        Assert.Equal("2026-12-07T21:01:03", (string?)JsonNode.Parse(line)!["t"]);
    }

    [Fact]
    public void Restart_DoesNotRepeatT()
    {
        PutConfig();
        _dir.Put(LogName, """{"t": "2026-12-07T21:05:00", "span": 300, "apps": {}, "other": 0}""" + "\n");

        RunFrom(NewMeter(), "21:04:00", "21:10:00");

        var lines = _dir.Lines(LogName);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-12-07T21:05:00", (string?)JsonNode.Parse(lines[0])!["t"]);
        Assert.Equal("2026-12-07T21:10:00", (string?)JsonNode.Parse(lines[1])!["t"]);
    }

    [Fact]
    public void LastLineInFuture_StillWrites()
    {
        PutConfig();
        _dir.Put(LogName, """{"t": "2026-12-08T00:00:00", "span": 300, "apps": {}, "other": 0}""" + "\n");

        RunFrom(NewMeter(), "21:00:00", "21:05:00");

        Assert.Equal(2, _dir.Lines(LogName).Length);
    }

    [Fact]
    public void LogBusy_DoesNotThrow()
    {
        PutConfig();
        var meter = NewMeter();
        RunFrom(meter, "21:00:00", "21:04:50");

        Directory.CreateDirectory(_dir.P("log"));
        using (new FileStream(_dir.P(LogName), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            RunFrom(meter, "21:05:00", "21:05:10");
        }

        Assert.Contains("meter write failed", Assert.Single(AgentLogLines));
    }

    [Fact]
    public async Task Busy_Skips()
    {
        PutConfig();
        var meter = NewMeter();
        using var entered = new ManualResetEventSlim();
        using var hold = new ManualResetEventSlim();
        _source.Entered = entered;
        _source.Hold = hold;

        var first = Task.Run(meter.RunOnce);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        meter.RunOnce();   // 前の回が動いているのですぐ返る

        Assert.Equal(1, _source.Calls);
        hold.Set();
        await first;
    }

    [Fact]
    public void MeterOnce_PrintsLine_AndWritesNoFile()
    {
        PutConfig();
        var output = new StringWriter();
        var error = new StringWriter();

        var code = MeterOnce.Run(_dir.Dir, _source, _meterProbe, _probe, output, error, _ => { });

        Assert.Equal(0, code);
        var json = JsonNode.Parse(output.ToString())!;
        Assert.True((bool?)json["parents"]);
        Assert.Equal(2, (int?)json["procs"]);
        Assert.Equal(10, (int?)json["line"]!["span"]);
        Assert.False(Directory.Exists(_dir.P("log")));
        Assert.False(File.Exists(_dir.P("agent.log")));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void MeterOnce_NoConfig_Returns1()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = MeterOnce.Run(_dir.Dir, _source, _meterProbe, _probe, output, error, _ => { });

        Assert.Equal(1, code);
        Assert.Equal("", output.ToString());
        Assert.NotEqual("", error.ToString());
    }

    [Fact]
    public void Latest_IsSetAfterAGoodRun_WithSampleSettingsRulesAndPaths()
    {
        PutConfig();
        _meterProbe.Paths[100] = @"C:\Tools\code.exe";
        var meter = NewMeter();
        Assert.Null(meter.Latest);

        RunFrom(meter, "21:00:00", "21:00:00");

        var latest = meter.Latest!;
        Assert.Equal(Time("21:00:00"), latest.Sample.Now);
        Assert.Equal(2, latest.Sample.Procs.Count);
        Assert.Contains("code.exe", latest.Settings.Registered.Keys);
        Assert.Equal(@"C:\Tools\code.exe", latest.Paths[new ProcKey(100, Started)]);
        Assert.NotNull(latest.Rules);
    }

    [Fact]
    public void Latest_GoesBackToNull_WhenConfigIsUnreadable_ThenRecovers()
    {
        PutConfig();
        var meter = NewMeter();
        RunFrom(meter, "21:00:00", "21:00:00");
        Assert.NotNull(meter.Latest);

        // 5 分の区切りで設定を読み直す回に，読めなくなる
        _dir.Put("shifts.json", "{");
        RunFrom(meter, "21:05:00", "21:05:10");
        Assert.Null(meter.Latest);

        PutConfig();
        RunFrom(meter, "21:10:00", "21:10:10");
        Assert.NotNull(meter.Latest);
    }

    [Fact]
    public void Latest_GoesBackToNull_WhenTheSourceThrows()
    {
        PutConfig();
        var meter = NewMeter();
        RunFrom(meter, "21:00:00", "21:00:00");
        Assert.NotNull(meter.Latest);

        _source.Throw = true;
        RunFrom(meter, "21:00:10", "21:00:10");

        Assert.Null(meter.Latest);
    }
}
