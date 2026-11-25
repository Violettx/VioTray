#Requires -Version 5.1
<#
    安装 VioTray：复制到本地目录，注册开机自启，然后启动。
    参数：
      -NoStartup  不注册开机自启
      -NoLaunch   安装后不自动启动
#>
param(
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $projectRoot 'src\VioTray\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\VioTray.exe'

if (-not (Test-Path $source)) {
    throw "找不到发布产物：$source`n请先执行 build.ps1"
}

$targetDir = Join-Path $env:LOCALAPPDATA 'VioTray'
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

$targetExe = Join-Path $targetDir 'VioTray.exe'

Get-Process VioTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Copy-Item $source $targetExe -Force
Write-Host "已安装：$targetExe" -ForegroundColor Green

if (-not $NoStartup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $runKey -Name 'VioTray' -Value "`"$targetExe`""
    Write-Host "已注册开机自启（HKCU\...\Run）" -ForegroundColor Green
}

if (-not $NoLaunch) {
    Start-Process $targetExe
    Write-Host "已启动，看右下角托盘图标" -ForegroundColor Green
}

Write-Host ""
Write-Host "配置文件：$targetDir\config.json"
Write-Host "日志目录：$targetDir\logs"
