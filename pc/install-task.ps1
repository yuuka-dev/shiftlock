# guard.ps1（5分おき）と make-report.ps1（毎週火曜 9:00）をタスクスケジューラに登録（管理者権限不要）
#   登録: powershell -ExecutionPolicy Bypass -File .\install-task.ps1
#   解除: powershell -ExecutionPolicy Bypass -File .\install-task.ps1 -Uninstall
#   Defender が conhost --headless を検知して騒ぐ場合: -NoHeadless（起動時に一瞬窓がチラつく）
param(
    [int]$IntervalMinutes = 5,
    [string]$ReportDay = 'Tuesday',
    [string]$ReportTime = '09:00',
    [switch]$NoHeadless,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$name  = 'shiftlock-guard'
$dir   = $PSScriptRoot
$guard = Join-Path $dir 'guard.ps1'
$user  = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$rname = 'shiftlock-report'
if ($Uninstall) {
    foreach ($n in $name, $rname) {
        if (Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $n -Confirm:$false
            Write-Output "解除した: $n"
        }
    }
    return
}
foreach ($f in $guard, (Join-Path $dir 'shifts.json'), (Join-Path $dir 'shiftlock.py'), (Join-Path $dir 'shiftlock\__init__.py')) {
    if (-not (Test-Path $f)) { throw "見つからへん: $f" }
}
Get-ChildItem $dir -Include *.ps1, *.cmd -Recurse | Unblock-File

$psArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$guard`""
if ($NoHeadless) {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $psArgs
} else {
    # Windows Terminal が既定だと -WindowStyle Hidden が効かず窓が出るので conhost --headless 経由
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe $psArgs"
}
$triggers = @(
    (New-ScheduledTaskTrigger -AtLogOn -User $user),
    # -RepetitionDuration 省略 = 無期限リピート
    (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
        -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes))
)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger $triggers `
    -Settings $settings -Principal $principal -Force | Out-Null
Write-Output "登録した: $name（$IntervalMinutes 分おき + ログオン時）。通知が基本。PC を止めるのは shl enforce on で同意した乗務だけ"

# --- 週 1 のレポート（PC がスリープ等で逃したら、起きた時に実行）
$report = Join-Path $dir 'make-report.ps1'
$rArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$report`""
$rAction = if ($NoHeadless) { New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $rArgs }
           else { New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe $rArgs" }
$rTrigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $ReportDay -At $ReportTime
$rSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
Register-ScheduledTask -TaskName $rname -Action $rAction -Trigger $rTrigger `
    -Settings $rSettings -Principal $principal -Force | Out-Null
Write-Output "登録した: $rname（毎週 $ReportDay $ReportTime）"
Write-Output "通知テスト: powershell -ExecutionPolicy Bypass -File `"$guard`" -TestNotify"
Write-Output "判定テスト: powershell -ExecutionPolicy Bypass -File `"$guard`" -DryRun"
