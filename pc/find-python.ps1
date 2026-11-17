# shiftlock.py を動かす Python を探して実行する（guard.ps1 と make-report.ps1 がドットソースで読む）
# Windows の Python 3.8 以上があればそれ、無ければ WSL の python3（従来どおり）。
# WSL を 5 分おきに起動すると、WSLg の RDP クライアント（msrdc.exe）が接続・切断のたびにキー入力を注入して
# 離席中でも Windows のアイドル秒数が戻ってしまう（issue #22）。それを避けるために tick とレポートは Windows 側の Python で動かす。
# hook（Claude Code）・shl・*.cmd は本人が触ってるときにしか動かへんので WSL のまま。

function Find-ShiftlockPython {
    # 見つかれば @{ exe; pre; version }、無ければ $null
    # 順番: py launcher（py.exe -3）→ PATH の python.exe / python3.exe（Store の実行エイリアス WindowsApps\*.exe は中身が無いことがあるので除く）
    $cands = @()
    $py = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($py) { $cands += @{ exe = $py.Source; pre = @('-3') } }
    foreach ($n in 'python.exe', 'python3.exe') {
        foreach ($c in @(Get-Command $n -All -ErrorAction SilentlyContinue)) {
            if ($c.Source -and $c.Source -notlike '*\WindowsApps\*') { $cands += @{ exe = $c.Source; pre = @() } }
        }
    }
    foreach ($c in $cands) {
        $v = ''
        try { $v = (& $c.exe @($c.pre) --version 2>&1 | Out-String).Trim() } catch { continue }
        if ($v -match '^Python 3\.(\d+)' -and [int]$Matches[1] -ge 8) {
            # -I: ユーザー環境（PYTHONPATH・site-packages）を読まない。-X utf8: 出力とファイルを UTF-8 に（Windows の既定は cp932）
            return @{ exe = $c.exe; pre = @($c.pre) + @('-I', '-X', 'utf8'); version = $v }
        }
    }
    return $null
}

function Invoke-Shiftlock([string]$dir, [string[]]$cmdArgs) {
    # $dir\shiftlock.py <cmdArgs> --config $dir\shifts.json を実行する。
    # 戻り値: @{ out = 出力行の配列; code = 終了コード; python = 使った python.exe のパス（WSL なら 'wsl'） }
    $py = Find-ShiftlockPython
    if ($py) {
        $out = & $py.exe @($py.pre) (Join-Path $dir 'shiftlock.py') @cmdArgs --config (Join-Path $dir 'shifts.json')
        return @{ out = @($out); code = $LASTEXITCODE; python = $py.exe }
    }
    $w = '/mnt/' + $dir.Substring(0, 1).ToLower() + $dir.Substring(2).Replace('\', '/')
    $out = & wsl.exe -e python3 "$w/shiftlock.py" @cmdArgs --config "$w/shifts.json"
    return @{ out = @($out); code = $LASTEXITCODE; python = 'wsl' }
}

function Write-WslFallbackWarning([string]$logPath, [scriptblock]$log) {
    # WSL で動かしたときの警告。同じ日に 1 回だけ書く（5 分おきに書くとログが埋まる）
    $today = (Get-Date).ToString('yyyy-MM-dd')
    if ((Test-Path -LiteralPath $logPath) -and (Select-String -LiteralPath $logPath -Pattern "^$today .*wsl fallback" -Quiet)) { return }
    & $log "wsl fallback: Windows の Python 3.8 以上が見つからんので WSL で動かしてる。離席中のアイドル秒数が WSLg の注入で戻るので、Windows に Python を入れてな（issue #22）"
}
