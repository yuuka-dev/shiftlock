using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

public readonly record struct ProcKey(int Pid, DateTime Start);

/// <summary>プロセス 1 つの 1 回分の読み．Name は小文字（取れたままを小文字にしたもの）．Start はローカル時刻．Cpu はユーザー＋カーネル．</summary>
public sealed record ProcSample(int Pid, int ParentPid, DateTime Start, TimeSpan Cpu, string Name)
{
    public ProcKey Key => new(Pid, Start);
}

/// <summary>1 回のサンプル．Mono は単調な経過時間（スリープをまたいでも時計の変更に影響されない側）．</summary>
public sealed record MeterSample(DateTime Now, TimeSpan Mono, IReadOnlyList<ProcSample> Procs, int? ForegroundPid,
    double IdleSeconds, IReadOnlySet<ProcKey> Games, int Cores, bool HasParents = true);

/// <summary>T は行の時刻，Rec は t を除いた中身（Store.LogAppend("app", T, Rec) にそのまま渡せる）．</summary>
public sealed record MeterLine(DateTime T, JsonObject Rec);

/// <summary>
/// アプリの稼働を 5 分の区間に積み上げて log/app-*.jsonl の行にする（設計書 §4.2〜4.4）．
/// 純粋な計算だけ（Windows API は呼ばない．サンプルは Agent が集めて渡す）．
/// </summary>
public sealed class AppMeter(DateTime? lastWritten = null)
{
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(30);
    private const string OtherKey = "";   // 「その他」の内部キー（プロセス名は空にならないので衝突しない）

    private sealed class Acc
    {
        public string Cat = "game";
        public TimeSpan Cpu;
        public TimeSpan Act;
    }

    private DateTime? _lastWritten = lastWritten;
    private bool _seenTime;

    private TimeSpan? _prevMono;
    private DateTime _prevNow;
    private Dictionary<ProcKey, TimeSpan> _prevCpu = [];

    private DateTime? _bucket;
    private TimeSpan _span;
    private int _cores = 1;
    private readonly Dictionary<string, Acc> _apps = [];
    private TimeSpan _other;

    /// <summary>1 サンプルを足す．区切りをまたいだら前の区間の行を返す．</summary>
    public MeterLine? Add(MeterSample sample, MeterSettings settings)
    {
        IgnoreFutureLastWritten(sample.Now);
        _cores = Math.Max(1, sample.Cores);

        TimeSpan? dt = _prevMono is { } prev ? sample.Mono - prev : null;
        if (dt is { } d && (d <= TimeSpan.Zero || d >= MaxGap))
        {
            dt = null;   // 起点の取り直し
        }
        if (_prevMono is null)
        {
            dt = null;
        }

        // プロセスごとの CPU 時間の増分（起点の取り直しのときは持たない）
        var delta = new Dictionary<ProcKey, TimeSpan>();
        if (dt is { } step)
        {
            foreach (var p in sample.Procs)
            {
                if (_prevCpu.TryGetValue(p.Key, out var before))
                {
                    delta[p.Key] = p.Cpu > before ? p.Cpu - before : TimeSpan.Zero;
                }
                else
                {
                    delta[p.Key] = p.Start > _prevNow ? p.Cpu : TimeSpan.Zero;
                }
            }
            _span += step;
        }

        Accumulate(sample, settings, dt, delta);

        _prevMono = sample.Mono;
        _prevNow = sample.Now;
        _prevCpu = sample.Procs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Cpu);

        return Roll(Floor(sample.Now), settings);
    }

    /// <summary>たまっている分を行にする（終了時）．返したら区間を空にする（起点は残す）．</summary>
    public MeterLine? Flush(DateTime now)
    {
        IgnoreFutureLastWritten(now);
        var t = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Kind);
        return Emit(t, null);
    }

    /// <summary>起点もたまった分も捨てる（設定が読めない間など）．最後に書いた t は残す．</summary>
    public void Reset()
    {
        _prevMono = null;
        _prevCpu = [];
        _bucket = null;
        ClearInterval();
    }

    // ---- 積み上げ ----

    private void Accumulate(MeterSample s, MeterSettings settings, TimeSpan? dt, Dictionary<ProcKey, TimeSpan> delta)
    {
        // 名前つきのアプリ（名前 → 根のプロセス）．登録が先，登録に無ければ Games にあるものをゲームとして
        var roots = new Dictionary<string, List<ProcSample>>(StringComparer.Ordinal);
        foreach (var p in s.Procs)
        {
            if (settings.Registered.ContainsKey(p.Name) || s.Games.Contains(p.Key))
            {
                if (!roots.TryGetValue(p.Name, out var list))
                {
                    roots[p.Name] = list = [];
                }
                list.Add(p);
            }
        }

        var children = s.HasParents ? ProcTree.ChildrenOf(s.Procs) : null;
        var trees = new Dictionary<string, List<ProcSample>>(StringComparer.Ordinal);
        foreach (var (name, list) in roots)
        {
            var acc = AppAcc(name, settings);
            var tree = ProcTree.Descend(list, children);
            trees[name] = tree;
            acc.Cpu += Sum(tree, delta);   // 起点の取り直しのときは delta が空なので 0
        }

        if (dt is not { } step || s.ForegroundPid is not { } fg || s.Procs.FirstOrDefault(p => p.Pid == fg) is not { } front)
        {
            return;
        }
        if (settings.GameIdle is { } limit && s.IdleSeconds >= limit.TotalSeconds)
        {
            return;
        }

        // 前面のプロセス自身の名前で，そのアプリか「その他」か
        string key;
        List<ProcSample> frontTree;
        if (trees.TryGetValue(front.Name, out var appTree)
            && (settings.Registered.ContainsKey(front.Name) || s.Games.Contains(front.Key)))
        {
            key = front.Name;
            frontTree = appTree;
        }
        else
        {
            key = OtherKey;
            frontTree = ProcTree.Descend([front], children);
        }

        var busy = Sum(frontTree, delta).TotalSeconds / (step.TotalSeconds * _cores) * 100 >= settings.ActiveCpuPercent;
        if (s.IdleSeconds < settings.ActiveInput.TotalSeconds || busy)
        {
            if (key == OtherKey)
            {
                _other += step;
            }
            else
            {
                _apps[key].Act += step;
            }
        }
    }

    private Acc AppAcc(string name, MeterSettings settings)
    {
        if (!_apps.TryGetValue(name, out var acc))
        {
            _apps[name] = acc = new Acc();
        }
        acc.Cat = settings.Registered.TryGetValue(name, out var cat) ? cat : "game";
        return acc;
    }

    private static TimeSpan Sum(List<ProcSample> tree, Dictionary<ProcKey, TimeSpan> delta)
    {
        var total = TimeSpan.Zero;
        foreach (var p in tree)
        {
            total += delta.GetValueOrDefault(p.Key);
        }
        return total;
    }

    // ---- 区切りと行 ----

    private static DateTime Floor(DateTime t) =>
        new(t.Ticks - t.Ticks % Bucket.Ticks, t.Kind);

    private MeterLine? Roll(DateTime bucket, MeterSettings settings)
    {
        if (_bucket is not { } current)
        {
            _bucket = bucket;
            return null;
        }
        if (bucket < current)
        {
            _bucket = bucket;   // 時計が戻った．行は作らず，たまった分は持ち越す
            return null;
        }
        if (bucket == current)
        {
            return null;
        }
        _bucket = bucket;
        return Emit(current + Bucket, settings);
    }

    private MeterLine? Emit(DateTime t, MeterSettings? settings)
    {
        var span = (int)Math.Round(_span.TotalSeconds, MidpointRounding.AwayFromZero);
        if (span <= 0)
        {
            ClearInterval();
            return null;
        }
        if (_lastWritten is { } last && t <= last)
        {
            return null;   // 同じ t を二度書かない．たまった分は次の区間に合算
        }

        var apps = new JsonObject();
        foreach (var name in _apps.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var acc = _apps[name];
            // カテゴリは行を作る時点の設定．設定に無くなっていれば最後に見たもの
            var cat = settings is not null && settings.Registered.TryGetValue(name, out var c) ? c : acc.Cat;
            var pct = Math.Round(acc.Cpu.TotalSeconds / (span * (double)_cores) * 100, 1, MidpointRounding.AwayFromZero);
            apps[name] = new JsonObject
            {
                ["cat"] = cat,
                ["act"] = Whole(acc.Act),
                ["cpu"] = JsonNode.Parse(pct.ToString("0.0", CultureInfo.InvariantCulture)),
            };
        }
        var rec = new JsonObject { ["span"] = span, ["apps"] = apps, ["other"] = Whole(_other) };

        _lastWritten = t;
        ClearInterval();
        return new MeterLine(t, rec);
    }

    private static int Whole(TimeSpan t) => (int)Math.Round(t.TotalSeconds, MidpointRounding.AwayFromZero);

    private void ClearInterval()
    {
        _span = TimeSpan.Zero;
        _other = TimeSpan.Zero;
        _apps.Clear();
    }

    /// <summary>
    /// 最初の Add／Flush の時刻の枠（5 分）の終わりより後の lastWritten は無視する（別の日の値・時計が大きく戻った値など）．
    /// 枠の終わりちょうどまでは有効（再起動直後に同じ t をもう一度書かないため）．
    /// </summary>
    private void IgnoreFutureLastWritten(DateTime now)
    {
        if (_seenTime)
        {
            return;
        }
        _seenTime = true;
        if (_lastWritten is { } last && last > Floor(now) + Bucket)
        {
            _lastWritten = null;
        }
    }
}
