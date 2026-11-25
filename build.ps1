#Requires -Version 5.1
<#
    编译并发布单文件版本。
    产物：src\VioTray\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\VioTray.exe
#>
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetRoot = Join-Path $env:USERPROFILE '.workbuddy\binaries\dotnet'

if (-not (Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) {
    throw "找不到 .NET SDK：$dotnetRoot\dotnet.exe"
}

$env:DOTNET_ROOT = $dotnetRoot
$env:MSBuildSDKsPath = Join-Path $dotnetRoot 'sdk\10.0.404\Sdks'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$csproj = Join-Path $projectRoot 'src\VioTray\VioTray.csproj'

& (Join-Path $dotnetRoot 'dotnet.exe') publish $csproj `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -v minimal

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码 $LASTEXITCODE"
}

$output = Join-Path $projectRoot 'src\VioTray\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\VioTray.exe'
Write-Host ""
Write-Host "发布完成：$output" -ForegroundColor Green
