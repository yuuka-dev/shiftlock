using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Shiftlock.Core;
using Windows.Data.Xml.Dom;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Notifications;

namespace Shiftlock.Agent;

/// <summary>
/// 知らせる側の Windows 実装．トーストは WinRT（アプリ名 shiftlock で出す），ポップアップは 20 秒で閉じるメッセージの窓，
/// 読み上げは SAPI，通知音は WinRT の MediaPlayer（WAV・MP3・M4A・WMA）．どれも NuGet の追加なし．
/// </summary>
public sealed class WindowsNotifier(AgentLog log) : INotifier
{
    /// <summary>
    /// トーストを出すときの名前（AppUserModelID）．非パッケージの exe は，この名前を
    /// HKCU\Software\Classes\AppUserModelId\&lt;名前&gt; に登録しておくと，その DisplayName で通知が出る（RegisterToastName）．
    /// </summary>
    public const string AppId = "shiftlock.Agent";

    private const string DisplayName = "shiftlock";
    private const int PopupSeconds = 20;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);

    private const uint MbIconWarning = 0x30;
    private const uint MbSetForeground = 0x10000;
    private const uint MbTopMost = 0x40000;

    /// <summary>
    /// トーストの名前を登録する（HKCU．管理者権限は要らない．何度呼んでもよい）．
    /// Agent の起動時と --install で呼ぶ．登録が無いとトーストは例外も出さずに出ないことがある．
    /// </summary>
    public static void RegisterToastName()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
    }

    /// <summary>トーストの名前の登録を外す（--uninstall）．無ければ何もしない．</summary>
    public static void UnregisterToastName() =>
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{AppId}", throwOnMissingSubKey: false);

    public void Toast(string title, string text, bool silent)
    {
        // 自前で音を鳴らしたときは既定の通知音を消す（二重に鳴らさない）
        var audio = silent ? "<audio silent='true'/>" : "";
        var xml = new XmlDocument();
        xml.LoadXml("<toast><visual><binding template='ToastGeneric'>"
            + $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text>"
            + $"</binding></visual>{audio}</toast>");
        ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(xml));
    }

    public void Popup(string title, string text)
    {
        // 20 秒で自動で閉じる（user32 の MessageBoxTimeoutW．WScript.Shell の Popup と同じ仕組み）
        MessageBoxTimeout(IntPtr.Zero, text, title, MbIconWarning | MbSetForeground | MbTopMost, 0, PopupSeconds * 1000);
    }

    public void Speak(string text)
    {
        var type = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("SAPI.SpVoice が無い");
        var voice = Activator.CreateInstance(type) ?? throw new InvalidOperationException("SAPI.SpVoice を作れない");
        try
        {
            type.InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, voice, [text, 0]);
        }
        finally
        {
            Marshal.FinalReleaseComObject(voice);
        }
    }

    public bool PlaySound(string path, double volume, int maxSeconds, CancellationToken cancel)
    {
        var local = LocalPath.ToLocal(path);
        if (!File.Exists(local))
        {
            log.Write($"sound not found: {path}");
            return false;
        }
        using var opened = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var failed = false;
        using var player = new MediaPlayer { Volume = Math.Clamp(double.IsNaN(volume) ? 0 : volume, 0, 1), AutoPlay = false };
        player.CommandManager.IsEnabled = false;   // 音量キーの横に再生中の表示を出さない
        player.MediaOpened += (_, _) => opened.Set();
        player.MediaEnded += (_, _) => done.Set();
        player.MediaFailed += (_, _) =>
        {
            failed = true;
            opened.Set();
            done.Set();
        };
        player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(local)));
        player.Play();
        // 壊れたファイル・知らない形式は開けない（MediaFailed）か，いつまでも開かない
        if (!opened.Wait(OpenTimeout, CancellationToken.None) || failed)
        {
            log.Write($"sound not playable: {path}");
            return false;
        }
        try
        {
            done.Wait(TimeSpan.FromSeconds(Math.Max(1, maxSeconds)), cancel);
        }
        catch (OperationCanceledException)
        {
            // 「終了」が選ばれた．すぐ止める
        }
        player.Pause();
        return !failed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxTimeoutW")]
    private static extern int MessageBoxTimeout(IntPtr owner, string text, string caption, uint type, ushort languageId, int milliseconds);
}
