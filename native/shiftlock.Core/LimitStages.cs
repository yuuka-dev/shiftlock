using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiftlock.Core;

/// <summary>減速・一時停止の段階 1〜5 の 1 つ．Percent が null は一時停止．</summary>
public sealed record LimitStage(int Step, DateTime At, int? Percent);

/// <summary>
/// 強制モードの動作 throttle／suspend の段階の計算（pc/shiftlock/limit.py）．
/// Python 版と同じ結果になることを docs/test-vectors/limit-stages.json で確かめている．
/// </summary>
public static class LimitStages
{
    public const string Throttle = "throttle";
    public const string Suspend = "suspend";

    /// <summary>throttle か suspend なら真．この動作は Enforce.Due が何も返さず，Agent の段階の処理が持つ．</summary>
    public static bool IsLimitAction(string? action) => action is Throttle or Suspend;

    /// <summary>
    /// 割合の並び（limit.check_steps）．4 個ちょうど・整数・1 以上 100 未満・大きい順（同じ値は不可）なら並び，
    /// そうでなければ null．bool・小数・文字列は整数と見ない．
    /// </summary>
    public static IReadOnlyList<int>? CheckSteps(JsonNode? steps)
    {
        if (steps is not JsonArray { Count: 4 } arr)
        {
            return null;
        }
        var list = new List<int>(4);
        foreach (var node in arr)
        {
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number
                || !v.TryGetValue<int>(out var n) || n < 1 || n >= 100)
            {
                return null;
            }
            list.Add(n);
        }
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i - 1] <= list[i])
            {
                return null;
            }
        }
        return list;
    }

    /// <summary>
    /// 段階 1〜5（limit.limit_stages）．D = (hard - soft) / 4．hard が soft より後なら 1〜4 が soft + (k-1)*D，5 が hard．
    /// hard が soft 以前なら 5 だけ．時刻は秒より下を切り捨てる．steps は 4 個（CheckSteps を通したもの）．
    /// </summary>
    public static IReadOnlyList<LimitStage> Of(DateTime soft, DateTime hard, IReadOnlyList<int> steps, string action)
    {
        var stages = new List<LimitStage>(5);
        if (hard > soft)
        {
            var span = (hard - soft).Ticks;
            for (var k = 0; k < 4; k++)
            {
                stages.Add(new LimitStage(k + 1, Floor(soft.AddTicks(span * k / 4)), steps[k]));
            }
        }
        stages.Add(new LimitStage(5, Floor(hard), action == Suspend ? null : steps[3]));
        return stages;
    }

    private static DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);
}
