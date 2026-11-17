using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>時刻の逆算・フェーズ・記録の対象が Python 版（plan.make_plan / current / target_plan）と同じになること．</summary>
public sealed class PlannerTests
{
    public static IEnumerable<object[]> MakePlanCases => Vectors.Cases("plan.json", "make_plan");

    public static IEnumerable<object[]> CurrentCases => Vectors.Cases("phase.json", "current");

    public static IEnumerable<object[]> TargetPlanCases => Vectors.Cases("phase.json", "target_plan");

    [Theory]
    [MemberData(nameof(MakePlanCases))]
    public void MakePlan_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var p = Planner.MakePlan(cfg, Vectors.Day(c.Input["day"]));
        var e = c.Expect;
        var expected = new Plan(
            e["day"]!.GetValue<string>(),
            Vectors.Time(e["start_target"]), Vectors.Time(e["start_latest"]),
            Vectors.Time(e["depart_target"]), Vectors.Time(e["depart_latest"]),
            Vectors.Time(e["soft_close"]), Vectors.Time(e["hard_close"]), Vectors.Time(e["lastcall"]));
        Assert.Equal(expected, p);
    }

    [Theory]
    [MemberData(nameof(CurrentCases))]
    public void Current_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        var status = JsonFiles.ReadObjectOrEmpty(folder.P("state.json"))["shifts"] as JsonObject ?? new JsonObject();
        var (phase, plan) = Planner.Current(cfg, status, Vectors.Time(c.Input["now"]));
        Assert.Equal(c.Expect["phase"]!.GetValue<string>(), phase);
        Assert.Equal(c.Expect["day"]?.GetValue<string>(), plan?.Day);
    }

    [Theory]
    [MemberData(nameof(TargetPlanCases))]
    public void TargetPlan_MatchesPython(VectorCase c)
    {
        using var folder = Vectors.Materialize(c.Files);
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        Assert.Equal(c.Expect["day"]?.GetValue<string>(), Planner.TargetPlan(cfg, Vectors.Time(c.Input["now"]))?.Day);
    }

    [Fact]
    public void Plans_ExpandsWorkdaysAroundTheDay()
    {
        using var folder = Vectors.Materialize(new JsonObject
        {
            ["shifts.json"] = JsonNode.Parse("""{"work_weekdays":["mon","tue","wed","thu","fri"]}"""),
        });
        var cfg = ShiftConfig.Load(folder.ShiftsJson);
        // 2026-12-07（月）の前後 1 日: 日曜は休みなので 月・火
        var days = Planner.Plans(cfg, new DateOnly(2026, 12, 7), back: 1, fwd: 1).Select(p => p.Day);
        Assert.Equal(["2026-12-07", "2026-12-08"], days);
    }
}
