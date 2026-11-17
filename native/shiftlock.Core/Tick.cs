using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>見張り 1 回に渡す Windows 側の値．UptimeSeconds は起動からの秒数（無ければ強制は発動しない）．</summary>
public sealed record TickInput(double IdleSeconds, bool Game, double? UptimeSeconds = null, bool Remote = false, bool DryRun = false);

/// <summary>
/// 出す通知，または強制の実行 1 件．Method は toast / popup / enforce．Kind は通知の種類（フェーズ名・nag・confirm など）．
/// Sound は鳴らす音声ファイル（無ければ空）．Action は強制の実行だけ（lock / sleep / shutdown）．
/// </summary>
public sealed record TickAction(string Method, string Kind, string Title, string Text, bool Speak, string Sound = "", string? Action = null);

/// <summary>見張り 1 回の結果．Actions は出す順．Warnings はログに書く文．</summary>
public sealed record TickResult(IReadOnlyList<TickAction> Actions, IReadOnlyList<string> Warnings, double SoundVolume, int SoundMaxSeconds);

/// <summary>
/// 5 分おきの見張りの本体（pc/shiftlock/tick.py の run_tick）．稼働の記録・通知・自動「起きた」・強制モードの判断．
/// Windows の値は input で受け，結果（出す通知と強制の実行）を返すだけで，自分では何も出さない．
/// 段の順番と条件は run_tick と同じにしてある（docs/test-vectors/tick.json で突き合わせる）．
/// </summary>
public static class Tick
{
    private const string Title = "shiftlock";
    private const string UsageFile = "usage-pc.json";

    /// <summary>離席がこれ以上続いたら離席区間として残す．</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(30);

    /// <summary>前の見張りからこれ（＋離席とみなす時間）より空いたら，見張りが止まっていた（スリープなど）とみて起点を切り直す．</summary>
    private static readonly TimeSpan StalledSlack = TimeSpan.FromMinutes(10);

    private const int MaxGaps = 20;

    /// <summary>
    /// 見張りを 1 回行う．fetch はカレンダーの取得（URL → 中身）．渡さなければ Core の既定の取得を使う．
    /// 設定の値がおかしければ ConfigException，ファイルが読み書きできなければ StateBusyException / IOException
    /// （呼ぶ側はその回を失敗として，次の回に進む）．
    /// </summary>
    public static TickResult Run(ShiftConfig cfg, Store store, Messages m, TickInput input, DateTime now,
        Func<string, string>? fetch = null)
    {
        var reset = TimeSpan.FromMinutes(cfg.SectionNumber("usage", "idle_reset_minutes", 20));
        var idle = TimeSpan.FromSeconds(double.IsNaN(input.IdleSeconds) ? 0 : Math.Clamp(input.IdleSeconds, 0, 1e9));
        // 乗務で外出中（「行ってきます」〜「ただいま」）は起動したままのゲームを見ない．
        // ゲーム中でも入力が game_idle_minutes 無ければ離席
        var gameOn = input.Game && !Usage.ShiftAway(store, now);
        var active = Usage.PcActive(idle, gameOn, reset, Usage.GameIdleLimit(cfg));
        store.LogAppend("pc", now, new JsonObject { ["idle"] = (long)idle.TotalSeconds, ["game"] = input.Game ? 1 : 0 });

        var u = ReadUsage(store);
        var gaps = u["gaps"]!.AsArray();
        if (active)
        {
            if (TimeUtil.ParseIso(JsonX.Str(u["gap_start"])) is { } gapStart)
            {
                var gapEnd = gameOn ? now : now - idle;
                if (gapEnd - gapStart >= MinGap)
                {
                    gaps.Add(new JsonArray(TimeUtil.Iso(gapStart), TimeUtil.Iso(gapEnd)));
                }
                u["gap_start"] = null;
                u["start"] = Store.IsoSeconds(gapEnd);
                u.Remove("last_nag");
            }
            else if (!u.ContainsKey("start")
                || (TimeUtil.ParseIso(JsonX.Str(u["last"])) is { } last && now - last > reset + StalledSlack))
            {
                // 初回か，見張りが止まっていた（スリープなど）→ 新しいセッション扱い
                u["start"] = Store.IsoSeconds(now);
                u.Remove("last_nag");
            }
            u["last"] = Store.IsoSeconds(now);
        }
        else if (u["gap_start"] is null)
        {
            u["gap_start"] = Store.IsoSeconds(now - idle);
        }
        while (gaps.Count > MaxGaps)
        {
            gaps.RemoveAt(0);
        }
        store.WriteJson(UsageFile, u);   // 自動起床の判定がこの直後に読む

        var notify = cfg.Section("notify");
        var speak = JsonX.Truthy(notify["speak"]);
        var actions = new List<TickAction>();
        if (active)
        {
            string autoMsg;
            try
            {
                autoMsg = Phone.ImportMarks(cfg, store, m, now);
            }
            catch (Exception)
            {
                autoMsg = "";   // iPhone の記録の取り込みに失敗しても本線（通知）を止めない（Python 版と同じ）
            }
            if (autoMsg.Length == 0)
            {
                autoMsg = AutoWake.MaybeAutoOkita(cfg, store, m, now);
            }
            if (autoMsg.Length > 0)
            {
                actions.Add(new TickAction(Method(notify, "phase", "toast"), "auto_okita", Title, autoMsg, speak));
            }
            var (phase, p) = Planner.Current(cfg, store.ShiftStatus(), now);
            if (p is not null && NotifyLog.Once(store, $"{p.Day}:{phase}", now, NotifyLog.Every(phase)))
            {
                var method = phase is "hard" or "go_last" ? Method(notify, "hard", "popup") : Method(notify, "phase", "toast");
                actions.Add(new TickAction(method, phase, Title, PhaseText.User(phase, p, now, cfg, m), speak));
            }
            var (unconfirmed, others) = Confirm.PickUnconfirmed(cfg, store, now);
            if (unconfirmed is not null)
            {
                // 終わった乗務の「出た？休んだ？」は 1 日 1 件，新しい乗務から
                actions.Add(new TickAction(Method(notify, "phase", "toast"), "confirm", Title,
                    Confirm.UserText(cfg, store, m, unconfirmed, now, others), speak));
                Confirm.MarkAsked(store, unconfirmed.Day, now);
            }
            if (Usage.DueNag(u, now, cfg) is { } cont)
            {
                var what = m[input.Game ? "TICK_NAG_GAME" : "TICK_NAG_PC"];
                actions.Add(new TickAction(Method(notify, "nag", "toast"), "nag", Title,
                    m.Format("TICK_NAG", ("what", what), ("dur", TimeUtil.FmtDur(cont, m))), speak));
            }
        }
        store.WriteJson(UsageFile, u);

        var warnings = new List<string>();
        try
        {
            warnings.AddRange(NotifyLog.AttachSounds(cfg, store, m, actions, now));
        }
        catch (Exception e)
        {
            // 通知音の設定ミスや履歴の書き込み失敗で本線（通知）を止めない
            warnings = [m.Format("TICK_SOUND_CONFIG_ERROR", ("err", e.Message))];
            for (var i = 0; i < actions.Count; i++)
            {
                actions[i] = actions[i] with { Sound = "" };
            }
        }
        var (volume, maxSeconds) = (DefaultVolume, DefaultMaxSeconds);
        if (SoundNumbers(notify) is { } numbers)
        {
            (volume, maxSeconds) = numbers;
        }
        else
        {
            warnings.Add(m["TICK_SOUND_NUMBER_ERROR"]);
        }
        return new TickResult(actions, warnings, volume, maxSeconds);
    }

    private const double DefaultVolume = 0.8;
    private const int DefaultMaxSeconds = 20;

    /// <summary>通知の出し方（notify.phase / hard / nag）．無ければ fallback．文字列でなければその表記（鳴らす側が知らない出し方として扱う）．</summary>
    private static string Method(JsonObject notify, string key, string fallback) =>
        notify.TryGetPropertyValue(key, out var v) ? JsonX.Str(v) ?? JsonX.Text(v) : fallback;

    /// <summary>
    /// 通知音の音量と最長秒数（Python の float(sound_volume), int(sound_max_seconds)）．どちらかが数として読めなければ null
    /// （呼ぶ側は両方とも既定にして警告する）．
    /// </summary>
    private static (double Volume, int MaxSeconds)? SoundNumbers(JsonObject notify)
    {
        var volume = notify.TryGetPropertyValue("sound_volume", out var v) ? Py.Float(v) : DefaultVolume;
        var seconds = notify.TryGetPropertyValue("sound_max_seconds", out var s) ? Py.Int(s) : DefaultMaxSeconds;
        return volume is { } vol && seconds is { } sec ? (vol, sec) : null;
    }

    /// <summary>
    /// usage-pc.json を読み，壊れているところを無いものにそろえる（空・JSON でない → 空から．
    /// 読めない時刻は消す．gaps は [開始, 終了] の形のものだけ残す）．手で壊れたファイルで見張りを止めない．
    /// </summary>
    private static JsonObject ReadUsage(Store store)
    {
        var u = store.ReadJson(UsageFile);
        var gaps = new JsonArray();
        foreach (var gap in u["gaps"] as JsonArray ?? [])
        {
            if (Usage.ReadGap(gap) is not null)
            {
                gaps.Add(gap!.DeepClone());
            }
        }
        u["gaps"] = gaps;
        if (u.ContainsKey("last") && TimeUtil.ParseIso(JsonX.Str(u["last"])) is null)
        {
            // 最後に見た時刻が読めないと続きかどうか分からないので，起点から付け直す
            u.Remove("last");
            u.Remove("start");
        }
        foreach (var key in (string[])["start", "last_nag"])
        {
            if (u.ContainsKey(key) && TimeUtil.ParseIso(JsonX.Str(u[key])) is null)
            {
                u.Remove(key);
            }
        }
        if (u.ContainsKey("gap_start") && TimeUtil.ParseIso(JsonX.Str(u["gap_start"])) is null)
        {
            u["gap_start"] = null;
        }
        return u;
    }
}
