using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 1 回分のサンプルを集める（Meter と MeterOnce が共有）．プロセスの exe のパスは (pid, 開始時刻) ごとに 1 回だけ聞いて覚える．
/// 覚えた入れ物は，今回の一覧に無いプロセスを毎回捨てる．
/// </summary>
internal sealed class MeterSampler(IProcessSource source, IMeterProbe meterProbe, ISystemProbe probe)
{
    private Dictionary<ProcKey, string?> _paths = [];

    /// <summary>Snapshot が例外なら，そのまま出す（呼ぶ側が握る）．</summary>
    public MeterSample Collect(DateTime now, TimeSpan mono, GameRules rules)
    {
        var snapshot = source.Snapshot();
        var next = new Dictionary<ProcKey, string?>();
        var games = new HashSet<ProcKey>();
        foreach (var p in snapshot.Procs)
        {
            if (!next.TryGetValue(p.Key, out var path))
            {
                if (!_paths.TryGetValue(p.Key, out path))
                {
                    path = meterProbe.ImagePath(p.Pid);
                }
                next[p.Key] = path;
            }
            var name = p.Name.EndsWith(".exe", StringComparison.Ordinal) ? p.Name[..^4] : p.Name;
            if (Games.IsGame(rules, name, path))
            {
                games.Add(p.Key);
            }
        }
        _paths = next;
        return new MeterSample(now, mono, snapshot.Procs, meterProbe.ForegroundPid(), probe.IdleSeconds(), games,
            Environment.ProcessorCount, snapshot.HasParents);
    }
}

/// <summary>
/// アプリの稼働計測の 1 回（10 秒おき）: プロセスの一覧と前面のプロセスを取り，AppMeter に渡して，5 分の区切りで
/// log/app-YYYY-MM.jsonl に行を足す．Watcher（5 分の見張り）とは別で，Python 版のタスクの状態も見ない．
/// 例外は外に出さない（Agent を落とさない）．同じ失敗は 1 日 1 回だけ agent.log に書く．
/// 登録していないアプリの名前は，ログにも例外の文にも出さない（出すのは型と，名前を含まない文だけ）．
/// 設定（shifts.json）を読むのは，読めていないとき（5 分の枠が変わるごと）と，行を返した直後の回だけ．
/// </summary>
public sealed class Meter(string dir, IProcessSource source, IMeterProbe meterProbe, ISystemProbe probe,
    Func<DateTime> clock, Func<TimeSpan> mono, OncePerDay once)
{
    /// <summary>計測の間隔．</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MeterSampler _sampler = new(source, meterProbe, probe);
    private AppMeter? _meter;
    private MeterSettings? _settings;
    private GameRules? _rules;
    private DateTime? _failedBucket;

    /// <summary>1 回測る．前の回（か Flush）が動いていたら何もしない．</summary>
    public void RunOnce()
    {
        if (!_gate.Wait(0))
        {
            return;
        }
        try
        {
            Run();
        }
        catch (Exception e)
        {
            // 文は入れない（何が入るか分からない）．型だけ書く
            once.Write("meter-run", $"meter failed: {e.GetType().Name}", DateOnly.FromDateTime(clock()));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>たまっている分を書く（終了時）．動いている回があれば 2 秒まで待ち，それでも動いていれば諦める．</summary>
    public void Flush()
    {
        if (!_gate.Wait(FlushWait))
        {
            return;
        }
        try
        {
            if (_meter is not null)
            {
                Write(_meter.Flush(clock()));
            }
        }
        catch (Exception e)
        {
            once.Write("meter-run", $"meter failed: {e.GetType().Name}", DateOnly.FromDateTime(clock()));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Run()
    {
        var now = clock();
        var today = DateOnly.FromDateTime(now);
        _meter ??= new AppMeter(new Store(dir).LastLogTime("app"));

        if (_settings is null && _failedBucket == Floor(now))
        {
            return;   // 読めなかった枠の間は読み直さない（10 秒ごとに読まない）
        }
        if (_settings is null && !ReadSettings(now, today))
        {
            return;
        }

        MeterSample sample;
        try
        {
            sample = _sampler.Collect(now, mono(), _rules!);
        }
        catch (Exception e)
        {
            once.Write("meter-source", $"meter failed: {e.GetType().Name}: {e.Message}", today);
            return;
        }

        var line = _meter.Add(sample, _settings!);
        if (line is not null)
        {
            _settings = null;   // 区間の頭．次の回で設定を読み直す
            Write(line);
        }
    }

    private bool ReadSettings(DateTime now, DateOnly today)
    {
        var (cfg, _, error) = Watcher.LoadConfig(dir);
        try
        {
            if (cfg is null)
            {
                Fail(now, today, error);
                return false;
            }
            // 両方作れてから代入する（片方だけ入ると次の回で読み直されず _rules が null のまま落ちる）
            var settings = MeterSettings.Read(cfg);
            var rules = Games.Rules(cfg, probe.SteamCommonDirs());
            _settings = settings;
            _rules = rules;
            _failedBucket = null;
            return true;
        }
        catch (ConfigException e)
        {
            Fail(now, today, e.Message);
            return false;
        }
    }

    private void Fail(DateTime now, DateOnly today, string? why)
    {
        _failedBucket = Floor(now);
        _meter!.Reset();
        once.Write("meter-config", "meter: not measuring: " + why, today);
    }

    private void Write(MeterLine? line)
    {
        if (line is null)
        {
            return;
        }
        try
        {
            new Store(dir).LogAppend("app", line.T, line.Rec);
        }
        catch (StateBusyException e)
        {
            // この行は諦める（やり直すと同じ t を二度書きうる）
            once.Write("meter-write", $"meter write failed: {e.Message}", DateOnly.FromDateTime(clock()));
        }
    }

    private static DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % Bucket.Ticks, t.Kind);
}
