using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>プロセスに手を加える唯一の場所．どれも例外を出さず，できなければ false．開始時刻が違うプロセスには触らない．</summary>
public interface ILimitControl
{
    /// <summary>procs をジョブ jobName に入れ（既に入っているものはそのまま），上限 percent（PC 全体 = 100）をかける．1 つでも入り上限がかかれば true．</summary>
    bool Cap(string jobName, IReadOnlyList<ProcKey> procs, double percent);

    /// <summary>ジョブの上限を外す．この Agent が持っているジョブだけ（名前で開き直さない）．持っていない・外せない → false．</summary>
    bool Uncap(string jobName);

    bool Suspend(ProcKey proc);

    bool Resume(ProcKey proc);

    /// <summary>同じ pid・同じ開始時刻のプロセスが今も在るか．</summary>
    bool Exists(ProcKey proc);
}
