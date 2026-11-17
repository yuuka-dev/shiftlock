# shiftlock-profile.ps1 を pwsh 7 と Windows PowerShell 5.1 の両方のプロファイル（全ホスト共通）に登録する。
# 何回実行しても 1 回分しか入らへん。外すときは -Uninstall。
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'shiftlock-profile.ps1'
$begin = '# >>> shiftlock >>>'
$end = '# <<< shiftlock <<<'
$block = @($begin, "if (Test-Path '$src') { . '$src' }", $end) -join "`r`n"

$shells = @('powershell.exe')
if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) { $shells += 'pwsh.exe' }
foreach ($sh in $shells) {
    $prof = (& $sh -NoProfile -Command '$PROFILE.CurrentUserAllHosts').Trim()
    $policy = (& $sh -NoProfile -Command 'Get-ExecutionPolicy').Trim()
    $text = if (Test-Path $prof) { Get-Content -Raw -Path $prof } else { '' }
    if ($null -eq $text) { $text = '' }
    $pattern = [regex]::Escape($begin) + '[\s\S]*?' + [regex]::Escape($end) + '\r?\n?'
    $text = [regex]::Replace($text, $pattern, '')
    if (-not $Uninstall) { $text = $text.TrimEnd() + "`r`n`r`n" + $block + "`r`n" }
    New-Item -ItemType Directory -Force -Path (Split-Path $prof) | Out-Null
    # BOM 付き UTF-8（5.1 で日本語パスでも化けんように）
    [IO.File]::WriteAllText($prof, $text, (New-Object Text.UTF8Encoding $true))
    $state = if ($Uninstall) { '外した' } else { '入れた' }
    Write-Output "$sh : $state -> $prof （ExecutionPolicy: $policy）"
    if (-not $Uninstall -and $policy -in @('Restricted', 'AllSigned')) {
        Write-Output "  ! この実行ポリシーやとプロファイルが読まれへん。必要なら: $sh -Command 'Set-ExecutionPolicy -Scope CurrentUser RemoteSigned'"
    }
}
Write-Output "新しいターミナルを開いたら shl / neru / okita / yasumi が使えるで"
