namespace Shiftlock.Core.Tests;

/// <summary>exe 名のツリー（根と子孫）．</summary>
public sealed class ProcTreeTests
{
    private static readonly DateTime T = new(2026, 12, 7, 20, 0, 0);

    private static ProcSample P(int pid, int parent, string name, int startMin = 0) =>
        new(pid, parent, T.AddMinutes(startMin), TimeSpan.Zero, name);

    private static int[] Pids(IReadOnlyList<ProcSample> tree) => [.. tree.Select(p => p.Pid).Order()];

    [Fact]
    public void OfName_IncludesChildrenAndGrandchildren()
    {
        var procs = new[] { P(1, 0, "steam.exe"), P(10, 1, "eldenring.exe", 1), P(11, 10, "node.exe", 2), P(12, 11, "conhost.exe", 3), P(99, 1, "notepad.exe", 1) };

        Assert.Equal([10, 11, 12], Pids(ProcTree.OfName(procs, "eldenring.exe")));
    }

    [Fact]
    public void OfName_ParentStartedAfterChild_IsNotChild()
    {
        // pid 11 の親 10 は，子より後に始まったプロセス（PID の使い回し）
        var procs = new[] { P(10, 1, "eldenring.exe", 1), P(11, 10, "node.exe", 0) };

        Assert.Equal([10], Pids(ProcTree.OfName(procs, "eldenring.exe")));
    }

    [Fact]
    public void OfName_TwoRoots_BothTrees()
    {
        var procs = new[] { P(10, 1, "code.exe"), P(11, 10, "node.exe", 1), P(20, 1, "code.exe", 2), P(21, 20, "conhost.exe", 3) };

        Assert.Equal([10, 11, 20, 21], Pids(ProcTree.OfName(procs, "code.exe")));
    }

    [Fact]
    public void OfName_NoDuplicates_WhenNestedSameName()
    {
        var procs = new[] { P(10, 1, "code.exe"), P(11, 10, "code.exe", 1), P(12, 11, "code.exe", 2) };

        var tree = ProcTree.OfName(procs, "code.exe");

        Assert.Equal(3, tree.Count);
        Assert.Equal(3, tree.Select(p => p.Key).Distinct().Count());
    }

    [Fact]
    public void OfName_NoRoot_IsEmpty() =>
        Assert.Empty(ProcTree.OfName([P(1, 0, "steam.exe")], "eldenring.exe"));

    [Fact]
    public void OfName_SelfParent_DoesNotLoop() =>
        Assert.Equal([10], Pids(ProcTree.OfName([P(10, 10, "apex.exe")], "apex.exe")));
}
