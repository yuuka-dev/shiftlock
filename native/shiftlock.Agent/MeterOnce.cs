using System.Globalization;
using System.Text.Json.Nodes;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 稼働計測を 10 秒あけて 2 回測り，書くはずの行を 1 行の JSON で出す（--meter-once．動作確認用）．
/// ファイルは書かない（読むのは shifts.json だけ）ので，本番のフォルダでも断らない．
/// 出力: {"parents": true|false, "procs": 数, "line": {"t", "span", "apps", "other"} | null}
/// </summary>
public static class MeterOnce
{
    private static readonly TimeSpan Gap = Meter.Interval;

    /// <summary>成功 0，失敗（設定が読めない・一覧が取れない）1．</summary>
    public static int Run(string dir, IProcessSource source, IMeterProbe meterProbe, ISystemProbe probe,
        TextWriter output, TextWriter error, Action<TimeSpan> wait)
    {
        var (cfg, _, loadError) = Watcher.LoadConfig(dir);
        if (cfg is null)
        {
            error.WriteLine(loadError);
            return 1;
        }
        MeterSettings settings;
        try
        {
            settings = MeterSettings.Read(cfg);
        }
        catch (ConfigException e)
        {
            error.WriteLine("meter: not measuring: " + e.Message);
            return 1;
        }

        var rules = Games.Rules(cfg, probe.SteamCommonDirs());
        var sampler = new MeterSampler(source, meterProbe, probe);
        var meter = new AppMeter();
        var start = DateTime.Now;
        try
        {
            var first = sampler.Collect(start, TimeSpan.Zero, rules);
            var line = meter.Add(first, settings);
            wait(Gap);
            var second = sampler.Collect(start + Gap, Gap, rules);
            line ??= meter.Add(second, settings);
            line ??= meter.Flush(start + Gap);

            var result = new JsonObject
            {
                ["parents"] = second.HasParents,
                ["procs"] = second.Procs.Count,
                ["line"] = line is null ? null : Compose(line),
            };
            output.WriteLine(result.ToJsonString());
            return 0;
        }
        catch (Exception e)
        {
            error.WriteLine($"meter failed: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static JsonObject Compose(MeterLine line)
    {
        var obj = new JsonObject { ["t"] = line.T.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture) };
        foreach (var (k, v) in line.Rec)
        {
            obj[k] = v?.DeepClone();
        }
        return obj;
    }
}
