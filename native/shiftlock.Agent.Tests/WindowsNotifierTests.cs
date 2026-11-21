using System.Diagnostics;
using Windows.UI.Notifications;

namespace Shiftlock.Agent.Tests;

/// <summary>
/// 知らせる側の Windows 実装のうち，音を出さず・画面を止めずに確かめられるところ（無音の WAV を鳴らす）．
/// トーストは環境変数 SHIFTLOCK_TEST_TOAST=1 のときだけ実際に 1 件出して確かめる．
/// ポップアップと読み上げは手で確かめる（guard.ps1 -TestNotify に当たる shiftlock-agent.exe --test-notify）．
/// </summary>
public sealed class WindowsNotifierTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly WindowsNotifier _notifier;

    public WindowsNotifierTests() => _notifier = new WindowsNotifier(new AgentLog(_dir.P("agent.log")));

    public void Dispose() => _dir.Dispose();

    /// <summary>無音の WAV（8 kHz・モノラル・16 bit）を作る．</summary>
    private string SilentWav(string name, double seconds)
    {
        var samples = (int)(8000 * seconds);
        using var s = new MemoryStream();
        using (var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + (samples * 2));
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(8000);
            w.Write(16000);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(samples * 2);
            w.Write(new byte[samples * 2]);
        }
        File.WriteAllBytes(_dir.P(name), s.ToArray());
        return _dir.P(name);
    }

    [Fact]
    public void PlaySound_PlayableFile_ReturnsTrue()
    {
        Assert.True(_notifier.PlaySound(SilentWav("a.wav", 0.3), 0.8, 20, CancellationToken.None));
    }

    [Fact]
    public void PlaySound_StopsAtMaxSeconds()
    {
        var path = SilentWav("long.wav", 30);
        var clock = Stopwatch.StartNew();

        Assert.True(_notifier.PlaySound(path, 0.8, 1, CancellationToken.None));

        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 10);
    }

    [Fact]
    public void PlaySound_Cancel_StopsRightAway()
    {
        var path = SilentWav("long.wav", 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        _notifier.PlaySound(path, 0.8, 20, cts.Token);

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 10);
    }

    [Fact]
    public void PlaySound_MissingFile_ReturnsFalse_AndLogs()
    {
        Assert.False(_notifier.PlaySound(_dir.P("無い.mp3"), 0.8, 20, CancellationToken.None));

        Assert.Contains(_dir.Lines("agent.log"), l => l.Contains("sound not found", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaySound_BrokenFile_ReturnsFalse_AndLogs()
    {
        _dir.Put("broken.mp3", "これは音声ではない");

        Assert.False(_notifier.PlaySound(_dir.P("broken.mp3"), 0.8, 20, CancellationToken.None));

        Assert.Contains(_dir.Lines("agent.log"), l => l.Contains("sound not playable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(5.0)]
    public void PlaySound_OddVolume_IsClamped(double volume)
    {
        Assert.True(_notifier.PlaySound(SilentWav("a.wav", 0.2), volume, 20, CancellationToken.None));
    }

    [Fact]
    public void Toast_IsShownUnderTheShiftlockName_WhenAskedTo()
    {
        if (Environment.GetEnvironmentVariable("SHIFTLOCK_TEST_TOAST") != "1")
        {
            return;   // 実際に通知が出るので，頼まれたときだけ
        }
        WindowsNotifier.RegisterToastName();
        ToastNotificationManager.History.Clear(WindowsNotifier.AppId);

        _notifier.Toast("shiftlock", "通知のテスト <&> \"引用\"", silent: true);

        // 名前の登録が無いと，例外も出ずに捨てられる．通知の履歴に残っていれば出ている
        Assert.True(SpinWait.SpinUntil(() => ToastNotificationManager.History.GetHistory(WindowsNotifier.AppId).Count == 1,
            TimeSpan.FromSeconds(5)));
        ToastNotificationManager.History.Clear(WindowsNotifier.AppId);
    }
}
