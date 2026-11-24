using System.Diagnostics;
using Shiftlock.Core;

namespace Shiftlock.Agent.Tests;

/// <summary>
/// 実機のテスト．手を加えるのは，このテストが自分で起動した子プロセス（ping）だけ．
/// 子は短命（60 回）で，finally で必ず Kill する（テストの後片付け．製品コードに Kill は無い）．
/// </summary>
public sealed class WindowsLimitControlTests : IDisposable
{
    private readonly WindowsLimitControl _control = new();

    public void Dispose() => _control.Dispose();

    private static Process StartChild()
    {
        var psi = new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        return Process.Start(psi)!;
    }

    private static ProcKey KeyOf(Process p) => new(p.Id, p.StartTime);

    private static void Cleanup(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill();
            }
            p.WaitForExit(5000);
        }
        catch (Exception)
        {
            // 片付けの失敗は無視する
        }
        p.Dispose();
    }

    private static string JobName() => "shiftlock-test-" + Guid.NewGuid().ToString("N");

    private static bool AllSuspended(Process p)
    {
        p.Refresh();
        var any = false;
        foreach (ProcessThread t in p.Threads)
        {
            any = true;
            if (t.ThreadState != System.Diagnostics.ThreadState.Wait || t.WaitReason != ThreadWaitReason.Suspended)
            {
                return false;
            }
        }
        return any;
    }

    [Fact]
    public void Cap_then_uncap_own_child()
    {
        var child = StartChild();
        try
        {
            var job = JobName();
            Assert.True(_control.Cap(job, [KeyOf(child)], 50.0));
            Assert.True(_control.Uncap(job));
            Assert.False(child.HasExited);
        }
        finally
        {
            Cleanup(child);
        }
    }

    [Fact]
    public void Cap_twice_changes_rate()
    {
        var child = StartChild();
        try
        {
            var job = JobName();
            Assert.True(_control.Cap(job, [KeyOf(child)], 50.0));
            Assert.True(_control.Cap(job, [KeyOf(child)], 10.0));
            Assert.True(_control.Uncap(job));
        }
        finally
        {
            Cleanup(child);
        }
    }

    [Fact]
    public void Suspend_then_resume_own_child()
    {
        var child = StartChild();
        var resumed = false;
        try
        {
            Assert.True(_control.Suspend(KeyOf(child)));
            Assert.True(AllSuspended(child));
            Assert.True(_control.Resume(KeyOf(child)));
            resumed = true;
            Assert.False(AllSuspended(child));
            Assert.False(child.HasExited);
        }
        finally
        {
            if (!resumed)
            {
                _control.Resume(KeyOf(child));
            }
            Cleanup(child);
        }
    }

    [Fact]
    public void Wrong_start_time_is_not_touched()
    {
        var child = StartChild();
        try
        {
            var wrong = new ProcKey(child.Id, child.StartTime.AddMinutes(1));
            Assert.False(_control.Suspend(wrong));
            Assert.False(_control.Cap(JobName(), [wrong], 50.0));
            Assert.False(_control.Exists(wrong));
            Assert.True(_control.Exists(KeyOf(child)));
            Assert.False(AllSuspended(child));
        }
        finally
        {
            Cleanup(child);
        }
    }

    [Fact]
    public void Missing_pid_returns_false()
    {
        var child = StartChild();
        var key = KeyOf(child);
        Cleanup(child);
        Assert.False(_control.Exists(key));
        Assert.False(_control.Suspend(key));
        Assert.False(_control.Resume(key));
        Assert.False(_control.Cap(JobName(), [key], 50.0));
    }

    [Fact]
    public void Cap_ignores_a_process_that_already_exited()
    {
        var child = StartChild();
        var gone = StartChild();
        var goneKey = KeyOf(gone);
        Cleanup(gone);
        try
        {
            var job = JobName();
            Assert.True(_control.Cap(job, [KeyOf(child), goneKey], 50.0));
            Assert.True(_control.Uncap(job));
        }
        finally
        {
            Cleanup(child);
        }
    }

    [Fact]
    public void Cap_touches_nothing_when_one_process_cannot_be_opened()
    {
        var child = StartChild();
        var other = StartChild();
        try
        {
            var denied = KeyOf(other);
            using var control = new WindowsLimitControl { DeniedForTest = k => k == denied };
            var job = JobName();

            Assert.False(control.Cap(job, [KeyOf(child), denied], 50.0));
            Assert.False(control.Uncap(job));   // ジョブを持っていない（一部だけ入れて上限をかけていない）
            Assert.True(control.Cap(job, [KeyOf(child)], 50.0));   // 開けるものだけなら，同じ名前でかけられる
            Assert.True(control.Uncap(job));
        }
        finally
        {
            Cleanup(child);
            Cleanup(other);
        }
    }

    [Fact]
    public void Uncap_unknown_job_returns_false()
    {
        Assert.False(_control.Uncap(JobName()));
    }
}
