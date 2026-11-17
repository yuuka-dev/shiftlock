using System.Globalization;
using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>
/// docs/test-vectors/plan.json（Python 版が作った「正」）と同じ結果になるか。
/// ベクタの形は pc/tools/make_test_vectors.py の先頭に書いてある。
/// </summary>
public class PlanVectorTests
{
    private static readonly JsonNode Doc = Vectors.Load("plan.json");

    public static IEnumerable<object[]> ScenarioNames() =>
        Doc["scenarios"]!.AsArray().Select(s => new object[] { s!["name"]!.GetValue<string>() });

    private static JsonNode Scenario(string name) =>
        Doc["scenarios"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!;

    private static string Iso(DateTime dt) => dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public void FormatIsKnown()
    {
        Assert.Equal(1, Doc["format"]!.GetValue<int>());
        Assert.True(Doc["scenarios"]!.AsArray().Count >= 7);
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void WorkdaysAndPlansMatchPython(string name)
    {
        var sc = Scenario(name);
        var cfg = PlanConfig.FromJson(sc["config"], sc["days"]);
        var rows = sc["plans"]!.AsArray();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var date = row!["date"]!.GetValue<string>();
            var d = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.True(row["workday"]!.GetValue<bool>() == Planner.IsWorkday(cfg, d), $"{name} {date} workday");
            if (row["plan"] is not JsonObject want)
                continue;
            var p = Planner.MakePlan(cfg, d);
            var got = new Dictionary<string, string>
            {
                ["day"] = p.Day,
                ["start_target"] = Iso(p.StartTarget),
                ["start_latest"] = Iso(p.StartLatest),
                ["depart_target"] = Iso(p.DepartTarget),
                ["depart_latest"] = Iso(p.DepartLatest),
                ["soft_close"] = Iso(p.SoftClose),
                ["hard_close"] = Iso(p.HardClose),
                ["lastcall"] = Iso(p.Lastcall),
            };
            Assert.Equal(got.Count, want.Count);
            foreach (var (k, v) in want)
                Assert.True(v!.GetValue<string>() == got[k], $"{name} {date} {k}: want {v} got {got[k]}");
        }
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void PhasesAndTargetsMatchPython(string name)
    {
        var sc = Scenario(name);
        var cfg = PlanConfig.FromJson(sc["config"], sc["days"]);
        var lookback = Doc["lookback_days"]!.GetValue<int>();
        var moments = sc["moments"]!.AsArray();
        Assert.NotEmpty(moments);
        foreach (var m in moments)
        {
            var nowText = m!["now"]!.GetValue<string>();
            var now = DateTime.ParseExact(nowText, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            foreach (var (st, want) in m["current"]!.AsObject())
            {
                // ベクタの決まり: その状態を前後 3 日の全部に入れて判定する（"" は記録なし）
                var status = new Dictionary<string, string>();
                if (st != "")
                {
                    for (var i = -3; i <= 3; i++)
                        status[TimeUtil.Iso(DateOnly.FromDateTime(now).AddDays(i))] = st;
                }
                var (phase, plan) = Planner.Current(cfg, status, now);
                var w = want!.AsArray();
                Assert.True(w[0]!.GetValue<string>() == phase && w[1]?.GetValue<string>() == plan?.Day,
                    $"{name} {nowText} status='{st}': want {w.ToJsonString()} got [{phase}, {plan?.Day}]");
            }
            var target = Planner.TargetPlan(cfg, now)?.Day;
            Assert.True(m["target"]?.GetValue<string>() == target, $"{name} {nowText} target: got {target}");
            var last = Planner.LastPastPlan(cfg, now, lookback)?.Day;
            Assert.True(m["last_past"]?.GetValue<string>() == last, $"{name} {nowText} last_past: got {last}");
        }
    }
}

internal static class Vectors
{
    /// <summary>リポジトリの docs/test-vectors/ を、テストの出力フォルダから上にたどって探す。</summary>
    public static JsonNode Load(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "test-vectors", name);
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path))!;
        }
        throw new FileNotFoundException($"docs/test-vectors/{name} が見つからない");
    }
}
