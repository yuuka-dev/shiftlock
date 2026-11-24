using System.ComponentModel;
using System.Diagnostics;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>System.Diagnostics.Process で取る代替．親の pid は取れない（0．HasParents = false）．</summary>
public sealed class DiagnosticsProcessSource : IProcessSource
{
    public ProcessSnapshot Snapshot()
    {
        var found = new List<ProcSample>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (name.Length == 0)
                {
                    continue;
                }
                found.Add(new ProcSample(p.Id, 0, p.StartTime, p.TotalProcessorTime, (name + ".exe").ToLowerInvariant()));
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // 権限が無い・数えている間に終わったプロセスは飛ばす
            }
            finally
            {
                p.Dispose();
            }
        }
        return new ProcessSnapshot(found, false);
    }
}
