using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>緊急解除の結果．Text は知らせる文．Complete は全部書けたか（偽なら，消えない形で知らせる）．</summary>
public sealed record ReleaseResult(string Text, bool Complete);

/// <summary>
/// トレイの「緊急解除」（pc/guard.ps1 -Release と同じ）．一番急ぐ「予約済みのシャットダウンの中止」を先に行い，
/// 続けて STOP.txt・宣言の released_at・events.log（理由は tray）．各手順は独立で，どれかが失敗しても次へ進む．
/// 解除するだけで，宣言・再開・設定の変更はしない．
/// afterRelease は記録（STOP.txt・released_at）を書き，2 回目のシャットダウンの中止をした後に 1 回呼ぶ
/// （アプリの減速・一時停止をその場で戻す．10 秒の仕事を待たない．排他を待ちうるので最後）．
/// 例外は握ってログに書く（解除の結果は変わらない）．
/// </summary>
public sealed class ReleaseFlow(IPowerControl power, Store store, Func<DateTime> clock, AgentLog log, Action? afterRelease = null)
{
    /// <summary>events.log と STOP.txt に書く出所．</summary>
    public const string Reason = "tray";

    /// <summary>解除して，知らせる文を返す．例外は出さない．</summary>
    public string Run(Messages m) => RunDetailed(m).Text;

    /// <summary>解除して，知らせる文と「全部書けたか」を返す．例外は出さない．</summary>
    public ReleaseResult RunDetailed(Messages m)
    {
        AbortShutdown();
        var result = Record(m);
        // STOP.txt を置く直前に，見張りの側がシャットダウンを予約したかもしれない．置いた後にもう一度止める
        AbortShutdown();
        // 減速・一時停止の解除は最後（排他を最大 10 秒待ちうるので，シャットダウンの中止を遅らせない）
        AfterRelease();
        return result;
    }

    private void AbortShutdown()
    {
        try
        {
            power.AbortShutdown();
        }
        catch (Exception e)
        {
            log.Write($"release: abort shutdown failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void AfterRelease()
    {
        try
        {
            afterRelease?.Invoke();
        }
        catch (Exception e)
        {
            log.Write($"release: after-release failed: {e.GetType().Name}");
        }
    }

    private ReleaseResult Record(Messages m)
    {
        try
        {
            var text = Enforce.Release(store, m, clock(), Reason);
            log.Write("release: STOP.txt written, declarations released");
            return new ReleaseResult(text, true);
        }
        catch (Exception e)
        {
            log.Write($"release: record failed: {e.GetType().Name}: {e.Message}");
        }
        // 記録の一部が書けなかった．STOP.txt が在れば強制は止まる．無ければ手で置く場所を知らせる
        return new ReleaseResult(store.StopActive
            ? m["AGENT_RELEASE_PARTIAL"]
            : m.Format("AGENT_RELEASE_FAILED", ("path", store.P(Store.StopFile))), false);
    }
}
