using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>全プロセスの 1 回分の一覧．HasParents は ParentPid が本当の値か（取れない経路では 0 で false）．</summary>
public sealed record ProcessSnapshot(IReadOnlyList<ProcSample> Procs, bool HasParents);

/// <summary>全プロセスの一覧．取れなければ例外（呼ぶ側が握る）．</summary>
public interface IProcessSource
{
    ProcessSnapshot Snapshot();
}
