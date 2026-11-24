using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>アプリ計測の設定（apps）の読み取り．</summary>
public sealed class MeterSettingsTests
{
    private static MeterSettings Read(string shiftsJson)
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.ShiftsJson, shiftsJson);
        return MeterSettings.Read(ShiftConfig.Load(folder.ShiftsJson));
    }

    [Theory]
    [InlineData("Code", "code.exe")]
    [InlineData(" Code.EXE ", "code.exe")]
    [InlineData("photoshop.exe", "photoshop.exe")]
    [InlineData("", null)]
    [InlineData(@"C:\x\code.exe", null)]
    public void NormalizeExe_Cases(string input, string? expected) =>
        Assert.Equal(expected, MeterSettings.NormalizeExe(input));

    [Fact]
    public void Read_Defaults()
    {
        var s = Read("{}");
        Assert.Empty(s.Registered);
        Assert.Equal(TimeSpan.FromMinutes(5), s.ActiveInput);
        Assert.Equal(5, s.ActiveCpuPercent);
        Assert.Equal(TimeSpan.FromMinutes(180), s.GameIdle);
    }

    [Fact]
    public void Read_NormalizesAndDropsBadEntries()
    {
        var s = Read("""{"apps":{"registered":{"Code":"dev","photoshop.exe":"adobe","x.exe":"game","y.exe":5}}}""");
        Assert.Equal(
            new Dictionary<string, string> { ["code.exe"] = "dev", ["photoshop.exe"] = "adobe" },
            s.Registered.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void Read_RegisteredNotDict_IsEmpty() =>
        Assert.Empty(Read("""{"apps":{"registered":[1]}}""").Registered);

    [Fact]
    public void Read_AppsNotDict_Throws() =>
        Assert.Throws<ConfigException>(() => Read("""{"apps":5}"""));

    [Theory]
    [InlineData("""{"apps":{"active_cpu_percent":"5"}}""")]
    [InlineData("""{"apps":{"active_input_minutes":-1}}""")]
    public void Read_BadNumber_Throws(string json) =>
        Assert.Throws<ConfigException>(() => Read(json));

    [Fact]
    public void Read_GameIdleZero_IsNull() =>
        Assert.Null(Read("""{"usage":{"game_idle_minutes":0}}""").GameIdle);
}
