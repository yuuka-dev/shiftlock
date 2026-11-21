using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>トレイのアイコンの種類．</summary>
public enum TrayLook
{
    /// <summary>平常．</summary>
    Normal,

    /// <summary>就寝準備・就寝目安（lastcall・soft）．</summary>
    Caution,

    /// <summary>最低ライン・出発の限界（hard・go_last）．</summary>
    Warning,

    /// <summary>設定が無い・読めない．見張っていない．</summary>
    ConfigError,

    /// <summary>Python 版のタスク（shiftlock-guard）が有効なので見張りを止めている．</summary>
    Paused,
}

/// <summary>トレイの見た目とホバーの 1 行を決める（判断だけ．描くのは Tray）．</summary>
public static class TrayState
{
    /// <summary>ホバーの文の長さの上限（通知領域のアイコンの決まり）．</summary>
    public const int MaxTip = 63;

    /// <summary>
    /// outcome は最後の見張りの結果（まだなら null），error は設定エラーの文，snapshot は今の状態（設定が読めなければ null）．
    /// Paused: shiftlock-guard が有効 ／ ConfigError: 設定が無い・読めない ／ Warning: hard・go_last ／ Caution: lastcall・soft ／ ほかは Normal．
    /// ホバーは残り時間の行（無ければフェーズの行）．見張りに失敗した後はその旨．
    /// </summary>
    public static (TrayLook Look, string Tip) From(WatchOutcome? outcome, string? error, StatusSnapshot? snapshot, Messages m)
    {
        if (outcome == WatchOutcome.GuardTaskEnabled)
        {
            return (TrayLook.Paused, Cut(m["AGENT_TIP_GUARD_TASK"]));
        }
        if (outcome is WatchOutcome.NoConfig or WatchOutcome.BadConfig || snapshot is null)
        {
            return (TrayLook.ConfigError, Cut(error ?? m["AGENT_TIP_FAILED"]));
        }
        var look = snapshot.Phase switch
        {
            "hard" or "go_last" => TrayLook.Warning,
            "lastcall" or "soft" => TrayLook.Caution,
            _ => TrayLook.Normal,
        };
        if (outcome == WatchOutcome.Failed)
        {
            return (look, Cut(m["AGENT_TIP_FAILED"]));
        }
        // Snapshot.Lines: [0] 現在時刻，[1] フェーズ，[2] 残り時間（あるときだけ）
        var lines = Snapshot.Lines(snapshot, m);
        return (look, Cut(snapshot.CountdownKey is not null && lines.Count > 2 ? lines[2] : lines[1]));
    }

    /// <summary>MaxTip 文字に収める．切ったら最後を … にする．サロゲートペアの途中では切らない．</summary>
    private static string Cut(string text)
    {
        if (text.Length <= MaxTip)
        {
            return text;
        }
        var keep = MaxTip - 1;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }
        return text[..keep] + "…";
    }
}
