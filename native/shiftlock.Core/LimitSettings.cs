using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// 減速・一時停止の設定（shifts.json の limit 節．設計書 §5.1）．
/// Apps・Exclude は NormalizeExe でそろえた exe 名（小文字・.exe つき）．
/// Never・Protect は ProcSample.Name と比べる名前なので，小文字にするだけで .exe は足さない（"system"・"vmmem" のような名前があるため）．
/// </summary>
public sealed record LimitSettings(
    IReadOnlyList<int> Steps, TimeSpan BaselineWindow, IReadOnlySet<string> Apps, IReadOnlySet<string> Exclude,
    IReadOnlyList<string> AntiCheatMarkers, IReadOnlySet<string> Never, IReadOnlySet<string> Protect)
{
    private const double MaxBaselineMinutes = 60 * 24 * 365;

    /// <summary>設定を読む．形がおかしければ ConfigException（文には設定のキーと値だけを入れる）．</summary>
    public static LimitSettings Read(ShiftConfig cfg)
    {
        var node = cfg.Raw["limit"];
        if (node is not JsonObject limit)
        {
            throw cfg.BadValue("limit", node);
        }
        var steps = LimitStages.CheckSteps(limit["steps"]) ?? throw cfg.BadValue("limit.steps", limit["steps"]);
        return new LimitSettings(
            steps,
            TimeSpan.FromMinutes(Minutes(cfg, limit)),
            Names(cfg, limit, "apps", MeterSettings.NormalizeExe),
            Names(cfg, limit, "exclude", MeterSettings.NormalizeExe),
            [.. Strings(cfg, limit, "anticheat_markers", s => s)],
            Names(cfg, limit, "never", Lower),
            Names(cfg, limit, "protect", Lower));
    }

    private static string? Lower(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.Length == 0 || t.IndexOfAny(['\\', '/', ':']) >= 0 ? null : t;
    }

    /// <summary>JSON の数で，0 より大きく，有限で，1 年以内．</summary>
    private static double Minutes(ShiftConfig cfg, JsonObject limit)
    {
        var node = limit["baseline_minutes"];
        if (node is JsonValue && node.GetValueKind() == JsonValueKind.Number
            && node.GetValue<double>() is var n && double.IsFinite(n) && n > 0 && n <= MaxBaselineMinutes)
        {
            return n;
        }
        throw cfg.BadValue("limit.baseline_minutes", node);
    }

    private static HashSet<string> Names(ShiftConfig cfg, JsonObject limit, string key, Func<string, string?> normalize) =>
        [.. Strings(cfg, limit, key, normalize)];

    /// <summary>文字列の配列．配列でない・文字列でない・空になる要素があれば ConfigException．</summary>
    private static List<string> Strings(ShiftConfig cfg, JsonObject limit, string key, Func<string, string?> convert)
    {
        var path = "limit." + key;
        if (limit[key] is not JsonArray arr)
        {
            throw cfg.BadValue(path, limit[key]);
        }
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var s = JsonX.Str(item);
            var converted = s is null ? null : convert(s);
            if (string.IsNullOrWhiteSpace(converted))
            {
                throw cfg.BadValue(path, item);
            }
            list.Add(converted);
        }
        return list;
    }
}
