using System.Text.Json.Nodes;

namespace Shiftlock.Core.Tests;

/// <summary>ゲームの判定が pc/guard.ps1（Normalize-Dir・Test-GameExe・Get-RunningGames）と同じ決まりになること．</summary>
public sealed class GamesTests
{
    private static GameRules Rules(string over = "{}", params string[] steam)
    {
        var shifts = JsonNode.Parse("""{"lang":"ja","game_dirs":["C:\\Games"]}""")!.AsObject();
        foreach (var (k, v) in JsonNode.Parse(over)!.AsObject())
        {
            shifts[k] = v?.DeepClone();
        }
        using var folder = Vectors.Materialize(new JsonObject { ["shifts.json"] = shifts });
        return Games.Rules(ShiftConfig.Load(folder.ShiftsJson), steam);
    }

    [Theory]
    [InlineData(@"C:\Games\Foo\bin\foo.exe", true)]
    [InlineData(@"c:\games\FOO\foo.exe", true)]            // 前方一致は大文字小文字を区別しない
    [InlineData(@"C:\GamesX\a.exe", false)]                // フォルダの末尾に \ を付けて比べる
    [InlineData(@"C:\Games", false)]
    [InlineData(@"D:\Games\Foo\foo.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGameExe_UnderGameDir(string? path, bool expected)
    {
        Assert.Equal(expected, Games.IsGameExe(Rules(), path));
    }

    [Theory]
    [InlineData(@"C:\Games\Foo\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Games\Foo\vc_redist.x64.exe")]
    [InlineData(@"C:\Games\Foo\FooSetup.exe")]
    [InlineData(@"C:\Games\Foo\UNINS000.EXE")]             // 除外名も大文字小文字を区別しない
    [InlineData(@"C:\Games\Foo\_CommonRedist\DirectX\a.exe")]   // フルパスにも当てる（*Redist*）
    public void IsGameExe_DefaultExcludes(string path)
    {
        Assert.False(Games.IsGameExe(Rules(), path));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    public void IsGameExe_EmptyExcludeList_ExcludesNothing(string exclude)
    {
        var rules = Rules($$"""{"game_exclude":{{exclude}}}""");

        Assert.True(Games.IsGameExe(rules, @"C:\Games\Foo\UnityCrashHandler64.exe"));
    }

    [Fact]
    public void IsGameExe_NullExclude_UsesDefaults()
    {
        var rules = Rules("""{"game_exclude":null}""");

        Assert.Equal(9, rules.ExcludeNames.Count);
        Assert.False(Games.IsGameExe(rules, @"C:\Games\Foo\UnityCrashHandler64.exe"));
    }

    [Theory]
    [InlineData("foo.exe", @"C:\Games\A\foo.exe", false)]      // ファイル名
    [InlineData("foo", @"C:\Games\A\foo.exe", false)]          // 拡張子なしの名前
    [InlineData(@"C:\Games\A\*", @"C:\Games\A\foo.exe", false)]   // フルパス
    [InlineData("fo?", @"C:\Games\A\foo.exe", false)]
    [InlineData("fo?", @"C:\Games\A\fooo.exe", true)]          // ? は 1 文字
    [InlineData("fo", @"C:\Games\A\foo.exe", true)]            // 全体が当たらなければ除外しない
    [InlineData("*o*o*", @"C:\Games\A\foo.exe", false)]
    public void IsGameExe_ExcludeName_MatchesLeafBaseOrFullPath(string pattern, string path, bool expected)
    {
        var rules = Rules(new JsonObject { ["game_exclude"] = new JsonArray(pattern) }.ToJsonString());

        Assert.Equal(expected, Games.IsGameExe(rules, path));
    }

    [Fact]
    public void IsGameExe_UnderExcludeDir_IsNotGame()
    {
        var rules = Rules("""{"game_exclude_dirs":["c:\\games\\Tools\\"]}""");

        Assert.False(Games.IsGameExe(rules, @"C:\Games\Tools\editor.exe"));
        Assert.True(Games.IsGameExe(rules, @"C:\Games\ToolsX\editor.exe"));
    }

    [Theory]
    [InlineData("Notepad", true)]
    [InlineData("notepad", true)]                           // PowerShell の -contains は大文字小文字を区別しない
    [InlineData("notepad2", false)]
    [InlineData("", false)]
    public void IsGame_ByProcessName_EvenWithoutPath(string name, bool expected)
    {
        var rules = Rules("""{"game_processes":["Notepad",""]}""");

        Assert.Equal(expected, Games.IsGame(rules, name, null));
    }

    [Fact]
    public void IsGame_ByPath_EvenIfNameIsUnknown()
    {
        Assert.True(Games.IsGame(Rules(), "foo", @"C:\Games\Foo\foo.exe"));
        Assert.False(Games.IsGame(Rules(), "UnityCrashHandler64", @"C:\Games\Foo\UnityCrashHandler64.exe"));
    }

    [Fact]
    public void Rules_SteamDirs_AreUsed_UnlessSteamLibrariesIsFalse()
    {
        const string exe = @"D:\SteamLibrary\steamapps\common\Foo\foo.exe";
        const string steam = @"D:\SteamLibrary\steamapps\common";

        Assert.True(Games.IsGameExe(Rules("{}", steam), exe));
        Assert.True(Games.IsGameExe(Rules("""{"steam_libraries":null}""", steam), exe));
        Assert.False(Games.IsGameExe(Rules("""{"steam_libraries":false}""", steam), exe));
    }

    [Fact]
    public void Rules_Dirs_AreNormalized_AndDeduplicated()
    {
        var rules = Rules("""{"game_dirs":["C:\\Games","c:\\games\\","C:/Games/Sub/..","D:\\Other\\\\"]}""", @"C:\GAMES");

        // 同じフォルダは 1 つ（大文字小文字は区別しない．どの表記が残るかは決めない）
        Assert.Equal([@"C:\Games\", @"D:\Other\"], rules.GameDirs, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""["C:\\a<>|", "C:\\Games"]""")]
    [InlineData("""["C:\\a\u0000b", "C:\\Games"]""")]
    [InlineData("""["", null, 5, {"x": 1}, "Games", "C:\\Games"]""")]   // 文字列でないもの・相対パスは使わない
    public void Rules_BrokenDirs_AreIgnored(string dirs)
    {
        var rules = Rules($$"""{"game_dirs":{{dirs}},"game_exclude_dirs":{{dirs}}}""");

        Assert.Contains(@"C:\Games\", rules.GameDirs);
        Assert.DoesNotContain(rules.GameDirs, d => d.Contains("Games", StringComparison.Ordinal) && d != @"C:\Games\");
        Assert.False(Games.IsGameExe(rules, @"C:\Games\Foo\foo.exe"));   // 除外フォルダにも同じ C:\Games が入っている
        Assert.False(Games.IsGameExe(rules, @"C:\Other\foo.exe"));
    }

    [Theory]
    [InlineData("\"C:\\\\Games\"")]                         // 配列でなく文字列 1 つでも受ける（PowerShell の @(値)）
    [InlineData("5")]
    [InlineData("null")]
    public void Rules_ValuesThatAreNotLists_DoNotThrow(string value)
    {
        var rules = Rules($$"""{"game_dirs":{{value}},"game_exclude_dirs":{{value}},"game_processes":{{value}},"game_exclude":{{value}}}""");

        Assert.False(Games.IsGameExe(rules, @"C:\Other\foo.exe"));
    }

    [Fact]
    public void SteamLibraryPaths_ReadsPathLines()
    {
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"label\"\t\t\"\"\n\t}\n"
            + "\t\"1\"\n\t{\n\t\t\"PATH\"\t\t\"D:\\\\SteamLibrary\"\r\n\t\t\"apps\"\n\t\t{\n\t\t\t\"path\"\n\t\t}\n\t}\n}\n";

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], Games.SteamLibraryPaths(vdf));
        Assert.Empty(Games.SteamLibraryPaths(""));
    }
}
