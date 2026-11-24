namespace Shiftlock.Core;

/// <summary>
/// プロセスのツリー（根と子孫）の組み立て．稼働計測（AppMeter）と減速の対象の決定（LimitTargets）が同じ決まりで使う．
/// 親の開始時刻が子より後のものは親子とみなさない（PID の使い回し）．
/// </summary>
public static class ProcTree
{
    /// <summary>名前が exeName のプロセス（根）と，その子孫．同じプロセスは 1 回．exeName は小文字（ProcSample.Name と同じ）．</summary>
    public static IReadOnlyList<ProcSample> OfName(IReadOnlyList<ProcSample> procs, string exeName)
    {
        var roots = procs.Where(p => p.Name == exeName).ToList();
        return Descend(roots, ChildrenOf(procs));
    }

    /// <summary>親の pid → 子（親が一覧にあり，親の Start ≤ 子の Start のものだけ）．</summary>
    internal static Dictionary<int, List<ProcSample>> ChildrenOf(IReadOnlyList<ProcSample> procs)
    {
        var byPid = new Dictionary<int, ProcSample>();
        foreach (var p in procs)
        {
            byPid.TryAdd(p.Pid, p);
        }
        var map = new Dictionary<int, List<ProcSample>>();
        foreach (var c in procs)
        {
            if (byPid.TryGetValue(c.ParentPid, out var parent) && parent.Start <= c.Start && parent.Pid != c.Pid)
            {
                if (!map.TryGetValue(parent.Pid, out var list))
                {
                    map[parent.Pid] = list = [];
                }
                list.Add(c);
            }
        }
        return map;
    }

    /// <summary>根と子孫．同じプロセスは 1 回だけ．children が null なら根だけ．</summary>
    internal static List<ProcSample> Descend(List<ProcSample> roots, Dictionary<int, List<ProcSample>>? children)
    {
        var seen = new HashSet<ProcKey>();
        var result = new List<ProcSample>();
        var stack = new Stack<ProcSample>(roots);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p.Key))
            {
                continue;
            }
            result.Add(p);
            if (children is not null && children.TryGetValue(p.Pid, out var kids))
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }
        return result;
    }
}
