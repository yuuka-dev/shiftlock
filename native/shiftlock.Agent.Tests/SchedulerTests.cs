namespace Shiftlock.Agent.Tests;

/// <summary>仕事の一覧（名前・間隔・処理）を回すタイマー．仕事の例外で止まらないこと．</summary>
public sealed class SchedulerTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private AgentLog Log => new(_dir.P("agent.log"));

    [Fact]
    public void Start_RunsEachJob_Repeatedly()
    {
        using var a = new CountdownEvent(3);
        using var b = new CountdownEvent(2);
        using var s = new Scheduler(
            [new Job("a", Short, Short, () => Signal(a)), new Job("b", Short, Short, () => Signal(b))], Log);

        s.Start();

        Assert.True(a.Wait(Wait));
        Assert.True(b.Wait(Wait));
    }

    [Fact]
    public void JobThatThrows_IsLogged_AndRunsAgain()
    {
        using var runs = new CountdownEvent(3);
        using var s = new Scheduler([new Job("こわれる", Short, Short, () =>
        {
            Signal(runs);
            throw new InvalidOperationException("だめ");
        })], Log);

        s.Start();

        Assert.True(runs.Wait(Wait));
        Assert.True(SpinWait.SpinUntil(() => _dir.Lines("agent.log").Any(l => l.Contains("こわれる") && l.Contains("だめ")), Wait));
    }

    [Fact]
    public void Kick_RunsTheNamedJobOnce_WithoutWaitingForTheInterval()
    {
        var count = 0;
        using var ran = new ManualResetEventSlim();
        using var s = new Scheduler([new Job("見張り", TimeSpan.FromHours(1), TimeSpan.FromHours(1), () =>
        {
            Interlocked.Increment(ref count);
            ran.Set();
        })], Log);
        s.Start();

        s.Kick("見張り", Short);
        s.Kick("知らない名前", Short);   // 無い名前は何もしない

        Assert.True(ran.Wait(Wait));
        Thread.Sleep(200);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dispose_StopsTheJobs()
    {
        var count = 0;
        var s = new Scheduler([new Job("a", Short, Short, () => Interlocked.Increment(ref count))], Log);
        s.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref count) > 0, Wait));

        s.Dispose();
        Thread.Sleep(100);   // 走りかけの 1 回が終わるのを待つ
        var after = Volatile.Read(ref count);
        Thread.Sleep(200);

        Assert.Equal(after, Volatile.Read(ref count));
    }

    private static void Signal(CountdownEvent e)
    {
        if (!e.IsSet)
        {
            try
            {
                e.Signal();
            }
            catch (InvalidOperationException)
            {
                // 数え終わった後にもう 1 回来た
            }
        }
    }
}
