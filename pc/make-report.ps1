# 訪看さん向けレポートを作る（締め期間ごと：今の期間の途中経過＋前の期間の確定版）
#   毎週火曜 9:00 にタスクから自動実行。手動は report.cmd / shl report
#   HTML を作って、Edge（ヘッドレス）で A4 横の PDF にもする。古いのは保存期間（既定 3 か月）で消える
#   -Open   作ったあと今の期間の PDF（無ければ HTML）を開く
param([switch]$Open)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir = $PSScriptRoot
$log = Join-Path $dir 'guard.log'
function Write-Log([string]$m) { "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) [report] $m" | Out-File -FilePath $log -Append -Encoding utf8 }

. (Join-Path $dir 'find-python.ps1')   # Invoke-Shiftlock / Write-WslFallbackWarning（Windows の Python で shiftlock.py を呼ぶ。無ければ WSL）
$ErrorActionPreference = 'Continue'
$r = Invoke-Shiftlock $dir @('report', '--auto')
$out = $r.out
$code = $r.code
$ErrorActionPreference = 'Stop'
if ($code -ne 0) { Write-Log "failed ($code): $out"; Write-Output "レポート作成に失敗: $out"; exit 1 }
if ($r.python -eq 'wsl') { Write-WslFallbackWarning $log { param($m) Write-Log $m } }

# WSL で動いたときのパス /mnt/c/Users/... -> C:\Users\...（Windows の Python ならそのまま）
$htmls = @($out | Where-Object { $_ -like "FILE`t*" } | ForEach-Object {
    $p = ($_ -split "`t", 2)[1].Trim()
    if ($p -match '^/mnt/([a-z])/(.*)$') { "$($Matches[1].ToUpper()):\" + $Matches[2].Replace('/', '\') } else { $p }
})

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
          "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$pdfs = @()
foreach ($h in $htmls) {
    $pdf = [IO.Path]::ChangeExtension($h, '.pdf')
    if ($edge) {
        $uri = ([Uri]$h).AbsoluteUri
        $eargs = @('--headless=new', '--disable-gpu', '--no-first-run', '--no-pdf-header-footer',
                  "--user-data-dir=$env:TEMP\shiftlock-edge", "--print-to-pdf=$pdf", $uri)
        Start-Process -FilePath $edge -ArgumentList $eargs -Wait -WindowStyle Hidden
        if (Test-Path $pdf) { $pdfs += $pdf } else { Write-Log "pdf not created: $pdf" }
    }
    Write-Output "作成: $h"
}
$pdfs | ForEach-Object { Write-Output "PDF : $_" }
if (-not $edge) { Write-Output "（Edge が見つからんかったので PDF はなし。HTML をブラウザで開いて印刷してな）" }
Write-Log "ok: $($htmls.Count) html, $($pdfs.Count) pdf"

if ($Open) {
    $target = if ($pdfs.Count) { $pdfs[-1] } else { $htmls[-1] }
    if ($target) { Invoke-Item $target }
}
exit 0
