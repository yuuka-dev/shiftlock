namespace Shiftlock.Agent;

/// <summary>Windows から値を取る側（テストで差し替える）．どれも例外を出さず，取れなければ決めた値を返す．</summary>
public interface ISystemProbe
{
    /// <summary>最後の入力からの秒数．取れなければ 0．</summary>
    double IdleSeconds();

    /// <summary>起動からの秒数．取れなければ null（強制は発動しない）．</summary>
    double? UptimeSeconds();

    /// <summary>動いているプロセスの名前（拡張子なし）と exe のパス（取れなければ null）．</summary>
    IReadOnlyList<(string Name, string? Path)> Processes();

    /// <summary>Steam のライブラリの steamapps\common のうち，在るもの．</summary>
    IReadOnlyList<string> SteamCommonDirs();
}

/// <summary>Python 版の見張りのタスク（shiftlock-guard）の状態．Unknown は確かめられなかった．</summary>
public enum GuardTaskState
{
    Missing,
    Disabled,
    Enabled,
    Unknown,
}
