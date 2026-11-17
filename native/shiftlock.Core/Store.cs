using System.Text;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 記録のファイルを読み書きできなかった（state.json が在るのに読めない・書けない，events.log に足せない）．
/// 読めなかったものを空として書き戻すと記録が消えるので，記録を断るために投げる．
/// </summary>
public sealed class StateBusyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 実行フォルダのファイル（pc/shiftlock/store.py の Store）．書くのは state.json と events.log だけ．
/// 排他はしない（Python 版同士と同じ．書き手が同時に state.json を書くと後勝ちになりうる）．
/// </summary>
public sealed class Store(string dir)
{
    /// <summary>実行フォルダにあれば強制は一切しない（緊急解除）．</summary>
    public const string StopFile = "STOP.txt";

    private const int Attempts = 3;
    private static readonly TimeSpan RetryWait = TimeSpan.FromMilliseconds(50);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string Dir { get; } = dir;

    public string P(string name) => Path.Combine(Dir, name);

    /// <summary>state.json の shifts（乗務日 → 記録）．無い・壊れている → 空．表示用（書き戻す前に読むときは ReadStateForWrite）．</summary>
    public JsonObject ShiftStatus() =>
        JsonFiles.ReadObjectOrEmpty(P("state.json"))["shifts"] as JsonObject ?? new JsonObject();

    /// <summary>書き戻すために state.json を読む．無ければ空．在るのに読めなければ（少し待ってやり直してから）StateBusyException．</summary>
    public JsonObject ReadStateForWrite()
    {
        for (var i = 0; ; i++)
        {
            if (JsonFiles.TryReadObject(P("state.json"), out var state))
            {
                return state;
            }
            if (i + 1 >= Attempts)
            {
                throw new StateBusyException("state.json が読めない");
            }
            Thread.Sleep(RetryWait);
        }
    }

    /// <summary>state.json を書く（.tmp に書いてから置き換え）．だめなら少し待ってやり直し，それでもだめなら StateBusyException．.tmp は残さない．</summary>
    public void WriteState(JsonObject state) =>
        Retry(() => JsonFiles.Write(P("state.json"), state), "state.json が書けない");

    /// <summary>events.log に 1 行足す（タブ区切り・LF．時刻は分まで）．</summary>
    public void AppendEvent(DateTime at, string day, string kind, string note = "")
    {
        var line = $"{TimeUtil.Iso(at)}\t{day}\t{kind}\t{note}\n";
        Retry(() => File.AppendAllText(P("events.log"), line, Utf8NoBom), "events.log に書けない");
    }

    /// <summary>STOP.txt がある（enforce.stop_active）．</summary>
    public bool StopActive => File.Exists(P(StopFile)) || Directory.Exists(P(StopFile));

    /// <summary>
    /// PC の記録（usage-pc.json）上，最近まとまって離席（≒睡眠）していたか（usage.recent_long_gap）．記録なしなら null．
    /// </summary>
    public bool? RecentLongGap(DateTime now, double minHours = 2.5, double withinHours = 4)
    {
        if (JsonFiles.ReadObjectOrEmpty(P("usage-pc.json"))["gaps"] is not JsonArray gaps)
        {
            return null;
        }
        foreach (var gap in gaps)
        {
            if (gap is not JsonArray { Count: 2 } pair
                || TimeUtil.ParseIso(JsonX.Str(pair[0])) is not { } s
                || TimeUtil.ParseIso(JsonX.Str(pair[1])) is not { } e)
            {
                continue;
            }
            if (e - s >= TimeSpan.FromHours(minHours) && now - e <= TimeSpan.FromHours(withinHours))
            {
                return true;
            }
        }
        return false;
    }

    private static void Retry(Action write, string what)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (i + 1 >= Attempts)
                {
                    throw new StateBusyException(what, e);
                }
                Thread.Sleep(RetryWait);
            }
        }
    }
}
