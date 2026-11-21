namespace Shiftlock.Agent;

/// <summary>知らせる側（テストで差し替える）．失敗は例外で知らせる（呼ぶ側の ActionRunner がログに書いて続ける）．</summary>
public interface INotifier
{
    /// <summary>トースト．silent なら Windows の既定の通知音を鳴らさない（自前で音を鳴らしたとき）．</summary>
    void Toast(string title, string text, bool silent);

    /// <summary>最前面のメッセージ．警告アイコン．20 秒で自動で閉じる（閉じるまで戻らない）．</summary>
    void Popup(string title, string text);

    /// <summary>読み上げ（終わるまで戻らない）．</summary>
    void Speak(string text);

    /// <summary>
    /// 音声ファイルを鳴らす（WAV・MP3・M4A・WMA）．volume は 0〜1，最長 maxSeconds 秒で打ち切る．cancel が立ったらすぐ止める．
    /// 鳴らせたら true．無いファイル・鳴らせない形式は false．
    /// </summary>
    bool PlaySound(string path, double volume, int maxSeconds, CancellationToken cancel);
}
