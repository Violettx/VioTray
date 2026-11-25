#Requires -Version 5.1
<#
    卸载 VioTray：停止进程、移除自启、删除安装目录。
    只操作 %LOCALAPPDATA%\VioTray，不碰其他位置。
#>
$ErrorActionPreference = 'Continue'

Get-Process VioTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Write-Host "已停止进程"

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'VioTray' -ErrorAction SilentlyContinue
Write-Host "已移除开机自启"

$targetDir = Join-Path $env:LOCALAPPDATA 'VioTray'
if (Test-Path $targetDir) {
    Write-Host "准备删除：$targetDir"
    Write-Host "（包含 config.json 和 logs，如需保留请先备份）"
    Remove-Item $targetDir -Recurse -Force
    Write-Host "已删除安装目录" -ForegroundColor Green
}
