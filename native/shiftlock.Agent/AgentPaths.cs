namespace Shiftlock.Agent;

/// <summary>Agent が読み書きする場所（ウィンドウの AppPaths と同じ決まり）．</summary>
public static class AgentPaths
{
    /// <summary>実行フォルダを差し替える環境変数（合成データや本番のコピーで確かめるとき用）．</summary>
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

    /// <summary>Agent のログ（実行フォルダの agent.log．guard.log と同じ役割）．</summary>
    public static string LogFile => Path.Combine(DataDir, "agent.log");
}
