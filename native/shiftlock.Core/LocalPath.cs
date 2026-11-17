namespace Shiftlock.Core;

/// <summary>
/// 設定に書かれたパスを Windows のパスとして読む（pc/shiftlock/config.py の to_local_path の逆向き）．
/// 同じ shifts.json を WSL の Python 版も読むので，/mnt/c/... と書いてあることがある．
/// </summary>
public static class LocalPath
{
    private const string Mnt = "/mnt/";

    /// <summary>"/mnt/&lt;文字&gt;/..." を "&lt;文字&gt;:\..." に．それ以外はそのまま返す．</summary>
    public static string ToLocal(string p)
    {
        if (!p.StartsWith(Mnt, StringComparison.Ordinal) || p.Length < Mnt.Length + 1 || !char.IsAsciiLetter(p[Mnt.Length]))
        {
            return p;
        }
        if (p.Length > Mnt.Length + 1 && p[Mnt.Length + 1] != '/')
        {
            return p;   // "/mnt/cc/..." はドライブではない
        }
        var rest = p.Length > Mnt.Length + 2 ? p[(Mnt.Length + 2)..] : "";
        return char.ToUpperInvariant(p[Mnt.Length]) + @":\" + rest.Replace('/', '\\');
    }
}
