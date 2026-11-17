using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shiftlock.Core;

/// <summary>Windows 通知の送信履歴（notified.json）と通知音の解決（pc/shiftlock/notify.py）．</summary>
public static partial class NotifyLog
{
    private const string File = "notified.json";

    /// <summary>同じ回に複数の通知があるとき，音を鳴らすのは 1 つだけ（この順で強い）．</summary>
    private static readonly string[] SoundPriority =
        ["enforce", "hard", "go_last", "confirm", "lastcall", "soft", "go", "auto_okita", "nag"];

    /// <summary>履歴に残す日数（これより古い日付で始まるキーを捨てる）．</summary>
    private const int KeepDays = 3;

    /// <summary>フェーズ通知の間隔（分）．null はフェーズごとに 1 回だけ（notify.NOTIFY_EVERY）．</summary>
    public static int? Every(string phase) => phase switch
    {
        "soft" => 30,
        "hard" => 10,
        _ => null,
    };

    /// <summary>
    /// 通知の間引き（notify._once）．key は "yyyy-MM-dd:..." で始める．
    /// 初回，または everyMinutes 以上空いていれば true を返して記録する．履歴の時刻が読めなければ，出していないものとして扱う．
    /// </summary>
    public static bool Once(Store store, string key, DateTime now, int? everyMinutes = null)
    {
        var sent = store.ReadJson(File);
        if (TimeUtil.ParseIso(JsonX.Str(sent[key])) is { } last
            && (everyMinutes is not { } every || now - last < TimeSpan.FromMinutes(every)))
        {
            return false;
        }
        sent[key] = Store.IsoSeconds(now);
        var cutoff = Planner.DayKey(DateOnly.FromDateTime(now.AddDays(-KeepDays)));
        var kept = new JsonObject();
        foreach (var (k, v) in sent)
        {
            if (string.CompareOrdinal(k.Split(':')[0], cutoff) >= 0)
            {
                kept[k] = v?.DeepClone();
            }
        }
        store.WriteJson(File, kept);
        return true;
    }

    /// <summary>
    /// actions に通知音を付ける（notify.attach_sounds）．鳴らすのは優先度の高い 1 件だけ．
    /// actions の要素を Sound 付きのものに置き換える．戻り値は警告（無いファイルは 1 日 1 回だけ）．
    /// </summary>
    public static IReadOnlyList<string> AttachSounds(ShiftConfig cfg, Store store, Messages m, IList<TickAction> actions, DateTime now)
    {
        var warnings = new List<string>();
        for (var i = 0; i < actions.Count; i++)
        {
            actions[i] = actions[i] with { Sound = "" };
        }
        foreach (var kind in SoundPriority)
        {
            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i].Kind != kind)
                {
                    continue;
                }
                var sound = ResolveSound(cfg, store, m, kind, now, warnings);
                actions[i] = actions[i] with { Sound = sound };
                if (sound.Length > 0)
                {
                    return warnings;
                }
            }
        }
        return warnings;
    }

    [GeneratedRegex(@"^([A-Za-z]:[\\/]|/)", RegexOptions.CultureInvariant)]
    private static partial Regex CheckablePath();

    /// <summary>
    /// 通知の種類に合う音声ファイル（notify.resolve_sound）．種類 → default → なし．
    /// ドライブか / で始まるパスは在るかを確かめ，無ければ警告して空を返す（それ以外は鳴らす側に任せる）．
    /// </summary>
    private static string ResolveSound(ShiftConfig cfg, Store store, Messages m, string kind, DateTime now, List<string> warnings)
    {
        if (cfg.Section("notify")["sound"] is not JsonObject snd)
        {
            return "";
        }
        var value = JsonX.Truthy(snd[kind]) ? snd[kind] : JsonX.Truthy(snd["default"]) ? snd["default"] : null;
        var path = (value is null ? "" : JsonX.Str(value) ?? ShiftConfig.PyRepr(value)).Trim();
        if (path.Length == 0)
        {
            return "";
        }
        if (CheckablePath().IsMatch(path) && !Exists(LocalPath.ToLocal(path)))
        {
            if (Once(store, $"{Planner.DayKey(DateOnly.FromDateTime(now))}:warn:sound:{path}", now))
            {
                warnings.Add(m.Format("NOTIFY_SOUND_MISSING", ("path", path)));
            }
            return "";
        }
        return path;
    }

    private static bool Exists(string path)
    {
        try
        {
            return System.IO.File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
