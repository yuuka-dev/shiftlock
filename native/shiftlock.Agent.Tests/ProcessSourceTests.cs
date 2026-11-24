using System.Diagnostics;
using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>プロセスの一覧．実機のテストは自分のテストホストのプロセスだけを確かめる（ほかのプロセスの名前は見ない・出さない）．</summary>
public sealed class ProcessSourceTests
{
    public static TheoryData<IProcessSource> RealSources() => new() { new NtProcessSource(), new DiagnosticsProcessSource() };

    private sealed class FakeSource(Func<ProcessSnapshot> next) : IProcessSource
    {
        public int Calls { get; private set; }

        public ProcessSnapshot Snapshot()
        {
            Calls++;
            return next();
        }
    }

    private static ProcessSnapshot Snap(int pid) =>
        new([new ProcSample(pid, 0, new DateTime(2026, 12, 1, 9, 0, 0), TimeSpan.Zero, "code.exe")], false);

    [Theory]
    [MemberData(nameof(RealSources))]
    public void Snapshot_ContainsThisProcess(IProcessSource source)
    {
        using var me = Process.GetCurrentProcess();
        var snap = source.Snapshot();
        var p = Assert.Single(snap.Procs, x => x.Pid == me.Id);
        Assert.Equal((me.ProcessName + ".exe").ToLowerInvariant(), p.Name);
        Assert.True((p.Start - me.StartTime).Duration() < TimeSpan.FromSeconds(1));
        Assert.True(p.Cpu > TimeSpan.Zero);
        Assert.All(snap.Procs, x => Assert.NotEqual("", x.Name));
    }

    [Fact]
    public void Nt_HasParents_AndThisProcessHasAParentPid()
    {
        var snap = new NtProcessSource().Snapshot();
        Assert.True(snap.HasParents);
        Assert.NotEqual(0, snap.Procs.Single(x => x.Pid == Environment.ProcessId).ParentPid);
    }

    [Fact]
    public void Diagnostics_HasNoParents()
    {
        var snap = new DiagnosticsProcessSource().Snapshot();
        Assert.False(snap.HasParents);
        Assert.All(snap.Procs, x => Assert.Equal(0, x.ParentPid));
    }

    [Fact]
    public void Nt_CpuGrows_BetweenSnapshots()
    {
        var source = new NtProcessSource();
        var before = source.Snapshot().Procs.Single(x => x.Pid == Environment.ProcessId).Cpu;
        var sw = Stopwatch.StartNew();
        var sink = 0L;
        while (sw.ElapsedMilliseconds < 200)
        {
            sink += Environment.TickCount64 & 1;
        }
        Assert.True(sink >= 0);
        var after = source.Snapshot().Procs.Single(x => x.Pid == Environment.ProcessId).Cpu;
        Assert.True(after > before);
    }

    [Fact]
    public void Fallback_PrimaryOk_UsesPrimary()
    {
        using var t = new TempDir();
        var primary = new FakeSource(() => Snap(1));
        var secondary = new FakeSource(() => Snap(2));
        var source = new FallbackProcessSource(primary, secondary, new OncePerDay(new AgentLog(t.P("agent.log"))), () => new DateTime(2026, 12, 1));

        Assert.Equal(1, source.Snapshot().Procs[0].Pid);
        Assert.Equal(0, secondary.Calls);
        Assert.Empty(t.Lines("agent.log"));
    }

    [Fact]
    public void Fallback_PrimaryThrows_UsesSecondary_AndLogsOncePerDay()
    {
        using var t = new TempDir();
        var primary = new FakeSource(() => throw new InvalidOperationException("NtQuerySystemInformation failed: 0xC0000005"));
        var secondary = new FakeSource(() => Snap(2));
        var day = new DateTime(2026, 12, 1, 9, 0, 0);
        var source = new FallbackProcessSource(primary, secondary, new OncePerDay(new AgentLog(t.P("agent.log"))), () => day);

        Assert.Equal(2, source.Snapshot().Procs[0].Pid);
        Assert.Equal(2, source.Snapshot().Procs[0].Pid);
        Assert.Equal(2, primary.Calls);   // 次の回もまず primary から
        Assert.Single(t.Lines("agent.log"), l => l.Contains("process list fell back", StringComparison.Ordinal));

        day = day.AddDays(1);
        source.Snapshot();
        Assert.Equal(2, t.Lines("agent.log").Count(l => l.Contains("process list fell back", StringComparison.Ordinal)));
    }

    [Fact]
    public void Fallback_BothThrow_Throws()
    {
        using var t = new TempDir();
        var primary = new FakeSource(() => throw new InvalidOperationException("a"));
        var secondary = new FakeSource(() => throw new InvalidOperationException("b"));
        var source = new FallbackProcessSource(primary, secondary, new OncePerDay(new AgentLog(t.P("agent.log"))), () => new DateTime(2026, 12, 1));

        var e = Assert.Throws<InvalidOperationException>(source.Snapshot);
        Assert.Equal("b", e.Message);
    }

    [Fact]
    public void OncePerDay_SameKeySameDay_WritesOnce_DifferentKeyOrDay_WritesAgain()
    {
        using var t = new TempDir();
        var once = new OncePerDay(new AgentLog(t.P("agent.log")));
        var d1 = new DateOnly(2026, 12, 1);

        once.Write("a", "one", d1);
        once.Write("a", "two", d1);
        Assert.Single(t.Lines("agent.log"));

        once.Write("b", "three", d1);
        Assert.Equal(2, t.Lines("agent.log").Length);

        once.Write("a", "four", d1.AddDays(1));
        Assert.Equal(3, t.Lines("agent.log").Length);
    }
}
