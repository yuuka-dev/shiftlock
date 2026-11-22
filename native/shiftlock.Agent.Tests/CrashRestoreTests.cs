namespace Shiftlock.Agent.Tests;

/// <summary>未処理の例外の受け口の中身（落ちるときに戻す）．Agent を実際に落とす試験はしない．</summary>
public sealed class CrashRestoreTests
{
    [Fact]
    public void Run_calls_restore_once_and_logs_only_the_type()
    {
        var calls = 0;
        var log = new List<string>();

        CrashRestore.Run(() => calls++, log.Add, new InvalidOperationException("eldenring.exe が原因"));

        Assert.Equal(1, calls);
        var line = Assert.Single(log);
        Assert.Contains("InvalidOperationException", line);
        Assert.DoesNotContain("eldenring", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_swallows_exceptions_from_restore_and_log()
    {
        var calls = 0;

        CrashRestore.Run(() => { calls++; throw new IOException("boom"); }, _ => throw new InvalidOperationException("log"), new object());

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Wait_is_shorter_than_the_normal_gate_wait() =>
        Assert.True(CrashRestore.Wait < TimeSpan.FromSeconds(10));
}
