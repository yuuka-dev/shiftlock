using System.Text.Json.Nodes;
using Microsoft.Win32;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 入口．引数なしなら常駐する: 同時に動く Agent は 1 つだけ（2 つ目は何もせず終わる）．
/// 起動 1 分後と以後 5 分おきに，UI でないスレッドで見張りを 1 回行う．スリープからの復帰でも 30 秒後に 1 回試す
/// （5 分たっていなければ Watcher が飛ばす）．
///   --test-notify   トースト・ポップアップ・読み上げ・通知音が出るかだけ試す（guard.ps1 -TestNotify と同じ）
/// </summary>
public static class Program
{
    /// <summary>二重起動を見分ける名前（ログオンしているセッションごと）．</summary>
    private const string MutexName = @"Local\shiftlock-agent";

    /// <summary>見張りの仕事の名前（Scheduler の一覧の中で）．</summary>
    private const string WatchJob = "watch";

    private const string Title = "shiftlock";

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(30);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return args[0] switch
            {
                "--test-notify" => TestNotify(),
                _ => 2,
            };
        }
        return RunAgent();
    }

    private static int RunAgent()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            return 0;
        }

        var dir = AgentPaths.DataDir;
        var log = new AgentLog(AgentPaths.LogFile);
        RegisterToastName(log);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // 「終了」で立てる．出している途中の通知を打ち切る
        using var quit = new CancellationTokenSource();
        var clock = () => DateTime.Now;
        var notifier = new WindowsNotifier(log);
        var power = new WindowsPower();
        var store = new Store(dir);
        var runner = new ActionRunner(notifier, power, store, log);
        var watcher = new Watcher(dir, new WindowsProbe(), clock, () => GuardTaskState.Unknown,
            (result, _) => runner.Run(result, quit.Token), log);
        using var tray = new Tray(dir, watcher, clock, notifier, new ReleaseFlow(power, store, clock, log), log, () =>
        {
            quit.Cancel();
            Application.ExitThread();
        });

        // 周期の違う仕事を足すときはここに並べる（今は 5 分の見張りだけ）
        using var scheduler = new Scheduler([new Job(WatchJob, FirstDelay, Watcher.Interval, () =>
        {
            watcher.RunOnce();
            tray.RequestRefresh();
        })], log);

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
            quit.Cancel();
            SystemEvents.PowerModeChanged -= OnPower;
            log.Write("agent stopped");
        }
        return 0;
    }

    /// <summary>
    /// 通知が出るかだけ試す: トースト（notify.sound.default があればその音つき）→ ポップアップと読み上げ．
    /// 見張りはしない．実行フォルダのファイルも書かない（agent.log 以外）．
    /// </summary>
    private static int TestNotify()
    {
        var dir = AgentPaths.DataDir;
        var log = new AgentLog(AgentPaths.LogFile);
        RegisterToastName(log);
        var m = Messages.Load(null);
        var sound = "";
        var volume = 0.8;
        var maxSeconds = 20;
        try
        {
            var (cfg, _, _) = Watcher.LoadConfig(dir);
            if (cfg is not null)
            {
                m = cfg.Msg;
                var notify = cfg.Section("notify");
                sound = (notify["sound"] as JsonObject)?["default"] is JsonValue s && s.TryGetValue<string>(out var path) ? path : "";
                volume = notify["sound_volume"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : volume;
                maxSeconds = notify["sound_max_seconds"] is JsonValue x && x.TryGetValue<int>(out var i) ? i : maxSeconds;
            }
        }
        catch (IOException)
        {
            // 設定が読めなければ既定の文と音なしで試す
        }
        var runner = new ActionRunner(new WindowsNotifier(log), new WindowsPower(), new Store(dir), log);
        runner.Run(new TickResult(
            [
                new TickAction("toast", "test", Title, m["AGENT_TEST_TOAST"], Speak: false, Sound: sound),
                new TickAction("popup", "test", Title, m["AGENT_TEST_POPUP"], Speak: true),
            ], [], volume, maxSeconds), CancellationToken.None);
        return 0;
    }

    /// <summary>トーストを shiftlock の名前で出すための登録（HKCU）．失敗してもログだけ（出せなければポップアップになる）．</summary>
    private static void RegisterToastName(AgentLog log)
    {
        try
        {
            WindowsNotifier.RegisterToastName();
        }
        catch (Exception e)
        {
            log.Write($"toast name registration failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
