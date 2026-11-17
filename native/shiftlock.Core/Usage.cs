using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 連続稼働（PC）の計測と声かけの判定，PC の離席区間（pc/shiftlock/usage.py）．
/// 「まとまった離席があったか」（recent_long_gap）は区切り 1 から Store.RecentLongGap にある．
/// </summary>
public static class Usage
{
    /// <summary>ゲーム起動中でも，入力がこれだけ無ければ離席とみなす（既定．usage.game_idle_minutes）．</summary>
    private const double GameIdleMinutes = 180;

    /// <summary>これ以上の分数は「無制限」として扱う（TimeSpan があふれないように）．</summary>
    private const double MaxMinutes = 1_000_000;

    private static readonly TimeSpan AwayHorizon = TimeSpan.FromHours(24);

    /// <summary>
    /// PC を触っている扱いか（usage.pc_active）．最後の入力から reset 未満なら触っている．
    /// ゲーム起動中はゲームパッドの入力が idle に乗らないので，gameIdle（null なら無制限）までは触っている扱い．
    /// </summary>
    public static bool PcActive(TimeSpan idle, bool game, TimeSpan reset, TimeSpan? gameIdle) =>
        idle < reset || (game && (gameIdle is not { } limit || idle < limit));

    /// <summary>usage.game_idle_minutes（usage.game_idle_limit）．0 以下なら null（無制限）．壊れた値・inf・nan は既定の 180 分．</summary>
    public static TimeSpan? GameIdleLimit(ShiftConfig cfg)
    {
        var usage = cfg.Section("usage");
        var n = usage.TryGetPropertyValue("game_idle_minutes", out var v) ? Py.Float(v) ?? GameIdleMinutes : GameIdleMinutes;
        if (double.IsNaN(n) || double.IsInfinity(n))
        {
            n = GameIdleMinutes;
        }
        return n <= 0 || n >= MaxMinutes ? null : TimeSpan.FromMinutes(n);
    }

    /// <summary>
    /// 乗務で外出中か（usage.shift_away）．「行ってきます」（state の worked の at が events.log の shuppatsu 行と一致）から
    /// 24 時間以内で，その後の「ただいま」（乗務日に紐づかない記録も含む）がまだ無ければ true．壊れた時刻は無視する．
    /// </summary>
    public static bool ShiftAway(Store store, DateTime now)
    {
        var (shuppatsu, kitaku) = store.Departures();
        var status = store.ShiftStatus();
        var homes = new List<DateTime>();
        foreach (var k in kitaku)
        {
            if (TimeUtil.ParseIso(k) is { } h)
            {
                homes.Add(h);
            }
        }
        foreach (var (_, v) in status)
        {
            if (TimeUtil.ParseIso(JsonX.Str((v as JsonObject)?["kitaku"])) is { } h)
            {
                homes.Add(h);
            }
        }
        foreach (var (day, v) in status)
        {
            if (v is not JsonObject rec || JsonX.Str(rec["status"]) != "worked"
                || JsonX.Str(rec["at"]) is not { } at || !shuppatsu.Contains((day, at)))
            {
                continue;
            }
            if (TimeUtil.ParseIso(at) is not { } dep || !(dep <= now && now < dep + AwayHorizon))
            {
                continue;
            }
            if (homes.Any(h => dep < h && h <= now))
            {
                continue;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 連続稼働の声かけを出すなら連続時間を返す（usage.due_nag）．出すときは usage["last_nag"] を書く．
    /// nag_after_minutes たってから，nag_every_minutes おき．
    /// </summary>
    public static TimeSpan? DueNag(JsonObject usage, DateTime now, ShiftConfig cfg)
    {
        var after = TimeSpan.FromMinutes(cfg.SectionNumber("usage", "nag_after_minutes", 120));
        var every = TimeSpan.FromMinutes(cfg.SectionNumber("usage", "nag_every_minutes", 30));
        if (TimeUtil.ParseIso(JsonX.Str(usage["start"])) is not { } start)
        {
            return null;
        }
        var cont = now - start;
        if (cont < after)
        {
            return null;
        }
        if (TimeUtil.ParseIso(JsonX.Str(usage["last_nag"])) is { } last && now - last < every)
        {
            return null;
        }
        usage["last_nag"] = Store.IsoSeconds(now);
        return cont;
    }

    /// <summary>PC を触っていない区間（usage.pc_gaps）．いま離席中なら「離席開始〜now」も含む．記録なしなら null．</summary>
    public static IReadOnlyList<(DateTime S, DateTime E)>? PcGaps(Store store, DateTime now)
    {
        var u = store.ReadJson("usage-pc.json");
        if (!u.ContainsKey("gaps"))
        {
            return null;
        }
        var result = new List<(DateTime S, DateTime E)>();
        foreach (var gap in u["gaps"] as JsonArray ?? [])
        {
            if (ReadGap(gap) is { } g)
            {
                result.Add(g);
            }
        }
        if (TimeUtil.ParseIso(JsonX.Str(u["gap_start"])) is { } gs)
        {
            result.Add((gs, now));
        }
        return result;
    }

    /// <summary>usage-pc.json の gaps の 1 件（[開始, 終了]）．形がおかしい・時刻が読めなければ null．</summary>
    internal static (DateTime S, DateTime E)? ReadGap(JsonNode? gap) =>
        gap is JsonArray { Count: 2 } pair
        && TimeUtil.ParseIso(JsonX.Str(pair[0])) is { } s
        && TimeUtil.ParseIso(JsonX.Str(pair[1])) is { } e
            ? (s, e)
            : null;
}
