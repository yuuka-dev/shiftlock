# リポジトリの pc/ から、実際に動いてるフォルダ（既定 %USERPROFILE%\shift-lock）へコードだけ反映する。
# 記録データ（log/, state.json, shifts.json など）には一切さわらへん。
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1            コードを反映して hook を入れ直す
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -Full      タスクとプロファイルも登録し直す
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -DryRun    何がコピーされるかだけ表示
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -SkipHook -Target <別のフォルダ>   hook の登録をせずにコピーだけ（動作確認用）
param(
    [string]$Target = (Join-Path $env:USERPROFILE 'shift-lock'),
    [switch]$Full,
    [switch]$DryRun,
    [switch]$SkipHook
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$src = $PSScriptRoot
if ([IO.Path]::GetFullPath($Target).TrimEnd('\') -ieq $src.TrimEnd('\')) { throw "Target がリポジトリの pc/ 自身や: $Target" }

# コードとして配るもの（データや個人設定はここに入れへん）
$files = @(
    'shiftlock.py', 'guard.ps1', 'find-python.ps1', 'install-task.ps1', 'install-profile.ps1', 'shiftlock-profile.ps1',
    'make-report.ps1', 'README.md', 'sample-report.png',
    'shiftlock.cmd', 'status.cmd', 'settings.cmd', 'report.cmd', 'scan-games.cmd',
    'yasumi.cmd', 'deru.cmd', 'okita.cmd', 'neru.cmd', 'kaijo.cmd'
)
# フォルダごと配るもの（Python のパッケージ。__pycache__ と .pyc は配らへん）
$dirs = @('shiftlock')

if (-not (Test-Path $Target)) { New-Item -ItemType Directory -Path $Target | Out-Null }
$ndir = 0   # パッケージ（フォルダ）を先に、エントリ shiftlock.py はあとで。途中で止まっても古い 1 ファイル版のエントリは動く
foreach ($d in $dirs) {
    $from = Join-Path $src $d
    if (-not (Test-Path -LiteralPath $from -PathType Container)) { throw "見つからへん: $from" }
    $to = Join-Path $Target $d
    $items = Get-ChildItem -LiteralPath $from -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\__pycache__\\' -and $_.Extension -ne '.pyc' }
    if ($DryRun) {
        foreach ($i in $items) { Write-Output ("copy $d/" + $i.FullName.Substring($from.Length + 1).Replace('\', '/')) }
        continue
    }
    # 無くなったモジュールが残らんように丸ごと入れ替える（このフォルダにデータは無い。shifts.json などは $Target 直下）
    if (Test-Path -LiteralPath $to) {
        if ((Resolve-Path -LiteralPath $to).ProviderPath -ieq (Resolve-Path -LiteralPath $from).ProviderPath) { throw "コピー先がコピー元と同じや: $to" }
        Remove-Item -LiteralPath $to -Recurse -Force
    }
    foreach ($i in $items) {
        $dest = Join-Path $to $i.FullName.Substring($from.Length + 1)
        New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
        Copy-Item -LiteralPath $i.FullName -Destination $dest -Force
        $ndir++
    }
}
foreach ($f in $files) {
    $from = Join-Path $src $f
    if (-not (Test-Path $from)) { throw "見つからへん: $from" }
    if ($DryRun) { Write-Output "copy $f"; continue }
    Copy-Item -LiteralPath $from -Destination (Join-Path $Target $f) -Force
}
# 初回だけ: 設定のひな形を置く（既にあるなら絶対に上書きせえへん）
$cfg = Join-Path $Target 'shifts.json'
if (-not (Test-Path $cfg)) {
    if ($DryRun) { Write-Output "create shifts.json from shifts.example.json" }
    else { Copy-Item (Join-Path $src 'shifts.example.json') $cfg; Write-Output "shifts.json を新規作成（settings.cmd で設定してな）" }
}
if ($DryRun) { return }
Get-ChildItem $Target -Include *.ps1, *.cmd -Recurse | Unblock-File
Write-Output "反映した: $($files.Count) 個 + $($dirs -join ', ') の $ndir 個 -> $Target"

# hook の本体（~/.claude/hooks にコピーされてる）を更新。WSL 側と Windows 側の設定を両方更新する
$w = '/mnt/' + $Target.Substring(0, 1).ToLower() + $Target.Substring(2).Replace('\', '/')
if ($SkipHook) { Write-Output "hook の登録は飛ばした（-SkipHook）" }
else { & wsl.exe -e python3 "$w/shiftlock.py" install-hook --config "$w/shifts.json" }

if ($Full) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Target 'install-task.ps1')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Target 'install-profile.ps1')
}
Write-Output "Claude Code を開いてたら再起動してな"
