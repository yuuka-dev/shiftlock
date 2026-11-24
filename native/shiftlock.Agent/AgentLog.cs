using System.Globalization;
using System.Text;

namespace Shiftlock.Agent;

/// <summary>
/// Agent のログ（agent.log．guard.log と同じ役割）．1 行 1 件で「yyyy-MM-dd HH:mm:ss 文」．
/// maxBytes を超えていたら &lt;名前&gt;.1 に回して作り直す（残すのは 1 世代だけ）．
/// 書けなくても例外を出さない（ログのために見張りを止めない）．フォルダは作らない．
/// </summary>
public sealed class AgentLog(string path, long maxBytes = 1_000_000)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Lock _gate = new();

    public void Write(string message)
    {
        var oneLine = message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        var line = DateTime.Now.ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss", CultureInfo.InvariantCulture) + " " + oneLine + "\n";
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > maxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, line, Utf8NoBom);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // 実行フォルダが無い・ほかのプロセスが開いている，など．この 1 行は諦める
            }
        }
    }
}
