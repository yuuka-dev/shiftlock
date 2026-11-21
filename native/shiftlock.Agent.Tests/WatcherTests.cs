using System.Text;
using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>テスト用の一時フォルダ（実行フォルダの代わり）．</summary>
public sealed class TempDir : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("shiftlock-agent-test-").FullName;

    public string P(string name) => Path.Combine(Dir, name);

    public void Put(string name, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(name))!);
        File.WriteAllText(P(name), body, new UTF8Encoding(false));
    }

    public string[] Lines(string name) =>
        File.Exists(P(name)) ? File.ReadAllText(P(name)).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}

/// <summary>決めた値を返す Windows 側．</summary>
public sealed class FakeProbe : ISystemProbe
{
    public double Idle { get; set; } = 30;
    public double? Uptime { get; set; } = 7200;
    public List<(string Name, string? Path)> Running { get; } = [];
    public List<string> Steam { get; } = [];

    public double IdleSeconds() => Idle;
    public double? UptimeSeconds() => Uptime;
    public IReadOnlyList<(string Name, string? Path)> Processes() => Running;
    public IReadOnlyList<string> SteamCommonDirs() => Steam;
}

/// <summary>
/// 見張りの 1 回（Watcher.RunOnce）: いつ見張り，いつ飛ばすか．見張りの例外で Agent を落とさないこと．
/// 時刻は 2026-12-05（土．乗務の無い日）で，通知は出ない．
/// </summary>
public sealed class WatcherTests : IDisposable
{
    public const string Shifts =
        """{"lang":"ja","work_weekdays":["mon","tue","wed","thu","fri"],"start_target":"17:00","start_latest":"17:30","prep_minutes":60,"sleep_target_hours":8,"sleep_min_hours":6,"confirm":{"mode":"off"},"game_dirs":["C:\\Games"]}""";

    private const string LogName = "log/pc-2026-12.jsonl";

    private readonly TempDir _dir = new();
    private readonly FakeProbe _probe = new();
    private readonly List<TickResult> _delivered = [];
    private DateTime _now = new(2026, 12, 5, 10, 0, 0);
    private GuardTaskState _guard = GuardTaskState.Missing;
    private Action<TickResult, Messages>? _deliver;

    public void Dispose() => _dir.Dispose();

    private Watcher NewWatcher(string? dir = null) =>
        new(dir ?? _dir.Dir, _probe, () => _now, () => _guard,
            (r, m) =>
            {
                _delivered.Add(r);
                _deliver?.Invoke(r, m);
            },
            new AgentLog(_dir.P("agent.log")),
            _ => throw new IOException("no fetch"));

    private string[] AgentLogLines => _dir.Lines("agent.log");

    [Fact]
    public void FirstRun_Runs_AndAppendsOneLogLine()
    {
        _dir.Put("shifts.json", Shifts);
        var w = NewWatcher();

        Assert.Equal(WatchOutcome.Ran, w.RunOnce());

        Assert.Equal(WatchOutcome.Ran, w.LastOutcome);
        Assert.Null(w.LastError);
        Assert.Equal(["""{"t": "2026-12-05T10:00:00", "idle": 30, "game": 0}"""], _dir.Lines(LogName));
        Assert.Single(_delivered);
    }

    [Fact]
    public void WithinFiveMinutes_IsTooSoon_ThenRunsAtFive()
    {
        _dir.Put("shifts.json", Shifts);
        var w = NewWatcher();
        w.RunOnce();

        _now = _now.AddMinutes(4);
        Assert.Equal(WatchOutcome.TooSoon, w.RunOnce());
        Assert.Single(_dir.Lines(LogName));
        Assert.Single(_delivered);

        _now = _now.AddMinutes(1);
        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
        Assert.Equal(2, _dir.Lines(LogName).Length);
    }

    [Fact]
    public void TimerJitter_OfAFewSeconds_DoesNotSkipTheRun()
    {
        // タイマーは 5 分ちょうどより少し早く来ることがある．それで 1 回飛ぶと 10 分空いてしまう
        _dir.Put("shifts.json", Shifts);
        var w = NewWatcher();
        w.RunOnce();

        _now = _now.AddMinutes(5).AddSeconds(-3);

        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
    }

    [Fact]
    public void NewWatcher_StillSeesTheLastRun()
    {
        // Agent を起動し直した直後でも，ログの最後の行を見て間引く
        _dir.Put("shifts.json", Shifts);
        NewWatcher().RunOnce();

        _now = _now.AddMinutes(4);

        Assert.Equal(WatchOutcome.TooSoon, NewWatcher().RunOnce());
        Assert.Single(_dir.Lines(LogName));
    }

    [Fact]
    public void LastLineInTheFuture_Runs()
    {
        // 時計が戻った・別の PC からコピーしたフォルダ．「5 分たっていない」が続かない
        _dir.Put("shifts.json", Shifts);
        _dir.Put(LogName, """{"t": "2026-12-05T11:00:00", "idle": 0, "game": 0}""" + "\n");

        Assert.Equal(WatchOutcome.Ran, NewWatcher().RunOnce());
        Assert.Equal(2, _dir.Lines(LogName).Length);
    }

    [Fact]
    public void NoShiftsJson_IsNoConfig_UntilItAppears()
    {
        var w = NewWatcher();

        Assert.Equal(WatchOutcome.NoConfig, w.RunOnce());
        Assert.Equal(WatchOutcome.NoConfig, w.LastOutcome);
        Assert.Contains("shifts.json", w.LastError);
        Assert.Empty(_dir.Lines(LogName));

        _dir.Put("shifts.json", Shifts);
        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
        Assert.Null(w.LastError);
    }

    [Fact]
    public void NoFolder_IsNoConfig_UntilItAppears()
    {
        var missing = _dir.P("まだ無い");
        var w = NewWatcher(missing);

        Assert.Equal(WatchOutcome.NoConfig, w.RunOnce());

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "shifts.json"), Shifts);
        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
    }

    [Fact]
    public void BrokenShiftsJson_IsBadConfig()
    {
        _dir.Put("shifts.json", "{");
        var w = NewWatcher();

        Assert.Equal(WatchOutcome.BadConfig, w.RunOnce());
        Assert.StartsWith("設定が読めないよ: ", w.LastError);
        Assert.Empty(_dir.Lines(LogName));
        Assert.Empty(_delivered);
    }

    [Fact]
    public void GuardTaskEnabled_DoesNotWatch()
    {
        // Python 版のタスクが有効なら見張らない（二重に見張らない）
        _dir.Put("shifts.json", Shifts);
        _guard = GuardTaskState.Enabled;
        var w = NewWatcher();

        Assert.Equal(WatchOutcome.GuardTaskEnabled, w.RunOnce());
        Assert.Empty(_dir.Lines(LogName));
        Assert.Empty(_delivered);

        _guard = GuardTaskState.Disabled;
        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
    }

    [Fact]
    public void GuardTaskUnknown_Watches_AndWarnsOncePerDay()
    {
        _dir.Put("shifts.json", Shifts);
        _guard = GuardTaskState.Unknown;
        var w = NewWatcher();

        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
        _now = _now.AddMinutes(5);
        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
        Assert.Single(AgentLogLines, l => l.Contains("shiftlock-guard", StringComparison.Ordinal));

        _now = _now.AddDays(1);
        w.RunOnce();
        Assert.Equal(2, AgentLogLines.Count(l => l.Contains("shiftlock-guard", StringComparison.Ordinal)));
    }

    [Fact]
    public void GuardTaskCheckThrows_IsTreatedAsUnknown()
    {
        _dir.Put("shifts.json", Shifts);
        var w = new Watcher(_dir.Dir, _probe, () => _now, () => throw new InvalidOperationException("schtasks"),
            (_, _) => { }, new AgentLog(_dir.P("agent.log")));

        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
    }

    [Fact]
    public void DeliverThrows_IsFailed_AndNextRunWorks()
    {
        _dir.Put("shifts.json", Shifts);
        var w = NewWatcher();
        _deliver = (_, _) => throw new InvalidOperationException("通知が出せない");

        Assert.Equal(WatchOutcome.Failed, w.RunOnce());
        Assert.Equal(WatchOutcome.Failed, w.LastOutcome);
        Assert.Contains(AgentLogLines, l => l.Contains("通知が出せない", StringComparison.Ordinal));

        _deliver = null;
        _now = _now.AddMinutes(5);
        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
    }

    [Fact]
    public void ProbeThrows_IsFailed_NotACrash()
    {
        _dir.Put("shifts.json", Shifts);
        var w = new Watcher(_dir.Dir, new ThrowingProbe(), () => _now, () => _guard, (_, _) => { }, new AgentLog(_dir.P("agent.log")));

        Assert.Equal(WatchOutcome.Failed, w.RunOnce());
    }

    [Fact]
    public void StateJsonHeldByAnotherWriter_DoesNotCrash_AndRecovers()
    {
        // ウィンドウや hook が state.json を書いている最中．乗務日（月曜）の「起きた」を自動で付ける回にして，書き込みを起こす
        _dir.Put("shifts.json", Shifts);
        _dir.Put("state.json", """{"shifts": {"2026-12-07": {"sleep_at": "2026-12-07T08:00"}}}""");
        _dir.Put("usage-pc.json", """{"gaps": [["2026-12-07T08:00", "2026-12-07T14:30"]], "start": "2026-12-07T14:30:00", "last": "2026-12-07T14:30:00"}""");
        _now = new DateTime(2026, 12, 7, 14, 35, 0);
        var w = NewWatcher();

        WatchOutcome held;
        using (new FileStream(_dir.P("state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            held = w.RunOnce();
        }
        Assert.Contains(held, new[] { WatchOutcome.Failed, WatchOutcome.Ran });

        _now = _now.AddMinutes(5);
        Assert.Equal(WatchOutcome.Ran, w.RunOnce());
    }

    [Fact]
    public void GameProcess_IsLoggedAsGame()
    {
        _dir.Put("shifts.json", Shifts);
        _probe.Running.Add(("explorer", @"C:\Windows\explorer.exe"));
        _probe.Running.Add(("foo", @"C:\Games\Foo\foo.exe"));

        NewWatcher().RunOnce();

        Assert.Equal(["""{"t": "2026-12-05T10:00:00", "idle": 30, "game": 1}"""], _dir.Lines(LogName));
    }

    [Fact]
    public void SteamDirs_FromProbe_AreUsed()
    {
        _dir.Put("shifts.json", Shifts);
        _probe.Steam.Add(@"D:\SteamLibrary\steamapps\common");
        _probe.Running.Add(("bar", @"D:\SteamLibrary\steamapps\common\Bar\bar.exe"));

        NewWatcher().RunOnce();

        Assert.EndsWith("\"game\": 1}", _dir.Lines(LogName)[0]);
    }

    [Fact]
    public void Warnings_AreWrittenToAgentLog()
    {
        _dir.Put("shifts.json", Shifts[..^1] + ""","notify":{"sound_volume":"大きめ"}}""");

        Assert.Equal(WatchOutcome.Ran, NewWatcher().RunOnce());

        Assert.Contains(AgentLogLines, l => l.Contains("warn: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondCallWhileRunning_IsBusy()
    {
        _dir.Put("shifts.json", Shifts);
        var w = NewWatcher();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _deliver = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var first = Task.Run(() => w.RunOnce());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = w.RunOnce();
        release.Set();

        Assert.Equal(WatchOutcome.Busy, second);
        Assert.Equal(WatchOutcome.Ran, await first);
        Assert.Equal(WatchOutcome.Ran, w.LastOutcome);   // 飛ばした回は前の結果を上書きしない
        Assert.Single(_dir.Lines(LogName));
    }

    [Fact]
    public void DryRun_IsPassedToTheTick()
    {
        // 宣言済みの強制の実行を dry_run で通しても，状態は進まない（runs が増えない）
        _dir.Put("shifts.json", Shifts);
        _dir.Put("state.json",
            """{"shifts": {"2026-12-07": {"enforce": {"action": "lock", "duration_hours": 2, "declared_at": "2026-12-07T06:00", "consented_at": "2026-12-07T06:00", "via": "shl", "runs": [], "grace_minutes": 15, "relock_minutes": 5, "max_runs": 6, "boot_grace_minutes": 15, "warned_at": "2026-12-07T10:30:00", "seen_at": "2026-12-07T10:40:00"}}}}""");
        _now = new DateTime(2026, 12, 7, 10, 45, 0);

        Assert.Equal(WatchOutcome.Ran, NewWatcher().RunOnce(dryRun: true));

        Assert.Contains(_delivered[0].Actions, a => a.Method == "enforce");
        Assert.Contains("\"runs\": []", File.ReadAllText(_dir.P("state.json")));
        Assert.False(File.Exists(_dir.P("events.log")));
    }

    private sealed class ThrowingProbe : ISystemProbe
    {
        public double IdleSeconds() => throw new InvalidOperationException("idle");
        public double? UptimeSeconds() => null;
        public IReadOnlyList<(string Name, string? Path)> Processes() => [];
        public IReadOnlyList<string> SteamCommonDirs() => [];
    }
}
