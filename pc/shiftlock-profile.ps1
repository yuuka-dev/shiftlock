# shiftlock: pwsh / Windows PowerShell のどのディレクトリからでも使えるコマンド
# install-profile.ps1 がプロファイルにこのファイルを読み込む 1 行を足す。
#
#   shl                 日本語のコマンド一覧（状態は shl status）
#   shl settings        設定メニュー
#   shl day off 11/5 通院 / shl day work 11/8 / shl day rm 11/5 / shl day list
#   shl report          締め期間のレポート（今の期間＋前の期間、PDF も）を作って開く
#   shl report 14       直近 14 日で作って開く
#   shl sync            カレンダーを今すぐ同期
#   neru / okita / yasumi / deru / yotei / itte（行ってきます）/ kakunin（未確認の乗務）
#   shl enforce [on [lock|sleep|shutdown] [2h]|off|resume]   強制モード（宣言は対話で y が要る）
#   shl kaijo / kaijo   強制モードの緊急解除（STOP.txt を置く）
#   scan-games          ゲーム扱いになる exe を一覧
#   show-games          いまゲーム扱いで動いてるプロセス
$global:ShiftLockDir = $PSScriptRoot

function global:shl {
    $d = $global:ShiftLockDir
    $w = '/mnt/' + $d.Substring(0, 1).ToLower() + $d.Substring(2).Replace('\', '/')
    if ($args.Count -ge 1 -and $args[0] -eq 'kaijo') { kaijo; return }
    if ($args.Count -eq 1 -and $args[0] -eq 'report') {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $d 'make-report.ps1') -Open
        return
    }
    [string[]]$a = if ($args.Count -eq 0) { 'help' } else { $args }   # 1 個でも配列のまま（if 式は配列をほどくので型で固定）．引数なしは一覧
    if ($a[0] -in 'help', '-h', '--help', '?', '/?') { [string[]]$a = @('help') }
    if ($a[0] -eq 'report' -and $a.Count -ge 2) { [string[]]$a = @('report', '--days', $a[1]) }
    $old = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        & wsl.exe -e python3 "$w/shiftlock.py" @a --config "$w/shifts.json"
    } finally {
        [Console]::OutputEncoding = $old
    }
    if ($a[0] -eq 'report' -and $LASTEXITCODE -eq 0) { Invoke-Item (Join-Path $d 'report.html') }
}
function global:shiftlock { shl @args }
function global:neru   { shl neru @args }
function global:okita  { shl okita @args }
function global:yasumi { shl yasumi @args }    # yasumi 11/3 = 事後報告
function global:deru   { shl deru @args }      # deru 11/3 = 事後報告（出た）
function global:yotei  { shl yotei @args }
function global:itte   { shl itte @args }
function global:kakunin { shl kakunin @args }
function global:kaijo {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $global:ShiftLockDir 'guard.ps1') -Release
}
function global:scan-games {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $global:ShiftLockDir 'guard.ps1') -ScanGames
}
function global:show-games {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $global:ShiftLockDir 'guard.ps1') -ShowGames
}
