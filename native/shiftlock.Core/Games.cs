using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shiftlock.Core;

/// <summary>ゲームの判定に使う決まり．フォルダは絶対パスで末尾に \ が付いている．</summary>
public sealed record GameRules(IReadOnlyList<string> GameDirs, IReadOnlyList<string> ExcludeDirs, IReadOnlyList<string> ExcludeNames, IReadOnlyList<string> ProcessNames);

/// <summary>
/// 動いているプロセスがゲームかどうかの判定（pc/guard.ps1 の Normalize-Dir・Test-GameExe・Get-RunningGames と同じ決まり）．
/// プロセスの一覧や Steam のフォルダ探しは Windows 側（Agent）が行い，ここは決まりだけを持つ．
/// 見るのは「ゲームが動いているか」だけで，ゲームを止める処理は無い．
/// </summary>
public static partial class Games
{
    /// <summary>game_exclude が null のときの除外名（config.py の DEFAULTS と同じ 9 個）．</summary>
    private static readonly string[] DefaultExclude =
        ["UnityCrashHandler*", "CrashReport*", "*crashpad*", "unins*", "vc_redist*", "dxsetup*", "*Redist*", "steamerrorreporter*", "*Setup*"];

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VdfPath();

    /// <summary>
    /// 設定からゲームの決まりを作る．steamCommonDirs は Agent が集めた steamapps\common の一覧（steam_libraries が偽なら使わない）．
    /// フォルダは絶対パスだけ使う（guard.ps1 は相対パスを実行時のフォルダから解釈するが，Agent ではどこになるか決まらないので使わない）．
    /// 読めないパス・文字列でない値は無視する．game_exclude は設定に無い・null なら既定の 9 個，空の配列なら除外なし．
    /// </summary>
    public static GameRules Rules(ShiftConfig cfg, IEnumerable<string> steamCommonDirs)
    {
        var raw = cfg.Raw;
        var useSteam = raw["steam_libraries"] is null || JsonX.Truthy(raw["steam_libraries"]);
        var dirs = (useSteam ? steamCommonDirs : []).Concat(Strings(raw["game_dirs"]));
        return new GameRules(
            NormalizeDirs(dirs),
            NormalizeDirs(Strings(raw["game_exclude_dirs"])),
            raw["game_exclude"] is null ? DefaultExclude : Strings(raw["game_exclude"]),
            Strings(raw["game_processes"]));
    }

    /// <summary>exe がゲームか: ゲームのフォルダの下にあり，除外フォルダの下でなく，除外名に当たらない．</summary>
    public static bool IsGameExe(GameRules rules, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !Under(rules.GameDirs, exePath) || Under(rules.ExcludeDirs, exePath))
        {
            return false;
        }
        string leaf, stem;
        try
        {
            leaf = Path.GetFileName(exePath);
            stem = Path.GetFileNameWithoutExtension(exePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return !rules.ExcludeNames.Any(n => Like(leaf, n) || Like(stem, n) || Like(exePath, n));
    }

    /// <summary>プロセスがゲームか: IsGameExe か，名前が game_processes にある（大文字小文字は区別しない．パスが取れなくても効く）．</summary>
    public static bool IsGame(GameRules rules, string processName, string? exePath) =>
        IsGameExe(rules, exePath) || rules.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Steam の libraryfolders.vdf から，ライブラリのフォルダ（"path" "..." の行）を拾う．\\ は \ に戻す．</summary>
    public static IReadOnlyList<string> SteamLibraryPaths(string libraryFoldersVdf)
    {
        var found = new List<string>();
        foreach (var line in libraryFoldersVdf.Split('\n'))
        {
            var match = VdfPath().Match(line);
            if (match.Success)
            {
                found.Add(match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal));
            }
        }
        return found;
    }

    /// <summary>設定の値を文字列の並びにする（PowerShell の @(値) | Where-Object { $_ }）．文字列 1 つでも受ける．空・文字列でない要素は捨てる．</summary>
    private static List<string> Strings(JsonNode? v) => v switch
    {
        JsonArray a => [.. a.Select(JsonX.Str).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)],
        _ when JsonX.Str(v) is { Length: > 0 } s => [s],
        _ => [],
    };

    private static List<string> NormalizeDirs(IEnumerable<string> dirs) =>
        [.. dirs.Select(NormalizeDir).Where(d => d is not null).Select(d => d!).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>絶対パスにして末尾を \ 1 つにする（"C:\Games" が "C:\GamesX" に前方一致しないように）．使えないパスは null．</summary>
    private static string? NormalizeDir(string? p)
    {
        if (string.IsNullOrEmpty(p))
        {
            return null;
        }
        try
        {
            return Path.IsPathFullyQualified(p) ? Path.GetFullPath(p).TrimEnd('\\') + "\\" : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool Under(IReadOnlyList<string> dirs, string path) =>
        dirs.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ワイルドカードの一致（PowerShell の -like のうち * と ?．全体が当たること．大文字小文字は区別しない）．
    /// [abc] の形は使わない（文字そのものとして比べる）．
    /// </summary>
    private static bool Like(string text, string pattern)
    {
        // * に当たったら位置を覚え，合わなくなったら * に 1 文字多く食わせてやり直す
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])) && pattern[p] != '*')
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }
}
