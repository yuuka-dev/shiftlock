namespace Shiftlock.Core.Tests;

/// <summary>アンチチートの根のフォルダの決め方と名前の一致．</summary>
public sealed class AntiCheatTests
{
    private static GameRules Rules(string[]? names = null) =>
        new([@"C:\Steam\steamapps\common\", @"C:\Games\"], [], [], names ?? []);

    [Fact]
    public void RootOf_UnderGameDir_IsFirstLevelFolder() =>
        Assert.Equal(new AntiCheat.Root(@"C:\Steam\steamapps\common\Game\", 3),
            AntiCheat.RootOf(Rules(), @"C:\Steam\steamapps\common\Game\bin\x64\game.exe"));

    [Fact]
    public void RootOf_DirectlyInGameDir_IsThatFolderOnly() =>
        Assert.Equal(new AntiCheat.Root(@"C:\Games\", 0), AntiCheat.RootOf(Rules(), @"C:\Games\foo.exe"));

    [Fact]
    public void RootOf_DirectlyUnderFirstLevel_IsFirstLevelFolder() =>
        Assert.Equal(new AntiCheat.Root(@"C:\Games\Foo\", 3), AntiCheat.RootOf(Rules(), @"C:\Games\Foo\foo.exe"));

    [Fact]
    public void RootOf_OutsideGameDirs_IsExeFolder() =>
        Assert.Equal(new AntiCheat.Root(@"D:\Apps\X\", 3), AntiCheat.RootOf(Rules(["x.exe"]), @"D:\Apps\X\x.exe"));

    [Fact]
    public void RootOf_CaseInsensitiveDirMatch() =>
        Assert.Equal(new AntiCheat.Root(@"C:\Steam\steamapps\common\Game\", 3),
            AntiCheat.RootOf(Rules(), @"c:\steam\STEAMAPPS\common\Game\game.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RootOf_NoPath_IsNull(string? path) => Assert.Null(AntiCheat.RootOf(Rules(), path));

    [Theory]
    [InlineData("EasyAntiCheat_EOS", true)]
    [InlineData("battleye", true)]
    [InlineData("game.des", true)]
    [InlineData("data.pak", false)]
    [InlineData("", false)]
    public void Matches_DefaultMarkers(string name, bool expected)
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.ShiftsJson, "{}");
        var markers = LimitSettings.Read(ShiftConfig.Load(folder.ShiftsJson)).AntiCheatMarkers;

        Assert.Equal(expected, AntiCheat.Matches(name, markers));
    }

    [Fact]
    public void Matches_NoMarkers_IsFalse() => Assert.False(AntiCheat.Matches("EasyAntiCheat", []));
}
