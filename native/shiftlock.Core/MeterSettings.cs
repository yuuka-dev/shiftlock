using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>
/// アプリの稼働計測の設定（shifts.json の apps と usage.game_idle_minutes）．
/// Registered は exe 名（小文字・.exe つき）→ カテゴリ（dev／adobe）．
/// </summary>
public sealed record MeterSettings(
    IReadOnlyDictionary<string, string> Registered, TimeSpan ActiveInput, double ActiveCpuPercent, TimeSpan? GameIdle)
{
    /// <summary>設定を読む．数値がおかしければ ConfigException（文にプロセス名は入らない）．</summary>
    public static MeterSettings Read(ShiftConfig cfg)
    {
        var appsNode = cfg.Raw["apps"];
        if (appsNode is not JsonObject apps)
        {
            throw cfg.BadValue("apps", appsNode);
        }
        var registered = new Dictionary<string, string>();
        if (apps["registered"] is JsonObject reg)
        {
            foreach (var (key, value) in reg)
            {
                var name = NormalizeExe(key);
                var cat = value is JsonValue && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
                if (name is not null && cat is "dev" or "adobe")
                {
                    registered[name] = cat!;
                }
            }
        }
        var minutes = Number(cfg, apps, "active_input_minutes");
        var percent = Number(cfg, apps, "active_cpu_percent");
        return new MeterSettings(registered, TimeSpan.FromMinutes(minutes), percent, Usage.GameIdleLimit(cfg));
    }

    /// <summary>"Code" → "code.exe"．空・パスの区切りや : を含むものは null．</summary>
    public static string? NormalizeExe(string? name)
    {
        var s = name?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s) || s.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            return null;
        }
        return s.EndsWith(".exe", StringComparison.Ordinal) ? s : s + ".exe";
    }

    /// <summary>JSON の数だけ受ける．数でない・負・非有限は ConfigException．</summary>
    private static double Number(ShiftConfig cfg, JsonObject apps, string key)
    {
        var node = apps[key];
        if (node is JsonValue && node.GetValueKind() == JsonValueKind.Number
            && node.GetValue<double>() is var n && double.IsFinite(n) && n >= 0)
        {
            return n;
        }
        throw cfg.BadValue("apps." + key, node);
    }
}
