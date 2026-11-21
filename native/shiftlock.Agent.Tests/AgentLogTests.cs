using System.Text.RegularExpressions;

namespace Shiftlock.Agent.Tests;

/// <summary>agent.log: 1 行 1 件・時刻つき．大きくなりすぎない．書けなくても Agent を止めない．</summary>
public sealed class AgentLogTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Write_AppendsOneTimestampedLine()
    {
        var log = new AgentLog(_dir.P("agent.log"));

        log.Write("最初");
        log.Write("2 行に\r\nまたがる文");

        var text = File.ReadAllText(_dir.P("agent.log"));
        Assert.Matches(new Regex(@"\A\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} 最初\n\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} 2 行に またがる文\n\z"), text);
        Assert.NotEqual(0xEF, File.ReadAllBytes(_dir.P("agent.log"))[0]);   // BOM なし
    }

    [Fact]
    public void Write_OverMaxBytes_RotatesToDot1()
    {
        var log = new AgentLog(_dir.P("agent.log"), maxBytes: 100);

        log.Write(new string('a', 120));
        log.Write("次");
        log.Write("その次");

        Assert.Contains(new string('a', 120), File.ReadAllText(_dir.P("agent.log.1")));
        Assert.Equal(2, _dir.Lines("agent.log").Length);
        Assert.EndsWith(" 次", _dir.Lines("agent.log")[0]);
    }

    [Fact]
    public void Write_RotatesAgain_ReplacingTheOldDot1()
    {
        var log = new AgentLog(_dir.P("agent.log"), maxBytes: 100);

        log.Write(new string('a', 120));
        log.Write(new string('b', 120));
        log.Write("c");

        Assert.Contains(new string('b', 120), File.ReadAllText(_dir.P("agent.log.1")));
        Assert.DoesNotContain("aaa", File.ReadAllText(_dir.P("agent.log.1")));
        Assert.Single(_dir.Lines("agent.log"));
    }

    [Fact]
    public void Write_FolderMissing_DoesNotThrow_AndWorksOnceItAppears()
    {
        var folder = _dir.P("まだ無い");
        var log = new AgentLog(Path.Combine(folder, "agent.log"));

        log.Write("捨てられる");
        Assert.False(Directory.Exists(folder));   // 実行フォルダを勝手に作らない

        Directory.CreateDirectory(folder);
        log.Write("書ける");
        Assert.Single(File.ReadAllLines(Path.Combine(folder, "agent.log")));
    }

    [Fact]
    public void Write_FileHeldByAnotherProcess_DoesNotThrow()
    {
        var log = new AgentLog(_dir.P("agent.log"));
        log.Write("前");

        using (new FileStream(_dir.P("agent.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write("書けない");
        }
        log.Write("後");

        Assert.Equal(2, _dir.Lines("agent.log").Length);
    }

    [Fact]
    public void Write_FromManyThreads_KeepsWholeLines()
    {
        var log = new AgentLog(_dir.P("agent.log"));

        Parallel.For(0, 200, i => log.Write($"行 {i}"));

        Assert.Equal(200, _dir.Lines("agent.log").Length);
        Assert.All(_dir.Lines("agent.log"), l => Assert.Matches(@" 行 \d+$", l));
    }
}
