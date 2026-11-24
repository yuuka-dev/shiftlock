namespace Shiftlock.Core.Tests;

/// <summary>アプリ計測（AppMeter）の積み上げ・区切り・行の字面．行は Store.LogAppend で書いた字面（Python と同じ）で比べる．</summary>
public sealed class AppMeterTests
{
    private static readonly DateTime T0 = new(2026, 12, 7, 21, 0, 0);
    private static readonly DateTime Early = new(2026, 12, 7, 20, 0, 0);

    private static DateTime At(int h, int m, int s = 0) => new(2026, 12, 7, h, m, s);

    private static MeterSettings Settings(bool registered = true) => new(
        registered
            ? new Dictionary<string, string> { ["code.exe"] = "dev", ["photoshop.exe"] = "adobe" }
            : new Dictionary<string, string>(),
        TimeSpan.FromMinutes(5), 5, TimeSpan.FromMinutes(180));

    /// <summary>プロセス 1 つ．Rate は 1 サンプルごとに増える CPU 秒，First は初めて現れたときの CPU 秒．</summary>
    private sealed record P(int Pid, string Name, double Rate, double First = 0, int Parent = 0, DateTime? Start = null);

    private sealed class Sim(AppMeter? meter = null, MeterSettings? settings = null)
    {
        public AppMeter Meter { get; } = meter ?? new AppMeter();
        public MeterSettings Settings { get; } = settings ?? AppMeterTests.Settings();
        public TimeSpan Mono { get; set; }
        public List<MeterLine> Lines { get; } = [];
        public IReadOnlySet<ProcKey> Games { get; set; } = new HashSet<ProcKey>();
        private bool _started;
        private readonly Dictionary<int, double> _cpu = [];

        /// <summary>1 サンプル．Mono は 10 秒進める（最初だけ進めない）．CPU 時間は前回から Rate × scale 増える．</summary>
        public MeterLine? Go(DateTime now, int? fg, double idle, bool hasParents = true, double scale = 1, params P[] procs)
        {
            if (_started)
            {
                Mono += TimeSpan.FromSeconds(10);
            }
            _started = true;
            var list = new List<ProcSample>();
            foreach (var p in procs)
            {
                _cpu[p.Pid] = _cpu.TryGetValue(p.Pid, out var c) ? c + p.Rate * scale : p.First;
                list.Add(new ProcSample(p.Pid, p.Parent, p.Start ?? Early, TimeSpan.FromSeconds(_cpu[p.Pid]), p.Name));
            }
            var sample = new MeterSample(now, Mono, list, fg, idle, Games, 4, hasParents);
            var line = Meter.Add(sample, Settings);
            if (line is not null)
            {
                Lines.Add(line);
            }
            return line;
        }

        /// <summary>from から to まで（両端を含む）10 秒おき．</summary>
        public void Seq(DateTime from, DateTime to, int? fg, double idle, params P[] procs) =>
            Seq(from, to, fg, idle, true, 1, procs);

        public void Seq(DateTime from, DateTime to, int? fg, double idle, bool hasParents, double scale, params P[] procs)
        {
            for (var t = from; t <= to; t = t.AddSeconds(10))
            {
                Go(t, fg, idle, hasParents, t == from ? scale : 1, procs);
            }
        }
    }

    private static string[] Written(IEnumerable<MeterLine> lines)
    {
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        foreach (var line in lines)
        {
            store.LogAppend("app", line.T, line.Rec);
        }
        var path = folder.P(Path.Combine("log", "app-2026-12.jsonl"));
        return File.Exists(path) ? File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    private static string Body(string line) => line[(line.IndexOf(", ", StringComparison.Ordinal) + 2)..^1];

    private static string One(Sim sim)
    {
        Assert.Single(sim.Lines);
        Assert.Equal(At(21, 5), sim.Lines[0].T);
        return Body(Written(sim.Lines)[0]);
    }

    private static readonly P Code = new(100, "code.exe", 0.4);

    [Fact]
    public void Foreground_WithInput_IsActive()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 100, 10, Code);
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 300, \"cpu\": 1.0}}, \"other\": 0", One(sim));
    }

    [Fact]
    public void Foreground_NoInput_HighCpu_IsActive()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 100, 600, Code with { Rate = 4 });
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 300, \"cpu\": 10.0}}, \"other\": 0", One(sim));
    }

    [Fact]
    public void Foreground_NoInput_LowCpu_IsNotActive()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 100, 600, Code);
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 0, \"cpu\": 1.0}}, \"other\": 0", One(sim));
    }

    [Fact]
    public void NotForeground_RecordsCpuOnly()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 200, 10, Code, new P(200, "explorer.exe", 0));
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 0, \"cpu\": 1.0}}, \"other\": 300", One(sim));
    }

    [Fact]
    public void IdleBeyondGameIdle_IsNotActive()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 100, 10800, Code with { Rate = 4 });
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 0, \"cpu\": 10.0}}, \"other\": 0", One(sim));
    }

    [Fact]
    public void Game_IsRecordedUnderExeName()
    {
        var game = new P(300, "eldenring.exe", 4);
        var sim = new Sim { Games = new HashSet<ProcKey> { new(300, Early) } };
        sim.Seq(T0, At(21, 5), 300, 600, game);
        Assert.Equal("\"span\": 300, \"apps\": {\"eldenring.exe\": {\"cat\": \"game\", \"act\": 300, \"cpu\": 10.0}}, \"other\": 0", One(sim));
    }

    [Fact]
    public void RegisteredWinsOverGame()
    {
        var sim = new Sim { Games = new HashSet<ProcKey> { new(100, Early) } };
        sim.Seq(T0, At(21, 5), 100, 10, Code);
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 300, \"cpu\": 1.0}}, \"other\": 0", One(sim));
    }

    [Fact]
    public void NoRegistration_OnlyOther()
    {
        var sim = new Sim(settings: Settings(registered: false));
        sim.Seq(T0, At(21, 5), 100, 10, Code);
        Assert.Equal("\"span\": 300, \"apps\": {}, \"other\": 300", One(sim));
    }

    [Fact]
    public void PidReuse_IsNotSameProcess()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 1, 50), 100, 10, Code);
        var notepad = new P(100, "notepad.exe", 0, First: 5, Start: At(21, 1, 55));
        sim.Seq(At(21, 2), At(21, 5), 100, 10, notepad);
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 110, \"cpu\": 0.4}}, \"other\": 190", One(sim));
    }

    private static readonly P[] Family =
    [
        Code,
        new(101, "node.exe", 0.4, Parent: 100, Start: Early.AddSeconds(1)),
        new(103, "code.exe", 0.4, Parent: 100),
        new(102, "notepad.exe", 4, Parent: 100, Start: Early.AddHours(-1)),
    ];

    [Fact]
    public void Tree_SumsChildrenOnce()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 200, 10, [.. Family, new P(200, "explorer.exe", 0)]);
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 0, \"cpu\": 3.0}}, \"other\": 300", One(sim));
    }

    [Fact]
    public void NoParents_CountsSameNameOnly()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 200, 10, hasParents: false, scale: 1, [.. Family, new P(200, "explorer.exe", 0)]);
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 0, \"cpu\": 2.0}}, \"other\": 300", One(sim));
    }

    [Fact]
    public void ProcessStartedMidInterval_CountsAllItsCpu()
    {
        var sim = new Sim();
        var explorer = new P(200, "explorer.exe", 0);
        sim.Seq(T0, At(21, 0, 50), 200, 10, explorer);
        var code = new P(100, "code.exe", 0.4, First: 2.0, Start: At(21, 0, 55));
        sim.Seq(At(21, 1), At(21, 5), 200, 10, code, explorer);
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 0, \"cpu\": 1.0}}, \"other\": 300", One(sim));
    }

    [Fact]
    public void Gap_DropsIncrement()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 2), 100, 10, Code);
        sim.Mono += TimeSpan.FromSeconds(50);   // 21:03:00 は 60 秒空く
        sim.Seq(At(21, 3), At(21, 5), 100, 10, true, 250, Code);   // その間に CPU が 100 秒伸びても数えない
        Assert.Equal("\"span\": 240, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 240, \"cpu\": 1.0}}, \"other\": 0", One(sim));
    }

    [Fact]
    public void LongSleep_WritesOneLineForOldBucket()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 2), 100, 10, Code);
        sim.Mono += TimeSpan.FromSeconds(29 * 60 - 10);
        var line = sim.Go(At(21, 31), 100, 10, procs: Code);
        Assert.NotNull(line);
        Assert.Equal(At(21, 5), line.T);
        Assert.Equal(120, line.Rec["span"]!.GetValue<int>());
        sim.Seq(At(21, 31, 10), At(21, 35), 100, 10, Code);
        Assert.Equal(At(21, 35), sim.Lines[^1].T);
        Assert.Equal(2, sim.Lines.Count);
    }

    [Fact]
    public void MonthBoundary()
    {
        var sim = new Sim();
        sim.Seq(new DateTime(2026, 12, 31, 23, 55, 0), new DateTime(2027, 1, 1, 0, 0, 0), 100, 10, Code);
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0), Assert.Single(sim.Lines).T);
    }

    [Fact]
    public void SameT_IsNotWrittenTwice()
    {
        var sim = new Sim(new AppMeter(lastWritten: At(21, 5)));
        sim.Seq(T0, At(21, 10), 100, 10, Code);
        var line = Assert.Single(sim.Lines);
        Assert.Equal(At(21, 10), line.T);
        Assert.Equal(600, line.Rec["span"]!.GetValue<int>());
        Assert.Equal(600, line.Rec["apps"]!["code.exe"]!["act"]!.GetValue<int>());
    }

    [Fact]
    public void LastWrittenInFuture_IsIgnored()
    {
        var sim = new Sim(new AppMeter(lastWritten: At(22, 0)));
        sim.Seq(T0, At(21, 5), 100, 10, Code);
        Assert.Equal(At(21, 5), Assert.Single(sim.Lines).T);
    }

    [Fact]
    public void ClockBack_DoesNotRepeatT()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 100, 10, Code);
        Assert.Single(sim.Lines);
        Assert.Null(sim.Go(At(21, 3), 100, 10, procs: Code));   // 戻ったサンプル
        for (var t = At(21, 3, 10); t < At(21, 5); t = t.AddSeconds(10))
        {
            Assert.Null(sim.Go(t, 100, 10, procs: Code));
        }
        Assert.Null(sim.Go(At(21, 5), 100, 10, procs: Code));   // 2 回目に 21:05:00 を越えた
        Assert.Null(sim.Go(At(21, 5, 10), 100, 10, procs: Code));
        sim.Seq(At(21, 5, 20), At(21, 10), 100, 10, Code);
        Assert.Equal(2, sim.Lines.Count);
        Assert.Equal(At(21, 10), sim.Lines[1].T);
        // 戻ってからの分（21:03:00 〜 21:10:00 の 43 サンプル = 430 秒）を全部含む
        Assert.Equal(430, sim.Lines[1].Rec["span"]!.GetValue<int>());
    }

    [Fact]
    public void Flush_WritesPending()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 1), 100, 10, Code);
        var line = sim.Meter.Flush(T0.AddMinutes(1).AddSeconds(3.5));
        Assert.NotNull(line);
        Assert.Equal(At(21, 1, 3), line.T);
        Assert.Equal(60, line.Rec["span"]!.GetValue<int>());
        Assert.Null(sim.Meter.Flush(At(21, 1, 4)));
    }

    [Fact]
    public void NothingMeasured_NoLine()
    {
        var sim = new Sim();
        sim.Go(T0, 100, 10, procs: Code);
        Assert.Null(sim.Meter.Flush(At(21, 0, 5)));
    }

    [Fact]
    public void RunningButIdleApp_IsListed()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 5), 200, 10, new P(100, "code.exe", 0), new P(200, "explorer.exe", 0));
        Assert.Equal("\"span\": 300, \"apps\": {\"code.exe\": {\"cat\": \"dev\", \"act\": 0, \"cpu\": 0.0}}, \"other\": 300", One(sim));
    }

    [Fact]
    public void Reset_DropsBaseline()
    {
        var sim = new Sim();
        sim.Seq(T0, At(21, 1), 100, 10, Code);
        sim.Meter.Reset();
        sim.Seq(At(21, 1, 10), At(21, 5), 100, 10, Code);
        Assert.Equal(230, Assert.Single(sim.Lines).Rec["span"]!.GetValue<int>());
    }

    [Fact]
    public void SharedVector_ScenarioWritesTheseLines()
    {
        var explorer = new P(200, "explorer.exe", 0);
        var sim = new Sim();
        // 21:00:00〜21:05:00 code 前面
        sim.Seq(T0, At(21, 5), 100, 10, Code, explorer);
        // 21:05:10〜21:10:00 explorer 前面
        sim.Seq(At(21, 5, 10), At(21, 10), 200, 10, Code, explorer);
        // 21:10:10〜21:15:00 eldenring（Games）
        sim.Games = new HashSet<ProcKey> { new(300, At(21, 10, 5)) };
        sim.Seq(At(21, 10, 10), At(21, 15), 300, 600, new P(300, "eldenring.exe", 4, First: 4, Start: At(21, 10, 5)));
        // 21:15:10〜21:17:30 photoshop
        sim.Games = new HashSet<ProcKey>();
        var ps = new P(400, "photoshop.exe", 0.4, First: 0.4, Start: At(21, 15, 5));
        sim.Seq(At(21, 15, 10), At(21, 17, 30), 400, 10, ps);
        // 21:18:30 は 60 秒空く（その間の Cpu の伸びは数えない）
        sim.Mono += TimeSpan.FromSeconds(50);
        sim.Seq(At(21, 18, 30), At(21, 20), 400, 10, true, 250, ps);

        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "app-log.jsonl"));
        using var folder = new TempFolder();
        var store = new Store(folder.Dir);
        foreach (var line in sim.Lines)
        {
            store.LogAppend("app", line.T, line.Rec);
        }
        Assert.Equal(expected, File.ReadAllText(folder.P(Path.Combine("log", "app-2026-12.jsonl"))));
    }
}
