namespace Shiftlock.Agent;

/// <summary>
/// PC を止める側（テストで差し替える）．呼ぶのは ActionRunner（見張りが返した，本人が同意済みの強制の実行）と，
/// 緊急解除の AbortShutdown だけ．ほかから呼ぶ入口を作らない．
/// </summary>
public interface IPowerControl
{
    /// <summary>画面のロック．</summary>
    void Lock();

    /// <summary>スリープ．</summary>
    void Sleep();

    /// <summary>seconds 秒後のシャットダウンを予約する（AbortShutdown で中止できる）．</summary>
    void ScheduleShutdown(int seconds);

    /// <summary>予約済みのシャットダウンを中止する．予約が無くても失敗にしない．</summary>
    void AbortShutdown();
}
