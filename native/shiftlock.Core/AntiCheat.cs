namespace Shiftlock.Core;

/// <summary>
/// アンチチートの印を探す場所と名前の一致（設計書 §4.4）．フォルダを実際に見るのは Agent．ここは決まりだけを持つ．
/// </summary>
public static class AntiCheat
{
    /// <summary>探す根のフォルダ（末尾に \）．Depth 3 = 根の下を 3 階層，0 = そのフォルダの直下だけ．</summary>
    public sealed record Root(string Dir, int Depth);

    private const int DeepDepth = 3;

    /// <summary>
    /// 根のフォルダの決め方．exe がゲームのフォルダの下にあれば，そのフォルダ直下の 1 階層目（3 階層まで見る）．
    /// ゲームのフォルダの直下にある exe は根を決められないので，そのフォルダの直下だけ（隣のゲームの印を拾わない）．
    /// ゲームのフォルダの外（game_processes の名前だけでゲームになった exe）は，exe のあるフォルダ．パスが無い・使えない → null．
    /// </summary>
    public static Root? RootOf(GameRules rules, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return null;
        }
        // 入れ子のゲームのフォルダなら，いちばん深いものを使う
        var dir = rules.GameDirs
            .Where(d => exePath.StartsWith(d, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length)
            .FirstOrDefault();
        if (dir is not null)
        {
            var rest = exePath[dir.Length..];
            var slash = rest.IndexOf('\\');
            return slash > 0
                ? new Root(dir + rest[..slash] + "\\", DeepDepth)
                : new Root(dir, 0);
        }
        try
        {
            return Path.GetDirectoryName(exePath) is { Length: > 0 } own
                ? new Root(own.TrimEnd('\\') + "\\", DeepDepth)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>ファイル・フォルダ・プロセスの名前が印のどれかに当たるか（Games.Like と同じ一致．大文字小文字は区別しない）．</summary>
    public static bool Matches(string fileOrDirName, IReadOnlyList<string> markers) =>
        markers.Any(m => Games.Like(fileOrDirName, m));
}
