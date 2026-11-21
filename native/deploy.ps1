# ネイティブ版（ウィンドウ shiftlock.exe と常駐 shiftlock-agent.exe）を発行して，常駐させる場所に置く。
# ビルドのフォルダから常駐させないための手順。記録データ（%USERPROFILE%\shift-lock）には一切さわらない。
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1            発行して %LOCALAPPDATA%\Programs\shiftlock\ に置く
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -WhatIf    何をするかだけ表示（発行もコピーもしない）
#   powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -Destination <別のフォルダ> -NoStart   別の場所に置くだけ（動作確認用）
# 置く形:
#   <場所>\shiftlock.exe ほか              ウィンドウ（自己完結）
#   <場所>\agent\shiftlock-agent.exe ほか  常駐（自己完結）。ウィンドウと同じフォルダに混ぜない
#                                          （どちらも .NET のランタイム一式を抱えている。同じ名前のファイルを上書きし合わないように分ける）
# 動いている Agent があれば先に終わらせ，置いた後に起動し直す（-NoStart なら起動しない）。
# Python 版の見張りからの切り替え（自動起動の登録とタスク shiftlock-guard の無効化）は，置いた後に本人が
#   <場所>\agent\shiftlock-agent.exe --install      戻すときは --uninstall
# を実行する（native/README.md）。このスクリプトは切り替えをしない。
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\shiftlock'),
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root     = $PSScriptRoot
$dest     = [IO.Path]::GetFullPath($Destination)
$agentDir = Join-Path $dest 'agent'
$agentExe = Join-Path $agentDir 'shiftlock-agent.exe'
$dataDir  = Join-Path $env:USERPROFILE 'shift-lock'
if ($dest.TrimEnd('\') -ieq $dataDir.TrimEnd('\')) { throw "Destination が記録データのフォルダと同じ: $dest" }
if ($dest.TrimEnd('\').StartsWith($root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw "Destination がリポジトリの native/ の中: $dest" }

$stage = Join-Path ([IO.Path]::GetTempPath()) ("shiftlock-deploy-" + [Guid]::NewGuid().ToString('N'))
$stageApp   = Join-Path $stage 'app'
$stageAgent = Join-Path $stage 'agent'

function Invoke-Publish([string]$project, [string]$out, [string[]]$extra) {
    $cmd = @('publish', (Join-Path $root $project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $out, '--nologo', '-v', 'quiet') + $extra
    & dotnet @cmd
    if ($LASTEXITCODE -ne 0) { throw "発行に失敗: $project（dotnet publish の終了コード $LASTEXITCODE）" }
}

try {
    # 1. 発行（一時フォルダへ。失敗したら置いてあるものには触らない）
    if ($PSCmdlet.ShouldProcess("shiftlock.App と shiftlock.Agent（Release・win-x64・自己完結）", "発行")) {
        Invoke-Publish 'shiftlock.App\shiftlock.App.csproj' $stageApp @('-p:Platform=x64', '-p:PublishTrimmed=false')   # 使わない部分を削る発行（Release の既定）は警告で止まるので使わない
        Invoke-Publish 'shiftlock.Agent\shiftlock.Agent.csproj' $stageAgent @()
        foreach ($f in @((Join-Path $stageApp 'shiftlock.exe'), (Join-Path $stageAgent 'shiftlock-agent.exe'))) {
            if (-not (Test-Path -LiteralPath $f)) { throw "発行物に見つからない: $f" }
        }
    }

    # 2. 動いている Agent を終わらせる（置いてある exe の --stop。アイコンを消して終わる）
    $wasRunning = [bool](Get-Process -Name 'shiftlock-agent' -ErrorAction SilentlyContinue)
    if ($wasRunning -and (Test-Path -LiteralPath $agentExe)) {
        if ($PSCmdlet.ShouldProcess($agentExe, "動いている Agent を終わらせる（--stop）")) {
            & $agentExe --stop | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "動いている Agent を終わらせられなかった。トレイの「終了」で終わらせてからやり直して" }
        }
    } elseif ($wasRunning) {
        Write-Warning "Agent が別の場所から動いている（$agentExe ではない）。トレイの「終了」で終わらせてからやり直して"
        if (-not $WhatIfPreference) { throw "動いている Agent を終わらせられない" }
    }

    # 3. 置く（ウィンドウは <場所>，常駐は <場所>\agent。古いファイルが残らないよう，常駐のフォルダは入れ替える）
    try {
        if ($PSCmdlet.ShouldProcess($dest, "ウィンドウを置く")) {
            if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }
            Copy-Item -Path (Join-Path $stageApp '*') -Destination $dest -Recurse -Force
        }
        if ($PSCmdlet.ShouldProcess($agentDir, "常駐を置く")) {
            # 入れ替えは「新しいものを隣に置いてから名前を替える」。途中で失敗しても，古い常駐か新しい常駐のどちらかが残る
            $newDir = "$agentDir.new"
            $oldDir = "$agentDir.old"
            foreach ($d in @($newDir, $oldDir)) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
            New-Item -ItemType Directory -Path $newDir | Out-Null
            Copy-Item -Path (Join-Path $stageAgent '*') -Destination $newDir -Recurse -Force
            if (Test-Path -LiteralPath $agentDir) { Rename-Item -LiteralPath $agentDir -NewName (Split-Path $oldDir -Leaf) }
            Rename-Item -LiteralPath $newDir -NewName (Split-Path $agentDir -Leaf)
            if (Test-Path -LiteralPath $oldDir) { Remove-Item -LiteralPath $oldDir -Recurse -Force -ErrorAction SilentlyContinue }
        }
    } catch {
        # 動いていた Agent を止めた後に失敗した: 見張りが止まったままにならないよう，残っている常駐を起動し直してから知らせる
        if ($wasRunning -and -not $NoStart) {
            $oldDir = "$agentDir.old"
            if (-not (Test-Path -LiteralPath $agentExe) -and (Test-Path -LiteralPath $oldDir)) { Rename-Item -LiteralPath $oldDir -NewName (Split-Path $agentDir -Leaf) }
            if (Test-Path -LiteralPath $agentExe) {
                Start-Process -FilePath $agentExe -WorkingDirectory $agentDir
                Write-Warning "置く途中で失敗した。前から在った Agent を起動し直した: $agentExe"
            } else {
                Write-Warning "置く途中で失敗し，Agent を起動し直せなかった。いまは何も見張っていない。もう一度 deploy.ps1 を実行するか，Agent の --uninstall で Python 版に戻して"
            }
        }
        throw
    }

    # 4. 動いていたなら起動し直す
    if ($wasRunning -and -not $NoStart) {
        if ($PSCmdlet.ShouldProcess($agentExe, "Agent を起動し直す")) { Start-Process -FilePath $agentExe -WorkingDirectory $agentDir }
    }

    if (-not $WhatIfPreference) {
        Write-Output "置いた: $dest"
        Write-Output "  ウィンドウ: $(Join-Path $dest 'shiftlock.exe')"
        Write-Output "  常駐:       $agentExe"
        if (-not $wasRunning) { Write-Output "Python 版の見張りから切り替えるときは: `"$agentExe`" --install    （戻すときは --uninstall）" }
    }
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
