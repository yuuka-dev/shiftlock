# shiftlock guard (Windows) — タスクスケジューラから5分おきに起動
# やること: アイドル秒数とゲーム起動を測って shiftlock.py tick（Windows の Python。無ければ WSL）に渡し、返ってきた通知を出すだけ。
# PC を止める（ロック／スリープ／シャットダウン）のは、本人が shl enforce on で同意した乗務で tick が指示したときだけ（下の Invoke-Enforce）。
# 対象: Windows PowerShell 5.1。pwsh 7 から起動しても、自動で powershell.exe（5.1）で起動し直す
#       （トースト通知の WinRT 読み込みが pwsh 7 では使えへんため）
#   -DryRun      通知を出さずに結果を表示（状態ファイルは更新される）
#   -TestNotify  トースト/ポップアップ/読み上げが出るかだけ試す（-Sound <path> で通知音も。無ければ shifts.json の notify.sound.default）
#   -ScanGames   ゲーム扱いになる exe を一覧（game-scan.txt にも保存）
#   -ShowGames   いま起動中でゲーム扱いになってるプロセスを表示
#   -Release     強制モードの緊急解除（STOP.txt を置き、予約済みのシャットダウンを中止。kaijo.cmd / shl kaijo から）
# 強制モード（ロック／スリープ／シャットダウン）は tick が {method: enforce} を返したときだけ実行する。
# 本人が shl enforce on で同意した乗務だけ。実行直前にも STOP.txt を見る。
param([switch]$DryRun, [switch]$TestNotify, [switch]$ScanGames, [switch]$ShowGames, [switch]$Release, [string]$Sound)
if ($PSVersionTable.PSEdition -eq 'Core') {
    $fwd = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    foreach ($n in 'DryRun', 'TestNotify', 'ScanGames', 'ShowGames', 'Release') { if ($PSBoundParameters[$n]) { $fwd += "-$n" } }
    if ($Sound) { $fwd += '-Sound'; $fwd += $Sound }
    & powershell.exe @fwd
    exit $LASTEXITCODE
}
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir     = $PSScriptRoot
$logPath = Join-Path $dir 'guard.log'
. (Join-Path $dir 'find-python.ps1')   # Invoke-Shiftlock / Write-WslFallbackWarning（Windows の Python で shiftlock.py を呼ぶ。deploy.ps1 が一緒にコピーする）
$script:SoundVolume = 0.8   # tick の戻り値（notify.sound_volume / sound_max_seconds）で上書き
$script:SoundMax    = 20

function Write-Log([string]$m) {
    "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $m" | Out-File -FilePath $logPath -Append -Encoding utf8
}

function Show-Popup([string]$title, [string]$text) {
    (New-Object -ComObject WScript.Shell).Popup($text, 20, $title, 0x30) | Out-Null
}

function Show-Toast([string]$title, [string]$text, [bool]$silent = $false) {
    $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
    $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
    $t = [Security.SecurityElement]::Escape($title)
    $m = [Security.SecurityElement]::Escape($text)
    $audio = if ($silent) { "<audio silent='true'/>" } else { '' }   # 自前で音を鳴らすときは既定音を消す（二重に鳴らさない）
    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$t</text><text>$m</text></binding></visual>$audio</toast>")
    # 既存の PowerShell の AppUserModelID を借りる（登録不要）
    $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show(
        [Windows.UI.Notifications.ToastNotification]::new($xml))
}

# 任意の音声ファイル（WAV / MP3 / M4A / WMA など）。MediaPlayer で鳴らし、最長 $maxSec 秒で打ち切る。失敗しても通知は続ける
function Play-WavFallback([string]$path) {
    if ([IO.Path]::GetExtension($path).ToLower() -ne '.wav') { return $false }
    try { (New-Object System.Media.SoundPlayer $path).PlaySync(); return $true }
    catch { Write-Log "sound failed (SoundPlayer): $_"; return $false }
}

# 鳴らせたら $true。壊れたファイル・未対応の形式は MediaPlayer が例外も出さず黙るので、長さが取れなければ諦めてログに残す
function Play-Sound([string]$path, [double]$volume, [int]$maxSec) {
    if (-not $path) { return $false }
    if (-not (Test-Path -LiteralPath $path)) { Write-Log "sound not found: $path"; return $false }
    try {
        Add-Type -AssemblyName PresentationCore
        $mp = New-Object System.Windows.Media.MediaPlayer
        $mp.Open([Uri]$path)
        $mp.Volume = [Math]::Max(0.0, [Math]::Min(1.0, $volume))
        $mp.Play()
        $n = 0
        while (-not $mp.NaturalDuration.HasTimeSpan -and $n -lt 30) { Start-Sleep -Milliseconds 100; $n++ }
        if (-not $mp.NaturalDuration.HasTimeSpan) {
            $mp.Close()
            Write-Log "sound not playable (MediaPlayer): $path"
            return (Play-WavFallback $path)
        }
        $sec = [Math]::Ceiling($mp.NaturalDuration.TimeSpan.TotalSeconds)
        Start-Sleep -Seconds ([Math]::Max(1, [Math]::Min($sec, $maxSec)))
        $mp.Stop(); $mp.Close()
        return $true
    } catch {
        Write-Log "sound failed (MediaPlayer): $_"
        return (Play-WavFallback $path)
    }
}

# 強制モードの実行（tick が {method: enforce} を返したときだけ）。実行直前にも STOP.txt を見る
function Invoke-Enforce($a) {
    $act = [string]$a.action
    if ($DryRun) { Write-Output "[enforce] $act (DryRun: not executed)"; return }
    $stopFile = Join-Path $dir 'STOP.txt'
    if (Test-Path -LiteralPath $stopFile) { Write-Log "enforce $act skipped: STOP.txt"; return }
    if ([string]$a.sound) { $null = Play-Sound ([string]$a.sound) $script:SoundVolume $script:SoundMax }
    if (Test-Path -LiteralPath $stopFile) { Write-Log "enforce $act skipped: STOP.txt (placed during sound)"; return }   # 実行の直前にもう一度
    Write-Log "enforce: $act"
    try {
        switch ($act) {
            'lock'     { Start-Process -FilePath 'rundll32.exe' -ArgumentList 'user32.dll,LockWorkStation' -WindowStyle Hidden }
            'sleep'    { Add-Type -AssemblyName System.Windows.Forms
                         [System.Windows.Forms.Application]::SetSuspendState([System.Windows.Forms.PowerState]::Suspend, $false, $false) | Out-Null }
            'shutdown' { Start-Process -FilePath 'shutdown.exe' -ArgumentList '/s /t 60 /c "shiftlock: kyousei mode. cancel = kaijo.cmd (60s)"' -WindowStyle Hidden }
            default    { Write-Log "enforce: unknown action $act" }
        }
    } catch { Write-Log "enforce failed: $_" }
}

function Send-Notice($a) {
    $snd = [string]$a.sound
    if ($DryRun) { Write-Output "[$($a.method)] $($a.text)$(if ($snd) { "  (sound: $snd)" })"; return }
    [bool]$played = $false
    if ($snd) { $played = Play-Sound $snd $script:SoundVolume $script:SoundMax }   # 音 → 通知 → 読み上げ（popup は 20 秒止まるので先に鳴らす）
    try {
        if ($a.method -eq 'popup') { Show-Popup $a.title $a.text } else { Show-Toast $a.title $a.text $played }   # 鳴らせへんかったら既定音は残す
    } catch {
        Write-Log "toast failed, fallback popup: $_"
        Show-Popup $a.title $a.text
    }
    if ($a.speak) {
        try {
            Add-Type -AssemblyName System.Speech
            $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
            $s.Speak(($a.text -replace '♡', ''))
        } catch { Write-Log "speak failed: $_" }
    }
}

if ($Release) {
    # 緊急解除。一番急ぐ「予約済みシャットダウンの中止」を先に、各手順は独立に（どれかが失敗しても次へ）
    $ErrorActionPreference = 'Continue'
    try { cmd.exe /c 'shutdown.exe /a >nul 2>&1' } catch {}
    $stop = Join-Path $dir 'STOP.txt'
    try { "$((Get-Date).ToString('yyyy-MM-dd HH:mm')) kaijo" | Out-File -FilePath $stop -Append -Encoding utf8 }
    catch { Write-Output "STOP.txt が書けへんかった: $_  （手で $stop を作ってな）" }
    try { Write-Log "release: pending shutdown aborted, STOP.txt created" } catch {}
    try {
        $r = Invoke-Shiftlock $dir @('enforce', 'release')
        $r.out | ForEach-Object { Write-Output $_ }
    } catch { Write-Output "記録に失敗（STOP.txt は置いた）: $_" }
    Write-Output "再開するときは shl enforce resume（STOP.txt を消す）"
    return
}

if ($TestNotify) {
    $p = $Sound
    if (-not $p) {
        try { $p = [string](Get-Content -Raw -Encoding UTF8 -Path (Join-Path $dir 'shifts.json') | ConvertFrom-Json).notify.sound.default }
        catch { Write-Log "TestNotify: shifts.json の notify.sound.default が読めへん: $_" }
    }
    Send-Notice @{ method = 'toast'; title = 'shiftlock'; text = 'トーストのテストやで'; speak = $false; sound = $p }
    Send-Notice @{ method = 'popup'; title = 'shiftlock'; text = 'ポップアップのテストやで'; speak = $true; sound = '' }
    return
}

$cfg = Get-Content -Raw -Encoding UTF8 -Path (Join-Path $dir 'shifts.json') | ConvertFrom-Json

# ------------------------------------------------------------ ゲーム判定
function Normalize-Dir([string]$p) {
    if (-not $p) { return $null }
    try { return ([IO.Path]::GetFullPath($p)).TrimEnd('\') + '\' } catch { return $null }
}

function Get-SteamCommonDirs {
    $roots = @()
    try {
        $sp = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath
        if ($sp) { $roots += ($sp -replace '/', '\') }
    } catch {}
    $roots += 'C:\Program Files (x86)\Steam'
    $libs = @()
    foreach ($r in ($roots | Select-Object -Unique)) {
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"')) {
                $libs += ($m.Matches[0].Groups[1].Value -replace '\\\\', '\')
            }
        }
        $libs += $r
    }
    $libs | Sort-Object -Unique | ForEach-Object { Join-Path $_ 'steamapps\common' } | Where-Object { Test-Path $_ }
}

$defaultExclude = @('UnityCrashHandler*', 'CrashReport*', '*crashpad*', 'unins*', 'vc_redist*',
                    'dxsetup*', '*Redist*', 'steamerrorreporter*', '*Setup*')
$useSteam   = if ($null -eq $cfg.steam_libraries) { $true } else { [bool]$cfg.steam_libraries }
$gameDirs   = @()
if ($useSteam) { $gameDirs += @(Get-SteamCommonDirs) }
$gameDirs  += @($cfg.game_dirs)
$gameDirs   = @($gameDirs | Where-Object { $_ } | ForEach-Object { Normalize-Dir $_ } | Where-Object { $_ } | Sort-Object -Unique)  # 大文字小文字を区別せずに重複除去
$exDirs     = @(@($cfg.game_exclude_dirs) | Where-Object { $_ } | ForEach-Object { Normalize-Dir $_ } | Where-Object { $_ })
$exNames    = if ($null -eq $cfg.game_exclude) { $defaultExclude } else { @($cfg.game_exclude) | Where-Object { $_ } }
$procNames  = @(@($cfg.game_processes) | Where-Object { $_ })

function Test-GameExe([string]$path) {
    if (-not $path) { return $false }
    $under = $false
    foreach ($d in $gameDirs) { if ($path.StartsWith($d, [StringComparison]::OrdinalIgnoreCase)) { $under = $true; break } }
    if (-not $under) { return $false }
    foreach ($d in $exDirs) { if ($path.StartsWith($d, [StringComparison]::OrdinalIgnoreCase)) { return $false } }
    $leaf = [IO.Path]::GetFileName($path)
    $base = [IO.Path]::GetFileNameWithoutExtension($path)
    foreach ($n in $exNames) { if ($leaf -like $n -or $base -like $n -or $path -like $n) { return $false } }
    return $true
}

function Get-RunningGames {
    $hits = @()
    foreach ($p in (Get-Process)) {
        $path = $null
        try { $path = $p.Path } catch {}
        if (Test-GameExe $path) { $hits += $p; continue }
        if ($procNames -contains $p.ProcessName) { $hits += $p }
    }
    $hits
}

if ($ScanGames) {
    Write-Output "ゲーム扱いのフォルダ:"
    $gameDirs | ForEach-Object { Write-Output "  + $_" }
    if ($exDirs.Count) { Write-Output "除外フォルダ:"; $exDirs | ForEach-Object { Write-Output "  - $_" } }
    Write-Output "除外する名前: $($exNames -join ', ')"
    Write-Output ""
    $all = foreach ($d in $gameDirs) {
        Get-ChildItem -LiteralPath $d -Recurse -File -Filter *.exe -ErrorAction SilentlyContinue
    }
    $ok = @($all | Where-Object { Test-GameExe $_.FullName })
    $ng = @($all | Where-Object { -not (Test-GameExe $_.FullName) })
    $lines = @("# shiftlock game scan $(Get-Date -Format 'yyyy-MM-dd HH:mm')", "# ゲーム扱い: $($ok.Count) 個 / 除外: $($ng.Count) 個", "")
    $lines += "## ゲーム扱い（フォルダごと）"
    foreach ($g in ($ok | Group-Object { $_.Directory.FullName } | Sort-Object Name)) {
        $lines += "$($g.Name)"
        $g.Group | ForEach-Object { $lines += "    $($_.Name)" }
    }
    $lines += ""; $lines += "## 除外された exe"
    $ng | Sort-Object FullName | ForEach-Object { $lines += "    $($_.FullName)" }
    $out = Join-Path $dir 'game-scan.txt'
    $lines | Out-File -FilePath $out -Encoding utf8
    Write-Output "ゲーム扱い $($ok.Count) 個 / 除外 $($ng.Count) 個。一覧: $out"
    Write-Output "ゲームごとの exe 数:"
    $ok | ForEach-Object {
        $p = $_.FullName
        $root = $gameDirs | Where-Object { $p.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        [pscustomobject]@{ Game = (Join-Path $root ($p.Substring($root.Length).Split('\')[0])) }
    } | Group-Object Game | Sort-Object Name | ForEach-Object { Write-Output ("  {0,3}  {1}" -f $_.Count, $_.Name) }
    return
}

$running = @(Get-RunningGames)
if ($ShowGames) {
    if ($running.Count) { $running | ForEach-Object { Write-Output "  $($_.ProcessName)  $($_.Path)" } }
    else { Write-Output "  いまゲーム扱いのプロセスは動いてへん" }
    return
}
$game = if ($running.Count -gt 0) { 1 } else { 0 }

# ------------------------------------------------------------ アイドル秒数
# ゲームパッド入力は拾えないことがあるので、ゲームは上のプロセス判定で別に見る
if (-not ('ShiftLockIdle' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShiftLockIdle {
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    public static double Seconds() {
        var i = new LASTINPUTINFO();
        i.cbSize = (uint)Marshal.SizeOf(i);
        if (!GetLastInputInfo(ref i)) return 0;
        return unchecked((uint)Environment.TickCount - i.dwTime) / 1000.0;
    }
}
'@
}
$idle = [int][ShiftLockIdle]::Seconds()
# 起動からの秒数（起動直後は強制モードを発動しないため。取れなければ 0 → 発動しない）
$uptime = 0
try { $uptime = [int]((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalSeconds } catch { Write-Log "uptime failed: $_" }

# ------------------------------------------------------------ Python で判定（Windows の Python。無ければ WSL。find-python.ps1）
$ErrorActionPreference = 'Continue'
$tickArgs = @('tick', '--idle-seconds', $idle, '--game', $game, '--uptime-seconds', $uptime)
if ($DryRun) { $tickArgs += '--dry-run' }   # 強制モードの状態を進めない
$r = Invoke-Shiftlock $dir $tickArgs
$out = $r.out
$code = $r.code
$ErrorActionPreference = 'Stop'
if ($code -ne 0) { Write-Log "tick failed ($code): $out"; exit 1 }
if ($r.python -eq 'wsl') {
    Write-WslFallbackWarning $logPath { param($m) Write-Log $m }
    if ($DryRun) { Write-Output "warn: Windows の Python が無いので WSL で実行（離席中のアイドル秒数が WSLg の注入で戻る）" }
}

$res = ($out -join "`n") | ConvertFrom-Json
if ($DryRun) {
    $gn = ($running | ForEach-Object { $_.ProcessName } | Select-Object -Unique) -join ','
    Write-Output "idle=${idle}s game=$game $gn python=$($r.python) actions=$(@($res.actions).Count)"
}
if ($res.sound) {
    try { $script:SoundVolume = [double]$res.sound.volume; $script:SoundMax = [int]$res.sound.max_seconds } catch {}
}
foreach ($w in @($res.warnings)) { if ($w) { Write-Log "warn: $w"; if ($DryRun) { Write-Output "warn: $w" } } }
foreach ($a in $res.actions) { if ($a.method -eq 'enforce') { Invoke-Enforce $a } else { Send-Notice $a } }
exit 0
