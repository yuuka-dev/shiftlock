using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>見張り 1 回の結果．Ran 以外は見張っていない（Failed は途中で失敗）．</summary>
public enum WatchOutcome
{
    /// <summary>見張った．</summary>
    Ran,

    /// <summary>前の見張りから 5 分たっていない．</summary>
    TooSoon,

    /// <summary>前の回がまだ終わっていない（通知を出している最中など）．</summary>
    Busy,

    /// <summary>Python 版のタスク（shiftlock-guard）が有効なので見張らない（二重に見張らない）．</summary>
    GuardTaskEnabled,

    /// <summary>shifts.json（か実行フォルダ）が無い．</summary>
    NoConfig,

    /// <summary>shifts.json が読めない・検証に落ちた．</summary>
    BadConfig,

    /// <summary>途中で失敗した（ログに書いてある）．次の回は普通に試す．</summary>
    Failed,
}

/// <summary>
/// 見張りの 1 回: Windows 側の値を取り，Core の Tick.Run を呼び，結果を deliver（通知と強制の実行）に渡す．
/// 設定は毎回読み直す（Python 版の tick と同じ）．例外は外に出さない（Agent を落とさない）．
/// 「前の見張りから 5 分」は log/pc-YYYY-MM.jsonl の最後の行で見る（Agent を起動し直しても行が重ならない）．
/// </summary>
public sealed class Watcher(string dir, ISystemProbe probe, Func<DateTime> clock, Func<GuardTaskState> guardTask,
    Action<TickResult, Messages> deliver, AgentLog log, Func<string, string>? fetch = null)
{
    /// <summary>見張りの間隔．</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// タイマーのずれの許容．タイマーは 5 分ちょうどより少し早く来ることがあり，それで飛ばすと次は 10 分後になる．
    /// 最後の行からこれだけ足りなくても見張る．
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    private int _running;
    private DateOnly? _guardWarnedOn;

    /// <summary>最後に見張ろうとした回の結果（Busy で飛ばした回は入らない）．まだ 1 回も呼ばれていなければ null．</summary>
    public WatchOutcome? LastOutcome { get; private set; }

    /// <summary>設定エラーの文（NoConfig・BadConfig のとき．APP_NO_CONFIG / APP_BAD_CONFIG）．ほかは null．</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 見張りを 1 回試す．順番: 前の回が動いている → Busy ／ shiftlock-guard が有効 → GuardTaskEnabled ／
    /// 設定が無い・読めない → NoConfig・BadConfig ／ 5 分たっていない → TooSoon ／ Tick.Run → 警告をログ → deliver → Ran．
    /// dryRun は強制モードの状態を進めず，結果も出さない（通知を出さない・強制を実行しない．件数をログに書くだけ）．
    /// 通知の履歴などのファイルは進む（guard.ps1 -DryRun と同じ）．
    /// </summary>
    public WatchOutcome RunOnce(bool dryRun = false)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return WatchOutcome.Busy;
        }
        try
        {
            WatchOutcome outcome;
            string? error = null;
            try
            {
                outcome = Run(dryRun, out error);
            }
            catch (Exception e)
            {
                // ほかの書き手がファイルを掴んでいる，通知が出せない，など．この回は諦めて次の回に任せる
                log.Write($"tick failed: {e.GetType().Name}: {e.Message}");
                outcome = WatchOutcome.Failed;
            }
            LastError = error;
            LastOutcome = outcome;
            return outcome;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private WatchOutcome Run(bool dryRun, out string? error)
    {
        error = null;
        var now = clock();
        if (GuardState(now) == GuardTaskState.Enabled)
        {
            return WatchOutcome.GuardTaskEnabled;
        }

        var (cfg, problem, configError) = LoadConfig(dir);
        if (cfg is null)
        {
            error = configError;
            return problem;
        }

        var store = new Store(dir);
        // 最後の行が今より後（時計が戻った・別の PC のフォルダ）なら待たない．待つと「5 分たっていない」が続いてしまう
        if (store.LastLogTime("pc") is { } last && last <= now && now - last < Interval - Slack)
        {
            return WatchOutcome.TooSoon;
        }

        var rules = Games.Rules(cfg, probe.SteamCommonDirs());
        var game = probe.Processes().Any(p => Games.IsGame(rules, p.Name, p.Path));
        var input = new TickInput(probe.IdleSeconds(), game, probe.UptimeSeconds(), Remote: false, DryRun: dryRun);
        var result = Tick.Run(cfg, store, cfg.Msg, input, now, fetch);
        foreach (var warning in result.Warnings)
        {
            if (warning.Length > 0)
            {
                log.Write("warn: " + warning);
            }
        }
        if (dryRun)
        {
            log.Write($"dry run: {result.Actions.Count} actions (not delivered)");
        }
        else
        {
            deliver(result, cfg.Msg);
        }
        return WatchOutcome.Ran;
    }

    /// <summary>
    /// 設定を読む．無い → (null, NoConfig, APP_NO_CONFIG の文)，読めない・検証に落ちた → (null, BadConfig, APP_BAD_CONFIG の文)．
    /// エラーの文は標準語（設定が読めないので言語が分からない）．ほかのプロセスが掴んでいて読めないときは IOException．
    /// トレイの表示も同じ読み方をする．
    /// </summary>
    public static (ShiftConfig? Config, WatchOutcome Problem, string? Error) LoadConfig(string dir)
    {
        var shiftsJson = Path.Combine(dir, "shifts.json");
        try
        {
            return (ShiftConfig.Load(shiftsJson), WatchOutcome.Ran, null);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, WatchOutcome.NoConfig, Messages.Load(null).Format("APP_NO_CONFIG", ("path", shiftsJson)));
        }
        catch (ConfigException e)
        {
            return (null, WatchOutcome.BadConfig, Messages.Load(null).Format("APP_BAD_CONFIG", ("err", e.Message)));
        }
    }

    /// <summary>shiftlock-guard の状態．確かめられなければ Unknown（見張りは行い，ログに 1 日 1 回書く）．</summary>
    private GuardTaskState GuardState(DateTime now)
    {
        GuardTaskState state;
        try
        {
            state = guardTask();
        }
        catch (Exception)
        {
            state = GuardTaskState.Unknown;
        }
        var today = DateOnly.FromDateTime(now);
        if (state == GuardTaskState.Unknown && _guardWarnedOn != today)
        {
            _guardWarnedOn = today;
            // ログの文は guard.log と同じく英語で固定（文面の対象外）
            log.Write("warn: cannot check task shiftlock-guard (watching anyway; ticks are doubled if the Python task is enabled)");
        }
        return state;
    }
}
