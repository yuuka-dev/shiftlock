namespace Shiftlock.App;

/// <summary>アプリが読み書きする場所．</summary>
public static class AppPaths
{
    /// <summary>実行フォルダを差し替える環境変数（本番のコピーで確かめるとき用）．</summary>
    public const string DirEnv = "SHIFTLOCK_DIR";

    /// <summary>実行フォルダ．環境変数 SHIFTLOCK_DIR があればそれ，無ければ Python 版と同じ %USERPROFILE%\shift-lock．</summary>
    public static string DataDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(DirEnv);
            return string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "shift-lock")
                : dir;
        }
    }
}
