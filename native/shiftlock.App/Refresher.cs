using Microsoft.UI.Dispatching;

namespace Shiftlock.App;

/// <summary>
/// 読み直しの合図を出す．毎分（表示の時刻が変わるとき）と，実行フォルダの見張っているファイルが変わったとき．
/// 短い間隔で続いた変更は 1 回にまとめる（書き込みは .tmp → 置き換えで，1 回の記録で何度か知らせが来る）．
/// Changed は UI スレッドで呼ぶ．
/// </summary>
public sealed class Refresher : IDisposable
{
    /// <summary>見張るファイル（実行フォルダ直下）．</summary>
    private static readonly string[] Watched = ["shifts.json", "state.json", "days.json", "calendar.json", "STOP.txt"];

    /// <summary>変更をまとめる間隔．</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly string _dir;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _minute;
    private readonly DispatcherQueueTimer _debounce;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>読み直してほしいとき（UI スレッド）．</summary>
    public event Action? Changed;

    /// <summary>UI スレッドで作る．dir が無くてもよい（できたら次の毎分の合図から見張る）．</summary>
    public Refresher(string dir, DispatcherQueue queue)
    {
        _dir = dir;
        _queue = queue;

        _debounce = queue.CreateTimer();
        _debounce.Interval = Debounce;
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Changed?.Invoke();

        _minute = queue.CreateTimer();
        _minute.IsRepeating = false;
        _minute.Tick += (_, _) =>
        {
            EnsureWatcher();
            ScheduleMinute();
            Changed?.Invoke();
        };

        EnsureWatcher();
        ScheduleMinute();
    }

    public void Dispose()
    {
        _disposed = true;
        _minute.Stop();
        _debounce.Stop();
        DropWatcher();
    }

    /// <summary>次の「分が変わるとき」に合わせる（固定の 60 秒だと表示が最大 1 分遅れるため）．</summary>
    private void ScheduleMinute()
    {
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
        // 分の境目ちょうどより少し後にする（手前で起きて前の分を出し直さないように）
        _minute.Interval = next - now + TimeSpan.FromMilliseconds(200);
        _minute.Start();
    }

    /// <summary>見張りが無ければ作る．フォルダが無い・作れないときは次の機会にやり直す．</summary>
    private void EnsureWatcher()
    {
        if (_watcher is not null || _disposed || !Directory.Exists(_dir))
        {
            return;
        }
        FileSystemWatcher? w = null;
        try
        {
            w = new FileSystemWatcher(_dir)
            {
                // STOP.txt はフォルダのこともあるので DirectoryName も見る
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            w.Changed += OnFile;
            w.Created += OnFile;
            w.Deleted += OnFile;
            w.Renamed += OnRenamed;
            w.Error += OnError;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            w?.Dispose();
        }
    }

    private void DropWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private static bool IsWatched(string? name) =>
        name is not null && Watched.Contains(name, StringComparer.OrdinalIgnoreCase);

    // ここから下の 3 つは見張りのスレッドで呼ばれる．UI スレッドに渡すだけにする

    private void OnFile(object sender, FileSystemEventArgs e)
    {
        if (IsWatched(e.Name))
        {
            Kick();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsWatched(e.Name) || IsWatched(e.OldName))
        {
            Kick();
        }
    }

    /// <summary>見張りが壊れた（フォルダが消えた・知らせがあふれた）．作り直して，取りこぼしの分を読み直す．</summary>
    private void OnError(object sender, ErrorEventArgs e) => _queue.TryEnqueue(() =>
    {
        if (_disposed)
        {
            return;
        }
        DropWatcher();
        EnsureWatcher();
        Restart();
    });

    private void Kick() => _queue.TryEnqueue(Restart);

    /// <summary>まとめる間隔を数え直す（UI スレッド）．</summary>
    private void Restart()
    {
        if (_disposed)
        {
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }
}
