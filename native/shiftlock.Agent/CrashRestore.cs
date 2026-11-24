namespace Shiftlock.Agent;

/// <summary>
/// 未処理の例外で Agent が落ちるとき，止めたまま・上限をかけたままにしないための受け口の中身（設計書 §6.3 の「代わりの案」）．
/// 受け口（AppDomain.UnhandledException）から呼ぶ．戻す処理で例外が出ても握る（落ちる処理の邪魔をしない）．
/// ログには例外の型だけ書く（文にはアプリの名前を入れない）．
/// </summary>
public static class CrashRestore
{
    /// <summary>受け口の中で排他を待つ最大．通常の 10 秒より短くする（落ちる処理を長く引き止めない）．</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    /// <summary>restore を 1 回呼ぶ．例外は握ってログに書く．</summary>
    public static void Run(Action restore, Action<string> log, object? cause)
    {
        // ログが書けなくても戻す処理は行う（別々に握る）
        Safe(() => log("unhandled exception: " + (cause is Exception e ? e.GetType().Name : "unknown") + ": restoring limits"));
        try
        {
            restore();
        }
        catch (Exception e)
        {
            Safe(() => log("unhandled exception: restore failed: " + e.GetType().Name));
        }
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 落ちる処理を続ける
        }
    }
}
