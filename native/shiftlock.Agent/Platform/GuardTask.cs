using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Shiftlock.Agent;

/// <summary>
/// Python 版の見張りのタスク（タスク スケジューラの shiftlock-guard）の状態と切り替え．
/// タスク スケジューラの COM（Schedule.Service）で見る（schtasks.exe の出力は表示言語で変わるので使わない）．
/// 本人が自分で登録したタスクなので，管理者権限なしで切り替えられる．
/// </summary>
public sealed class GuardTask(string name) : IGuardTaskControl
{
    /// <summary>Python 版の install-task.ps1 が登録するタスクの名前．</summary>
    public const string DefaultName = "shiftlock-guard";

    /// <summary>タスクの名前を差し替える環境変数（試験用のタスクで確かめるとき用）．</summary>
    public const string NameEnv = "SHIFTLOCK_GUARD_TASK";

    private const int FileNotFound = unchecked((int)0x80070002);

    /// <summary>見るタスクの名前（SHIFTLOCK_GUARD_TASK があればそれ，無ければ shiftlock-guard）．</summary>
    public static string ConfiguredName
    {
        get
        {
            var name = Environment.GetEnvironmentVariable(NameEnv);
            return string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        }
    }

    public string Name => name;

    /// <summary>タスクが無い → Missing，有効・無効，確かめられない（COM が使えないなど）→ Unknown．例外は出さない．</summary>
    public GuardTaskState State()
    {
        try
        {
            return With(task => (bool)task.Enabled ? GuardTaskState.Enabled : GuardTaskState.Disabled, GuardTaskState.Missing);
        }
        catch (Exception)
        {
            return GuardTaskState.Unknown;
        }
    }

    /// <summary>有効・無効を切り替える．タスクが無い・権限が無いなどは例外．</summary>
    public void SetEnabled(bool enabled)
    {
        var done = With(task =>
        {
            task.Enabled = enabled;
            return true;
        }, false);
        if (!done)
        {
            throw new FileNotFoundException($"task {name} not found");
        }
    }

    /// <summary>タスクを開いて use を呼ぶ．タスクが無ければ missing を返す．</summary>
    private T With<T>(Func<dynamic, T> use, T missing)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Schedule.Service not available");
        dynamic service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Schedule.Service not available");
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic task;
            try
            {
                task = folder.GetTask(name);
            }
            catch (FileNotFoundException)
            {
                return missing;
            }
            catch (COMException e) when (e.HResult == FileNotFound)
            {
                return missing;
            }
            return use(task);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }
}

/// <summary>ログオン時の自動起動（HKCU の Run．管理者権限は要らない）．</summary>
public sealed class RegistryAutoStart(string valueName = RegistryAutoStart.DefaultValueName) : IAutoStart
{
    public const string DefaultValueName = "shiftlock-agent";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(valueName) as string;
    }

    public void Set(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
