namespace Shiftlock.Agent.Tests;

public sealed class WindowsFolderListerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shiftlock-lister-" + Guid.NewGuid().ToString("N"));

    public WindowsFolderListerTests()
    {
        // root/a.txt, d1/b.txt, d1/d2/c.txt, d1/d2/d3/e.txt, d1/d2/d3/d4/f.txt
        var d4 = Path.Combine(_root, "d1", "d2", "d3", "d4");
        Directory.CreateDirectory(d4);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(_root, "d1", "b.txt"), "");
        File.WriteAllText(Path.Combine(_root, "d1", "d2", "c.txt"), "");
        File.WriteAllText(Path.Combine(_root, "d1", "d2", "d3", "e.txt"), "");
        File.WriteAllText(Path.Combine(d4, "f.txt"), "");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Depth3_includes_third_level_not_fourth()
    {
        var names = new WindowsFolderLister().Names(_root, 3)!;
        Assert.Contains("e.txt", names);
        Assert.Contains("d4", names);
        Assert.DoesNotContain("f.txt", names);
    }

    [Fact]
    public void Depth0_is_top_level_only()
    {
        var names = new WindowsFolderLister().Names(_root, 0)!;
        Assert.Equivalent(new[] { "a.txt", "d1" }, names);
    }

    [Fact]
    public void Missing_dir_returns_null()
    {
        Assert.Null(new WindowsFolderLister().Names(Path.Combine(_root, "nothing"), 2));
    }

    [Fact]
    public void MaxEntries_stops_early()
    {
        Assert.Equal(2, new WindowsFolderLister().Names(_root, 3, 2)!.Count);
    }
}
