using System.Diagnostics;

namespace Shiftlock.Agent.Tests;

/// <summary>
/// タスク スケジューラのタスクの状態と切り替え．本番のタスク（shiftlock-guard）には触らない．
/// 実際にタスクを作って確かめる分は，環境変数 SHIFTLOCK_TEST_TASK=1 のときだけ（試験用の名前で作り，終わったら消す）．
/// </summary>
public sealed class GuardTaskTests
{
    private const string TestTask = "shiftlock-guard-test";

    [Fact]
    public void State_OfATaskThatDoesNotExist_IsMissing()
    {
        Assert.Equal(GuardTaskState.Missing, new GuardTask("shiftlock-そんなタスクは無い-" + Guid.NewGuid().ToString("N")).State());
    }

    [Fact]
    public void SetEnabled_OfATaskThatDoesNotExist_Throws()
    {
        var task = new GuardTask("shiftlock-そんなタスクは無い-" + Guid.NewGuid().ToString("N"));

        Assert.ThrowsAny<Exception>(() => task.SetEnabled(false));
    }

    [Fact]
    public void ConfiguredName_DefaultsToTheRealTask_AndCanBeOverridden()
    {
        var before = Environment.GetEnvironmentVariable(GuardTask.NameEnv);
        try
        {
            Environment.SetEnvironmentVariable(GuardTask.NameEnv, null);
            Assert.Equal("shiftlock-guard", GuardTask.ConfiguredName);
            Environment.SetEnvironmentVariable(GuardTask.NameEnv, " " + TestTask + " ");
            Assert.Equal(TestTask, GuardTask.ConfiguredName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GuardTask.NameEnv, before);
        }
    }

    [Fact]
    public void RealTask_StateAndSetEnabled_WhenAskedTo()
    {
        if (Environment.GetEnvironmentVariable("SHIFTLOCK_TEST_TASK") != "1")
        {
            return;   // 実際にタスクを作るので，頼まれたときだけ
        }
        var task = new GuardTask(TestTask);
        Schtasks($"/Create /TN {TestTask} /TR \"cmd.exe /c exit\" /SC ONCE /ST 23:59 /F");
        try
        {
            Assert.Equal(GuardTaskState.Enabled, task.State());
            task.SetEnabled(false);
            Assert.Equal(GuardTaskState.Disabled, task.State());
            task.SetEnabled(false);   // 2 回しても同じ
            Assert.Equal(GuardTaskState.Disabled, task.State());
            task.SetEnabled(true);
            Assert.Equal(GuardTaskState.Enabled, task.State());
        }
        finally
        {
            Schtasks($"/Delete /TN {TestTask} /F");
        }
        Assert.Equal(GuardTaskState.Missing, task.State());
    }

    private static void Schtasks(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit(20000);
        Assert.True(p.ExitCode == 0, $"schtasks {arguments}: {p.StandardError.ReadToEnd()}");
    }
}
