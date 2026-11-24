namespace Shiftlock.Core;

public enum LimitVerdict
{
    /// <summary>減速・一時停止の対象．</summary>
    Target,
    /// <summary>ゲームで，アンチチートの印が見つかった．</summary>
    AntiCheat,
    /// <summary>ゲームで，アンチチートを調べられていない（判定が無い・調べられなかった）．</summary>
    Unknown,
    /// <summary>ゲームで，limit.exclude にある（手で外した）．</summary>
    Excluded,
    /// <summary>登録アプリだが，足していない（limit.apps か宣言に固定した一覧に無い）．</summary>
    NotAdded,
    /// <summary>対象外の名前（limit.never），または子孫に守る名前（limit.protect）がある．</summary>
    Protected,
}

/// <summary>Tree は同じ名前のプロセスすべての根と子孫（代替の経路で親が分からないときは根だけ）．</summary>
public sealed record LimitApp(string Exe, bool IsGame, IReadOnlyList<ProcSample> Tree, LimitVerdict Verdict);

/// <summary>減速・一時停止の対象の決定（設計書 §4.3）．</summary>
public static class LimitTargets
{
    public const string VerdictTarget = "target";
    public const string VerdictAntiCheat = "anticheat";
    public const string VerdictUnknown = "unknown";

    /// <summary>
    /// 動いているアプリ（登録アプリ・ゲーム）ごとに判定を返す（名前の順）．登録にもゲームにも無い exe は入れない．
    /// games は「exe 名 → target｜anticheat｜unknown」（Agent がフォルダを見て決めた結果．無い名前は unknown）．
    /// declaredApps は宣言に固定した limit_apps．登録に当たる exe はゲームの判定を見ない（区切り 3 と同じ）．
    /// declaredExclude は宣言に固定した limit_exclude（無い古い宣言・null は空）．ゲームは「宣言の limit_exclude ∪ 今の limit.exclude」
    /// にあれば外す（外す側は今の設定がすぐ効き，宣言の後に include で広げた分は次の宣言から効く）．
    /// </summary>
    public static IReadOnlyList<LimitApp> Resolve(MeterSample sample, IReadOnlyDictionary<string, string> registered,
        LimitSettings settings, IReadOnlySet<string> declaredApps, IReadOnlyDictionary<string, string> games,
        IReadOnlySet<string>? declaredExclude = null)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in sample.Procs)
        {
            if (registered.ContainsKey(p.Name) || sample.Games.Contains(p.Key))
            {
                names.Add(p.Name);
            }
        }

        var result = new List<LimitApp>(names.Count);
        foreach (var exe in names)
        {
            var isGame = !registered.ContainsKey(exe);
            var tree = sample.HasParents
                ? ProcTree.OfName(sample.Procs, exe)
                : [.. sample.Procs.Where(p => p.Name == exe)];
            result.Add(new LimitApp(exe, isGame, tree, Judge(exe, isGame, tree, settings, declaredApps, games, declaredExclude)));
        }
        return result;
    }

    private static LimitVerdict Judge(string exe, bool isGame, IReadOnlyList<ProcSample> tree, LimitSettings settings,
        IReadOnlySet<string> declaredApps, IReadOnlyDictionary<string, string> games, IReadOnlySet<string>? declaredExclude)
    {
        if (isGame)
        {
            if (settings.Exclude.Contains(exe) || declaredExclude?.Contains(exe) == true)
            {
                return LimitVerdict.Excluded;
            }
            var verdict = games.GetValueOrDefault(exe);
            if (verdict == VerdictAntiCheat)
            {
                return LimitVerdict.AntiCheat;
            }
            if (verdict != VerdictTarget)
            {
                return LimitVerdict.Unknown;
            }
        }
        else if (!settings.Apps.Contains(exe) || !declaredApps.Contains(exe))
        {
            return LimitVerdict.NotAdded;
        }
        if (settings.Never.Contains(exe))
        {
            return LimitVerdict.Protected;
        }
        // 祖先は見ない．根と同じ名前のプロセスは根自身として数える
        return tree.Any(p => p.Name != exe && settings.Protect.Contains(p.Name))
            ? LimitVerdict.Protected
            : LimitVerdict.Target;
    }
}
