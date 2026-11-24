namespace Shiftlock.Agent;

/// <summary>周期の仕事 1 件．FirstDelay 後に 1 回目，以後 Interval おきに Run を呼ぶ（UI でないスレッド）．</summary>
public sealed record Job(string Name, TimeSpan FirstDelay, TimeSpan Interval, Action Run);

/// <summary>
/// 仕事の一覧（名前・間隔・処理）をタイマーで回す．周期の違う仕事を後から足せるようにしてある（今は 5 分の見張りと，10 秒の稼働計測 meter）．
/// 仕事の例外はログに書いて握る（次の回は普通に呼ぶ）．前の回が終わっていないときにどうするかは仕事の側が決める
/// （見張りは Watcher が Busy で飛ばす）．
/// </summary>
public sealed class Scheduler(IReadOnlyList<Job> jobs, AgentLog log) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<System.Threading.Timer> _timers = [];
    private readonly Dictionary<string, System.Threading.Timer> _kicks = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>全部の仕事のタイマーを始める．</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timers.Count > 0)
            {
                return;
            }
            foreach (var job in jobs)
            {
                _timers.Add(new System.Threading.Timer(_ => Invoke(job), null, job.FirstDelay, job.Interval));
            }
        }
    }

    /// <summary>
    /// 名前の仕事を，周期とは別に delay 後に 1 回呼ぶ（スリープからの復帰など）．待っている間にもう一度呼ばれたら数え直す．
    /// 無い名前は何もしない．
    /// </summary>
    public void Kick(string name, TimeSpan delay)
    {
        var job = jobs.FirstOrDefault(j => j.Name == name);
        if (job is null)
        {
            return;
        }
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_kicks.Remove(name, out var old))
            {
                old.Dispose();
            }
            _kicks[name] = new System.Threading.Timer(_ => Invoke(job), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var t in _timers.Concat(_kicks.Values))
            {
                t.Dispose();
            }
            _timers.Clear();
            _kicks.Clear();
        }
    }

    private void Invoke(Job job)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            job.Run();
        }
        catch (Exception e)
        {
            // タイマーのスレッドで例外を外に出すとプロセスが落ちる
            log.Write($"job {job.Name} failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
