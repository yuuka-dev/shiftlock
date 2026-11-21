using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>
/// 切り替え（--install / --uninstall）．Python 版のタスクと Agent が同時に見張る状態（二重の見張り）を残さないこと．
/// 登録先とタスクは偽物（本番のタスクには触らない）．
/// </summary>
public sealed class SetupTests
{
    private const string Exe = @"C:\Users\x\AppData\Local\Programs\shiftlock\shiftlock-agent.exe";

    private readonly List<string> _calls = [];
    private readonly List<string> _out = [];
    private readonly FakeAutoStart _auto;
    private readonly FakeGuard _guard;
    private readonly Messages _m = Messages.Load("ja");
    private Action<string>? _onStart;
    private bool _stopResult = true;

    public SetupTests()
    {
        _auto = new FakeAutoStart(_calls);
        _guard = new FakeGuard(_calls);
    }

    private Setup NewSetup() => new(new SetupDeps(_auto, _guard,
        RegisterToast: () => _calls.Add("toast:on"),
        UnregisterToast: () => _calls.Add("toast:off"),
        StartAgent: exe =>
        {
            _calls.Add("start:" + exe);
            _onStart?.Invoke(exe);
        },
        StopAgent: () =>
        {
            _calls.Add("stop");
            return _stopResult;
        },
        Output: _out.Add), _m, "shiftlock-guard");

    [Fact]
    public void Install_Registers_DisablesTheTask_ThenStarts()
    {
        Assert.Equal(0, NewSetup().Install(Exe));

        Assert.Equal(["auto:set", "toast:on", "task:off", "start:" + Exe], _calls);
        Assert.Equal($"\"{Exe}\"", _auto.Value);
        Assert.Equal(GuardTaskState.Disabled, _guard.Current);
        Assert.Equal(
            [_m["AGENT_SETUP_AUTOSTART_ON"], _m.Format("AGENT_SETUP_TASK_DISABLED", ("task", "shiftlock-guard")), _m["AGENT_SETUP_STARTED"]],
            _out);
    }

    [Fact]
    public void Install_TaskMissing_SkipsIt_AndSucceeds()
    {
        _guard.Current = GuardTaskState.Missing;

        Assert.Equal(0, NewSetup().Install(Exe));

        Assert.Equal(["auto:set", "toast:on", "start:" + Exe], _calls);
        Assert.Contains(_m.Format("AGENT_SETUP_TASK_MISSING", ("task", "shiftlock-guard")), _out);
    }

    [Fact]
    public void Install_TaskAlreadyDisabled_Succeeds()
    {
        _guard.Current = GuardTaskState.Disabled;

        Assert.Equal(0, NewSetup().Install(Exe));

        Assert.Equal(GuardTaskState.Disabled, _guard.Current);
        Assert.Contains("start:" + Exe, _calls);
    }

    [Fact]
    public void Install_CannotDisableTheTask_Fails_AndLeavesNoAutoStart()
    {
        // 半端に終わると，次のログオンから Agent と Python 版の両方が見張ることになる
        _guard.FailOnSet = true;

        Assert.Equal(1, NewSetup().Install(Exe));

        Assert.Null(_auto.Value);
        Assert.Equal(GuardTaskState.Enabled, _guard.Current);
        Assert.DoesNotContain(_calls, c => c.StartsWith("start:", StringComparison.Ordinal));
        Assert.Contains(_out, l => l.StartsWith(_m.Format("AGENT_SETUP_TASK_FAILED", ("task", "shiftlock-guard"), ("err", "")), StringComparison.Ordinal));
        Assert.Contains(_m["AGENT_SETUP_ROLLED_BACK"], _out);
    }

    [Fact]
    public void Install_TaskStillEnabledAfterDisabling_Fails()
    {
        // 無効にしたはずなのに有効のまま（確かめ直して気付く）
        _guard.IgnoreSet = true;

        Assert.Equal(1, NewSetup().Install(Exe));

        Assert.Null(_auto.Value);
        Assert.DoesNotContain(_calls, c => c.StartsWith("start:", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_TaskStateUnknown_Fails_AndLeavesNoAutoStart()
    {
        _guard.Current = GuardTaskState.Unknown;
        _guard.FailOnSet = true;

        Assert.Equal(1, NewSetup().Install(Exe));

        Assert.Null(_auto.Value);
    }

    [Fact]
    public void Install_Twice_KeepsOneRegistration()
    {
        NewSetup().Install(Exe);
        Assert.Equal(0, NewSetup().Install(Exe));

        Assert.Equal(1, _auto.Count);
        Assert.Equal($"\"{Exe}\"", _auto.Value);
    }

    [Fact]
    public void Install_AutoStartCannotBeWritten_Fails_BeforeTouchingTheTask()
    {
        _auto.FailOnSet = true;

        Assert.Equal(1, NewSetup().Install(Exe));

        Assert.Equal(GuardTaskState.Enabled, _guard.Current);
        Assert.DoesNotContain("task:off", _calls);
    }

    [Fact]
    public void Install_AgentCannotStart_Fails_ButKeepsTheSwitch()
    {
        // 登録は済んでいるので次のログオンからは動く．今は動いていないことを知らせる
        _onStart = _ => throw new InvalidOperationException("起動できない");

        Assert.Equal(1, NewSetup().Install(Exe));

        Assert.NotNull(_auto.Value);
        Assert.Equal(GuardTaskState.Disabled, _guard.Current);
        Assert.Contains(_out, l => l.Contains("起動できない", StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_StopsTheAgent_RemovesRegistration_EnablesTheTask()
    {
        NewSetup().Install(Exe);
        _calls.Clear();
        _out.Clear();

        Assert.Equal(0, NewSetup().Uninstall());

        Assert.Equal(["stop", "auto:remove", "toast:off", "task:on"], _calls);
        Assert.Null(_auto.Value);
        Assert.Equal(GuardTaskState.Enabled, _guard.Current);
        Assert.Equal(
            [_m["AGENT_SETUP_STOPPED"], _m["AGENT_SETUP_AUTOSTART_OFF"], _m.Format("AGENT_SETUP_TASK_ENABLED", ("task", "shiftlock-guard"))],
            _out);
    }

    [Fact]
    public void Uninstall_TaskMissing_SkipsIt()
    {
        _guard.Current = GuardTaskState.Missing;

        Assert.Equal(0, NewSetup().Uninstall());

        Assert.DoesNotContain("task:on", _calls);
    }

    [Fact]
    public void Uninstall_CannotEnableTheTask_Fails_AndSaysSo()
    {
        // Agent は止めたのにタスクが無効のまま = 何も見張っていない．失敗として知らせる
        _guard.Current = GuardTaskState.Disabled;
        _guard.FailOnSet = true;

        Assert.Equal(1, NewSetup().Uninstall());

        Assert.Contains(_out, l => l.StartsWith(_m.Format("AGENT_SETUP_TASK_FAILED", ("task", "shiftlock-guard"), ("err", "")), StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_AgentWasNotRunning_SaysNothingAboutStopping()
    {
        _stopResult = false;

        Assert.Equal(0, NewSetup().Uninstall());

        Assert.DoesNotContain(_m["AGENT_SETUP_STOPPED"], _out);
    }

    [Fact]
    public void QuoteCommand_QuotesThePath()
    {
        Assert.Equal("\"C:\\Program Files\\x\\shiftlock-agent.exe\"", Setup.Command(@"C:\Program Files\x\shiftlock-agent.exe"));
    }

    private sealed class FakeAutoStart(List<string> calls) : IAutoStart
    {
        public string? Value { get; private set; }
        public int Count => Value is null ? 0 : 1;
        public bool FailOnSet { get; set; }

        public string? Current() => Value;

        public void Set(string command)
        {
            if (FailOnSet)
            {
                throw new UnauthorizedAccessException("書けない");
            }
            calls.Add("auto:set");
            Value = command;
        }

        public void Remove()
        {
            calls.Add("auto:remove");
            Value = null;
        }
    }

    private sealed class FakeGuard(List<string> calls) : IGuardTaskControl
    {
        public GuardTaskState Current { get; set; } = GuardTaskState.Enabled;
        public bool FailOnSet { get; set; }
        public bool IgnoreSet { get; set; }

        public GuardTaskState State() => Current;

        public void SetEnabled(bool enabled)
        {
            if (FailOnSet)
            {
                throw new UnauthorizedAccessException("アクセスが拒否された");
            }
            calls.Add(enabled ? "task:on" : "task:off");
            if (!IgnoreSet)
            {
                Current = enabled ? GuardTaskState.Enabled : GuardTaskState.Disabled;
            }
        }
    }
}
