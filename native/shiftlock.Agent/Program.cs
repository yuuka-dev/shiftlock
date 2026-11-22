using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// 入口．引数なしなら常駐する: 同時に動く Agent は 1 つだけ（2 つ目は何もせず終わる）．
/// 起動 1 分後と以後 5 分おきに，UI でないスレッドで見張りを 1 回行う．スリープからの復帰でも 30 秒後に 1 回試す
/// （5 分たっていなければ Watcher が飛ばす）．
///   --test-notify   トースト・ポップアップ・読み上げ・通知音が出るかだけ試す（guard.ps1 -TestNotify と同じ）
///   --install       Python 版の見張りから切り替える（自動起動を登録し，タスク shiftlock-guard を無効にして，Agent を起動）
///   --uninstall     戻す（Agent を終わらせ，登録を外し，タスク shiftlock-guard を有効に戻す）
///   --stop          動いている Agent を終わらせる（発行物を置き直すとき用）
///   --meter-once    稼働計測を 10 秒あけて 2 回測り，書くはずの行を JSON で出す（ファイルは書かない．MeterOnce）
///   --tick-once     時刻と値を指定して見張りを 1 回だけ行い，結果の JSON を出す（Python 版との突き合わせ用．TickOnce）
///   --limit-once    アプリの減速・一時停止の判断を 1 回だけ行い，「すること」の並びを JSON で出す（何も実行せず，何も書かない．
///                   本番のフォルダは断る．LimitOnce）
/// 強制モードの宣言・解除・設定を行う引数は無い（解除はトレイのメニューだけ）．
/// </summary>
public static class Program
{
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
                "--install" => RunSetup(install: true),
                "--uninstall" => RunSetup(install: false),
                "--stop" => StopAgent(),
                "--tick-once" => RunTickOnce(args),
                "--meter-once" => RunMeterOnce(),
                "--limit-once" => RunLimitOnce(),
                _ => 2,
            };
        }
        return RunAgent();
    }

    private static int RunAgent()
    {
        using var mutex = new Mutex(initiallyOwned: true, AgentControl.MutexName, out var first);
        if (!first)
        {
            return 0;
        }

        var dir = AgentPaths.DataDir;
        var log = new AgentLog(AgentPaths.LogFile);
        log.Write($"agent started: {dir}");

        // 「終了」で立てる．出している途中の通知を打ち切る
        using var quit = new CancellationTokenSource();
        // 外からの「終われ」の合図（--uninstall・--stop）．「終了」を選んだときと同じ手順で終わる．
        // 起動の途中で来ても取りこぼさないよう，一番先に聞き始める（画面の準備ができる前なら，できた時点で終わる）
        SynchronizationContext? ui = null;
        void Quit()
        {
            quit.Cancel();
            Volatile.Read(ref ui)?.Post(_ => Application.ExitThread(), null);
        }
        using var quitSignal = AgentControl.ListenForQuit(Quit);

        RegisterToastName(log);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var clock = () => DateTime.Now;
        var notifier = new WindowsNotifier(log);
        var power = new WindowsPower();
        var store = new Store(dir);
        var runner = new ActionRunner(notifier, power, store, log);
        var windowsProbe = new WindowsProbe();
        var once = new OncePerDay(log);
        // 10 秒の仕事（Limiter）は計測（Meter）の回のサンプルを読む．別の仕事なので，どちらかの失敗・遅さがもう一方を巻き込まない
        using var control = new WindowsLimitControl();
        var meter = new Meter(dir, new FallbackProcessSource(new NtProcessSource(), new DiagnosticsProcessSource(), once, clock),
            windowsProbe, windowsProbe, clock, () => Stopwatch.GetElapsedTime(0), once);
        var watcher = new Watcher(dir, windowsProbe, clock, new GuardTask(GuardTask.ConfiguredName).State,
            (result, _) => runner.Run(result, quit.Token), log);
        var limiter = new Limiter(dir, () => meter.Latest, control, new WindowsFolderLister(),
            actions =>
            {
                var (volume, seconds) = SoundNumbers(dir);
                runner.Run(new TickResult(actions, [], volume, seconds), quit.Token);   // 通知音は付けない（TickAction の Sound は空）
            }, clock, once);
        using var tray = new Tray(dir, watcher, clock, notifier,
            new ReleaseFlow(power, store, clock, log, afterRelease: () => limiter.RestoreAll("kaijo")), log, Quit);
        Volatile.Write(ref ui, SynchronizationContext.Current);

        // 周期の違う仕事を足すときはここに並べる（5 分の見張りと 10 秒の稼働計測）．計測はスリープ復帰で急かさない
        using var scheduler = new Scheduler([
            new Job(WatchJob, FirstDelay, Watcher.Interval, () =>
            {
                watcher.RunOnce();
                tray.RequestRefresh();
            }),
            new Job("meter", Meter.Interval, Meter.Interval, meter.RunOnce),
            // 終了の合図（quit）の後に始まってしまう回で，戻した後にもう一度かけない
            new Job("limit", Limiter.Interval, Limiter.Interval, () =>
            {
                if (!quit.IsCancellationRequested)
                {
                    limiter.RunOnce();
                }
            }),
        ], log);

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
            if (!quit.IsCancellationRequested)
            {
                // 見張りや計測を始める前に，前回止めたままのアプリを戻す（Agent が落ちた後の再開）．失敗しても起動は止めない
                SafeStartup(limiter, log);
                scheduler.Start();
                Application.Run();
            }
        }
        finally
        {
            quit.Cancel();
            SystemEvents.PowerModeChanged -= OnPower;
            // タイマーを止めてから，たまっている計測を書く（Dispose は 2 回呼んでも害は無い）
            scheduler.Dispose();
            // 止めたまま Agent がいなくならないよう，全部戻す（計測の Flush より前．失敗しても終了は続ける）
            SafeRestore(limiter, log);
            meter.Flush();
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

    /// <summary>切り替え（--install / --uninstall）．結果を 1 行ずつ標準出力に出す．成功 0，失敗 1．</summary>
    private static int RunSetup(bool install)
    {
        OpenConsole();
        var m = Messages.Load(null);
        try
        {
            m = Watcher.LoadConfig(AgentPaths.DataDir).Config?.Msg ?? m;
        }
        catch (IOException)
        {
            // 設定が読めなければ標準語で出す
        }
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "shiftlock-agent.exe");
        var task = new GuardTask(GuardTask.ConfiguredName);
        var setup = new Setup(new SetupDeps(new RegistryAutoStart(), task,
            WindowsNotifier.RegisterToastName, WindowsNotifier.UnregisterToastName,
            StartAgent: path =>
            {
                // シェル経由で起動する: こちらの標準出力（パイプ）を Agent に引き継がせない（引き継ぐと，出力を読んでいる側が Agent の終了まで待ち続ける）
                using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            },
            StopAgent: AgentControl.Stop,
            Output: Console.WriteLine), m, task.Name);
        return install ? setup.Install(exe) : setup.Uninstall();
    }

    /// <summary>突き合わせ用の 1 回だけの見張り（--tick-once）．通知は出さず，強制も実行しない．</summary>
    private static int RunTickOnce(string[] args)
    {
        OpenConsole();
        return TickOnce.Run(args[1..], Console.Out, Console.Error);
    }

    /// <summary>稼働計測の動作確認（--meter-once）．10 秒かかる．何も書かない．</summary>
    private static int RunMeterOnce()
    {
        OpenConsole();
        var windowsProbe = new WindowsProbe();
        return MeterOnce.Run(AgentPaths.DataDir, new FallbackProcessSource(new NtProcessSource(), new DiagnosticsProcessSource(),
            new OncePerDay(new AgentLog(AgentPaths.LogFile)), () => DateTime.Now), windowsProbe, windowsProbe,
            Console.Out, Console.Error, Thread.Sleep);
    }

    /// <summary>アプリの減速・一時停止の判断を 1 回だけ（--limit-once）．何も実行せず，何も書かない．</summary>
    private static int RunLimitOnce()
    {
        OpenConsole();
        var windowsProbe = new WindowsProbe();
        return LimitOnce.Run(AgentPaths.DataDir, new FallbackProcessSource(new NtProcessSource(), new DiagnosticsProcessSource(),
            new OncePerDay(new AgentLog(Path.Combine(AgentPaths.DataDir, "limit-once-no-log", "never.log"))), () => DateTime.Now),
            windowsProbe, windowsProbe, new WindowsFolderLister(), new LimitOnce.Untouchable(), DateTime.Now, Console.Out, Console.Error);
    }

    private static void SafeStartup(Limiter limiter, AgentLog log)
    {
        try
        {
            limiter.Startup();
        }
        catch (Exception e)
        {
            log.Write($"limit startup failed: {e.GetType().Name}");
        }
    }

    private static void SafeRestore(Limiter limiter, AgentLog log)
    {
        try
        {
            limiter.RestoreAll(null);
        }
        catch (Exception e)
        {
            log.Write($"limit restore failed: {e.GetType().Name}");
        }
    }

    /// <summary>通知の音量と最長秒数（TestNotify と同じ読み方．通知音は付けないので値は使われないが，TickResult の形に合わせる）．</summary>
    private static (double Volume, int Seconds) SoundNumbers(string dir)
    {
        var volume = 0.8;
        var seconds = 20;
        try
        {
            if (Watcher.LoadConfig(dir).Config is { } cfg)
            {
                var notify = cfg.Section("notify");
                volume = notify["sound_volume"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : volume;
                seconds = notify["sound_max_seconds"] is JsonValue x && x.TryGetValue<int>(out var i) ? i : seconds;
            }
        }
        catch (Exception)
        {
            // 読めなければ既定のまま
        }
        return (volume, seconds);
    }

    /// <summary>動いている Agent を終わらせる（--stop）．終わらせた・動いていなかった → 0，止められない → 1．</summary>
    private static int StopAgent()
    {
        try
        {
            AgentControl.Stop();
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>
    /// ウィンドウのアプリ（コンソールを持たない）から標準出力に書けるようにする．
    /// 出力がファイルやパイプに向いていればそのまま，端末から起動されていればその端末に書く．
    /// </summary>
    private static void OpenConsole()
    {
        AttachConsole(AttachParentProcess);
        Console.OutputEncoding = new UTF8Encoding(false);
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

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
