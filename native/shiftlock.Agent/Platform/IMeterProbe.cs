namespace Shiftlock.Agent;

/// <summary>計測が使う Windows 側の値．どれも例外を出さず，取れなければ null．</summary>
public interface IMeterProbe
{
    /// <summary>前面の窓を持つプロセスの pid．窓が無い・pid 0 は null．</summary>
    int? ForegroundPid();

    /// <summary>プロセスの exe のフルパス．取れなければ null．</summary>
    string? ImagePath(int pid);
}
