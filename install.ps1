$ErrorActionPreference = 'Stop'
function Pause-End($msg) {
    Write-Host ''
    Write-Host $msg
    Read-Host 'Enterキーで閉じます'
}
try {
    if (Get-Process -Name 'Pokemon TCG Live' -ErrorAction SilentlyContinue) {
        Pause-End 'ゲームが起動しています。ゲームを終了してから、もう一度実行してください。'
        exit 1
    }
    $payload = Join-Path $PSScriptRoot 'payload'
    $tpci = Get-ChildItem $env:USERPROFILE -Directory | Where-Object { $_.Name -like '*Company International*' } | Select-Object -First 1
    if (-not $tpci) { throw 'ゲームのフォルダが見つかりませんでした。READMEの手動の方法で入れてください。' }
    $game = Get-ChildItem $tpci.FullName -Directory | Where-Object { $_.Name -like '*Trading Card Game Live*' } | Select-Object -First 1
    if (-not $game) { throw 'ゲームのフォルダが見つかりませんでした。READMEの手動の方法で入れてください。' }
    Write-Host ('コピー先: ' + $game.FullName)
    Copy-Item (Join-Path $payload '*') $game.FullName -Recurse -Force
    Copy-Item (Join-Path $payload '.doorstop_version') $game.FullName -Force
    Pause-End 'コピーが終わりました。ゲームを起動してください。'
} catch {
    Pause-End ('エラー: ' + $_.Exception.Message)
}
