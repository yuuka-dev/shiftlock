using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// トレイの「緊急解除」（pc/guard.ps1 -Release と同じ）．一番急ぐ「予約済みのシャットダウンの中止」を先に行い，
/// 続けて STOP.txt・宣言の released_at・events.log（理由は tray）．各手順は独立で，どれかが失敗しても次へ進む．
/// 解除するだけで，宣言・再開・設定の変更はしない．
/// </summary>
public sealed class ReleaseFlow(IPowerControl power, Store store, Func<DateTime> clock, AgentLog log)
{
    /// <summary>events.log と STOP.txt に書く出所．</summary>
    public const string Reason = "tray";

    /// <summary>解除して，知らせる文を返す．例外は出さない．</summary>
    public string Run(Messages m)
    {
        try
        {
            power.AbortShutdown();
        }
        catch (Exception e)
        {
            log.Write($"release: abort shutdown failed: {e.GetType().Name}: {e.Message}");
        }
        try
        {
            var text = Enforce.Release(store, m, clock(), Reason);
            log.Write("release: STOP.txt written, declarations released");
            return text;
        }
        catch (Exception e)
        {
            log.Write($"release: record failed: {e.GetType().Name}: {e.Message}");
        }
        // 記録の一部が書けなかった．STOP.txt が在れば強制は止まる．無ければ手で置く場所を知らせる
        return store.StopActive
            ? m["AGENT_RELEASE_PARTIAL"]
            : m.Format("AGENT_RELEASE_FAILED", ("path", store.P(Store.StopFile)));
    }
}
