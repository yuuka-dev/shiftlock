using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Shiftlock.Core;

namespace Shiftlock.Agent;

/// <summary>
/// Windows から値を取る（pc/guard.ps1 の アイドル秒数・起動からの秒数・Get-Process・Get-SteamCommonDirs と同じもの）．
/// どれも例外を出さない．
/// </summary>
public sealed class WindowsProbe : ISystemProbe
{
    private const string DefaultSteamRoot = @"C:\Program Files (x86)\Steam";
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>最後の入力からの秒数（GetLastInputInfo）．ゲームパッドの入力は拾えないことがある（ゲームはプロセスで別に見る）．</summary>
    public double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000.0 : 0;
    }

    public double? UptimeSeconds() => Environment.TickCount64 / 1000.0;

    /// <summary>動いているプロセス．パスが取れないもの（システムのプロセスなど）は null．1 つ失敗しても全体は続ける．</summary>
    public IReadOnlyList<(string Name, string? Path)> Processes()
    {
        var found = new List<(string Name, string? Path)>();
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception)
        {
            return found;
        }
        foreach (var p in all)
        {
            try
            {
                var name = p.ProcessName;
                if (name.Length > 0)
                {
                    found.Add((name, ImagePath(p.Id)));
                }
            }
            catch (Exception)
            {
                // 数えている間に終わったプロセスなど
            }
            finally
            {
                p.Dispose();
            }
        }
        return found;
    }

    /// <summary>Steam のライブラリ（レジストリの SteamPath と既定の場所から）の steamapps\common のうち，在るもの．</summary>
    public IReadOnlyList<string> SteamCommonDirs()
    {
        var roots = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string path && path.Length > 0)
            {
                roots.Add(path.Replace('/', '\\'));
            }
        }
        catch (Exception)
        {
            // レジストリが読めなければ既定の場所だけ見る
        }
        roots.Add(DefaultSteamRoot);
        return SteamCommonDirs(roots);
    }

    /// <summary>Steam の入っているフォルダの一覧から，各ライブラリ（libraryfolders.vdf の path と，そのフォルダ自身）の steamapps\common を集める．</summary>
    public static IReadOnlyList<string> SteamCommonDirs(IEnumerable<string> steamRoots)
    {
        var libraries = new List<string>();
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    libraries.AddRange(Games.SteamLibraryPaths(File.ReadAllText(vdf, Encoding.UTF8)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // 読めないライブラリの一覧は無いものとして続ける
            }
            libraries.Add(root);
        }
        var found = new List<string>();
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var common = Path.Combine(library, "steamapps", "common");
                if (Directory.Exists(common))
                {
                    found.Add(common);
                }
            }
            catch (ArgumentException)
            {
                // パスとして読めない行
            }
        }
        return found;
    }

    /// <summary>プロセスの exe のフルパス．取れなければ null（権限の無いプロセスでも，名前だけは Processes が返す）．</summary>
    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new char[32768];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
