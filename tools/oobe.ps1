<#
.SYNOPSIS
    重新观看初次配置向导（OOBE），已安装的正式版同样适用。

.DESCRIPTION
    正式版（Release）里没有 --oobe-preview，该参数只存在于调试构建。因此脚本分两条路：

      · 调试版（bin\Debug 下的产物）：直接带 --oobe-preview 启动，向导处于预览模式，
        不读写 OobeState、不应用任何设置，本机的配置文件一个字节都不会变。
      · 已安装的正式版：临时把设置中的 OobeState 置为 Pending(=1)，应用会按
        「上次配置没有走完」的逻辑重新弹出向导。向导里的选项由当前设置初始化，
        看到的就是平时用的那一套。应用退出后，脚本把设置文件原样还原，
        本次在向导里改过的选择不会留下。

    无论走哪条路，都会先结束正在运行的实例：应用的单实例机制会让第二个进程
    只唤醒旧窗口然后自己退出，不结束就看不到向导。

.PARAMETER Exe
    指定要启动的程序路径。默认依次查卸载记录与默认安装目录。

.PARAMETER Build
    先编译 Debug 并以预览模式启动（仅在仓库内使用；脚本在其它位置时需配 -Project）。

.PARAMETER Project
    -Build 时使用的 csproj 路径。

.PARAMETER Preview
    强制以 --oobe-preview 启动。仅调试构建、或将来正式版开放该参数时有效。

.PARAMETER NoWait
    启动后立即返回。此模式下脚本不会自动还原设置，体验结束后请再运行一次 -Restore。

.PARAMETER Restore
    只做一件事：还原上次留下的设置备份。

.PARAMETER KeepRunning
    不结束正在运行的实例。单实例机制下通常看不到向导，仅用于排查其它问题。

.PARAMETER Yes
    跳过结束实例前的确认。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\oobe.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\oobe.ps1 -Exe "C:\Program Files\ClassSoftwareHub\ClassSoftwareHub.exe"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\oobe.ps1 -Restore
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$Build,
    [string]$Project,
    [switch]$Preview,
    [switch]$NoWait,
    [switch]$Restore,
    [switch]$KeepRunning,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$appName      = 'ClassSoftwareHub'
$appExe       = 'ClassSoftwareHub.exe'
$uninstallId  = '{7A2C4D18-9F31-4C6B-8E5A-2D0B7F4A1C93}_is1'
$pending      = 1
$done         = 2
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$settingsPath = Join-Path (Join-Path $localAppData $appName) 'settings.json'
$backupDir    = Join-Path $env:TEMP 'ClassSoftwareHub-OobeReplay'
$backupFile   = Join-Path $backupDir 'settings.backup.json'
$manifestFile = Join-Path $backupDir 'manifest.txt'

function Say([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
}

function Read-Text([string]$path) {
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

function Write-Text([string]$path, [string]$text) {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

function Restore-Settings {
    if (-not (Test-Path $manifestFile)) { return $false }

    $target = (Get-Content -Raw -Encoding UTF8 $manifestFile).Trim()
    if (-not (Test-Path $backupFile)) {
        Say ('备份文件缺失，无法自动还原：' + $backupFile) 'Red'
        return $false
    }

    try {
        [System.IO.File]::Copy($backupFile, $target, $true)
        Remove-Item -LiteralPath $manifestFile -Force
        Remove-Item -LiteralPath $backupFile -Force
        Say ('设置已还原：' + $target) 'Green'
        return $true
    }
    catch {
        Say ('还原失败：' + $_.Exception.Message) 'Red'
        Say ('请手动将 ' + $backupFile + ' 复制回 ' + $target) 'Yellow'
        return $false
    }
}

function Find-InstalledExe {
    $keys = @(
        (Join-Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' $uninstallId),
        (Join-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' $uninstallId),
        (Join-Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' $uninstallId)
    )
    foreach ($key in $keys) {
        if (-not (Test-Path $key)) { continue }
        $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($item -and $item.InstallLocation) {
            $candidate = Join-Path $item.InstallLocation $appExe
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }

    $candidates = @(
        (Join-Path (Join-Path $env:LOCALAPPDATA 'Programs') (Join-Path $appName $appExe)),
        (Join-Path (Join-Path $env:ProgramFiles $appName) $appExe)
    )
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path (Join-Path ${env:ProgramFiles(x86)} $appName) $appExe)
    }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

function Find-DebugExe([string]$root) {
    $pattern = Join-Path $root (Join-Path 'bin\Debug' (Join-Path '*' (Join-Path 'win-x64' $appExe)))
    $hit = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if ($hit) { return $hit[0].FullName }
    return $null
}

function Stop-RunningInstances {
    $running = @(Get-Process -Name $appName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    if (-not $Yes) {
        Say ('即将结束正在运行的 ' + $appName + '（PID ' + ($running.Id -join ', ') + '）。单实例机制要求先退出旧进程，否则新进程只会唤醒旧窗口。') 'Yellow'
        $answer = Read-Host '是否继续 [Y/N]'
        if ($answer -notmatch '^[Yy]') { throw '已取消。' }
    }

    $running | Stop-Process -Force
    for ($i = 0; $i -lt 30; $i++) {
        if (@(Get-Process -Name $appName -ErrorAction SilentlyContinue).Count -eq 0) { return }
        Start-Sleep -Milliseconds 100
    }
    Say '等待进程退出超时，向导可能无法正常显示。' 'Yellow'
}

# ── 只还原 ───────────────────────────────────────────────────────────
if ($Restore) {
    if (-not (Restore-Settings)) { Say '当前没有待还原的设置备份。' 'Yellow' }
    exit 0
}

# ── 处理上次异常中断留下的备份 ──────────────────────────────────────
if ((Test-Path $manifestFile) -and (Test-Path $backupFile)) {
    $oldTarget = (Get-Content -Raw -Encoding UTF8 $manifestFile).Trim()
    Say ('检测到上次未还原的设置备份：' + $oldTarget) 'Yellow'
    if ($Yes) {
        Restore-Settings | Out-Null
    }
    else {
        $answer = Read-Host '是否先还原 [Y/N]'
        if ($answer -match '^[Yy]') { Restore-Settings | Out-Null }
        else { throw '请先运行一次 -Restore，避免两份设置互相覆盖。' }
    }
}

# ── 定位程序 ───────────────────────────────────────────────────────
$exePath = $null
if ($Exe) {
    if (-not (Test-Path -LiteralPath $Exe)) { throw ('指定的程序不存在：' + $Exe) }
    $exePath = (Resolve-Path -LiteralPath $Exe).Path
}
else { $exePath = Find-InstalledExe }

$usePreview = $false
if ($exePath -and ($exePath -match '\\bin\\Debug\\')) { $usePreview = $true }
if ($Preview) { $usePreview = $true }

if ($Build) {
    $csproj = $Project
    if (-not $csproj) { $csproj = Join-Path (Split-Path -Parent $PSScriptRoot) 'ClassSoftwareHub.Desktop.csproj' }
    if (-not (Test-Path -LiteralPath $csproj)) {
        throw '无法编译：请用 -Project 指定 csproj 路径。'
    }
    Say '编译 Debug ...' 'Cyan'
    & dotnet build $csproj -c Debug -v m --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw ('编译失败（退出码 ' + $LASTEXITCODE + '）') }
    $exePath = Find-DebugExe (Split-Path -Parent $csproj)
    if ($exePath) { $usePreview = $true }
}

if (-not $exePath) {
    throw '找不到 ClassSoftwareHub.exe。请用 -Exe 指定路径，或在仓库内使用 -Build。'
}

Say ('程序：' + $exePath) 'Cyan'

# ── 调试版：零副作用预览 ───────────────────────────────────────────
if ($usePreview) {
    if ($exePath -notmatch '\\bin\\Debug\\') {
        Say '注意：--oobe-preview 目前只在调试构建中有效，正式版会忽略该参数。' 'Yellow'
    }
    if (-not $KeepRunning) { Stop-RunningInstances }
    $p = Start-Process -FilePath $exePath -ArgumentList '--oobe-preview' `
        -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
    Start-Sleep -Milliseconds 2500
    if ($p.HasExited) {
        Say ('进程已退出，退出码 ' + $p.ExitCode) 'Red'
        exit 1
    }
    Say ('预览已启动，PID ' + $p.Id + '。此模式不写入任何设置，关闭向导窗口即可。') 'Green'
    exit 0
}

# ── 正式版：临时置 Pending，结束后还原 ─────────────────────────────
$hasSettings = Test-Path -LiteralPath $settingsPath
if (-not $hasSettings) {
    Say ('设置文件不存在（' + $settingsPath + '），应用会按新装流程自行弹出向导，本次不改动设置。') 'Yellow'
}
else {
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }
    [System.IO.File]::Copy($settingsPath, $backupFile, $true)
    Write-Text $manifestFile $settingsPath

    $text = Read-Text $settingsPath
    $pattern = '"OobeState"\s*:\s*\d+'
    if ($text -match $pattern) {
        $text = [regex]::Replace($text, $pattern, ('"OobeState": ' + $pending))
    }
    else {
        $text = [regex]::Replace($text, '^(\s*\{)', ('$1"OobeState": ' + $pending + ','))
    }
    Write-Text $settingsPath $text
    Say '设置已备份，并临时标记为「初次配置未完成」。' 'Cyan'
}

if (-not $KeepRunning) { Stop-RunningInstances }

$proc = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
Start-Sleep -Milliseconds 2500
if ($proc.HasExited) {
    Say ('进程已退出，退出码 ' + $proc.ExitCode + '，向导可能没有弹出。') 'Red'
    if ($hasSettings) { Restore-Settings | Out-Null }
    exit 1
}
Say ('向导已启动，PID ' + $proc.Id + '。') 'Green'

if ($NoWait) {
    Say '已按 -NoWait 返回。体验结束后请运行一次 -Restore，将设置还原。' 'Yellow'
    exit 0
}

Say '体验完成后，请从托盘图标退出应用；脚本会在进程结束后自动还原设置。' 'Cyan'

$hinted = $false
$restored = $false
try {
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 500
        if (-not $hinted -and (Test-Path -LiteralPath $settingsPath)) {
            try {
                $now = Read-Text $settingsPath
                if ($now -match ('"OobeState"\s*:\s*' + $done)) {
                    Say '向导已经收尾。关闭应用后即完成本次体验。' 'Green'
                    $hinted = $true
                }
            }
            catch { }
        }
    }
}
finally {
    if ($hasSettings) {
        Say '应用已退出，正在还原设置 ...' 'Cyan'
        $restored = Restore-Settings
    }
}

if ($hasSettings -and -not $restored) {
    Say '设置未能自动还原，请手动运行一次 -Restore。' 'Red'
    exit 1
}
Say '本次体验结束，本机设置与体验前一致。' 'Green'
