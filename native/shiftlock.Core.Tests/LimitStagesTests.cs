using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>減速・一時停止の段階の計算．docs/test-vectors/limit-stages.json（Python 版から書き出し）の全例で一致すること．</summary>
public sealed class LimitStagesTests
{
    private static DateTime Time(JsonNode? n) => TimeUtil.ParseIso(n!.GetValue<string>())!.Value;

    public static IEnumerable<object[]> Stages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "limit-stages.json");
        var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        foreach (var c in root["stages"]!.AsArray())
        {
            yield return [new VectorCase(c!["name"]!.GetValue<string>(), [], c["input"]!.AsObject(), new JsonObject { ["expect"] = c["expect"]!.DeepClone() })];
        }
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public void Of_MatchesPythonVectors(VectorCase c)
    {
        var steps = LimitStages.CheckSteps(c.Input["steps"]) ?? throw new InvalidOperationException("見本の steps が不正");

        var got = LimitStages.Of(Time(c.Input["soft"]), Time(c.Input["hard"]), steps, c.Input["action"]!.GetValue<string>());

        var expect = c.Expect["expect"]!.AsArray().Select(x =>
            new LimitStage(x!["step"]!.GetValue<int>(), Time(x["at"]), x["percent"]?.GetValue<int>())).ToList();
        Assert.Equal(expect, got);
    }

    [Fact]
    public void Of_HasAtLeastEightVectors() => Assert.True(Stages().Count() >= 8);

    [Theory]
    [InlineData("[75,50,25,10]")]
    [InlineData("[99,3,2,1]")]
    public void CheckSteps_Accepts(string json)
    {
        Assert.Equal(JsonNode.Parse(json)!.AsArray().Select(x => x!.GetValue<int>()), LimitStages.CheckSteps(JsonNode.Parse(json)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"75\"")]
    [InlineData("{\"a\":1}")]
    [InlineData("[75,50,25]")]
    [InlineData("[75,50,25,10,5]")]
    [InlineData("[10,25,50,75]")]
    [InlineData("[75,75,25,10]")]
    [InlineData("[75,50,25,0]")]
    [InlineData("[100,50,25,10]")]
    [InlineData("[75,50,25,true]")]
    [InlineData("[75.5,50,25,10]")]
    [InlineData("[75.0,50,25,10]")]
    [InlineData("[\"75\",50,25,10]")]
    [InlineData("[75,50,25,null]")]
    public void CheckSteps_Rejects(string json) => Assert.Null(LimitStages.CheckSteps(JsonNode.Parse(json)));

    [Fact]
    public void CheckSteps_NullNode_IsNull() => Assert.Null(LimitStages.CheckSteps(null));

    [Theory]
    [InlineData("throttle", true)]
    [InlineData("suspend", true)]
    [InlineData("lock", false)]
    [InlineData("sleep", false)]
    [InlineData("Throttle", false)]
    [InlineData(null, false)]
    public void IsLimitAction(string? action, bool expected) => Assert.Equal(expected, LimitStages.IsLimitAction(action));
}
