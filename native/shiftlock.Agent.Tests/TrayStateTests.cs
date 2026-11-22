using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>トレイの見た目とホバーの 1 行の決まり．乗務日は 2026-12-07（月）: 就寝準備 07:20／就寝目安 08:00／最低ライン 10:30．</summary>
public sealed class TrayStateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Messages _m = Messages.Load("ja");

    public TrayStateTests() => _dir.Put("shifts.json", WatcherTests.Shifts);

    public void Dispose() => _dir.Dispose();

    private StatusSnapshot At(string hm, int day = 7)
    {
        var now = DateTime.Parse($"2026-12-{day:00}T{hm}:00", System.Globalization.CultureInfo.InvariantCulture);
        return Snapshot.Build(ShiftConfig.Load(_dir.P("shifts.json")), new Store(_dir.Dir), now);
    }

    [Theory]
    [InlineData("10:00", 5, TrayLook.Normal)]      // 土曜（乗務なし）
    [InlineData("06:00", 7, TrayLook.Normal)]
    [InlineData("07:25", 7, TrayLook.Caution)]     // lastcall
    [InlineData("08:05", 7, TrayLook.Caution)]     // soft
    [InlineData("10:35", 7, TrayLook.Warning)]     // hard
    public void Look_FollowsThePhase(string hm, int day, TrayLook expected)
    {
        var s = At(hm, day);

        var (look, tip) = TrayState.From(WatchOutcome.Ran, null, s, _m);

        Assert.Equal(expected, look);
        Assert.Contains(tip, Snapshot.Lines(s, _m));
    }

    [Fact]
    public void GoLast_IsWarning()
    {
        _dir.Put("state.json", """{"shifts": {"2026-12-07": {"status": "okita", "at": "2026-12-07T14:00"}}}""");

        Assert.Equal(TrayLook.Warning, TrayState.From(WatchOutcome.Ran, null, At("16:15"), _m).Look);
        Assert.Equal(TrayLook.Normal, TrayState.From(WatchOutcome.Ran, null, At("15:45"), _m).Look);   // go
    }

    [Fact]
    public void Tip_IsTheCountdownLine_WhenThereIsOne()
    {
        var s = At("07:25");
        var lines = Snapshot.Lines(s, _m);

        Assert.NotNull(s.CountdownKey);
        Assert.Equal(lines[2], TrayState.From(WatchOutcome.Ran, null, s, _m).Tip);
    }

    [Fact]
    public void Tip_IsThePhaseLine_WhenThereIsNoCountdown()
    {
        var s = At("10:00", day: 5);

        Assert.Null(s.CountdownKey);
        Assert.Equal(Snapshot.Lines(s, _m)[1], TrayState.From(WatchOutcome.TooSoon, null, s, _m).Tip);
    }

    [Fact]
    public void GuardTaskEnabled_IsPaused()
    {
        var (look, tip) = TrayState.From(WatchOutcome.GuardTaskEnabled, null, At("10:35"), _m);

        Assert.Equal(TrayLook.Paused, look);
        Assert.Equal(_m["AGENT_TIP_GUARD_TASK"], tip);
    }

    [Theory]
    [InlineData(WatchOutcome.NoConfig)]
    [InlineData(WatchOutcome.BadConfig)]
    public void ConfigError_ShowsTheError(WatchOutcome outcome)
    {
        var (look, tip) = TrayState.From(outcome, "設定が読めないよ: x", null, _m);

        Assert.Equal(TrayLook.ConfigError, look);
        Assert.Equal("設定が読めないよ: x", tip);
    }

    [Fact]
    public void Failed_KeepsThePhaseLook_AndSaysSo()
    {
        var (look, tip) = TrayState.From(WatchOutcome.Failed, null, At("10:35"), _m);

        Assert.Equal(TrayLook.Warning, look);
        Assert.Equal(_m["AGENT_TIP_FAILED"], tip);
    }

    [Fact]
    public void NotYetWatched_UsesTheSnapshot()
    {
        Assert.Equal(TrayLook.Caution, TrayState.From(null, null, At("08:05"), _m).Look);
    }

    [Fact]
    public void NoSnapshot_IsConfigError_EvenIfTheLastWatchRan()
    {
        // 見張りの後に shifts.json が消えた・壊れた（毎分の更新で気付く）
        var (look, tip) = TrayState.From(WatchOutcome.Ran, "まだ設定がないよ", null, _m);

        Assert.Equal(TrayLook.ConfigError, look);
        Assert.Equal("まだ設定がないよ", tip);
    }

    [Fact]
    public void Tip_IsCutAt63Characters()
    {
        var error = "設定が読めないよ: " + new string('あ', 200);

        var tip = TrayState.From(WatchOutcome.BadConfig, error, null, _m).Tip;

        Assert.Equal(63, tip.Length);
        Assert.StartsWith("設定が読めないよ: ", tip);
        Assert.EndsWith("…", tip);
    }

    [Fact]
    public void Tip_DoesNotSplitASurrogatePair()
    {
        var error = new string('a', 61) + "😀😀";

        var tip = TrayState.From(WatchOutcome.BadConfig, error, null, _m).Tip;

        Assert.True(tip.Length <= 63);
        Assert.False(char.IsHighSurrogate(tip[^2]) && tip[^1] == '…');
    }

    [Fact]
    public void FindApp_LooksBesideTheAgent_ThenOneFolderUp()
    {
        // 置いた形: <場所>\shiftlock.exe と <場所>\agent\shiftlock-agent.exe
        var agentDir = _dir.P("agent");
        Directory.CreateDirectory(agentDir);

        Assert.Equal(Path.Combine(agentDir, "shiftlock.exe"), Tray.FindApp(agentDir));   // どこにも無い → 同じフォルダのパス（知らせる文用）

        _dir.Put("shiftlock.exe", "");
        Assert.Equal(_dir.P("shiftlock.exe"), Tray.FindApp(agentDir));
        Assert.Equal(_dir.P("shiftlock.exe"), Tray.FindApp(agentDir + Path.DirectorySeparatorChar));

        _dir.Put("agent/shiftlock.exe", "");
        Assert.Equal(Path.Combine(agentDir, "shiftlock.exe"), Tray.FindApp(agentDir));   // 同じフォルダが優先
    }

    [Fact]
    public void AllTipsInBothLanguages_FitWithoutCutting()
    {
        foreach (var lang in new[] { "ja", "ja_kansai" })
        {
            var m = Messages.Load(lang);
            Assert.True(m["AGENT_TIP_GUARD_TASK"].Length <= 63, lang);
            Assert.True(m["AGENT_TIP_FAILED"].Length <= 63, lang);
        }
    }
}
