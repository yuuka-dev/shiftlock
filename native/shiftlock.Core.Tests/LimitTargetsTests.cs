namespace Shiftlock.Core.Tests;

/// <summary>減速・一時停止の対象の決定（設計書 §4.3）．</summary>
public sealed class LimitTargetsTests
{
    private static readonly DateTime T = new(2026, 12, 7, 20, 0, 0);

    private static ProcSample P(int pid, int parent, string name, int startMin = 0) =>
        new(pid, parent, T.AddMinutes(startMin), TimeSpan.Zero, name);

    private static readonly LimitSettings Settings = new(
        [75, 50, 25, 10], TimeSpan.FromMinutes(30),
        new HashSet<string> { "photoshop.exe", "code.exe" }, new HashSet<string> { "excluded.exe" },
        ["EasyAntiCheat*"],
        new HashSet<string> { "steam.exe", "conhost.exe", "claude.exe", "node.exe", "notepad.exe" },
        new HashSet<string> { "claude.exe", "node.exe" });

    private static readonly IReadOnlyDictionary<string, string> Registered =
        new Dictionary<string, string> { ["photoshop.exe"] = "adobe", ["code.exe"] = "dev", ["notepad.exe"] = "dev" };

    private static IReadOnlySet<string> Declared(params string[] names) => new HashSet<string>(names);

    private static IReadOnlyDictionary<string, string> Verdicts(params (string, string)[] v) =>
        v.ToDictionary(x => x.Item1, x => x.Item2);

    private static IReadOnlyList<LimitApp> Resolve(ProcSample[] procs, string[] gameExes, IReadOnlySet<string>? declared = null,
        IReadOnlyDictionary<string, string>? games = null, LimitSettings? settings = null, bool hasParents = true,
        IReadOnlySet<string>? declaredExclude = null)
    {
        var gameKeys = procs.Where(p => gameExes.Contains(p.Name)).Select(p => p.Key).ToHashSet();
        var sample = new MeterSample(T.AddHours(2), TimeSpan.Zero, procs, null, 0, gameKeys, 8, hasParents);
        return LimitTargets.Resolve(sample, Registered, settings ?? Settings, declared ?? Declared(), games ?? Verdicts(),
            declaredExclude);
    }

    private static LimitVerdict VerdictOf(IReadOnlyList<LimitApp> apps, string exe) => apps.Single(a => a.Exe == exe).Verdict;

    [Fact]
    public void Game_Target()
    {
        var apps = Resolve([P(1, 0, "eldenring.exe")], ["eldenring.exe"], games: Verdicts(("eldenring.exe", "target")));
        Assert.Equal(LimitVerdict.Target, VerdictOf(apps, "eldenring.exe"));
        Assert.True(apps.Single().IsGame);
    }

    [Fact]
    public void Game_AntiCheat() =>
        Assert.Equal(LimitVerdict.AntiCheat, VerdictOf(Resolve([P(1, 0, "apex.exe")], ["apex.exe"], games: Verdicts(("apex.exe", "anticheat"))), "apex.exe"));

    [Fact]
    public void Game_NoVerdict_IsUnknown() =>
        Assert.Equal(LimitVerdict.Unknown, VerdictOf(Resolve([P(1, 0, "eldenring.exe")], ["eldenring.exe"]), "eldenring.exe"));

    [Fact]
    public void Game_UnknownVerdict_IsUnknown() =>
        Assert.Equal(LimitVerdict.Unknown, VerdictOf(Resolve([P(1, 0, "eldenring.exe")], ["eldenring.exe"], games: Verdicts(("eldenring.exe", "unknown"))), "eldenring.exe"));

    [Fact]
    public void Game_InExclude_IsExcluded_EvenIfAntiCheat() =>
        Assert.Equal(LimitVerdict.Excluded, VerdictOf(
            Resolve([P(1, 0, "excluded.exe")], ["excluded.exe"], games: Verdicts(("excluded.exe", "anticheat"))), "excluded.exe"));

    [Fact]
    public void Excluded_at_declaration_stays_excluded_after_include() =>
        // 宣言の後に include して今の設定から外れても，その乗務では対象にならない（同意の表示に載っていたため）
        Assert.Equal(LimitVerdict.Excluded, VerdictOf(
            Resolve([P(1, 0, "apex.exe")], ["apex.exe"], games: Verdicts(("apex.exe", "target")), declaredExclude: Declared("apex.exe")), "apex.exe"));

    [Fact]
    public void Excluded_after_declaration_takes_effect() =>
        // 宣言に無く今の設定にある（宣言の後に外した）→ すぐ効く
        Assert.Equal(LimitVerdict.Excluded, VerdictOf(
            Resolve([P(1, 0, "excluded.exe")], ["excluded.exe"], games: Verdicts(("excluded.exe", "target")), declaredExclude: Declared()), "excluded.exe"));

    [Fact]
    public void Old_declaration_without_limit_exclude_uses_current()
    {
        var games = Verdicts(("excluded.exe", "target"), ("apex.exe", "target"));
        var apps = Resolve([P(1, 0, "excluded.exe"), P(2, 0, "apex.exe")], ["excluded.exe", "apex.exe"], games: games, declaredExclude: null);
        Assert.Equal(LimitVerdict.Excluded, VerdictOf(apps, "excluded.exe"));
        Assert.Equal(LimitVerdict.Target, VerdictOf(apps, "apex.exe"));
    }

    [Fact]
    public void Registered_InAppsAndDeclared_IsTarget()
    {
        var apps = Resolve([P(1, 0, "photoshop.exe")], [], Declared("photoshop.exe"));
        Assert.Equal(LimitVerdict.Target, VerdictOf(apps, "photoshop.exe"));
        Assert.False(apps.Single().IsGame);
    }

    [Fact]
    public void Registered_AddedAfterDeclaration_IsNotAdded() =>
        Assert.Equal(LimitVerdict.NotAdded, VerdictOf(Resolve([P(1, 0, "photoshop.exe")], [], Declared()), "photoshop.exe"));

    [Fact]
    public void Registered_DeclaredButRemovedFromSettings_IsNotAdded()
    {
        var settings = Settings with { Apps = new HashSet<string>() };
        Assert.Equal(LimitVerdict.NotAdded, VerdictOf(Resolve([P(1, 0, "photoshop.exe")], [], Declared("photoshop.exe"), settings: settings), "photoshop.exe"));
    }

    [Fact]
    public void Registered_AndGame_IsTreatedAsRegistered()
    {
        // 登録に当たるので，ゲームの判定（anticheat）は見ない
        var apps = Resolve([P(1, 0, "photoshop.exe")], ["photoshop.exe"], Declared("photoshop.exe"), Verdicts(("photoshop.exe", "anticheat")));
        Assert.Equal(LimitVerdict.Target, VerdictOf(apps, "photoshop.exe"));
        Assert.False(apps.Single().IsGame);
    }

    [Fact]
    public void Registered_InNever_IsProtected()
    {
        // notepad.exe は登録も足してあるが never にある
        var settings = Settings with { Apps = new HashSet<string> { "notepad.exe" } };
        var apps = Resolve([P(1, 0, "notepad.exe")], [], Declared("notepad.exe"), settings: settings);
        Assert.Equal(LimitVerdict.Protected, VerdictOf(apps, "notepad.exe"));
    }

    [Fact]
    public void GrandchildClaude_ProtectsWholeApp()
    {
        var procs = new[] { P(1, 0, "code.exe"), P(2, 1, "conhost.exe", 1), P(3, 2, "claude.exe", 2) };
        Assert.Equal(LimitVerdict.Protected, VerdictOf(Resolve(procs, [], Declared("code.exe")), "code.exe"));
    }

    [Fact]
    public void Ancestor_Steam_DoesNotMatter()
    {
        var procs = new[] { P(1, 0, "steam.exe"), P(2, 1, "eldenring.exe", 1) };
        Assert.Equal(LimitVerdict.Target, VerdictOf(Resolve(procs, ["eldenring.exe"], games: Verdicts(("eldenring.exe", "target"))), "eldenring.exe"));
    }

    [Fact]
    public void ChildConhost_OnlyIsStillTarget()
    {
        var procs = new[] { P(1, 0, "eldenring.exe"), P(2, 1, "conhost.exe", 1) };
        Assert.Equal(LimitVerdict.Target, VerdictOf(Resolve(procs, ["eldenring.exe"], games: Verdicts(("eldenring.exe", "target"))), "eldenring.exe"));
    }

    [Fact]
    public void SameNameDescendant_IsNotAProtector()
    {
        // 根と同じ名前の子孫は「根自身」として数える（protect に自分の名前があっても見送りにしない）
        var settings = Settings with { Never = new HashSet<string>(), Protect = new HashSet<string> { "eldenring.exe" } };
        var procs = new[] { P(1, 0, "eldenring.exe"), P(2, 1, "eldenring.exe", 1) };
        Assert.Equal(LimitVerdict.Target, VerdictOf(
            Resolve(procs, ["eldenring.exe"], games: Verdicts(("eldenring.exe", "target")), settings: settings), "eldenring.exe"));
    }

    [Fact]
    public void NoParents_UsesRootsOnly()
    {
        var procs = new[] { P(1, 0, "code.exe"), P(3, 1, "claude.exe", 2) };
        var apps = Resolve(procs, [], Declared("code.exe"), hasParents: false);
        Assert.Single(apps.Single().Tree);
    }

    [Fact]
    public void NeitherRegisteredNorGame_IsNotInResult() =>
        Assert.Empty(Resolve([P(1, 0, "chrome.exe")], []));

    [Fact]
    public void TwoRoots_OneEntry_WithBothTrees()
    {
        var procs = new[] { P(1, 0, "eldenring.exe"), P(2, 1, "x.exe", 1), P(3, 0, "eldenring.exe", 2) };
        var apps = Resolve(procs, ["eldenring.exe"], games: Verdicts(("eldenring.exe", "target")));
        Assert.Equal(3, apps.Single().Tree.Count);
    }

    [Fact]
    public void Result_IsSortedByName()
    {
        var procs = new[] { P(1, 0, "photoshop.exe"), P(2, 0, "apex.exe"), P(3, 0, "eldenring.exe") };
        var apps = Resolve(procs, ["apex.exe", "eldenring.exe"], Declared("photoshop.exe"));
        Assert.Equal(["apex.exe", "eldenring.exe", "photoshop.exe"], apps.Select(a => a.Exe));
    }
}
