using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>呼ばれた順を記録する「知らせる側」．</summary>
public sealed class FakeNotifier(List<string> calls) : INotifier
{
    public bool SoundPlays { get; set; } = true;
    public Action<string>? OnToast { get; set; }
    public Action<string>? OnPopup { get; set; }
    public Action<string>? OnSpeak { get; set; }
    public Action<string>? OnSound { get; set; }

    public void Toast(string title, string text, bool silent)
    {
        calls.Add($"toast:{title}:{text}:{(silent ? "silent" : "sound")}");
        OnToast?.Invoke(text);
    }

    public void Popup(string title, string text)
    {
        calls.Add($"popup:{title}:{text}");
        OnPopup?.Invoke(text);
    }

    public void Speak(string text)
    {
        calls.Add($"speak:{text}");
        OnSpeak?.Invoke(text);
    }

    public bool PlaySound(string path, double volume, int maxSeconds, CancellationToken cancel)
    {
        calls.Add(FormattableString.Invariant($"sound:{path}:{volume}:{maxSeconds}"));
        OnSound?.Invoke(path);
        return SoundPlays;
    }
}

/// <summary>呼ばれた順を記録する「止める側」．実際には何も止めない．</summary>
public sealed class FakePower(List<string> calls) : IPowerControl
{
    public Action? OnAbort { get; set; }

    public void Lock() => calls.Add("lock");
    public void Sleep() => calls.Add("sleep");
    public void ScheduleShutdown(int seconds) => calls.Add($"shutdown:{seconds}");

    public void AbortShutdown()
    {
        calls.Add("abort");
        OnAbort?.Invoke();
    }
}

/// <summary>
/// 見張りの結果を出す順番と決まり（pc/guard.ps1 の Send-Notice・Invoke-Enforce と同じ）．
/// 強制の実行は直前に STOP.txt を見る．1 件の失敗で残りを止めない．
/// </summary>
public sealed class ActionRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<string> _calls = [];
    private readonly FakeNotifier _notifier;
    private readonly FakePower _power;
    private readonly ActionRunner _runner;

    public ActionRunnerTests()
    {
        _notifier = new FakeNotifier(_calls);
        _power = new FakePower(_calls);
        _runner = new ActionRunner(_notifier, _power, new Store(_dir.Dir), new AgentLog(_dir.P("agent.log")));
    }

    public void Dispose() => _dir.Dispose();

    private static TickAction Notice(string method, string text, bool speak = false, string sound = "", string kind = "soft") =>
        new(method, kind, "shiftlock", text, speak, sound);

    private static TickAction Enforce(string action, string sound = "") =>
        new("enforce", "enforce", "shiftlock", "強制の実行", Speak: true, sound, action);

    private void Run(params TickAction[] actions) => Run(CancellationToken.None, actions);

    private void Run(CancellationToken cancel, params TickAction[] actions) =>
        _runner.Run(new TickResult(actions, [], 0.5, 15), cancel);

    private string[] Log => _dir.Lines("agent.log");

    [Fact]
    public void Toast_WithSound_PlaysFirst_ThenSilentToast()
    {
        Run(Notice("toast", "ねる時間", sound: @"C:\s\a.mp3"));

        Assert.Equal([@"sound:C:\s\a.mp3:0.5:15", "toast:shiftlock:ねる時間:silent"], _calls);
    }

    [Fact]
    public void Toast_WhenSoundCannotPlay_KeepsTheDefaultSound()
    {
        _notifier.SoundPlays = false;

        Run(Notice("toast", "ねる時間", sound: @"C:\s\a.mp3"));

        Assert.Equal([@"sound:C:\s\a.mp3:0.5:15", "toast:shiftlock:ねる時間:sound"], _calls);
    }

    [Fact]
    public void Toast_WithoutSound_IsNotSilent()
    {
        Run(Notice("toast", "ねる時間"));

        Assert.Equal(["toast:shiftlock:ねる時間:sound"], _calls);
    }

    [Fact]
    public void Popup_Method_ShowsPopup()
    {
        Run(Notice("popup", "最低ライン", sound: @"C:\s\a.wav"));

        Assert.Equal([@"sound:C:\s\a.wav:0.5:15", "popup:shiftlock:最低ライン"], _calls);
    }

    [Fact]
    public void UnknownMethod_IsShownAsToast()
    {
        Run(Notice("banner", "知らない出し方"));

        Assert.Equal(["toast:shiftlock:知らない出し方:sound"], _calls);
    }

    [Fact]
    public void ToastThrows_FallsBackToPopup()
    {
        _notifier.OnToast = _ => throw new InvalidOperationException("toast が出せない");

        Run(Notice("toast", "ねる時間"));

        Assert.Equal(["toast:shiftlock:ねる時間:sound", "popup:shiftlock:ねる時間"], _calls);
        Assert.Contains(Log, l => l.Contains("toast が出せない", StringComparison.Ordinal));
    }

    [Fact]
    public void Speak_ComesLast_WithoutHearts()
    {
        Run(Notice("toast", "おやすみ♡ またね♡", speak: true, sound: @"C:\s\a.mp3"));

        Assert.Equal([@"sound:C:\s\a.mp3:0.5:15", "toast:shiftlock:おやすみ♡ またね♡:silent", "speak:おやすみ またね"], _calls);
    }

    [Fact]
    public void SoundThrows_NoticeIsStillShown()
    {
        _notifier.OnSound = _ => throw new InvalidOperationException("音が出せない");

        Run(Notice("toast", "ねる時間", speak: true, sound: @"C:\s\a.mp3"));

        Assert.Equal([@"sound:C:\s\a.mp3:0.5:15", "toast:shiftlock:ねる時間:sound", "speak:ねる時間"], _calls);
    }

    [Fact]
    public void EnforceLock_LocksOnce_WithoutShowingANotice()
    {
        Run(Enforce("lock"));

        Assert.Equal(["lock"], _calls);
        Assert.Contains(Log, l => l.EndsWith(" enforce: lock", StringComparison.Ordinal));
    }

    [Fact]
    public void EnforceSleep_AndShutdown()
    {
        Run(Enforce("sleep"), Enforce("shutdown"));

        Assert.Equal(["sleep", "shutdown:60"], _calls);
    }

    [Fact]
    public void Enforce_WithSound_PlaysBeforeRunning()
    {
        Run(Enforce("lock", sound: @"C:\s\a.mp3"));

        Assert.Equal([@"sound:C:\s\a.mp3:0.5:15", "lock"], _calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enforce_WithStopFile_DoesNothing_AndLogsOneLine(bool stopIsFolder)
    {
        if (stopIsFolder)
        {
            Directory.CreateDirectory(_dir.P("STOP.txt"));
        }
        else
        {
            _dir.Put("STOP.txt", "2026-12-07 11:00 kaijo\n");
        }

        Run(Enforce("lock", sound: @"C:\s\a.mp3"));

        Assert.Empty(_calls);   // 音も鳴らさない
        Assert.Single(Log, l => l.Contains("STOP.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Enforce_StopFilePlacedDuringSound_DoesNotRun()
    {
        _notifier.OnSound = _ => _dir.Put("STOP.txt", "2026-12-07 11:00 kaijo\n");

        Run(Enforce("shutdown", sound: @"C:\s\a.mp3"));

        Assert.Equal([@"sound:C:\s\a.mp3:0.5:15"], _calls);
        Assert.Single(Log, l => l.Contains("STOP.txt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hibernate")]
    [InlineData("")]
    [InlineData(null)]
    public void Enforce_UnknownAction_DoesNothing_AndLogs(string? action)
    {
        Run(new TickAction("enforce", "enforce", "shiftlock", "x", true, "", action));

        Assert.Empty(_calls);
        Assert.Contains(Log, l => l.Contains("unknown action", StringComparison.Ordinal));
    }

    [Fact]
    public void FailureInOneAction_DoesNotStopTheRest()
    {
        _notifier.OnToast = text =>
        {
            if (text == "2 件目")
            {
                throw new InvalidOperationException("だめ");
            }
        };
        _notifier.OnPopup = _ => throw new InvalidOperationException("popup もだめ");

        Run(Notice("toast", "1 件目"), Notice("toast", "2 件目", speak: true), Notice("toast", "3 件目"));

        Assert.Equal(
            ["toast:shiftlock:1 件目:sound", "toast:shiftlock:2 件目:sound", "popup:shiftlock:2 件目", "speak:2 件目", "toast:shiftlock:3 件目:sound"],
            _calls);
        Assert.Contains(Log, l => l.Contains("popup もだめ", StringComparison.Ordinal));
    }

    [Fact]
    public void PowerThrows_IsLogged_AndTheRestGoesOn()
    {
        var runner = new ActionRunner(_notifier, new ThrowingPower(), new Store(_dir.Dir), new AgentLog(_dir.P("agent.log")));

        runner.Run(new TickResult([Enforce("lock"), Notice("toast", "次")], [], 0.5, 15), CancellationToken.None);

        Assert.Equal(["toast:shiftlock:次:sound"], _calls);
        Assert.Contains(Log, l => l.Contains("enforce failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancel_AfterFirstAction_DropsTheRest()
    {
        using var cts = new CancellationTokenSource();
        _notifier.OnToast = _ => cts.Cancel();

        Run(cts.Token, Notice("toast", "1 件目", speak: true), Notice("toast", "2 件目"), Enforce("lock"));

        Assert.Equal(["toast:shiftlock:1 件目:sound"], _calls);   // 読み上げも出さない
    }

    [Fact]
    public void Cancel_DuringEnforceSound_DoesNotRun()
    {
        using var cts = new CancellationTokenSource();
        _notifier.OnSound = _ => cts.Cancel();

        Run(cts.Token, Enforce("lock", sound: @"C:\s\a.mp3"));

        Assert.Equal([@"sound:C:\s\a.mp3:0.5:15"], _calls);
    }

    [Fact]
    public void AlreadyCancelled_DoesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Run(cts.Token, Notice("toast", "1 件目"), Enforce("lock"));

        Assert.Empty(_calls);
    }

    [Fact]
    public void Order_IsTheListOrder()
    {
        Run(Notice("toast", "a"), Notice("popup", "b"), Enforce("lock"));

        Assert.Equal(["toast:shiftlock:a:sound", "popup:shiftlock:b", "lock"], _calls);
    }

    private sealed class ThrowingPower : IPowerControl
    {
        public void Lock() => throw new InvalidOperationException("lock できない");
        public void Sleep() => throw new InvalidOperationException();
        public void ScheduleShutdown(int seconds) => throw new InvalidOperationException();
        public void AbortShutdown() => throw new InvalidOperationException();
    }
}
