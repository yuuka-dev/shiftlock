using System.Diagnostics;

namespace Shiftlock.Agent.Tests;

/// <summary>Windows から取る値．実機の値なので形だけ確かめる．</summary>
public sealed class WindowsProbeTests
{
    private readonly WindowsProbe _probe = new();

    [Fact]
    public void IdleSeconds_IsNotNegative_AndNotAbsurd()
    {
        var idle = _probe.IdleSeconds();

        // 49.7 日（TickCount の 1 周）より大きくはならない
        Assert.InRange(idle, 0, uint.MaxValue / 1000.0);
    }

    [Fact]
    public void UptimeSeconds_IsPositive()
    {
        Assert.True(_probe.UptimeSeconds() > 0);
    }

    [Fact]
    public void Processes_ContainThisProcess_WithItsPath()
    {
        using var me = Process.GetCurrentProcess();

        var found = _probe.Processes().Where(p => p.Name == me.ProcessName && p.Path is not null).ToList();

        Assert.Contains(found, p => string.Equals(p.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Processes_NeverHaveEmptyNames()
    {
        var all = _probe.Processes();

        Assert.NotEmpty(all);
        Assert.All(all, p => Assert.False(string.IsNullOrEmpty(p.Name)));
    }

    [Fact]
    public void SteamCommonDirs_DoesNotThrow_AndReturnsExistingDistinctDirs()
    {
        var dirs = _probe.SteamCommonDirs();

        Assert.All(dirs, d => Assert.True(Directory.Exists(d)));
        Assert.All(dirs, d => Assert.EndsWith(@"steamapps\common", d, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(dirs.Count, dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SteamCommonDirs_FromRoots_ReadsLibraryFolders_AndSkipsMissing()
    {
        using var dir = new TempDir();
        var main = dir.P("Steam");
        var second = dir.P("Lib2");
        Directory.CreateDirectory(Path.Combine(main, "steamapps", "common"));
        Directory.CreateDirectory(Path.Combine(second, "steamapps", "common"));
        var vdf = $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{main.Replace(@"\", @"\\")}\"\n\t}}\n"
            + $"\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{second.Replace(@"\", @"\\")}\"\n\t}}\n"
            + $"\t\"2\"\n\t{{\n\t\t\"path\"\t\t\"{dir.P("外したドライブ").Replace(@"\", @"\\")}\"\n\t}}\n}}\n";
        File.WriteAllText(Path.Combine(main, "steamapps", "libraryfolders.vdf"), vdf);

        var dirs = WindowsProbe.SteamCommonDirs([main, main.ToUpperInvariant(), dir.P("無い Steam")]);

        Assert.Equal(
            new[] { Path.Combine(main, "steamapps", "common"), Path.Combine(second, "steamapps", "common") }.Order(StringComparer.OrdinalIgnoreCase),
            dirs.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }
}
