using Microsoft.Win32;

namespace Shiftlock.Agent;

/// <summary>
/// 入口．同時に動く Agent は 1 つだけ（2 つ目は何もせず終わる）．
/// 起動 1 分後と以後 5 分おきに，UI でないスレッドで見張りを 1 回行う．スリープからの復帰でも 30 秒後に 1 回試す
/// （5 分たっていなければ Watcher が飛ばす）．
/// </summary>
public static class Program
{
    /// <summary>二重起動を見分ける名前（ログオンしているセッションごと）．</summary>
    private const string MutexName = @"Local\shiftlock-agent";

    /// <summary>見張りの仕事の名前（Scheduler の一覧の中で）．</summary>
    private const string WatchJob = "watch";

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(30);

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            return 0;
        }

        var dir = AgentPaths.DataDir;
        var log = new AgentLog(AgentPaths.LogFile);
        var watcher = new Watcher(dir, new NoProbe(), () => DateTime.Now, () => GuardTaskState.Unknown,
            (result, _) => log.Write($"actions: {result.Actions.Count} (not delivered)"), log);

        // 周期の違う仕事を足すときはここに並べる（今は 5 分の見張りだけ）
        using var scheduler = new Scheduler([new Job(WatchJob, FirstDelay, Watcher.Interval, () => watcher.RunOnce())], log);

        void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                scheduler.Kick(WatchJob, ResumeDelay);
            }
        }

        SystemEvents.PowerModeChanged += OnPower;
        try
        {
            log.Write($"agent started: {dir}");
            scheduler.Start();
            Application.Run();
        }
        finally
        {
            SystemEvents.PowerModeChanged -= OnPower;
            log.Write("agent stopped");
        }
        return 0;
    }

    /// <summary>Windows 側の値がまだ無い間の代わり（何も取れない）．</summary>
    private sealed class NoProbe : ISystemProbe
    {
        public double IdleSeconds() => 0;
        public double? UptimeSeconds() => null;
        public IReadOnlyList<(string Name, string? Path)> Processes() => [];
        public IReadOnlyList<string> SteamCommonDirs() => [];
    }
}
