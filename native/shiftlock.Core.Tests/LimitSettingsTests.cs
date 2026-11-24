namespace Shiftlock.Core.Tests;

/// <summary>shifts.json の limit 節の読み取り．</summary>
public sealed class LimitSettingsTests
{
    private static LimitSettings Read(string shiftsJson)
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.ShiftsJson, shiftsJson);
        return LimitSettings.Read(ShiftConfig.Load(folder.ShiftsJson));
    }

    [Fact]
    public void Read_Defaults()
    {
        var s = Read("{}");
        Assert.Equal([75, 50, 25, 10], s.Steps);
        Assert.Equal(TimeSpan.FromMinutes(30), s.BaselineWindow);
        Assert.Empty(s.Apps);
        Assert.Empty(s.Exclude);
        Assert.Contains("EasyAntiCheat*", s.AntiCheatMarkers);
        Assert.Contains("system", s.Never);
        Assert.Contains("vmmem", s.Never);
        Assert.Contains("claude.exe", s.Never);
        Assert.Equal(8, s.Protect.Count);
        Assert.Contains("node.exe", s.Protect);
    }

    [Theory]
    [InlineData("""{"limit":{"steps":"75,50,25,10"}}""")]
    [InlineData("""{"limit":{"steps":[75,50,25]}}""")]
    [InlineData("""{"limit":{"steps":[10,25,50,75]}}""")]
    [InlineData("""{"limit":{"steps":null}}""")]
    public void Read_BadSteps_Throws(string json) =>
        Assert.Throws<ConfigException>(() => Read(json));

    [Fact]
    public void Read_AppsAndExclude_AreNormalized()
    {
        var s = Read("""{"limit":{"apps":["Photoshop"," Code.EXE "],"exclude":["EldenRing"]}}""");
        Assert.True(s.Apps.SetEquals(["photoshop.exe", "code.exe"]));
        Assert.True(s.Exclude.SetEquals(["eldenring.exe"]));
    }

    [Fact]
    public void Read_NeverAndProtect_KeepLowerCaseNames()
    {
        var s = Read("""{"limit":{"never":["System","VMMEM","Claude.exe"],"protect":["Node.EXE"]}}""");
        Assert.True(s.Never.SetEquals(["system", "vmmem", "claude.exe"]));
        Assert.True(s.Protect.SetEquals(["node.exe"]));
    }

    [Theory]
    [InlineData("""{"limit":[1]}""")]
    [InlineData("""{"limit":{"apps":"photoshop.exe"}}""")]
    [InlineData("""{"limit":{"apps":[5]}}""")]
    [InlineData("""{"limit":{"apps":["C:\\x\\a.exe"]}}""")]
    [InlineData("""{"limit":{"anticheat_markers":[""]}}""")]
    [InlineData("""{"limit":{"never":null}}""")]
    public void Read_BadShape_Throws(string json) =>
        Assert.Throws<ConfigException>(() => Read(json));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"30\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void Read_BadBaselineMinutes_Throws(string value) =>
        Assert.Throws<ConfigException>(() => Read("{\"limit\":{\"baseline_minutes\":" + value + "}}"));

    [Fact]
    public void Read_BaselineMinutes_Custom() =>
        Assert.Equal(TimeSpan.FromMinutes(45), Read("""{"limit":{"baseline_minutes":45}}""").BaselineWindow);

    [Fact]
    public void Read_ErrorMessage_NamesTheKey()
    {
        var e = Assert.Throws<ConfigException>(() => Read("""{"limit":{"apps":[5]}}"""));
        Assert.Contains("limit.apps", e.Message);
    }
}
