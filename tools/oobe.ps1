<#
.SYNOPSIS
    一键启动 OOBE（初次配置向导）预览。

.DESCRIPTION
    --oobe-preview 只存在于 DEBUG 构建（见 App.xaml.cs 的 #if DEBUG），所以本脚本只认 Debug 产物。
    预览模式不读写 OobeState、不应用设置，随便点、随便关，不会把本机配置改掉。

    依次做四件事：
      1. 找 bin\Debug\<tfm>\win-x64\ClassSoftwareHub.exe，有多个取最新的
      2. 带 -Build 时先 dotnet build；产物不存在时也会自动编一次
      3. 结束正在运行的 ClassSoftwareHub（单实例：不杀干净，新进程只会唤醒旧实例然后自己退出）
      4. 以 --oobe-preview 启动

.PARAMETER Build
    启动前先编译 Debug。

.PARAMETER KeepRunning
    不结束已在运行的实例。⚠️ 单实例机制下这样启动看不到向导，一般只在调试别的东西时用。

.EXAMPLE
    pwsh tools\oobe.ps1

.EXAMPLE
    pwsh tools\oobe.ps1 -Build
#>
param(
    [switch]$Build,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'ClassSoftwareHub.Desktop.csproj'
$exePattern = Join-Path $root 'bin\Debug\*\win-x64\ClassSoftwareHub.exe'

function Find-OobeExe {
    $hit = Get-ChildItem -Path $exePattern -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending
    if ($hit) { return $hit[0].FullName }
    return $null
}

$exe = Find-OobeExe

if ($Build -or -not $exe) {
    Write-Host '编译 Debug ...' -ForegroundColor Cyan
    & dotnet build $proj -c Debug -v m
    if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
    $exe = Find-OobeExe
}

if (-not $exe) { throw '找不到 bin\Debug\...\win-x64\ClassSoftwareHub.exe，先 dotnet build 一次。' }

if (-not $KeepRunning) {
    $running = @(Get-Process -Name 'ClassSoftwareHub' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host ("结束正在运行的实例：PID " + ($running.Id -join ', ')) -ForegroundColor Yellow
        $running | Stop-Process -Force
        # 互斥体要一会儿才释放：不等等就启动，新进程会以为"已经有一个在跑"直接退出
        for ($i = 0; $i -lt 20; $i++) {
            if (@(Get-Process -Name 'ClassSoftwareHub' -ErrorAction SilentlyContinue).Count -eq 0) { break }
            Start-Sleep -Milliseconds 100
        }
    }
}

Write-Host ("启动 OOBE 预览：" + $exe) -ForegroundColor Cyan
$proc = Start-Process -FilePath $exe -ArgumentList '--oobe-preview' `
    -WorkingDirectory (Split-Path -Parent $exe) -PassThru

Start-Sleep -Milliseconds 2500
if ($proc.HasExited) {
    Write-Host ("进程已退出，退出码 " + $proc.ExitCode) -ForegroundColor Red
    $crash = Join-Path $env:LOCALAPPDATA 'ClassSoftwareHub\crash.log'
    if (Test-Path $crash) {
        Write-Host '--- crash.log 末尾 ---' -ForegroundColor DarkGray
        Get-Content $crash -Tail 20
    }
    exit 1
}

Write-Host ("已启动，PID " + $proc.Id + "。关掉向导窗口即退出。") -ForegroundColor Green
