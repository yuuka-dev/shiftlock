using System.Runtime.InteropServices;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// プロセスに手を加える唯一の場所（設計書 2026-11-23-native-app-limit-design.md §6）．
/// 減速はジョブオブジェクトの CPU 上限（ENABLE | HARD_CAP），一時停止は NtSuspendProcess／NtResumeProcess．
/// 開いた後に GetProcessTimes で開始時刻を確かめ，違えば（pid の使い回し）何もしない．
/// ジョブのハンドルは Agent が生きている間だけ持つ．外すのはこのハンドルでだけ（名前で開き直さない・ハンドルを対象に複製しない．
/// 実機の下調べの結果と本人の決定による）．プロセスを終了させる操作は無い（KILL_ON_JOB_CLOSE も付けない）．
/// </summary>
public sealed class WindowsLimitControl : ILimitControl, IDisposable
{
    // OpenProcess の権限．ジョブに入れる側は AssignProcessToJobObject が求める最小（PROCESS_TERMINATE は API の要求で，終了させる呼び出しは書かない）
    private const uint QueryLimited = 0x1000;
    private const uint SetQuota = 0x0100;
    private const uint Terminate = 0x0001;
    private const uint SuspendResume = 0x0800;
    private const uint AssignAccess = QueryLimited | SetQuota | Terminate;
    private const uint SuspendAccess = QueryLimited | SuspendResume;

    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CpuRateEnable = 0x1;
    private const uint CpuRateHardCap = 0x4;
    private const uint StillActive = 259;
    private const int ErrorInvalidParameter = 87;   // OpenProcess: その pid のプロセスが無い
    private const int MinRate = 1;
    private const int MaxRate = 10000;

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, IntPtr> _jobs = new(StringComparer.Ordinal);

    public bool Cap(string jobName, IReadOnlyList<ProcKey> procs, double percent)
    {
        try
        {
            if (procs.Count == 0 || double.IsNaN(percent))
            {
                return false;
            }
            var rate = (uint)Math.Clamp((int)Math.Round(Math.Min(Math.Max(percent, 0), 100) * 100, MidpointRounding.AwayFromZero), MinRate, MaxRate);

            lock (_gate)
            {
                // 先に全部を開く．権限が無くて開けないものが 1 つでもあれば，何も入れずに失敗にする
                // （アプリの一部だけを遅くしない．設計書 §4.3）．居なくなったもの（終わった・pid の使い回し）は数えない
                var handles = new List<IntPtr>();
                try
                {
                    foreach (var proc in procs)
                    {
                        switch (Open(proc, AssignAccess, out var handle))
                        {
                            case Opened.Ok:
                                handles.Add(handle);
                                break;
                            case Opened.Denied:
                                return false;
                        }
                    }
                    if (handles.Count == 0)
                    {
                        return false;
                    }

                    var created = false;
                    if (!_jobs.TryGetValue(jobName, out var job))
                    {
                        job = CreateJobObjectW(IntPtr.Zero, jobName);
                        if (job == IntPtr.Zero)
                        {
                            return false;
                        }
                        created = true;
                    }

                    // 入れられないものがあれば失敗（入れる間に終わったものは数えない）．上限をかける前なので，
                    // 新しいジョブなら何も効かない．既にあるジョブなら呼ぶ側が Uncap で外す
                    var all = true;
                    foreach (var handle in handles)
                    {
                        var inJob = IsProcessInJob(handle, job, out var already) && already || AssignProcessToJobObject(job, handle);
                        if (!inJob && IsRunning(handle))
                        {
                            all = false;
                            break;
                        }
                    }

                    var info = new CpuRateControlInformation { ControlFlags = CpuRateEnable | CpuRateHardCap, CpuRate = rate };
                    var set = all && SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<CpuRateControlInformation>());

                    if (created)
                    {
                        if (set)
                        {
                            _jobs[jobName] = job;
                        }
                        else
                        {
                            CloseHandle(job);   // 上限をかけられなかった新しいジョブは持たない
                        }
                    }
                    return set;
                }
                finally
                {
                    foreach (var handle in handles)
                    {
                        CloseHandle(handle);
                    }
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Uncap(string jobName)
    {
        try
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(jobName, out var job))
                {
                    return false;   // 持っていないジョブは名前で開き直さない
                }
                var info = new CpuRateControlInformation { ControlFlags = 0, CpuRate = 0 };
                return SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf<CpuRateControlInformation>());
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Suspend(ProcKey proc) => WithProcess(proc, SuspendAccess, h => NtSuspendProcess(h) == 0);

    public bool Resume(ProcKey proc) => WithProcess(proc, SuspendAccess, h => NtResumeProcess(h) == 0);

    public bool Exists(ProcKey proc) => WithProcess(proc, QueryLimited, _ => true);

    /// <summary>持っているジョブのハンドルを閉じる．上限は外さない（外すのは呼ぶ側）．</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Values)
            {
                CloseHandle(job);
            }
            _jobs.Clear();
        }
    }

    private enum Opened
    {
        Ok,
        Gone,
        Denied,
    }

    /// <summary>テスト用．真を返したプロセスは「権限が無くて開けない」として扱う（実機で開けないプロセスを用意できないため）．</summary>
    public Func<ProcKey, bool>? DeniedForTest { get; init; }

    /// <summary>
    /// pid を開いて開始時刻を確かめる．Ok なら handle は呼ぶ側が閉じる．居ない（pid が無い・開始時刻が違う・終わっている）は Gone，
    /// それ以外で開けないのは Denied（権限が無い，など）．
    /// </summary>
    private Opened Open(ProcKey proc, uint access, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (DeniedForTest?.Invoke(proc) == true)
        {
            return Opened.Denied;
        }
        var h = OpenProcess(access, false, (uint)proc.Pid);
        if (h == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() == ErrorInvalidParameter ? Opened.Gone : Opened.Denied;
        }
        if (StartOf(h) != proc.Start || !IsRunning(h))
        {
            CloseHandle(h);
            return Opened.Gone;
        }
        handle = h;
        return Opened.Ok;
    }

    /// <summary>pid を開き，開始時刻が proc と同じときだけ action を呼ぶ．開けない・時刻が違う・例外 → false．</summary>
    private static bool WithProcess(ProcKey proc, uint access, Func<IntPtr, bool> action)
    {
        try
        {
            var handle = OpenProcess(access, false, (uint)proc.Pid);
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return StartOf(handle) == proc.Start && IsRunning(handle) && action(handle);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>終わったプロセスは，ハンドルが残っていて開けても「在る」にしない（終了コードが STILL_ACTIVE でなければ終わっている）．</summary>
    private static bool IsRunning(IntPtr handle) => GetExitCodeProcess(handle, out var code) && code == StillActive;

    /// <summary>開始時刻（ローカル時刻）．NtProcessSource が ProcSample.Start を作るときと同じ FILETIME → DateTime 変換．取れなければ null．</summary>
    private static DateTime? StartOf(IntPtr handle)
    {
        if (!GetProcessTimes(handle, out var create, out _, out _, out _))
        {
            return null;
        }
        var ticks = ((long)create.dwHighDateTime << 32) | create.dwLowDateTime;
        return DateTime.FromFileTime(ticks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRateControlInformation info, uint length);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);
}
