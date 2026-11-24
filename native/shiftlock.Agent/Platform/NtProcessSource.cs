using System.Runtime.InteropServices;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// NtQuerySystemInformation(SystemProcessInformation) で全プロセスを 1 回で取る（プロセスを開かない）．
/// 親の pid まで取れる（HasParents = true）．構造体は IntPtr の幅（32/64 ビット）で位置を決めて読む．
/// </summary>
public sealed class NtProcessSource : IProcessSource
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const int MaxTries = 8;

    // SYSTEM_PROCESS_INFORMATION の位置（CreateTime 32，UserTime 40，KernelTime 48，ImageName 56 は幅によらず同じ）．
    private const int OffCreateTime = 32;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffImageName = 56;
    private static readonly int Ptr = IntPtr.Size;
    // UNICODE_STRING = Length(2) MaxLength(2) [64 ビットでは 4 詰め] Buffer(Ptr)．その後ろに BasePriority(4)，Ptr に揃えて pid，親の pid
    private static readonly int OffImageBuffer = OffImageName + Ptr;
    private static readonly int OffPid = Align(OffImageName + Ptr + Ptr + 4, Ptr);
    private static readonly int OffParent = OffPid + Ptr;

    public ProcessSnapshot Snapshot()
    {
        var size = 1 << 20;
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, (uint)size, out var needed);
                if (status == 0)
                {
                    return new ProcessSnapshot(Parse(buffer, size), true);
                }
                if ((uint)status != StatusInfoLengthMismatch)
                {
                    throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{(uint)status:X8}");
                }
                // 取っている間に増えることがあるので余裕を足す
                size = Math.Max(size * 2, (int)Math.Min(int.MaxValue / 2, (long)needed + (256 << 10)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{StatusInfoLengthMismatch:X8}");
    }

    private static List<ProcSample> Parse(IntPtr buffer, int size)
    {
        var found = new List<ProcSample>();
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset + OffParent + Ptr > size)
            {
                throw new InvalidOperationException("NtQuerySystemInformation returned a malformed buffer");
            }
            var entry = buffer + offset;
            var length = (ushort)Marshal.ReadInt16(entry, OffImageName);
            var namePtr = Marshal.ReadIntPtr(entry, OffImageBuffer);
            if (length > 0 && namePtr != IntPtr.Zero)
            {
                var name = Marshal.PtrToStringUni(namePtr, length / 2);
                var create = Marshal.ReadInt64(entry, OffCreateTime);
                var cpu = Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime);
                var pid = (int)Marshal.ReadIntPtr(entry, OffPid);
                var parent = (int)Marshal.ReadIntPtr(entry, OffParent);
                found.Add(new ProcSample(pid, parent, DateTime.FromFileTime(create), TimeSpan.FromTicks(cpu), name.ToLowerInvariant()));
            }
            var next = Marshal.ReadInt32(entry, 0);
            if (next == 0)
            {
                return found;
            }
            offset += next;
        }
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, uint length, out uint returnLength);
}
