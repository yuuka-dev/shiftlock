using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>
/// トレイの「緊急解除」（guard.ps1 -Release と同じ）: 予約済みのシャットダウンの中止 → STOP.txt → 宣言に released_at と events.log．
/// 各手順は独立で，どれかが失敗しても次へ進む．
/// </summary>
public sealed class ReleaseFlowTests : IDisposable
{
    private const string Declared =
        """{"shifts": {"2026-12-07": {"enforce": {"action": "shutdown", "duration_hours": 2, "declared_at": "2026-12-07T06:00", "consented_at": "2026-12-07T06:00", "via": "shl", "runs": []}}}}""";

    private static readonly DateTime Now = new(2026, 12, 7, 11, 0, 0);

    private readonly TempDir _dir = new();
    private readonly List<string> _calls = [];
    private readonly FakePower _power;
    private readonly Messages _m = Messages.Load("ja");

    public ReleaseFlowTests() => _power = new FakePower(_calls);

    public void Dispose() => _dir.Dispose();

    private ReleaseFlow Flow(IPowerControl? power = null, string? dir = null) =>
        new(power ?? _power, new Store(dir ?? _dir.Dir), () => Now, new AgentLog(_dir.P("agent.log")));

    [Fact]
    public void WithDeclaration_AbortsShutdownFirst_ThenStopFile_ThenRecords()
    {
        _dir.Put("state.json", Declared);
        var stopExistedAtAbort = true;
        _power.OnAbort = () => stopExistedAtAbort = File.Exists(_dir.P("STOP.txt"));

        var text = Flow().Run(_m);

        Assert.Equal(["abort"], _calls);
        Assert.False(stopExistedAtAbort);   // 一番急ぐ「シャットダウンの中止」が先
        Assert.Equal(["2026-12-07 11:00 tray"], _dir.Lines("STOP.txt"));
        Assert.Contains("\"released_at\": \"2026-12-07T11:00\"", File.ReadAllText(_dir.P("state.json")));
        Assert.Equal(["2026-12-07T11:00\t2026-12-07\tenforce_release\ttray"], _dir.Lines("events.log"));
        Assert.Equal(_m["ENFORCE_RELEASED"], text);
        Assert.Contains(_dir.Lines("agent.log"), l => l.Contains("release", StringComparison.Ordinal));
    }

    [Fact]
    public void AbortShutdownThrows_StillStopsAndRecords()
    {
        _dir.Put("state.json", Declared);
        _power.OnAbort = () => throw new InvalidOperationException("shutdown.exe が無い");

        var text = Flow().Run(_m);

        Assert.True(File.Exists(_dir.P("STOP.txt")));
        Assert.Single(_dir.Lines("events.log"));
        Assert.Equal(_m["ENFORCE_RELEASED"], text);
        Assert.Contains(_dir.Lines("agent.log"), l => l.Contains("shutdown.exe が無い", StringComparison.Ordinal));
    }

    [Fact]
    public void StateUnreadable_StillWritesStopFile_AndSaysPartial()
    {
        _dir.Put("state.json", "{");

        var text = Flow().Run(_m);

        Assert.Equal(["2026-12-07 11:00 tray"], _dir.Lines("STOP.txt"));   // STOP.txt があれば強制は止まる
        Assert.Equal(_m["AGENT_RELEASE_PARTIAL"], text);
        Assert.Equal("{", File.ReadAllText(_dir.P("state.json")));
    }

    [Fact]
    public void NoDeclaration_WritesStopFile_AndDashLine()
    {
        var text = Flow().Run(_m);

        Assert.Equal(["2026-12-07 11:00 tray"], _dir.Lines("STOP.txt"));
        Assert.Equal(["2026-12-07T11:00\t-\tenforce_release\ttray"], _dir.Lines("events.log"));
        Assert.Equal(_m["ENFORCE_RELEASED"], text);
    }

    [Fact]
    public void AlreadyReleased_SaysSo()
    {
        _dir.Put("STOP.txt", "2026-12-07 10:00 kaijo\n");

        Assert.Equal(_m["ENFORCE_RELEASE_ALREADY"], Flow().Run(_m));

        Assert.Equal(2, _dir.Lines("STOP.txt").Length);
    }

    [Fact]
    public void NothingCouldBeWritten_TellsHowToStopByHand()
    {
        // 実行フォルダが無い（外したドライブなど）: STOP.txt も記録も書けない．手で置く場所を知らせる
        var missing = _dir.P("無いフォルダ");

        var text = Flow(dir: missing).Run(_m);

        Assert.Equal(["abort"], _calls);
        Assert.Equal(_m.Format("AGENT_RELEASE_FAILED", ("path", Path.Combine(missing, "STOP.txt"))), text);
    }

    [Fact]
    public void KansaiMessages_AreUsed()
    {
        var kansai = Messages.Load("ja_kansai");

        Assert.Equal(kansai["ENFORCE_RELEASED"], Flow().Run(kansai));
    }
}
